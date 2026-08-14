using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>
    /// One coherent picture of the massing: the object-level view, the geometry views for
    /// whichever masses the query needed, and the document's units.
    ///
    /// Every analytical read tool takes one of these and returns plain data. That is what
    /// makes check_wall_window_ratio, get_roof_analysis, check_massing_composition and
    /// get_zoning_envelope testable without a Rhino document — the numbers the reviewer will
    /// treat as fact are computed by code with tests on it.
    /// </summary>
    public sealed class MassingSnapshot
    {
        public SemanticView View { get; }
        public UnitContext Units { get; }

        private readonly Dictionary<string, MassGeometryView> _geometry =
            new Dictionary<string, MassGeometryView>(StringComparer.OrdinalIgnoreCase);

        public MassingSnapshot(SemanticView view, IEnumerable<MassGeometryView> geometry, UnitContext units)
        {
            View = view ?? new SemanticView();
            Units = units ?? UnitContext.Feet();

            foreach (var item in geometry ?? Enumerable.Empty<MassGeometryView>())
                if (item?.MassId != null) _geometry[item.MassId] = item;
        }

        public IReadOnlyList<MassView> Masses => View.Masses;

        public MassGeometryView GeometryFor(string massId) =>
            massId != null && _geometry.TryGetValue(massId, out var view) ? view : null;

        public IEnumerable<MassGeometryView> AllGeometry => _geometry.Values;

        /// <summary>Every labelled face across every mass in the snapshot.</summary>
        public IEnumerable<FaceView> AllFaces => _geometry.Values.SelectMany(g => g.Faces);

        public IEnumerable<FaceView> FacesWithRole(string role) => AllFaces.Where(f => f.HasRole(role));

        public IEnumerable<OpeningView> AllOpenings => _geometry.Values.SelectMany(g => g.AllOpenings);

        public MassView MassOf(FaceView face) => face == null ? null : View.FindMass(face.MassId);

        /// <summary>Snapshot scoped to one mass — the common case for a per-mass query.</summary>
        public MassingSnapshot Scoped(string massId)
        {
            var mass = View.FindMass(massId);
            if (mass == null) return new MassingSnapshot(new SemanticView(), null, Units);

            var scoped = new SemanticView
            {
                UnitSystem = View.UnitSystem,
                Tolerance = View.Tolerance,
                FloorToFloorDefault = View.FloorToFloorDefault
            };
            scoped.Masses.Add(mass);
            foreach (var site in View.SiteElements) scoped.SiteElements.Add(site);
            foreach (var level in View.Levels) scoped.Levels.Add(level);

            var geometry = GeometryFor(mass.ElementId);
            return new MassingSnapshot(scoped, geometry == null ? null : new[] { geometry }, Units);
        }
    }
}
