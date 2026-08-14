using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoClaude.Semantic;

namespace RhinoClaude.Services.Semantic
{
    /// <summary>
    /// Geometry-level classification (semantic plan §5.1's second workload): given one Mass,
    /// label its Brep faces with orientations and roles, its edges with roles, its inner trim
    /// loops as Openings, and its inner shells as Cuts.
    ///
    /// The rules live in the Rhino-free <see cref="FaceClassifier"/>, <see cref="EdgeClassifier"/>
    /// and <see cref="OpeningClassifier"/>; this service measures the Brep and calls them. It
    /// runs on demand, per Mass, because face labelling is fast for one mass and expensive
    /// across a whole document (plan §6.2's &lt;50 ms per-Mass budget).
    ///
    /// UI-thread only.
    /// </summary>
    public sealed class MassGeometryAnalyzer
    {
        private readonly uint _docSerialNumber;
        private readonly BooleanHistoryReader _history;

        public MassGeometryAnalyzer(RhinoDoc doc, BooleanHistoryReader history)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _docSerialNumber = doc.RuntimeSerialNumber;
            _history = history;
        }

        private RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(_docSerialNumber);

        public MassGeometryView Analyze(MassView mass, SemanticView context, UnitContext units)
        {
            var stopwatch = Stopwatch.StartNew();

            var view = new MassGeometryView
            {
                MassId = mass?.ElementId,
                SourceObjectId = mass?.PrimaryObjectId
            };

            var doc = Doc;
            if (doc == null || mass == null)
            {
                view.Notes.Add("The document or mass is no longer available.");
                stopwatch.Stop();
                view.AnalyzeMs = stopwatch.ElapsedMilliseconds;
                return view;
            }

            units = units ?? SemanticClassifier.UnitsFor(doc);

            RhinoObject obj = null;
            if (Guid.TryParse(mass.PrimaryObjectId, out var id)) obj = doc.Objects.FindId(id);

            var brep = obj == null ? null : SemanticClassifier.AsBrep(obj.Geometry);
            if (brep == null)
            {
                view.Notes.Add("This mass has no Brep geometry to analyse — its faces, edges and " +
                               "openings are unavailable. Fall back to the raw geometry tools.");
                stopwatch.Stop();
                view.AnalyzeMs = stopwatch.ElapsedMilliseconds;
                return view;
            }

            var faceRoleOverrides = ReadFaceRoleOverrides(obj);
            var interiorFaceIndices = FindInnerShellFaces(brep, units, view, mass);

            BuildFaces(brep, mass, context, units, faceRoleOverrides, interiorFaceIndices, view);
            BuildEdges(brep, view);
            DetectRecesses(mass, units, view);
            DetectCantileverOverhangs(mass, units, view);
            AttachLooseElements(mass, context, units, view);

            if (_history != null && !string.IsNullOrEmpty(mass.PrimaryObjectId))
            {
                var operations = _history.Read(mass.PrimaryObjectId, out bool available);
                view.HistoryAvailable = available;
                view.History.AddRange(operations);
            }

            stopwatch.Stop();
            view.AnalyzeMs = stopwatch.ElapsedMilliseconds;
            return view;
        }

        // ── Faces ─────────────────────────────────────────────────────

        private void BuildFaces(
            Brep brep, MassView mass, SemanticView context, UnitContext units,
            Dictionary<int, string> overrides, HashSet<int> interiorFaceIndices, MassGeometryView view)
        {
            double massBase = mass.Bbox != null && mass.Bbox.IsValid ? mass.Bbox.Min.Z : 0;
            var neighbours = (context?.Masses ?? new List<MassView>())
                .Where(m => m.ElementId != mass.ElementId && m.Bbox != null && m.Bbox.IsValid)
                .ToList();

            for (int i = 0; i < brep.Faces.Count; i++)
            {
                var face = brep.Faces[i];
                var box = face.GetBoundingBox(true);

                var areaProperties = SafeArea(face);
                var centroid = areaProperties?.Centroid ?? box.Center;
                var normal = OutwardNormal(face, centroid);

                bool isPlanar = face.IsPlanar(units.Tolerance);

                var faceView = new FaceView
                {
                    FaceId = FaceId(mass.ElementId, i),
                    MassId = mass.ElementId,
                    FaceIndex = i,
                    Area = areaProperties == null ? 0 : Math.Abs(areaProperties.Area),
                    Centroid = SemanticClassifier.ToVec(centroid),
                    Normal = SemanticClassifier.ToVec(normal),
                    IsPlanar = isPlanar,
                    ElevationMin = box.IsValid ? box.Min.Z : 0,
                    ElevationMax = box.IsValid ? box.Max.Z : 0,
                    SurfaceType = SurfaceTypeOf(face, units)
                };

                var facts = new FaceFacts
                {
                    Normal = faceView.Normal,
                    IsPlanar = isPlanar,
                    ElevationMin = faceView.ElevationMin,
                    ElevationMax = faceView.ElevationMax,
                    MassBaseElevation = massBase,
                    BaseTolerance = units.AdjacencyTolerance,
                    BoundsInteriorVoid = interiorFaceIndices.Contains(i),
                    CoincidentWithAnotherMass = IsPartyWall(faceView, neighbours, units)
                };

                faceView.Orientation = FaceClassifier.Orientation(faceView.Normal, isPlanar);

                // An explicit RhinoClaude:FaceRole tag on the parent mass is the trump card —
                // the escape hatch for the curved-facade case (plan risk #3).
                if (overrides.TryGetValue(i, out string overridden))
                {
                    faceView.Roles.Add(overridden);
                    faceView.ClassifiedBy = SemanticVocabulary.ByUserData;
                }
                else
                {
                    faceView.Roles.AddRange(FaceClassifier.Roles(facts, out string note));
                    faceView.ClassifiedBy = SemanticVocabulary.ByGeometryInference;
                    if (!string.IsNullOrEmpty(note)) faceView.Notes.Add(note);
                }

                if (!isPlanar && faceView.Orientation == SemanticVocabulary.OrientationOther)
                {
                    faceView.Notes.Add(
                        "Curved face — it wraps more than one compass direction, so it has no single " +
                        "orientation. Tag " + SemanticVocabulary.FaceRoleKey(i) + " on the mass to label it by hand.");
                }

                DetectOpeningsInFace(face, faceView, units);
                view.Faces.Add(faceView);
            }
        }

        /// <summary>
        /// The face's outward normal. A Brep face's surface normal points out of the *surface*,
        /// not out of the solid — <c>OrientationIsReversed</c> is the difference, and getting it
        /// wrong flips every facade into a floor.
        /// </summary>
        private static Vector3d OutwardNormal(BrepFace face, Point3d nearPoint)
        {
            double u, v;
            if (!face.ClosestPoint(nearPoint, out u, out v))
            {
                var domainU = face.Domain(0);
                var domainV = face.Domain(1);
                u = domainU.Mid;
                v = domainV.Mid;
            }

            var normal = face.NormalAt(u, v);
            if (face.OrientationIsReversed) normal.Reverse();
            return normal;
        }

        private static AreaMassProperties SafeArea(BrepFace face)
        {
            try { return AreaMassProperties.Compute(face); }
            catch (Exception) { return null; }
        }

        private static string SurfaceTypeOf(BrepFace face, UnitContext units)
        {
            if (face.IsPlanar(units.Tolerance)) return "planar";

            try
            {
                var surface = face.UnderlyingSurface();
                if (surface != null && surface.TryGetCylinder(out _)) return "cylindrical";
                if (surface != null && surface.TryGetSphere(out _)) return "spherical";
            }
            catch (Exception)
            {
                // Fall through to the generic answer.
            }

            return "nurbs";
        }

        /// <summary>
        /// A face is a party wall when it sits against another Mass. Tested by pushing a point
        /// just past the face along its outward normal and asking whether that lands inside a
        /// neighbouring mass's extents — cheap, and right at the scale SD massing works at.
        /// </summary>
        private static bool IsPartyWall(FaceView face, List<MassView> neighbours, UnitContext units)
        {
            if (neighbours.Count == 0) return false;

            var probe = face.Centroid + face.Normal.Unit() * units.AdjacencyTolerance * 2;
            return neighbours.Any(n => n.Bbox.Contains(probe, units.AdjacencyTolerance));
        }

        /// <summary>Explicit face-role overrides written on the Mass as RhinoClaude:FaceRole:&lt;index&gt;.</summary>
        private static Dictionary<int, string> ReadFaceRoleOverrides(RhinoObject obj)
        {
            var overrides = new Dictionary<int, string>();
            if (obj == null) return overrides;

            System.Collections.Specialized.NameValueCollection strings;
            try
            {
                strings = obj.Attributes.GetUserStrings();
            }
            catch (Exception)
            {
                return overrides;
            }

            if (strings == null) return overrides;

            foreach (string key in strings.AllKeys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (!key.StartsWith(SemanticVocabulary.KeyFaceRolePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                string tail = key.Substring(SemanticVocabulary.KeyFaceRolePrefix.Length);
                if (!int.TryParse(tail, out int index)) continue;

                string role = SemanticVocabulary.Normalize(strings[key], SemanticVocabulary.FaceRoles);
                if (role != null) overrides[index] = role;
            }

            return overrides;
        }

        // ── Openings from inner trim loops (plan §5.5) ────────────────

        private static void DetectOpeningsInFace(BrepFace face, FaceView faceView, UnitContext units)
        {
            var faceBox = face.GetBoundingBox(true);

            // The face's own horizontal axis, so "distance from the left edge" means something
            // on a face that is not aligned with world X or Y.
            var normal = faceView.Normal.Unit();
            var horizontal = Vec3.Cross(new Vec3(0, 0, 1), normal);
            if (horizontal.Length <= 1e-9) horizontal = new Vec3(1, 0, 0);
            horizontal = horizontal.Unit();

            int loopIndex = 0;
            foreach (var loop in face.Loops)
            {
                if (loop.LoopType != BrepLoopType.Inner) continue;
                loopIndex++;

                Curve curve;
                try
                {
                    curve = loop.To3dCurve();
                }
                catch (Exception)
                {
                    continue;
                }
                if (curve == null) continue;

                double area = 0;
                try
                {
                    var properties = AreaMassProperties.Compute(curve);
                    if (properties != null) area = Math.Abs(properties.Area);
                }
                catch (Exception)
                {
                    area = 0;
                }

                var box = curve.GetBoundingBox(true);
                if (!box.IsValid) continue;

                double width = Math.Sqrt(
                    Math.Pow(box.Max.X - box.Min.X, 2) + Math.Pow(box.Max.Y - box.Min.Y, 2));
                double height = box.Max.Z - box.Min.Z;

                if (area <= 0) area = width * height;
                if (!OpeningClassifier.IsSignificant(area, units)) continue;

                var facts = new OpeningFacts
                {
                    Width = width,
                    Height = height,
                    SillHeight = box.Min.Z - (faceBox.IsValid ? faceBox.Min.Z : 0),
                    Area = area
                };

                string type = OpeningClassifier.InferType(facts, units, out string note);

                var opening = new OpeningView
                {
                    ElementId = faceView.FaceId + ":opening:" + loopIndex,
                    MassId = faceView.MassId,
                    FaceId = faceView.FaceId,
                    FaceIndex = faceView.FaceIndex,
                    OpeningType = type,
                    Width = width,
                    Height = height,
                    SillHeight = facts.SillHeight,
                    Area = area,
                    Centroid = SemanticClassifier.ToVec(box.Center),
                    Origin = "subtracted",
                    ClassifiedBy = SemanticVocabulary.ByGeometryInference,
                    Name = type + " opening"
                };

                var offset = opening.Centroid - faceView.Centroid;
                opening.CentroidOnFace = new[]
                {
                    Vec3.Round(Vec3.Dot(offset, horizontal)),
                    Vec3.Round(box.Center.Z - (faceBox.IsValid ? faceBox.Min.Z : 0))
                };

                if (!string.IsNullOrEmpty(note)) opening.Notes.Add(note);
                faceView.Openings.Add(opening);
            }
        }

        // ── Edges ─────────────────────────────────────────────────────

        private void BuildEdges(Brep brep, MassGeometryView view)
        {
            for (int i = 0; i < brep.Edges.Count; i++)
            {
                var edge = brep.Edges[i];

                int[] adjacent;
                try
                {
                    adjacent = edge.AdjacentFaces() ?? new int[0];
                }
                catch (Exception)
                {
                    adjacent = new int[0];
                }

                var edgeView = new EdgeView
                {
                    EdgeId = EdgeId(view.MassId, i),
                    MassId = view.MassId,
                    EdgeIndex = i,
                    Length = SafeLength(edge),
                    StartPoint = SemanticClassifier.ToVec(edge.PointAtStart),
                    EndPoint = SemanticClassifier.ToVec(edge.PointAtEnd),
                    IsLinear = edge.IsLinear(RhinoMath.SqrtEpsilon)
                };
                edgeView.AdjacentFaceIndices.AddRange(adjacent);

                var a = adjacent.Length > 0 ? view.FaceAt(adjacent[0]) : null;
                var b = adjacent.Length > 1 ? view.FaceAt(adjacent[1]) : null;

                var facts = new EdgeFacts
                {
                    AdjacentFaceCount = adjacent.Length,
                    Midpoint = new Vec3(
                        (edgeView.StartPoint.X + edgeView.EndPoint.X) / 2,
                        (edgeView.StartPoint.Y + edgeView.EndPoint.Y) / 2,
                        (edgeView.StartPoint.Z + edgeView.EndPoint.Z) / 2),
                    Length = edgeView.Length,
                    IsLinear = edgeView.IsLinear
                };

                if (a != null)
                {
                    facts.FaceARoles = a.Roles;
                    facts.FaceANormal = a.Normal;
                    facts.FaceACentroid = a.Centroid;
                    facts.FaceAOrientation = a.Orientation;
                }
                if (b != null)
                {
                    facts.FaceBRoles = b.Roles;
                    facts.FaceBNormal = b.Normal;
                    facts.FaceBCentroid = b.Centroid;
                    facts.FaceBOrientation = b.Orientation;
                }

                edgeView.Role = EdgeClassifier.Role(facts);
                view.Edges.Add(edgeView);

                if (a != null) a.BoundingEdgeIndices.Add(i);
                if (b != null) b.BoundingEdgeIndices.Add(i);
            }
        }

        private static double SafeLength(BrepEdge edge)
        {
            try { return edge.GetLength(); }
            catch (Exception) { return edge.PointAtStart.DistanceTo(edge.PointAtEnd); }
        }

        // ── Cuts from inner shells (plan §5.5) ────────────────────────

        /// <summary>
        /// A closed void inside a solid is a second shell of the same Brep: a set of faces that
        /// shares no edge with the outer skin. Grouping faces into edge-connected components
        /// finds those shells directly, and any component whose extents sit inside the largest
        /// component's is a void. Each becomes a Cut, and its faces take the interior role.
        ///
        /// Face adjacency rather than a shell API, deliberately: it works on the original face
        /// indices — no centroid matching back from a copied Brep — and it compiles identically
        /// against the Rhino 7 and Rhino 8 reference assemblies.
        ///
        /// A *through* cut (a light well open top and bottom) is welded to the outer skin, so
        /// it is one component and this path does not see it. That case is what boolean history
        /// is for, per plan §5.5's two Cut paths.
        /// </summary>
        private static HashSet<int> FindInnerShellFaces(
            Brep brep, UnitContext units, MassGeometryView view, MassView mass)
        {
            var interiorFaceIndices = new HashSet<int>();
            if (!brep.IsSolid || brep.Faces.Count < 2) return interiorFaceIndices;

            var components = ConnectedFaceComponents(brep);
            if (components.Count < 2) return interiorFaceIndices;

            var boxes = components.Select(c => c.Aggregate(
                BoxView.Unset,
                (acc, index) => acc.Union(SemanticClassifier.ToBox(brep.Faces[index].GetBoundingBox(true))))).ToList();

            int outerIndex = 0;
            for (int i = 1; i < boxes.Count; i++)
                if (CompositionAnalyzer.Volume(boxes[i]) > CompositionAnalyzer.Volume(boxes[outerIndex]))
                    outerIndex = i;

            var outerBox = boxes[outerIndex];
            int cutIndex = 0;

            for (int i = 0; i < components.Count; i++)
            {
                if (i == outerIndex) continue;

                var shellBox = boxes[i];
                if (!shellBox.IsValid) continue;
                if (!outerBox.Contains(shellBox.Min, units.AdjacencyTolerance)
                    || !outerBox.Contains(shellBox.Max, units.AdjacencyTolerance))
                    continue;   // a second solid alongside, not a void inside

                double volume = ShellVolume(brep, components[i], shellBox);

                cutIndex++;
                var cut = new CutView
                {
                    ElementId = view.MassId + ":cut:" + cutIndex,
                    MassId = view.MassId,
                    Volume = volume,
                    Bbox = shellBox,
                    Centroid = shellBox.Center,
                    TopOpen = Math.Abs(shellBox.Max.Z - outerBox.Max.Z) <= units.AdjacencyTolerance,
                    BottomOpen = Math.Abs(shellBox.Min.Z - outerBox.Min.Z) <= units.AdjacencyTolerance,
                    ClassifiedBy = SemanticVocabulary.ByGeometryInference,
                    Name = "Cut in " + (mass?.Name ?? "mass")
                };

                if (volume < units.MinCutVolume)
                {
                    cut.Notes.Add("Below the " + Math.Round(units.VolumeToCubicFeet(units.MinCutVolume)) +
                                  " ft³ cut threshold — reported as a Cut because it is a genuine closed " +
                                  "void, but it reads more like a recess or a services chase.");
                }

                foreach (int faceIndex in components[i])
                {
                    interiorFaceIndices.Add(faceIndex);
                    cut.InteriorFaceIds.Add(FaceId(view.MassId, faceIndex));
                }

                view.Cuts.Add(cut);
            }

            return interiorFaceIndices;
        }

        /// <summary>Face indices grouped into edge-connected components, via union-find.</summary>
        private static List<List<int>> ConnectedFaceComponents(Brep brep)
        {
            int count = brep.Faces.Count;
            var parent = Enumerable.Range(0, count).ToArray();

            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }

            void Union(int a, int b)
            {
                int rootA = Find(a), rootB = Find(b);
                if (rootA != rootB) parent[rootB] = rootA;
            }

            foreach (var edge in brep.Edges)
            {
                int[] adjacent;
                try { adjacent = edge.AdjacentFaces(); }
                catch (Exception) { continue; }
                if (adjacent == null || adjacent.Length < 2) continue;

                for (int i = 1; i < adjacent.Length; i++)
                    if (adjacent[0] >= 0 && adjacent[0] < count && adjacent[i] >= 0 && adjacent[i] < count)
                        Union(adjacent[0], adjacent[i]);
            }

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < count; i++)
            {
                int root = Find(i);
                if (!groups.TryGetValue(root, out var members))
                {
                    members = new List<int>();
                    groups[root] = members;
                }
                members.Add(i);
            }

            return groups.Values.ToList();
        }

        /// <summary>
        /// Volume enclosed by one shell. Uses the real Brep when RhinoCommon can rebuild the
        /// sub-Brep, and the shell's bounding box otherwise — a void's box is close enough for
        /// the "is this a Cut or a chase" call, and the Cut carries its box either way.
        /// </summary>
        private static double ShellVolume(Brep brep, List<int> faceIndices, BoxView fallback)
        {
            try
            {
                var sub = brep.DuplicateSubBrep(faceIndices);
                if (sub != null)
                {
                    var properties = VolumeMassProperties.Compute(sub);
                    if (properties != null && Math.Abs(properties.Volume) > 0)
                        return Math.Abs(properties.Volume);
                }
            }
            catch (Exception)
            {
                // Fall through to the bounding box.
            }

            return CompositionAnalyzer.Volume(fallback);
        }

        // ── Recesses (plan §3.6) ──────────────────────────────────────

        /// <summary>
        /// A recess is a facade face set back from the mass's outer envelope on the side it
        /// faces, with a larger face on the same side standing further out. Loggia, recessed
        /// entry, covered porch — the inward complement of an overhang.
        ///
        /// Geometry inference, so every result carries a note. Boolean history would be a more
        /// certain signal; it is usually not there.
        /// </summary>
        private static void DetectRecesses(MassView mass, UnitContext units, MassGeometryView view)
        {
            if (mass?.Bbox == null || !mass.Bbox.IsValid) return;

            var facades = view.Faces
                .Where(f => f.HasRole(SemanticVocabulary.RoleFacade) && f.IsPlanar && f.Area > units.Area(10))
                .ToList();
            if (facades.Count < 2) return;

            int index = 0;
            foreach (var byOrientation in facades.GroupBy(f => f.Orientation))
            {
                if (!SemanticVocabulary.IsCompassSector(byOrientation.Key)) continue;

                var group = byOrientation.ToList();
                if (group.Count < 2) continue;

                // How far out each face stands, measured along its own normal.
                var outermost = group.OrderByDescending(f => Vec3.Dot(f.Centroid, f.Normal.Unit())).First();
                double frontage = Vec3.Dot(outermost.Centroid, outermost.Normal.Unit());

                foreach (var face in group)
                {
                    if (ReferenceEquals(face, outermost)) continue;

                    double setback = frontage - Vec3.Dot(face.Centroid, face.Normal.Unit());
                    if (setback < units.MinOverhangProjection) continue;
                    if (face.Area > outermost.Area) continue;      // the plane behind is the main wall

                    index++;
                    var recess = new RecessView
                    {
                        ElementId = view.MassId + ":recess:" + index,
                        MassId = view.MassId,
                        DepthIntoMass = setback,
                        OpeningFaceId = face.FaceId,
                        Area = face.Area,
                        Volume = face.Area * setback,
                        ClassifiedBy = SemanticVocabulary.ByGeometryInference,
                        Name = byOrientation.Key + "-facing recess"
                    };
                    recess.Notes.Add(
                        "Inferred: this " + byOrientation.Key + "-facing face sits " +
                        Math.Round(units.ToFeet(setback), 1) + " ft behind the outermost face on the same " +
                        "side. Read it as a loggia or recessed entry, not a certainty.");
                    view.Recesses.Add(recess);
                }
            }
        }

        // ── Cantilever overhangs (plan §3.5 heuristic 4) ──────────────

        /// <summary>
        /// A down-facing face well above the mass base is the underside of something projecting —
        /// a cantilevered plate, a canopy cut from the same solid, a deep eave.
        /// </summary>
        private static void DetectCantileverOverhangs(MassView mass, UnitContext units, MassGeometryView view)
        {
            if (mass?.Bbox == null || !mass.Bbox.IsValid) return;

            double massBase = mass.Bbox.Min.Z;
            int index = 0;

            foreach (var face in view.Faces)
            {
                if (face.Orientation != SemanticVocabulary.OrientationDown) continue;
                if (face.HasRole(SemanticVocabulary.RoleFloor)) continue;
                if (face.HasRole(SemanticVocabulary.RoleInterior)) continue;
                if (face.ElevationMax - massBase <= units.AdjacencyTolerance) continue;
                if (face.Area < units.Area(20)) continue;

                index++;
                var overhang = new OverhangView
                {
                    ElementId = view.MassId + ":overhang:" + index,
                    AttachedToMassId = view.MassId,
                    AttachedToFaceId = face.FaceId,
                    Area = face.Area,
                    Centroid = face.Centroid,
                    Origin = SemanticVocabulary.OverhangFromFaceCantilever,
                    Subtype = "Other",
                    ClassifiedBy = SemanticVocabulary.ByGeometryInference,
                    Name = "Cantilever soffit at " + Math.Round(units.ToFeet(face.ElevationMax - massBase), 1) + " ft",
                    ProjectionDistance = Math.Sqrt(face.Area),
                    Width = Math.Sqrt(face.Area)
                };
                overhang.Notes.Add(
                    "Inferred from a down-facing face above the mass base; the projection distance is " +
                    "estimated from its area, not measured against the wall below.");
                view.Overhangs.Add(overhang);
            }
        }

        // ── Loose objects that belong to this mass ────────────────────

        /// <summary>
        /// Openings and overhangs the architect drew as separate objects get attached to the
        /// face they sit against. Plan risk #11: when a drawn opening and a boolean-cut hole
        /// describe the same aperture, keep the hole and warn — the geometry is the more
        /// reliable statement.
        /// </summary>
        private static void AttachLooseElements(
            MassView mass, SemanticView context, UnitContext units, MassGeometryView view)
        {
            if (context == null) return;

            foreach (var loose in context.LooseOpenings)
            {
                var face = NearestFace(view, loose.Centroid, units);
                if (face == null) continue;

                var duplicate = face.Openings.FirstOrDefault(
                    o => o.Centroid.DistanceTo(loose.Centroid) <= units.Length(2.0));

                if (duplicate != null)
                {
                    duplicate.Notes.Add(
                        "An object on " + (loose.Layer ?? "an opening layer") + " describes the same " +
                        "aperture. The hole in the mass face was kept; the drawn object is redundant.");
                    continue;
                }

                loose.MassId = mass.ElementId;
                loose.FaceId = face.FaceId;
                loose.FaceIndex = face.FaceIndex;
                if (loose.SillHeight <= 0) loose.SillHeight = loose.Centroid.Z - face.ElevationMin - loose.Height / 2;
                face.Openings.Add(loose);
            }

            foreach (var loose in context.LooseOverhangs)
            {
                var face = NearestFace(view, loose.Centroid, units);
                if (face == null) continue;
                if (!string.IsNullOrEmpty(loose.AttachedToMassId)) continue;

                loose.AttachedToMassId = mass.ElementId;
                loose.AttachedToFaceId = face.FaceId;
                view.Overhangs.Add(loose);
            }
        }

        private static FaceView NearestFace(MassGeometryView view, Vec3 point, UnitContext units)
        {
            FaceView best = null;
            double bestDistance = double.MaxValue;

            foreach (var face in view.Faces)
            {
                double distance = Math.Abs(Vec3.Dot(point - face.Centroid, face.Normal.Unit()));
                double lateral = (point - face.Centroid).Length;
                if (distance > units.Length(3.0)) continue;
                if (lateral > Math.Sqrt(Math.Max(face.Area, 1)) * 1.5) continue;
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = face;
            }

            return best;
        }

        // ── Ids ───────────────────────────────────────────────────────

        /// <summary>
        /// Face ids are positional, not persistent: the Brep's face indexing changes whenever
        /// the solid is edited. Encoding the mass id and index makes that visible rather than
        /// pretending a face survives a boolean.
        /// </summary>
        public static string FaceId(string massId, int faceIndex) => massId + ":face:" + faceIndex;

        public static string EdgeId(string massId, int edgeIndex) => massId + ":edge:" + edgeIndex;

        /// <summary>Pull the owning mass id back out of a face or edge id.</summary>
        public static string MassIdOf(string compositeId)
        {
            if (string.IsNullOrWhiteSpace(compositeId)) return null;
            int marker = compositeId.IndexOf(":face:", StringComparison.Ordinal);
            if (marker < 0) marker = compositeId.IndexOf(":edge:", StringComparison.Ordinal);
            if (marker < 0) marker = compositeId.IndexOf(":opening:", StringComparison.Ordinal);
            return marker < 0 ? null : compositeId.Substring(0, marker);
        }
    }
}
