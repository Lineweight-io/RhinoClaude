using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Geometry;
using RhinoClaude.Semantic;

namespace RhinoClaude.Services.Semantic
{
    /// <summary>
    /// The solid-preserving massing moves: subdivide a face, translate a face or an edge, and
    /// the gable composite built out of the two.
    ///
    /// These exist because the alternative is what the agent reached for without them — loose
    /// surfaces laid over the top of a solid. A gable is not a pair of planes resting on a box;
    /// it is the box's top face split along the ridge with the new edge lifted, and the result
    /// is still one closed manifold that every downstream query can measure.
    ///
    /// Same contract as the rest of the semantic writes: one undo record through
    /// <see cref="RhinoClaude.Services.Agent.RhinoMutationService.RunComposite{T}"/>, the
    /// registry invalidated afterwards because face and edge indices do not survive a topology
    /// change, and the document left untouched when the operation cannot be completed — every
    /// failure throws before <c>doc.Objects.Replace</c> is reached. UI-thread only.
    /// </summary>
    public sealed partial class SemanticMutationService
    {
        // ══ subdivide_face ════════════════════════════════════════════

        /// <summary>
        /// Divide one face of a solid in two without opening the solid. The returned edge ids
        /// are the point of the tool: they are what <c>move_edge</c> takes to turn a subdivided
        /// roof into a gable, a dormer or a setback.
        /// </summary>
        public object SubdivideFace(string massId, FaceSelector selector, FaceCut cut)
        {
            var context = ResolveFace(massId, selector);
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(context.SelectorNote)) notes.Add(context.SelectorNote);

            var plan = FaceCutPlanner.Plan(
                context.Face, cut, Doc.ModelAbsoluteTolerance, Units.Length(1.0));

            if (!plan.Resolved) throw new ArgumentException(plan.Error);
            if (!string.IsNullOrWhiteSpace(plan.Note)) notes.Add(plan.Note);

            var result = _mutation.RunComposite("subdivide_face", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;
                var brep = RequireBrep(doc, context.ObjectId);
                RequireFaceIndex(brep, context.Face.FaceIndex);

                var facePlane = PlaneOf(context.Face);
                var before = EdgeSegments(brep);
                var cutters = BuildCutters(doc, brep, context.Face, plan, tolerance);

                var edited = SplitFaceInPlace(brep, context.Face.FaceIndex, cutters, tolerance,
                                              out int fragmentCount, notes);

                var after = EdgeSegments(edited);
                var diff = EdgeTopologyDiff.Compare(before, after, Math.Max(tolerance, 1e-6));

                var newEdgeIds = diff.NewIndices
                    .Select(i => MassGeometryAnalyzer.EdgeId(context.Mass.ElementId, i))
                    .ToList();

                var newFaceIds = CoplanarFaceIndices(edited, facePlane, tolerance)
                    .Select(i => MassGeometryAnalyzer.FaceId(context.Mass.ElementId, i))
                    .ToList();

                if (diff.FragmentIndices.Count > 0)
                    notes.Add("The cut also split " + diff.FragmentIndices.Count + " existing edge(s) into " +
                              "pieces where it crossed them. Those are renumbered, not new — moving one " +
                              "would tear the face rather than reshape it, so they are not listed below.");

                if (!edited.IsSolid)
                    notes.Add("The result did not close back into a solid. Check it with capture_views " +
                              "before building on it.");

                if (!doc.Objects.Replace(context.ObjectId, edited))
                    throw new InvalidOperationException("Rhino refused to replace the mass with the subdivided Brep.");

                return (object)new Dictionary<string, object>
                {
                    { "resultMassId", context.Mass.ElementId },
                    { "faceId", context.Face.FaceId },
                    { "newEdgeIds", newEdgeIds },
                    { "newFaceIds", newFaceIds },
                    { "piecesFromFace", fragmentCount },
                    { "topologyChanged", true },
                    { "isSolid", edited.IsSolid },
                    { "allFacesPlanar", NonPlanarFaceCount(edited, tolerance) == 0 },
                    { "newBbox", SemanticClassifier.ToBox(edited.GetBoundingBox(true)).ToJson() },
                    { "notes", Join(notes,
                        newEdgeIds.Count == 0
                            ? "The split ran but produced no new edge, which should not happen — re-read " +
                              "get_mass_edges before moving anything."
                            : "Pass one of newEdgeIds straight to move_edge to lift or drop it; that is how " +
                              "a ridge, a dormer or a setback gets made without leaving the solid.") }
                };
            });

            Invalidate();
            return result;
        }

        // ══ move_face ═════════════════════════════════════════════════

        /// <summary>
        /// Translate a whole face and let the faces around it follow — Rhino's MoveFace, on a
        /// face picked by role rather than by an index the agent would have to guess.
        /// </summary>
        public object MoveFace(
            string massId, FaceSelector selector, string directionName, double[] directionVector, double distance)
        {
            var context = ResolveFace(massId, selector);
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(context.SelectorNote)) notes.Add(context.SelectorNote);

            if (Math.Abs(distance) <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("distance must be non-zero.");

            var direction = ResolveDirection(
                directionName, directionVector, context.Face.Normal.Unit(), notes);

            var result = _mutation.RunComposite("move_face", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;
                var brep = RequireBrep(doc, context.ObjectId);
                RequireFaceIndex(brep, context.Face.FaceIndex);

                if (!brep.Faces[context.Face.FaceIndex].IsPlanar(tolerance))
                    notes.Add("This face is not planar, so the translation may distort it. Check the result " +
                              "before building on it.");

                var edited = TranslateComponent(
                    brep, brep.Faces[context.Face.FaceIndex].ComponentIndex(),
                    direction * distance, tolerance, "face", notes);

                if (!doc.Objects.Replace(context.ObjectId, edited))
                    throw new InvalidOperationException("Rhino refused to replace the mass with the edited Brep.");

                return (object)new Dictionary<string, object>
                {
                    { "resultMassId", context.Mass.ElementId },
                    { "faceId", context.Face.FaceId },
                    { "direction", new[] { Round(direction.X), Round(direction.Y), Round(direction.Z) } },
                    { "distance", Round(distance) },
                    { "newVolume", Round(Measure(edited)) },
                    { "newBbox", SemanticClassifier.ToBox(edited.GetBoundingBox(true)).ToJson() },
                    { "isSolid", edited.IsSolid },
                    { "notes", Join(notes, "Face and edge indices on this mass have changed — call " +
                                           "get_mass_faces again before the next face operation.") }
                };
            });

            Invalidate();
            return result;
        }

        // ══ move_edge ═════════════════════════════════════════════════

        /// <summary>
        /// Translate one edge, letting the faces that meet it stretch to follow. The other half
        /// of the gable move: subdivide the roof, then lift the edge the subdivision made.
        /// </summary>
        public object MoveEdge(
            string massId, IReadOnlyList<EdgeSelector> selectors, string directionName,
            double[] directionVector, double distance)
        {
            var mass = RequireMass(massId);
            var edges = ResolveEdges(mass, selectors);
            var notes = new List<string>();

            if (Math.Abs(distance) <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("distance must be non-zero.");

            if (MoveDirection.IsFaceRelative(directionName))
                throw new ArgumentException(
                    "'" + directionName + "' is a face direction and an edge has no single normal. Give a " +
                    "named axis (" + MoveDirection.Names + ") or a vector.");

            var direction = ResolveDirection(directionName, directionVector, Vec3.Zero, notes);
            if (!Guid.TryParse(mass.PrimaryObjectId, out var objectId))
                throw new InvalidOperationException("That mass has no Rhino object behind it.");

            var result = _mutation.RunComposite("move_edge", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;
                var brep = RequireBrep(doc, objectId);

                foreach (var edge in edges)
                    if (edge.EdgeIndex < 0 || edge.EdgeIndex >= brep.Edges.Count)
                        throw new ArgumentException(
                            "Edge index " + edge.EdgeIndex + " is no longer valid on this Brep — it has " +
                            brep.Edges.Count + " edges. Call get_mass_edges again; indices change whenever " +
                            "the solid is edited.");

                bool wasSolid = brep.IsSolid;
                int warpedBefore = NonPlanarFaceCount(brep, tolerance);

                var components = edges.Select(e => brep.Edges[e.EdgeIndex].ComponentIndex()).ToList();

                var edited = TranslateComponents(
                    brep, components, direction * distance, tolerance,
                    edges.Count == 1 ? "edge" : "edges", notes);

                // Damage is rejected, not reported. Replacing the mass and then telling the caller
                // to undo leaves it holding geometry it has to repair before it can retry, and the
                // repair is the part that goes wrong. Nothing is written unless the move is clean.
                if (wasSolid && !edited.IsSolid)
                    throw new InvalidOperationException(
                        "That move would open the solid — the mass was closed before it and would not be " +
                        "after. Nothing was changed. Try a smaller distance, or a different edge.");

                // A face that warped is the signature of a partly-moved ridge: the edges that
                // needed to travel together did not, so the faces spanning them had to bend.
                int warped = NonPlanarFaceCount(edited, tolerance) - warpedBefore;
                if (warped > 0)
                    throw new InvalidOperationException(
                        warped + " face(s) would stop being planar, so nothing was changed. On a massing " +
                        "model that means the edges around them have to move together and did not — an " +
                        "L-shaped ridge is two edges, and both belong in one move_edge call. Reissue this " +
                        "move with every edge of the feature in edgeSelectors; the indices you already " +
                        "have are still valid, because the mass was left untouched.");

                if (!doc.Objects.Replace(objectId, edited))
                    throw new InvalidOperationException("Rhino refused to replace the mass with the edited Brep.");

                return (object)new Dictionary<string, object>
                {
                    { "resultMassId", mass.ElementId },
                    { "edgeIds", edges.Select(e => e.EdgeId).ToList() },
                    { "edgeRoles", edges.Select(e => e.Role).Distinct().ToList() },
                    { "direction", new[] { Round(direction.X), Round(direction.Y), Round(direction.Z) } },
                    { "distance", Round(distance) },
                    { "movedEdges", edges.Select(e => DescribeEdge(edited, e.EdgeIndex)).ToList() },
                    { "newVolume", Round(Measure(edited)) },
                    { "newBbox", SemanticClassifier.ToBox(edited.GetBoundingBox(true)).ToJson() },
                    { "isSolid", edited.IsSolid },
                    { "allFacesPlanar", NonPlanarFaceCount(edited, tolerance) == 0 },
                    { "notes", Join(notes, "Face and edge indices on this mass have changed — call " +
                                           "get_mass_edges again before the next edge operation.") }
                };
            });

            Invalidate();
            return result;
        }

        // ══ create_gable_roof ═════════════════════════════════════════

        /// <summary>
        /// The standard gable, in one undo record: split the top face along the ridge line, then
        /// lift the edge that split created. Three tool calls' worth of composition for the case
        /// that comes up most, so the agent does not have to get all three right in a row.
        /// </summary>
        public object CreateGableRoof(
            string massId, List<double[]> ridgePoints, double pitchHeight, FaceSelector selector,
            List<double[][]> additionalCuts)
        {
            var context = ResolveTopFace(massId, selector);
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(context.SelectorNote)) notes.Add(context.SelectorNote);

            if (Math.Abs(pitchHeight) <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("pitchHeight must be non-zero — it is how far the ridge rises " +
                                            "above the face it was cut from.");

            if (ridgePoints == null || ridgePoints.Count < 2 || ridgePoints.Any(p => p == null || p.Length < 2))
                throw new ArgumentException(
                    "A ridge of at least two points is required — ridgeLineStart and ridgeLineEnd for a " +
                    "straight ridge, or ridgePoints for a bent one. Each is [x, y] or [x, y, z] in plan; " +
                    "the z is ignored, because the ridge is projected onto the top face.");

            var cut = new FaceCut { PolylinePoints = ridgePoints, Lines = additionalCuts };
            var plan = FaceCutPlanner.Plan(context.Face, cut, Doc.ModelAbsoluteTolerance, Units.Length(1.0));

            if (!plan.Resolved)
                throw new ArgumentException(
                    "The ridge will not work on face " + context.Face.FaceId + ". " + plan.Error);

            // The ridge as given, unextended, so the edges lying on it can be told apart from the
            // hip and valley edges the same split creates.
            // Qualified: the service already has a FaceFrame(FaceView) member of its own.
            var frame = RhinoClaude.Semantic.FaceFrame.For(context.Face);
            var ridgeLines = new List<EdgeSegment>();
            for (int i = 0; i + 1 < ridgePoints.Count; i++)
                ridgeLines.Add(new EdgeSegment(
                    frame.Project(ToVec3(ridgePoints[i], frame.Origin.Z)),
                    frame.Project(ToVec3(ridgePoints[i + 1], frame.Origin.Z))));

            var result = _mutation.RunComposite("create_gable_roof", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;
                var brep = RequireBrep(doc, context.ObjectId);
                RequireFaceIndex(brep, context.Face.FaceIndex);

                bool wasSolid = brep.IsSolid;
                int warpedBefore = NonPlanarFaceCount(brep, tolerance);
                double ridgeElevation = context.Face.ElevationMax + pitchHeight;

                var before = EdgeSegments(brep);
                var cutters = BuildCutters(doc, brep, context.Face, plan, tolerance);

                var split = SplitFaceInPlace(brep, context.Face.FaceIndex, cutters, tolerance,
                                             out int fragmentCount, notes);

                if (fragmentCount < 2)
                    throw new InvalidOperationException(
                        "The ridge did not divide the top face. The cuts have to divide it between them — a " +
                        "single ridge has to run right across, and a bent ridge on an L needs the hip and " +
                        "valley cuts passed as additionalCuts to close the division off.");

                var after = EdgeSegments(split);
                var diff = EdgeTopologyDiff.Compare(before, after, Math.Max(tolerance, 1e-6));

                var ridgeIndices = EdgeTopologyDiff.OnAnyLine(
                    after, diff.NewIndices, ridgeLines, Math.Max(tolerance * 10, 1e-5));

                if (ridgeIndices.Count == 0)
                    throw new InvalidOperationException(
                        "The split produced no new edge lying on the ridge, so there is nothing to raise. " +
                        "Nothing was changed. Try subdivide_face on its own to see what the cut did.");

                if (ridgeIndices.Count < ridgePoints.Count - 1)
                    notes.Add("The ridge was given as " + (ridgePoints.Count - 1) + " segments but only " +
                              ridgeIndices.Count + " matching edge(s) came back from the split. Check the " +
                              "result before building on it.");

                int otherNew = diff.NewIndices.Count - ridgeIndices.Count;
                if (otherNew > 0)
                    notes.Add("The cut also created " + otherNew + " edge(s) away from the ridge — the hip " +
                              "and valley lines. They stay at eave level, which is what makes the roof " +
                              "planes come out flat.");

                // Every ridge segment moves in one transform. Lifting them one at a time is what
                // warps an L-shaped roof instead of raising it.
                var edited = TranslateComponents(
                    split, ridgeIndices.Select(i => split.Edges[i].ComponentIndex()).ToList(),
                    new Vector3d(0, 0, 1) * pitchHeight, tolerance, "ridge edges", notes);

                // Same contract as move_edge: a gable that damages the mass is refused outright
                // rather than written and flagged, so a failed attempt costs the caller nothing
                // and it can retry against the mass it already knows.
                if (wasSolid && !edited.IsSolid)
                    throw new InvalidOperationException(
                        "The gable would open the solid — the mass was closed before it and would not be " +
                        "after. Nothing was changed. Try a smaller pitchHeight.");

                int warpedNow = NonPlanarFaceCount(edited, tolerance);
                if (warpedNow - warpedBefore > 0)
                    throw new InvalidOperationException(
                        (warpedNow - warpedBefore) + " roof face(s) would come out non-planar, so nothing " +
                        "was changed. An L or T plan needs a cut from the ridge turning point out to the " +
                        "outside corner and in to the inside corner; without them the roof planes have to " +
                        "bend. Pass them as additionalCuts and call create_gable_roof again.");

                if (!doc.Objects.Replace(context.ObjectId, edited))
                    throw new InvalidOperationException("Rhino refused to replace the mass with the gabled Brep.");

                var slopeFaceIds = ridgeIndices
                    .Where(i => i < edited.Edges.Count)
                    .SelectMany(i => SafeAdjacentFaces(edited.Edges[i]))
                    .Distinct()
                    .Select(i => MassGeometryAnalyzer.FaceId(context.Mass.ElementId, i))
                    .ToList();

                return (object)new Dictionary<string, object>
                {
                    { "resultMassId", context.Mass.ElementId },
                    { "roofFaceId", context.Face.FaceId },
                    { "ridgeEdgeIds", ridgeIndices
                        .Select(i => MassGeometryAnalyzer.EdgeId(context.Mass.ElementId, i)).ToList() },
                    { "ridgeLines", ridgeIndices.Select(i => DescribeEdge(edited, i)).ToList() },
                    { "ridgeElevation", Round(ridgeElevation) },
                    { "pitchHeight", Round(pitchHeight) },
                    { "slopeFaceIds", slopeFaceIds },
                    { "newVolume", Round(Measure(edited)) },
                    { "newBbox", SemanticClassifier.ToBox(edited.GetBoundingBox(true)).ToJson() },
                    { "isSolid", edited.IsSolid },
                    { "allFacesPlanar", warpedNow == 0 },
                    { "notes", Join(notes, "One closed solid, not a roof laid on a box: describe_massing " +
                                           "still reads it as a single mass, and the ridge comes back from " +
                                           "get_mass_edges with role 'roof-ridge'.") }
                };
            });

            Invalidate();
            return result;
        }

        private static Vec3 ToVec3(double[] values, double fallbackZ) =>
            new Vec3(values[0], values[1], values.Length >= 3 ? values[2] : fallbackZ);

        /// <summary>
        /// The raw shape of move_face and move_edge: a Brep id and a component index, on any
        /// object in the document rather than a classified Mass.
        ///
        /// The semantic tools take over both names, so this is what keeps the phase 1 path
        /// alive — 'move face 3 of this polysurface' is still a reasonable request, and it must
        /// not stop working because the semantic layer is switched on.
        /// </summary>
        public object MoveSubObjectRaw(
            bool isFace, string brepId, int index, string directionName, double[] directionVector, double distance)
        {
            var direction = ResolveDirection(directionName, directionVector, Vec3.Zero, new List<string>());
            var result = _mutation.MoveSubObject(isFace, brepId, index, direction, distance);
            Invalidate();
            return result;
        }

        // ══ Shared ════════════════════════════════════════════════════

        /// <summary>
        /// The gable's face selection: the roof, however it happens to be labelled. An explicit
        /// selector wins; otherwise role 'roof' is tried, then the up-facing face, because a
        /// mass that has been edited already may have neither label where you expect it.
        /// </summary>
        private FaceContext ResolveTopFace(string massId, FaceSelector selector)
        {
            if (selector != null && !selector.IsEmpty) return ResolveFace(massId, selector);

            var mass = RequireMass(massId);
            var geometry = _registry.GeometryFor(mass);
            if (geometry == null)
                throw new InvalidOperationException("Could not analyse the geometry of mass " + mass.ElementId + ".");

            var byRole = FaceSelectorResolver.Resolve(
                geometry, new FaceSelector { Role = SemanticVocabulary.RoleRoof });

            var resolution = byRole.Resolved
                ? byRole
                : FaceSelectorResolver.Resolve(
                    geometry, new FaceSelector { Orientation = SemanticVocabulary.OrientationUp });

            if (!resolution.Resolved)
                throw new ArgumentException(
                    "This mass has no roof face to gable. " + resolution.Error +
                    " Pass an explicit faceSelector if the top face is labelled differently.");

            if (!Guid.TryParse(mass.PrimaryObjectId, out var objectId))
                throw new InvalidOperationException("That mass has no Rhino object behind it.");

            return new FaceContext
            {
                Mass = mass,
                Geometry = geometry,
                Face = resolution.Face,
                ObjectId = objectId,
                SelectorNote = byRole.Resolved
                    ? resolution.Note
                    : Join(new[] { resolution.Note },
                           "No face is classified as a roof, so the up-facing face was used.")
            };
        }

        /// <summary>
        /// The edges a move applies to. Several selectors resolve to several edges and they all
        /// move together; a single selector matching several edges is the ambiguous case, and
        /// there the longest still wins so "move the ridge" keeps working on a simple mass.
        /// </summary>
        private List<EdgeView> ResolveEdges(MassView mass, IReadOnlyList<EdgeSelector> selectors)
        {
            var geometry = _registry.GeometryFor(mass);
            if (geometry == null)
                throw new InvalidOperationException("Could not analyse the geometry of mass " + mass.ElementId + ".");

            var given = (selectors ?? new EdgeSelector[0]).Where(s => s != null && !s.IsEmpty).ToList();
            if (given.Count == 0)
                throw new ArgumentException(
                    "An edgeSelector is required. Give one of: {edgeId} — including one returned by " +
                    "subdivide_face — {edgeIndex}, or {role}. Pass edgeSelectors as a list to move several " +
                    "edges together, which is what an L-shaped or bent ridge needs.");

            var matches = EdgeSelector.ResolveAll(geometry, given);
            if (matches.Count == 0)
            {
                var roles = geometry.Edges.Select(e => e.Role).Distinct().OrderBy(r => r, StringComparer.Ordinal);
                throw new ArgumentException(
                    "No edge on mass " + mass.ElementId + " matches " +
                    string.Join(" or ", given.Select(s => s.ToString())) + ". This mass has " +
                    geometry.Edges.Count + " edges with roles [" + string.Join(", ", roles) + "]. Call " +
                    "get_mass_edges for the current ids — they change whenever the solid is edited.");
            }

            // One selector that caught a crowd is a guess, not an instruction: narrow it to the
            // longest. Several selectors are an explicit set and are taken as given.
            if (given.Count == 1 && matches.Count > 1)
                return new List<EdgeView> { matches.OrderByDescending(e => e.Length).First() };

            return matches;
        }

        private static Vector3d ResolveDirection(
            string directionName, double[] directionVector, Vec3 faceNormal, List<string> notes)
        {
            if (!string.IsNullOrWhiteSpace(directionName))
            {
                if (MoveDirection.IsFaceRelative(directionName))
                {
                    if (faceNormal.Length < 0.5)
                        throw new ArgumentException(
                            "'" + directionName + "' needs a face normal and this face has none.");

                    var normal = SemanticClassifier.ToVector(faceNormal);
                    if (MoveDirection.IsInward(directionName)) normal.Reverse();

                    notes.Add("Moving along the face normal is what push_pull_face does; either tool is fine " +
                              "here.");
                    return normal;
                }

                if (MoveDirection.TryParseNamed(directionName, out var named))
                    return SemanticClassifier.ToVector(named);

                throw new ArgumentException(
                    "'" + directionName + "' is not a direction I know. Use one of: " + MoveDirection.Names +
                    " — or pass directionVector as [x, y, z].");
            }

            if (directionVector != null && directionVector.Length >= 3)
            {
                var vector = new Vector3d(directionVector[0], directionVector[1], directionVector[2]);
                if (vector.Length <= RhinoMath.ZeroTolerance)
                    throw new ArgumentException("directionVector must be a non-zero vector.");
                vector.Unitize();
                return vector;
            }

            throw new ArgumentException(
                "A direction is required: either direction as a named axis (" + MoveDirection.Names +
                ") or directionVector as [x, y, z].");
        }

        /// <summary>
        /// Split one face of a solid and leave the solid closed. Rhino has no single call for
        /// this: <c>BrepFace.Split</c> works on a face in isolation, so the face is lifted out,
        /// split, and joined back into the shell it came from.
        /// </summary>
        private static Brep SplitFaceInPlace(
            Brep brep, int faceIndex, List<Curve> cutters, double tolerance,
            out int fragmentCount, List<string> notes)
        {
            fragmentCount = 0;

            var faceBrep = brep.Faces[faceIndex].DuplicateFace(false);
            if (faceBrep == null || faceBrep.Faces.Count == 0)
                throw new InvalidOperationException("Could not lift that face off the solid to split it.");

            var fragments = faceBrep.Faces[0].Split(cutters, tolerance);
            if (fragments == null || fragments.Faces.Count == 0)
                throw new InvalidOperationException(
                    "Rhino could not split that face with the cut given. A curve that does not lie on the " +
                    "face is the usual cause — check the cutting curve is on the face's plane.");

            fragmentCount = fragments.Faces.Count;
            if (fragmentCount < 2)
                throw new InvalidOperationException(
                    "The cut did not divide the face — it came back in one piece, from " + cutters.Count +
                    " cutting curve(s). The rule is about the set, not each curve: together they have to " +
                    "separate the face into pieces. One line has to cross it from edge to edge. Several cuts " +
                    "have to meet each other end to end and reach the boundary at both ends of the chain — " +
                    "an L-shaped ridge divides nothing until the hip and valley cuts join it to the corners.");

            var shell = brep.DuplicateBrep();
            shell.Faces.RemoveAt(faceIndex);
            shell.Compact();

            var joined = Brep.JoinBreps(new[] { shell, fragments }, tolerance);
            if (joined == null || joined.Length == 0)
                throw new InvalidOperationException(
                    "The split face would not join back onto the solid. Nothing was changed.");

            if (joined.Length > 1)
                notes.Add("The rejoin produced " + joined.Length + " pieces; the one with the most faces was " +
                          "kept. Check the result before building on it.");

            var result = joined.OrderByDescending(b => b.Faces.Count).First();

            if (!result.IsValid)
            {
                result.Repair(tolerance);
                if (!result.IsValid)
                    notes.Add("The subdivided Brep is not valid; the geometry may be self-intersecting.");
            }

            return result;
        }

        /// <summary>
        /// The curves the split runs on, on the face's plane whichever way they were given.
        /// All of them go into one <c>BrepFace.Split</c> call, which is what lets a set of cuts
        /// divide a face that no single member of the set crosses.
        /// </summary>
        private List<Curve> BuildCutters(
            RhinoDoc doc, Brep brep, FaceView face, CutLinePlan plan, double tolerance)
        {
            if (!plan.UsesCurve)
                return plan.Segments
                    .Select(s => (Curve)new LineCurve(
                        SemanticClassifier.ToPoint(s.Start), SemanticClassifier.ToPoint(s.End)))
                    .ToList();

            var cutters = new List<Curve>();
            var plane = PlaneOf(face);

            foreach (string curveId in plan.CuttingCurveIds)
            {
                var obj = _query.RequireObject(curveId);
                var curve = obj.Geometry as Curve;
                if (curve == null)
                    throw new ArgumentException(
                        "Object " + curveId + " is a " + obj.ObjectType + ", not a curve.");

                var projected = Curve.ProjectToPlane(curve, plane);
                if (projected != null)
                {
                    cutters.Add(projected);
                    continue;
                }

                var pulled = curve.PullToBrepFace(brep.Faces[face.FaceIndex], tolerance);
                if (pulled != null && pulled.Length > 0)
                {
                    cutters.AddRange(pulled);
                    continue;
                }

                throw new ArgumentException(
                    "Curve " + curveId + " could not be projected onto face " + face.FaceId +
                    ". Draw it over the face in plan, or use {line: {startPoint, endPoint}} instead.");
            }

            return cutters;
        }

        private static Brep TranslateComponent(
            Brep brep, ComponentIndex component, Vector3d translation, double tolerance,
            string what, List<string> notes) =>
            TranslateComponents(brep, new[] { component }, translation, tolerance, what, notes);

        /// <summary>
        /// Translate Brep components, letting the neighbours follow. The same
        /// <c>TransformComponent</c> call Rhino's own MoveFace and MoveEdge make.
        ///
        /// Moving several components in one call is not a convenience — it is the difference
        /// between a gable and a torn roof. An L-shaped ridge is two edges meeting at a corner,
        /// and each of the four faces around them touches both. Lift one edge and the faces that
        /// span the pair have to warp to reach the half that stayed down; lift both in the same
        /// transform and every face stays planar. Rhino's own MoveEdge works on a selection for
        /// exactly this reason.
        /// </summary>
        private static Brep TranslateComponents(
            Brep brep, IReadOnlyList<ComponentIndex> components, Vector3d translation, double tolerance,
            string what, List<string> notes)
        {
            var edited = brep.DuplicateBrep();

            bool moved = edited.TransformComponent(
                components, Transform.Translation(translation), tolerance, 0.0, false);

            if (!moved)
                throw new InvalidOperationException(
                    "Rhino could not move that " + what + ". The adjacent faces may not be able to follow — " +
                    "try a smaller distance, or check the " + what + " is still where get_mass_" +
                    (what == "face" ? "faces" : "edges") + " said it was.");

            if (!edited.IsValid)
            {
                edited.Repair(tolerance);
                if (!edited.IsValid)
                    notes.Add("The resulting Brep is not valid; the geometry may be self-intersecting. Check " +
                              "it with capture_views before building on it.");
            }

            return edited;
        }

        /// <summary>
        /// Faces that stopped being planar as a result of an edit. A warped face on a massing
        /// model is nearly always a mistake — a roof plane that had to twist because only half
        /// of a ridge moved — and the caller cannot see it in the volume or the bounding box.
        /// </summary>
        private static int NonPlanarFaceCount(Brep brep, double tolerance)
        {
            int count = 0;
            for (int i = 0; i < brep.Faces.Count; i++)
                if (!brep.Faces[i].IsPlanar(tolerance)) count++;
            return count;
        }

        private static void RequireFaceIndex(Brep brep, int faceIndex)
        {
            if (faceIndex < 0 || faceIndex >= brep.Faces.Count)
                throw new ArgumentException(
                    "Face index " + faceIndex + " is no longer valid on this Brep, which has " +
                    brep.Faces.Count + " faces. Call get_mass_faces again — indices change whenever the " +
                    "solid is edited.");
        }

        /// <summary>Every edge reduced to its endpoints, for the before/after topology diff.</summary>
        private static List<EdgeSegment> EdgeSegments(Brep brep)
        {
            var segments = new List<EdgeSegment>(brep.Edges.Count);
            foreach (var edge in brep.Edges)
                segments.Add(new EdgeSegment(
                    SemanticClassifier.ToVec(edge.PointAtStart),
                    SemanticClassifier.ToVec(edge.PointAtEnd)));
            return segments;
        }

        private static Plane PlaneOf(FaceView face) => new Plane(
            SemanticClassifier.ToPoint(face.Centroid),
            SemanticClassifier.ToVector(face.Normal.Unit()));

        /// <summary>
        /// Faces still lying on the plane the split was made on — the pieces the subdivision
        /// produced. Read after the split, before anything is moved off that plane.
        /// </summary>
        private static List<int> CoplanarFaceIndices(Brep brep, Plane plane, double tolerance)
        {
            var indices = new List<int>();
            double slack = Math.Max(tolerance * 10, 1e-6);

            for (int i = 0; i < brep.Faces.Count; i++)
            {
                var face = brep.Faces[i];
                if (!face.IsPlanar(tolerance)) continue;

                Plane facePlane;
                if (!face.TryGetPlane(out facePlane, tolerance)) continue;

                if (Math.Abs(Math.Abs(facePlane.Normal * plane.Normal) - 1) > 1e-4) continue;
                if (Math.Abs(plane.DistanceTo(facePlane.Origin)) > slack) continue;

                indices.Add(i);
            }

            return indices;
        }

        /// <summary>Where an edge ended up, or null when the edit renumbered it away.</summary>
        private static object DescribeEdge(Brep brep, int edgeIndex)
        {
            if (edgeIndex < 0 || edgeIndex >= brep.Edges.Count) return null;

            var edge = brep.Edges[edgeIndex];
            return new Dictionary<string, object>
            {
                { "start", SemanticClassifier.ToVec(edge.PointAtStart).ToArray() },
                { "end", SemanticClassifier.ToVec(edge.PointAtEnd).ToArray() },
                { "length", Round(edge.PointAtStart.DistanceTo(edge.PointAtEnd)) }
            };
        }

        private static IEnumerable<int> SafeAdjacentFaces(BrepEdge edge)
        {
            try
            {
                return edge.AdjacentFaces() ?? new int[0];
            }
            catch (Exception)
            {
                return new int[0];
            }
        }
    }
}
