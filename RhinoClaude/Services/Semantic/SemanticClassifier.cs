using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoClaude.Agent;
using RhinoClaude.Semantic;

namespace RhinoClaude.Services.Semantic
{
    /// <summary>
    /// Object-level classification (semantic plan §5.1's first workload): walk the document
    /// once, decide what each object is, and hand back an immutable <see cref="SemanticView"/>.
    ///
    /// The decision itself lives in <see cref="ObjectClassifier"/>, which is Rhino-free and
    /// tested. This service's job is only to turn Rhino objects into <see cref="ObjectFacts"/>
    /// and to assemble the result — measuring geometry, deriving groups and adjacencies,
    /// counting what stayed unclassified.
    ///
    /// UI-thread only, like every other service that touches the document.
    /// </summary>
    public sealed class SemanticClassifier
    {
        private readonly RhinoDoc _docHandle;
        private readonly uint _docSerialNumber;
        private readonly LayerConventionStore _conventions;

        public SemanticClassifier(RhinoDoc doc, LayerConventionStore conventions)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _docHandle = doc;
            _docSerialNumber = doc.RuntimeSerialNumber;
            _conventions = conventions ?? throw new ArgumentNullException(nameof(conventions));
        }

        private RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(_docSerialNumber) ?? _docHandle;

        /// <summary>Model units per foot, plus tolerance — every threshold in the plan is in feet.</summary>
        public static UnitContext UnitsFor(RhinoDoc doc)
        {
            double unitsPerFoot;
            try
            {
                unitsPerFoot = RhinoMath.UnitScale(UnitSystem.Feet, doc.ModelUnitSystem);
            }
            catch (Exception)
            {
                unitsPerFoot = 1.0;
            }

            if (unitsPerFoot <= 1e-9 || double.IsNaN(unitsPerFoot)) unitsPerFoot = 1.0;
            return new UnitContext(unitsPerFoot, doc.ModelUnitSystem.ToString(), doc.ModelAbsoluteTolerance);
        }

        public SemanticView Classify()
        {
            var stopwatch = Stopwatch.StartNew();
            var doc = Doc;
            var units = UnitsFor(doc);
            var resolver = _conventions.BuildResolver();

            var view = new SemanticView
            {
                UnitSystem = units.UnitSystemName,
                Tolerance = units.Tolerance,
                FloorToFloorDefault = resolver.FloorToFloorDefault
            };

            var explicitGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rhinoGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var unclassifiedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var obj in doc.Objects)
            {
                if (obj == null || obj.IsDeleted || obj.Geometry == null) continue;

                var facts = BuildFacts(doc, obj, units);
                var verdict = ObjectClassifier.Classify(facts, resolver, units);

                if (!verdict.IsClassified)
                {
                    view.UnclassifiedCount++;
                    if (!string.IsNullOrEmpty(facts.LayerFullPath)) unclassifiedLayers.Add(facts.LayerFullPath);
                    continue;
                }

                switch (verdict.ElementType)
                {
                    case SemanticVocabulary.Mass:
                        {
                            var mass = BuildMass(doc, obj, facts, verdict, units);
                            view.Masses.Add(mass);
                            if (!string.IsNullOrWhiteSpace(verdict.MassGroupName))
                                explicitGroups[mass.ElementId] = verdict.MassGroupName;
                            CollectRhinoGroups(doc, obj, mass.ElementId, rhinoGroups);
                            break;
                        }

                    case SemanticVocabulary.Opening:
                        view.LooseOpenings.Add(BuildLooseOpening(obj, facts, verdict));
                        break;

                    case SemanticVocabulary.Overhang:
                        view.LooseOverhangs.Add(BuildLooseOverhang(obj, facts, verdict));
                        break;

                    case SemanticVocabulary.Site:
                        view.SiteElements.Add(BuildSite(obj, facts, verdict));
                        break;

                    case SemanticVocabulary.Level:
                        view.Levels.Add(BuildLevel(obj, facts, verdict));
                        break;

                    case SemanticVocabulary.MassGroup:
                        // A MassGroup tag on an object names the group its Mass belongs to; the
                        // group itself is derived from membership, not created here.
                        explicitGroups[ElementId(obj)] = verdict.MassGroupName ?? facts.Name;
                        break;

                    default:
                        view.UnclassifiedCount++;
                        break;
                }
            }

            foreach (var layer in unclassifiedLayers.OrderBy(l => l, StringComparer.Ordinal))
                view.UnclassifiedLayers.Add(layer);

            CompositionAnalyzer.FillAdjacencies(view.Masses, units);
            foreach (var group in CompositionAnalyzer.DeriveGroups(view.Masses, explicitGroups, rhinoGroups))
                view.Groups.Add(group);

            foreach (var level in InferLevels(view, resolver.FloorToFloorDefault))
                view.Levels.Add(level);

            if (view.Masses.Count == 0)
            {
                view.Notes.Add(
                    "No Masses found. If your layers use a different naming convention, run " +
                    "ClaudeLearnNamingConvention, or use ClaudeSetElement to tag your solid Breps as Masses. " +
                    "See LAYER_CONVENTIONS.md.");
            }

            if (view.UnclassifiedCount > 0)
            {
                view.Notes.Add(view.UnclassifiedCount + " object(s) could not be classified and are " +
                               "absent from every semantic result.");
            }

            stopwatch.Stop();
            view.BuildMs = stopwatch.ElapsedMilliseconds;
            return view;
        }

        // ── Facts ─────────────────────────────────────────────────────

        private static ObjectFacts BuildFacts(RhinoDoc doc, RhinoObject obj, UnitContext units)
        {
            var facts = new ObjectFacts
            {
                ObjectId = obj.Id.ToString(),
                LayerFullPath = doc.Layers[obj.Attributes.LayerIndex]?.FullPath,
                Name = obj.Name
            };

            foreach (var key in ReadUserStringKeys(obj))
            {
                if (!key.StartsWith("RhinoClaude:", StringComparison.OrdinalIgnoreCase)) continue;
                facts.UserStrings[key] = obj.Attributes.GetUserString(key);
            }

            var geometry = obj.Geometry;
            var box = geometry.GetBoundingBox(true);
            facts.Bbox = ToBox(box);

            switch (geometry)
            {
                case Brep brep:
                    facts.IsBrep = true;
                    facts.IsClosedSolid = brep.IsSolid;
                    facts.IsPlanarSurface = brep.Faces.Count == 1 && brep.Faces[0].IsPlanar(units.Tolerance);
                    Measure(brep, facts);
                    break;

                case Extrusion extrusion:
                    {
                        facts.IsBrep = true;
                        facts.IsClosedSolid = extrusion.IsSolid;
                        var converted = extrusion.ToBrep();
                        if (converted != null) Measure(converted, facts);
                        break;
                    }

                case Surface surface:
                    {
                        facts.IsBrep = true;
                        var converted = surface.ToBrep();
                        facts.IsPlanarSurface = surface.IsPlanar(units.Tolerance);
                        if (converted != null) Measure(converted, facts);
                        break;
                    }

                case Curve curve:
                    facts.IsCurve = true;
                    facts.IsClosedCurve = curve.IsClosed;
                    if (curve.IsClosed && curve.IsPlanar(units.Tolerance))
                    {
                        var area = AreaMassProperties.Compute(curve);
                        if (area != null) facts.Area = Math.Abs(area.Area);
                    }
                    break;

                case Mesh mesh:
                    facts.IsMesh = true;
                    facts.IsClosedSolid = mesh.IsClosed;
                    if (mesh.IsClosed)
                    {
                        var volume = VolumeMassProperties.Compute(mesh);
                        if (volume != null) facts.Volume = Math.Abs(volume.Volume);
                    }
                    break;
            }

            return facts;
        }

        private static void Measure(Brep brep, ObjectFacts facts)
        {
            try
            {
                if (brep.IsSolid)
                {
                    var volume = VolumeMassProperties.Compute(brep);
                    if (volume != null) facts.Volume = Math.Abs(volume.Volume);
                }

                var area = AreaMassProperties.Compute(brep);
                if (area != null) facts.Area = Math.Abs(area.Area);
            }
            catch (Exception)
            {
                // A Brep too broken to measure is a Brep the classifier should leave alone.
            }
        }

        /// <summary>
        /// RhinoCommon exposes user strings as a NameValueCollection on the attributes; the
        /// enumerable form differs enough between versions that reading the keys defensively
        /// is cheaper than a version check.
        /// </summary>
        private static IEnumerable<string> ReadUserStringKeys(RhinoObject obj)
        {
            System.Collections.Specialized.NameValueCollection strings;
            try
            {
                strings = obj.Attributes.GetUserStrings();
            }
            catch (Exception)
            {
                yield break;
            }

            if (strings == null) yield break;
            foreach (string key in strings.AllKeys)
                if (!string.IsNullOrEmpty(key)) yield return key;
        }

        // ── Element construction ──────────────────────────────────────

        /// <summary>
        /// Element ids are derived from the Rhino object id rather than freshly generated, so
        /// they are stable across sessions without a side table to maintain (plan §3 preamble's
        /// "stable across sessions").
        /// </summary>
        public static string ElementId(RhinoObject obj) => obj.Id.ToString();

        private static MassView BuildMass(
            RhinoDoc doc, RhinoObject obj, ObjectFacts facts, ObjectClassification verdict, UnitContext units)
        {
            var mass = new MassView
            {
                ElementId = ElementId(obj),
                Layer = facts.LayerFullPath,
                Name = string.IsNullOrWhiteSpace(obj.Name) ? SynthesizeName(verdict.Subtype, obj) : obj.Name,
                ClassifiedBy = verdict.ClassifiedBy,
                Function = SemanticVocabulary.Normalize(verdict.Subtype, SemanticVocabulary.MassFunctions,
                                                        SemanticVocabulary.FunctionOther),
                Volume = facts.Volume,
                Bbox = facts.Bbox,
                IsSolid = facts.IsClosedSolid
            };
            mass.RhinoObjectIds.Add(facts.ObjectId);
            if (!string.IsNullOrEmpty(verdict.Note)) mass.Notes.Add(verdict.Note);
            CopyTags(obj, mass);

            var brep = AsBrep(obj.Geometry);
            if (brep != null)
            {
                mass.FaceCount = brep.Faces.Count;
                mass.EdgeCount = brep.Edges.Count;
                mass.FootprintArea = FootprintArea(brep, units);
                mass.PrincipalAxes = GeometryMath.PrincipalAxes(
                    brep.Vertices.Select(v => ToVec(v.Location)).ToList());

                var volume = brep.IsSolid ? VolumeMassProperties.Compute(brep) : null;
                mass.Centroid = volume != null ? ToVec(volume.Centroid) : mass.Bbox.Center;
            }
            else
            {
                mass.FootprintArea = mass.Bbox.FootprintArea;
                mass.Centroid = mass.Bbox.Center;
            }

            mass.HeightAboveGrade = mass.Bbox.IsValid ? mass.Bbox.Height : 0;

            if (!mass.IsSolid)
            {
                mass.Notes.Add("This Brep is not closed. Volume and face-role results on it are " +
                               "approximate; a boolean against it may fail.");
            }

            return mass;
        }

        /// <summary>
        /// Footprint as the horizontal projection of the solid, computed as the summed
        /// horizontally-projected area of its downward-facing faces. Exact for the prismatic
        /// and stepped solids SD massing is made of, and far cheaper than a real projection —
        /// which matters because it runs for every Mass on every object-level rebuild.
        /// </summary>
        private static double FootprintArea(Brep brep, UnitContext units)
        {
            double total = 0;
            foreach (var face in brep.Faces)
            {
                var area = AreaMassProperties.Compute(face);
                if (area == null) continue;

                var centroid = area.Centroid;
                double u, v;
                if (!face.ClosestPoint(centroid, out u, out v)) continue;

                var normal = face.NormalAt(u, v);
                if (face.OrientationIsReversed) normal.Reverse();
                if (normal.Z >= 0) continue;                       // only the undersides project

                total += Math.Abs(area.Area) * Math.Abs(normal.Z);
            }

            if (total > 0) return total;

            // An open Brep may have no downward faces at all; the bounding box is the honest
            // fallback and the Mass already carries a "not closed" note.
            var box = brep.GetBoundingBox(true);
            return box.IsValid ? Math.Max(0, box.Max.X - box.Min.X) * Math.Max(0, box.Max.Y - box.Min.Y) : 0;
        }

        private static OpeningView BuildLooseOpening(RhinoObject obj, ObjectFacts facts, ObjectClassification verdict)
        {
            var opening = new OpeningView
            {
                ElementId = ElementId(obj),
                Layer = facts.LayerFullPath,
                Name = string.IsNullOrWhiteSpace(obj.Name) ? (verdict.Subtype ?? "Opening") : obj.Name,
                ClassifiedBy = verdict.ClassifiedBy,
                OpeningType = SemanticVocabulary.Normalize(verdict.Subtype, SemanticVocabulary.OpeningTypes,
                                                           SemanticVocabulary.OpeningWindow),
                Origin = verdict.ClassifiedBy == SemanticVocabulary.ByUserData ? "explicit-tag" : "drawn-on-layer",
                IsEntry = verdict.IsEntry,
                EntryType = verdict.EntryType,
                Area = facts.Area,
                Centroid = facts.Bbox.Center
            };
            opening.RhinoObjectIds.Add(facts.ObjectId);
            CopyTags(obj, opening);

            if (facts.Bbox.IsValid)
            {
                var size = facts.Bbox.Size;
                opening.Height = size.Z;
                // The opening lies in a vertical plane, so its width is whichever horizontal
                // extent is non-degenerate.
                opening.Width = Math.Max(size.X, size.Y);
                if (opening.Area <= 0) opening.Area = opening.Width * opening.Height;
            }

            return opening;
        }

        private static OverhangView BuildLooseOverhang(RhinoObject obj, ObjectFacts facts, ObjectClassification verdict)
        {
            var overhang = new OverhangView
            {
                ElementId = ElementId(obj),
                Layer = facts.LayerFullPath,
                Name = string.IsNullOrWhiteSpace(obj.Name) ? (verdict.Subtype ?? "Overhang") : obj.Name,
                ClassifiedBy = verdict.ClassifiedBy,
                Subtype = SemanticVocabulary.Normalize(verdict.Subtype, SemanticVocabulary.OverhangTypes, "Other"),
                Bbox = facts.Bbox,
                Centroid = facts.Bbox.Center,
                Area = facts.Area,
                Origin = facts.IsClosedSolid
                    ? SemanticVocabulary.OverhangFromSeparateMass
                    : SemanticVocabulary.OverhangFromExtrudedEdge
            };
            overhang.RhinoObjectIds.Add(facts.ObjectId);
            CopyTags(obj, overhang);

            if (facts.Bbox.IsValid)
            {
                var size = facts.Bbox.Size;
                overhang.Thickness = Math.Min(Math.Min(size.X, size.Y), size.Z);
                overhang.Width = Math.Max(Math.Max(size.X, size.Y), size.Z);
                overhang.ProjectionDistance = size.X + size.Y + size.Z - overhang.Width - overhang.Thickness;
            }

            return overhang;
        }

        private static SiteView BuildSite(RhinoObject obj, ObjectFacts facts, ObjectClassification verdict)
        {
            var site = new SiteView
            {
                ElementId = ElementId(obj),
                Layer = facts.LayerFullPath,
                Name = string.IsNullOrWhiteSpace(obj.Name) ? (verdict.Subtype ?? "Site") : obj.Name,
                ClassifiedBy = verdict.ClassifiedBy,
                SiteType = SemanticVocabulary.Normalize(verdict.Subtype, SemanticVocabulary.SiteTypes, "Other"),
                Bbox = facts.Bbox,
                Centroid = facts.Bbox.Center,
                IsClosedCurve = facts.IsClosedCurve
            };
            site.RhinoObjectIds.Add(facts.ObjectId);
            CopyTags(obj, site);

            if (facts.Area > 0) site.Area = facts.Area;
            if (obj.Geometry is Curve curve) site.Length = curve.GetLength();

            return site;
        }

        private static LevelView BuildLevel(RhinoObject obj, ObjectFacts facts, ObjectClassification verdict)
        {
            var level = new LevelView
            {
                ElementId = ElementId(obj),
                Layer = facts.LayerFullPath,
                Name = string.IsNullOrWhiteSpace(obj.Name)
                    ? (string.IsNullOrWhiteSpace(verdict.Subtype) ? "Level" : "Level " + verdict.Subtype)
                    : obj.Name,
                ClassifiedBy = verdict.ClassifiedBy,
                Elevation = verdict.Elevation ?? (facts.Bbox.IsValid ? facts.Bbox.Min.Z : 0),
                Inferred = false
            };
            level.RhinoObjectIds.Add(facts.ObjectId);
            CopyTags(obj, level);

            level.IsRoofLevel = (verdict.Subtype ?? string.Empty)
                .IndexOf("roof", StringComparison.OrdinalIgnoreCase) >= 0;

            return level;
        }

        /// <summary>
        /// Plan §3.11 heuristic 4: with no Levels drawn and a configured floor-to-floor, build
        /// the ladder from the lowest Mass base up to the highest Mass top. Every synthesized
        /// Level is flagged <c>inferred</c> so nobody mistakes it for something the user drew.
        /// </summary>
        private static List<LevelView> InferLevels(SemanticView view, double floorToFloor)
        {
            var levels = new List<LevelView>();
            if (view.Levels.Count > 0 || floorToFloor <= 0 || view.Masses.Count == 0) return levels;

            var extents = view.BuildingExtents();
            if (!extents.IsValid || extents.Height < floorToFloor) return levels;

            int index = 1;
            for (double z = extents.Min.Z; z <= extents.Max.Z + 1e-6; z += floorToFloor)
            {
                bool isRoof = z + floorToFloor > extents.Max.Z + 1e-6;
                var level = new LevelView
                {
                    ElementId = "level:inferred:" + index,
                    Name = isRoof ? "Roof" : "Level " + index.ToString("00"),
                    Elevation = z,
                    FloorToFloor = floorToFloor,
                    IsRoofLevel = isRoof,
                    Inferred = true,
                    ClassifiedBy = SemanticVocabulary.ByGeometryInference
                };
                level.Notes.Add("Synthesized from the firm floor-to-floor default; no Level was drawn.");
                levels.Add(level);

                index++;
                if (index > 200) break;      // a runaway floor-to-floor must not hang the classifier
            }

            return levels;
        }

        // ── Shared helpers ────────────────────────────────────────────

        private static void CopyTags(RhinoObject obj, SemanticElement element)
        {
            var tagService = RhinoClaudePlugin.Instance?.TagService;
            if (tagService == null) return;

            try
            {
                foreach (var pair in tagService.GetAllTags(obj))
                    element.Tags[pair.Key] = ToolJson.Safe(pair.Value);
            }
            catch (Exception)
            {
                // Tag reading is a nicety; a failure here must not cost the classification.
            }
        }

        private static void CollectRhinoGroups(
            RhinoDoc doc, RhinoObject obj, string elementId, Dictionary<string, List<string>> into)
        {
            int[] groupIndices;
            try
            {
                groupIndices = obj.Attributes.GetGroupList();
            }
            catch (Exception)
            {
                return;
            }

            if (groupIndices == null) return;

            foreach (int index in groupIndices)
            {
                string name;
                try
                {
                    name = doc.Groups.FindIndex(index)?.Name;
                }
                catch (Exception)
                {
                    name = null;
                }

                if (string.IsNullOrWhiteSpace(name)) name = "Rhino group " + index;
                if (!into.TryGetValue(name, out var members))
                {
                    members = new List<string>();
                    into[name] = members;
                }
                members.Add(elementId);
            }
        }

        private static string SynthesizeName(string function, RhinoObject obj)
        {
            string label = string.IsNullOrWhiteSpace(function) ? "Mass" : function + " Mass";
            return label + " (" + obj.Id.ToString().Substring(0, 8) + ")";
        }

        public static Brep AsBrep(GeometryBase geometry)
        {
            switch (geometry)
            {
                case Brep brep: return brep;
                case Extrusion extrusion: return extrusion.ToBrep();
                case Surface surface: return surface.ToBrep();
                default: return null;
            }
        }

        public static Vec3 ToVec(Point3d point) => new Vec3(point.X, point.Y, point.Z);
        public static Vec3 ToVec(Vector3d vector) => new Vec3(vector.X, vector.Y, vector.Z);
        public static Point3d ToPoint(Vec3 v) => new Point3d(v.X, v.Y, v.Z);
        public static Vector3d ToVector(Vec3 v) => new Vector3d(v.X, v.Y, v.Z);

        public static BoxView ToBox(BoundingBox box) =>
            !box.IsValid ? BoxView.Unset : BoxView.From(ToVec(box.Min), ToVec(box.Max));

        public static BoundingBox ToBoundingBox(BoxView box) =>
            box == null || !box.IsValid ? BoundingBox.Unset : new BoundingBox(ToPoint(box.Min), ToPoint(box.Max));
    }
}
