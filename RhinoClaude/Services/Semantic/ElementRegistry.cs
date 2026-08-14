using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using RhinoClaude.Agent;
using RhinoClaude.Semantic;

namespace RhinoClaude.Services.Semantic
{
    /// <summary>
    /// The two-tier cache from semantic plan §6.2.
    ///
    /// Tier 1 — the object-level <see cref="SemanticView"/>. Small, rebuilt whenever the
    /// document changes shape. Budget: &lt;150 ms on a mid-scale SD model.
    ///
    /// Tier 2 — a <see cref="MassGeometryView"/> per Mass. Larger, and only ever built for the
    /// masses the agent actually asks about. Invalidated when that mass's Rhino object is
    /// replaced or deleted. Budget: &lt;50 ms per mass.
    ///
    /// Invalidation is a dirty flag rather than an eager rebuild, which makes plan risk #7
    /// (invalidation storms during a 500-object paste) cost one boolean per event instead of a
    /// debounce timer — the rebuild happens once, lazily, on the next query.
    ///
    /// Cached views are immutable snapshots, so reading one from a background thread is safe;
    /// refreshes happen on the UI thread because they touch the document.
    /// </summary>
    public sealed class ElementRegistry : IDisposable
    {
        private readonly uint _docSerialNumber;
        private readonly SemanticClassifier _classifier;
        private readonly MassGeometryAnalyzer _analyzer;
        private readonly JsonlLogger _timingLog;
        private readonly object _gate = new object();

        private SemanticView _view;
        private bool _objectLevelDirty = true;

        private readonly Dictionary<string, MassGeometryView> _geometryViews =
            new Dictionary<string, MassGeometryView>(StringComparer.OrdinalIgnoreCase);

        private bool _disposed;

        public ElementRegistry(
            RhinoDoc doc,
            SemanticClassifier classifier,
            MassGeometryAnalyzer analyzer,
            JsonlLogger timingLog = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _docSerialNumber = doc.RuntimeSerialNumber;
            _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
            _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
            _timingLog = timingLog;

            Subscribe();
        }

        private RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(_docSerialNumber);

        /// <summary>Model units per foot and tolerance for the current document.</summary>
        public UnitContext Units
        {
            get
            {
                var doc = Doc;
                return doc == null ? UnitContext.Feet() : SemanticClassifier.UnitsFor(doc);
            }
        }

        // ── Tier 1 ────────────────────────────────────────────────────

        /// <summary>The object-level view, rebuilding it first if the document has moved on.</summary>
        public SemanticView View
        {
            get
            {
                lock (_gate)
                {
                    if (_view != null && !_objectLevelDirty) return _view;

                    _view = _classifier.Classify();
                    _objectLevelDirty = false;
                    _geometryViews.Clear();     // face indices are only valid against one snapshot

                    Log("object-level", null, _view.BuildMs, new Dictionary<string, object>
                    {
                        { "massCount", _view.Masses.Count },
                        { "unclassified", _view.UnclassifiedCount },
                        { "siteCount", _view.SiteElements.Count }
                    });

                    return _view;
                }
            }
        }

        // ── Tier 2 ────────────────────────────────────────────────────

        /// <summary>
        /// Face and edge labels for one Mass, computed on first ask and cached until that
        /// mass's Brep changes.
        /// </summary>
        public MassGeometryView GeometryFor(MassView mass)
        {
            if (mass == null) return null;

            var view = View;   // ensures tier 1 is current, which may clear tier 2

            lock (_gate)
            {
                if (_geometryViews.TryGetValue(mass.ElementId, out var cached)) return cached;
            }

            var geometry = _analyzer.Analyze(mass, view, Units);

            lock (_gate)
            {
                _geometryViews[mass.ElementId] = geometry;
            }

            Log("mass-geometry", mass.ElementId, geometry.AnalyzeMs, new Dictionary<string, object>
            {
                { "faceCount", geometry.Faces.Count },
                { "edgeCount", geometry.Edges.Count },
                { "openingCount", geometry.AllOpenings.Count() },
                { "cutCount", geometry.Cuts.Count },
                { "historyAvailable", geometry.HistoryAvailable }
            });

            return geometry;
        }

        public MassGeometryView GeometryFor(string massId)
        {
            var mass = View.FindMass(massId);
            return mass == null ? null : GeometryFor(mass);
        }

        /// <summary>Every mass's geometry — the expensive path, used by whole-building queries.</summary>
        public List<MassGeometryView> AllGeometry()
        {
            return View.Masses.Select(GeometryFor).Where(g => g != null).ToList();
        }

        /// <summary>Resolve a mass by element id, object id, or name; null when nothing matches.</summary>
        public MassView FindMass(string identifier)
        {
            var view = View;
            var mass = view.FindMass(identifier);
            if (mass != null) return mass;

            if (string.IsNullOrWhiteSpace(identifier)) return null;

            return view.Masses.FirstOrDefault(m =>
                string.Equals(m.Name, identifier, StringComparison.OrdinalIgnoreCase));
        }

        // ── Invalidation ──────────────────────────────────────────────

        /// <summary>Drop everything. Called on layer-table changes and after semantic writes.</summary>
        public void InvalidateAll()
        {
            lock (_gate)
            {
                _objectLevelDirty = true;
                _geometryViews.Clear();
            }
        }

        /// <summary>Drop one mass's geometry without rebuilding the object level.</summary>
        public void InvalidateMass(string massId)
        {
            if (string.IsNullOrWhiteSpace(massId)) return;
            lock (_gate) _geometryViews.Remove(massId);
        }

        private void Subscribe()
        {
            RhinoDoc.AddRhinoObject += OnObjectChanged;
            RhinoDoc.DeleteRhinoObject += OnObjectChanged;
            RhinoDoc.ReplaceRhinoObject += OnObjectReplaced;
            RhinoDoc.UndeleteRhinoObject += OnObjectChanged;
            RhinoDoc.LayerTableEvent += OnLayerTableEvent;
            RhinoDoc.ModifyObjectAttributes += OnAttributesChanged;
        }

        private void Unsubscribe()
        {
            RhinoDoc.AddRhinoObject -= OnObjectChanged;
            RhinoDoc.DeleteRhinoObject -= OnObjectChanged;
            RhinoDoc.ReplaceRhinoObject -= OnObjectReplaced;
            RhinoDoc.UndeleteRhinoObject -= OnObjectChanged;
            RhinoDoc.LayerTableEvent -= OnLayerTableEvent;
            RhinoDoc.ModifyObjectAttributes -= OnAttributesChanged;
        }

        private bool IsOurDocument(RhinoDoc doc) => doc != null && doc.RuntimeSerialNumber == _docSerialNumber;

        private void OnObjectChanged(object sender, Rhino.DocObjects.RhinoObjectEventArgs e)
        {
            if (!IsOurDocument(e?.TheObject?.Document)) return;
            InvalidateAll();
        }

        private void OnObjectReplaced(object sender, Rhino.DocObjects.RhinoReplaceObjectEventArgs e)
        {
            if (e == null) return;
            if (!IsOurDocument(e.Document)) return;

            // A replaced Brep is the "the mass changed shape" case the plan names explicitly:
            // its geometry view must go, and the object level with it, because volumes and
            // adjacencies moved too.
            InvalidateAll();
        }

        private void OnAttributesChanged(object sender, Rhino.DocObjects.RhinoModifyObjectAttributesEventArgs e)
        {
            if (e == null || !IsOurDocument(e.Document)) return;

            // Layer moves and RhinoClaude:* user strings both live on attributes, and both
            // change what an object classifies as.
            InvalidateAll();
        }

        private void OnLayerTableEvent(object sender, Rhino.DocObjects.Tables.LayerTableEventArgs e)
        {
            // Plan risk #6: a layer renamed mid-session must not leave a stale classification.
            if (e != null && e.Document != null && e.Document.RuntimeSerialNumber != _docSerialNumber) return;
            InvalidateAll();
        }

        // ── Instrumentation (plan §6.2) ───────────────────────────────

        private void Log(string tier, string massId, long elapsedMs, Dictionary<string, object> extra)
        {
            if (_timingLog == null) return;

            var entry = new Dictionary<string, object>
            {
                { "tier", tier },
                { "massId", massId },
                { "elapsedMs", elapsedMs }
            };

            if (extra != null)
                foreach (var pair in extra) entry[pair.Key] = pair.Value;

            try { _timingLog.Append(entry); }
            catch (Exception) { /* logging must never break a query */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Unsubscribe();
        }
    }
}
