using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using RhinoClaude.Agent;
using RhinoClaude.Semantic;
using RhinoClaude.Services.Agent;

namespace RhinoClaude.Services.Semantic
{
    /// <summary>
    /// The massing operations — semantic plan §4.6. These are the verbs an architect uses on a
    /// solid model: push and pull a face, add a mass, subtract one, cut an opening, slice at an
    /// elevation, extrude a face outward, fillet corners.
    ///
    /// Each tool call is one undo record, opened through phase 1's
    /// <see cref="RhinoMutationService.RunComposite{T}"/>, so a semantic move undoes as one
    /// action and lands in the session mutation log exactly like a raw one. There is no second
    /// mutation pipeline.
    ///
    /// After every write the element registry is invalidated: face indices do not survive a
    /// boolean, and handing the agent a stale face id is how a second edit lands in the wrong
    /// place. UI-thread only.
    /// </summary>
    public sealed class SemanticMutationService
    {
        private readonly RhinoQueryService _query;
        private readonly RhinoMutationService _mutation;
        private readonly ElementRegistry _registry;

        public SemanticMutationService(
            RhinoQueryService query, RhinoMutationService mutation, ElementRegistry registry)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _mutation = mutation ?? throw new ArgumentNullException(nameof(mutation));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        private RhinoDoc Doc => _query.Doc;
        private UnitContext Units => _registry.Units;

        // ══ push_pull_face ════════════════════════════════════════════

        /// <summary>
        /// The fundamental massing operation: move a face along its own normal. Positive
        /// extends the mass outward, negative pushes it in — and a negative push into a facade
        /// is how a recess gets made (plan §11 example 15).
        /// </summary>
        public object PushPullFace(string massId, FaceSelector selector, double distance, string propagate)
        {
            var context = ResolveFace(massId, selector);
            var notes = new List<string>();

            if (Math.Abs(distance) <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("distance must be non-zero.");

            if (!context.Face.IsPlanar)
            {
                // Plan risk #5: redirect rather than fail.
                throw new ArgumentException(
                    "Face " + context.Face.FaceId + " is not planar, so it cannot be pushed along a single " +
                    "normal. Use run_rhinocommon_script with TransformControlPoints for a curved face, or " +
                    "pick a planar face with get_mass_faces.");
            }

            if (!string.Equals(propagate, "none", StringComparison.OrdinalIgnoreCase))
                propagate = "auto";

            if (propagate == "none")
            {
                notes.Add("propagate 'none' is not implemented as a detached face translation; the move ran " +
                          "as 'auto' (connected faces followed). For a detached face plane use " +
                          "run_rhinocommon_script.");
            }

            double before = context.Mass.Volume;
            var direction = SemanticClassifier.ToVector(context.Face.Normal.Unit());

            var result = _mutation.RunComposite("push_pull_face", doc =>
            {
                var brep = RequireBrep(doc, context.ObjectId);
                double tolerance = doc.ModelAbsoluteTolerance;

                var edited = brep.DuplicateBrep();
                if (context.Face.FaceIndex < 0 || context.Face.FaceIndex >= edited.Faces.Count)
                    throw new ArgumentException(
                        "Face index " + context.Face.FaceIndex + " is no longer valid on this Brep. " +
                        "Call get_mass_faces again — indices change whenever the solid is edited.");

                var component = edited.Faces[context.Face.FaceIndex].ComponentIndex();
                bool moved = edited.TransformComponent(
                    new[] { component }, Transform.Translation(direction * distance), tolerance, 0.0, false);

                if (!moved)
                    throw new InvalidOperationException(
                        "Rhino could not move that face. The adjacent faces may not be able to follow — " +
                        "try a smaller distance, or use extrude_face_outward if you meant to add a bump-out " +
                        "rather than move the existing plane.");

                if (!edited.IsValid)
                {
                    edited.Repair(tolerance);
                    if (!edited.IsValid)
                        notes.Add("The resulting Brep is not valid; the geometry may be self-intersecting. " +
                                  "Check it with capture_views before building on it.");
                }

                if (!doc.Objects.Replace(context.ObjectId, edited))
                    throw new InvalidOperationException("Rhino refused to replace the mass with the edited Brep.");

                double after = Measure(edited);
                var box = SemanticClassifier.ToBox(edited.GetBoundingBox(true));

                return (object)new Dictionary<string, object>
                {
                    { "resultMassId", context.Mass.ElementId },
                    { "faceId", context.Face.FaceId },
                    { "distance", Round(distance) },
                    { "propagate", propagate },
                    { "deltaVolume", Round(after - before) },
                    { "newVolume", Round(after) },
                    { "newBbox", box.ToJson() },
                    { "isSolid", edited.IsSolid },
                    { "affectedFaceIds", new List<string> { context.Face.FaceId } },
                    { "notes", Join(notes, distance < 0
                        ? "Negative distance pushed the face inward; if it created a niche rather than moving " +
                          "the whole plane, describe_massing will now report it as a Recess."
                        : null) }
                };
            });

            Invalidate();
            return result;
        }

        // ══ add_mass ══════════════════════════════════════════════════

        public object AddMass(
            string shape, double[] location, double[] dimensions, string footprintCurveId,
            double? height, string function, string name, List<string> unionWithExisting)
        {
            function = SemanticVocabulary.Normalize(function, SemanticVocabulary.MassFunctions,
                                                    SemanticVocabulary.FunctionOther);
            var notes = new List<string>();

            var result = _mutation.RunComposite("add_mass", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;
                var brep = BuildMassBrep(doc, shape, location, dimensions, footprintCurveId, height, notes);

                if (brep == null || !brep.IsValid)
                    throw new InvalidOperationException("Rhino could not build that solid from the inputs given.");

                string layer = CanonicalConvention.LayerForFunction(function);
                int layerIndex = EnsureLayerIndex(doc, layer);

                var attributes = doc.CreateDefaultAttributes();
                attributes.LayerIndex = layerIndex;
                attributes.Name = string.IsNullOrWhiteSpace(name) ? function + " mass" : name;
                attributes.SetUserString(SemanticVocabulary.KeyElementPrefix + SemanticVocabulary.Mass, "1");
                attributes.SetUserString(SemanticVocabulary.KeyMassFunction, function);

                var unionTargets = new List<RhinoObject>();
                foreach (var id in unionWithExisting ?? new List<string>())
                {
                    var mass = _registry.FindMass(id);
                    if (mass == null)
                        throw new ArgumentException("No Mass '" + id + "' to union with. Call list_masses first.");
                    var obj = doc.Objects.FindId(Guid.Parse(mass.PrimaryObjectId));
                    if (obj != null) unionTargets.Add(obj);
                }

                Guid resultId;
                if (unionTargets.Count == 0)
                {
                    resultId = doc.Objects.AddBrep(brep, attributes);
                    if (resultId == Guid.Empty)
                        throw new InvalidOperationException("Rhino refused to add the mass to the document.");
                }
                else
                {
                    // Plan §10.2 question 7: creating a mass and unioning it into an existing
                    // one is a single operation with a single undo record, not two tools.
                    var inputs = new List<Brep> { brep };
                    inputs.AddRange(unionTargets.Select(o => SemanticClassifier.AsBrep(o.Geometry))
                                                .Where(b => b != null));

                    var unioned = Brep.CreateBooleanUnion(inputs, tolerance);
                    if (unioned == null || unioned.Length == 0)
                    {
                        notes.Add("The boolean union produced nothing — the new mass most likely does not " +
                                  "touch the masses named in unionWithExisting. The new mass was added on " +
                                  "its own and the existing masses were left alone.");
                        resultId = doc.Objects.AddBrep(brep, attributes);
                    }
                    else
                    {
                        if (unioned.Length > 1)
                            notes.Add("The union produced " + unioned.Length + " separate pieces; the largest " +
                                      "is reported as the result.");

                        var largest = unioned.OrderByDescending(Measure).First();
                        resultId = doc.Objects.AddBrep(largest, attributes);
                        foreach (var obj in unionTargets) doc.Objects.Delete(obj.Id, true);
                    }
                }

                var added = doc.Objects.FindId(resultId);
                var finalBrep = SemanticClassifier.AsBrep(added?.Geometry) ?? brep;
                var box = SemanticClassifier.ToBox(finalBrep.GetBoundingBox(true));

                return (object)new Dictionary<string, object>
                {
                    { "massId", resultId.ToString() },
                    { "rhinoObjectIds", new List<string> { resultId.ToString() } },
                    { "function", function },
                    { "layer", layer },
                    { "bbox", box.ToJson() },
                    { "volume", Round(Measure(finalBrep)) },
                    { "footprintArea", Round(box.FootprintArea) },
                    { "isSolid", finalBrep.IsSolid },
                    { "notes", Join(notes, "Tagged as a Mass and placed on " + layer +
                                           ", so describe_massing will pick it up immediately.") }
                };
            });

            Invalidate();
            return result;
        }

        private Brep BuildMassBrep(
            RhinoDoc doc, string shape, double[] location, double[] dimensions,
            string footprintCurveId, double? height, List<string> notes)
        {
            double tolerance = doc.ModelAbsoluteTolerance;
            var origin = location == null || location.Length < 3
                ? Point3d.Origin
                : new Point3d(location[0], location[1], location[2]);

            switch ((shape ?? "box").ToLowerInvariant())
            {
                case "box":
                    {
                        if (dimensions == null || dimensions.Length < 3)
                            throw new ArgumentException("A box needs dimensions as [width, depth, height].");

                        var box = new BoundingBox(
                            origin,
                            new Point3d(origin.X + dimensions[0], origin.Y + dimensions[1], origin.Z + dimensions[2]));
                        if (!box.IsValid || box.Diagonal.Length < RhinoMath.ZeroTolerance)
                            throw new ArgumentException("Those dimensions give a box with zero size.");

                        return Brep.CreateFromBox(box);
                    }

                case "cylinder":
                    {
                        if (dimensions == null || dimensions.Length < 2)
                            throw new ArgumentException("A cylinder needs dimensions as [radius, height].");

                        var circle = new Circle(new Plane(origin, Vector3d.ZAxis), dimensions[0]);
                        var cylinder = new Cylinder(circle, dimensions[1]);
                        return cylinder.ToBrep(true, true);
                    }

                case "prism-from-curve":
                    {
                        if (string.IsNullOrWhiteSpace(footprintCurveId))
                            throw new ArgumentException("prism-from-curve needs a footprintCurveId.");
                        if (height == null || Math.Abs(height.Value) <= RhinoMath.ZeroTolerance)
                            throw new ArgumentException("prism-from-curve needs a non-zero height.");

                        var obj = _query.RequireObject(footprintCurveId);
                        var curve = obj.Geometry as Curve;
                        if (curve == null)
                            throw new ArgumentException("Object " + footprintCurveId + " is a " + obj.ObjectType +
                                                        ", not a curve.");
                        if (!curve.IsClosed)
                            throw new ArgumentException("The footprint curve must be closed for the prism to " +
                                                        "be a solid.");

                        var surface = Surface.CreateExtrusion(curve, Vector3d.ZAxis * height.Value);
                        var extruded = surface?.ToBrep();
                        if (extruded == null)
                            throw new InvalidOperationException("Rhino could not extrude that footprint curve.");

                        var capped = extruded.CapPlanarHoles(tolerance);
                        if (capped == null)
                        {
                            notes.Add("Rhino could not cap the extrusion, so the result is an open surface " +
                                      "rather than a solid. Semantic queries on it will be approximate.");
                            return extruded;
                        }
                        return capped;
                    }

                default:
                    throw new ArgumentException("Unknown shape '" + shape +
                                                "'. Use 'box', 'cylinder' or 'prism-from-curve'.");
            }
        }

        // ══ subtract_mass ═════════════════════════════════════════════

        public object SubtractMass(string baseMassId, string cutterMassId, bool deleteCutter)
        {
            var baseMass = RequireMass(baseMassId);
            var cutterMass = _registry.FindMass(cutterMassId);

            // The cutter is often a throwaway box the agent just made, which may not have
            // classified as a Mass at all. Fall back to the raw object.
            var cutterObjectId = cutterMass?.PrimaryObjectId ?? cutterMassId;
            var notes = new List<string>();

            var result = _mutation.RunComposite("subtract_mass", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;

                var baseObject = _query.RequireObject(baseMass.PrimaryObjectId);
                var cutterObject = _query.RequireObject(cutterObjectId);

                var baseBrep = SemanticClassifier.AsBrep(baseObject.Geometry);
                var cutterBrep = SemanticClassifier.AsBrep(cutterObject.Geometry);

                if (baseBrep == null || cutterBrep == null)
                    throw new ArgumentException("Both the base and the cutter must be solids.");

                double volumeBefore = Measure(baseBrep);
                double cutterVolume = Measure(cutterBrep);

                var results = Brep.CreateBooleanDifference(
                    new[] { baseBrep }, new[] { cutterBrep }, tolerance);

                if (results == null || results.Length == 0)
                    throw new InvalidOperationException(
                        "The boolean difference produced nothing — the cutter may not intersect the base " +
                        "mass, or one of them is not closed. Nothing was changed.");

                if (results.Length > 1)
                    notes.Add("The subtraction split the base mass into " + results.Length + " pieces; all of " +
                              "them were kept.");

                var attributes = baseObject.Attributes.Duplicate();
                var resultIds = new List<string>();

                var largest = results.OrderByDescending(Measure).First();
                if (!doc.Objects.Replace(baseObject.Id, largest))
                    throw new InvalidOperationException("Rhino refused to replace the base mass.");
                resultIds.Add(baseObject.Id.ToString());

                foreach (var piece in results.Where(r => !ReferenceEquals(r, largest)))
                {
                    var id = doc.Objects.AddBrep(piece, attributes);
                    if (id != Guid.Empty) resultIds.Add(id.ToString());
                }

                if (deleteCutter) doc.Objects.Delete(cutterObject.Id, true);

                double volumeAfter = results.Sum(Measure);
                double subtracted = volumeBefore - volumeAfter;

                // Plan §3.7: a big subtraction reads as a Cut; a small one is an Opening or a
                // Recess, which the geometry analyzer will classify on the next query.
                bool readsAsCut = subtracted >= Units.MinCutVolume;

                return (object)new Dictionary<string, object>
                {
                    { "resultMassId", baseObject.Id.ToString() },
                    { "resultIds", resultIds },
                    { "subtractedVolume", Round(subtracted) },
                    { "cutterVolume", Round(cutterVolume) },
                    { "classifiesAs", readsAsCut ? SemanticVocabulary.Cut : SemanticVocabulary.Opening },
                    { "deletedCutter", deleteCutter },
                    { "notes", Join(notes,
                        readsAsCut
                            ? "The subtracted volume is above the cut threshold, so this reads as a Cut " +
                              "(light well, atrium, courtyard). Confirm with describe_massing."
                            : "The subtracted volume is below the cut threshold, so it will classify as an " +
                              "Opening or a Recess depending on whether it pierced the mass.") }
                };
            });

            Invalidate();
            return result;
        }

        // ══ cut_opening ═══════════════════════════════════════════════

        /// <summary>
        /// The convenience wrapper: build a cutter sized and placed on a face, boolean it out,
        /// and let the classifier pick the hole up as an Opening on the next query. This is
        /// "add a window" expressed as what it actually is — a rectangular hole cut in a face.
        /// </summary>
        public object CutOpening(
            string massId, FaceSelector selector, string openingType,
            double width, double height, double sillHeight,
            double? distanceFromLeftEdge, double[] centroidOnFace, double? depth)
        {
            var context = ResolveFace(massId, selector);
            var notes = new List<string>();

            openingType = SemanticVocabulary.Normalize(openingType, SemanticVocabulary.OpeningTypes,
                                                       SemanticVocabulary.OpeningWindow);

            if (width <= 0 || height <= 0)
                throw new ArgumentException("width and height must both be positive.");

            if (!context.Face.IsPlanar)
                throw new ArgumentException(
                    "Face " + context.Face.FaceId + " is curved, so a rectangular opening cannot be placed on " +
                    "it by width and height. Model the cutter yourself and use subtract_mass.");

            if (!context.Face.HasRole(SemanticVocabulary.RoleFacade))
                notes.Add("This face is not classified as a facade (roles: " +
                          string.Join(", ", context.Face.Roles) + "). The opening was cut anyway.");

            var result = _mutation.RunComposite("cut_opening", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;
                var brep = RequireBrep(doc, context.ObjectId);
                var frame = FaceFrame(context.Face);

                double faceWidth = frame.Width;
                double faceHeight = frame.Height;

                if (width > faceWidth + tolerance || height > faceHeight + tolerance)
                    throw new ArgumentException(
                        "The opening (" + Round(width) + " × " + Round(height) + ") does not fit on this face (" +
                        Round(faceWidth) + " × " + Round(faceHeight) + ").");

                double u = distanceFromLeftEdge
                           ?? (centroidOnFace != null && centroidOnFace.Length >= 1
                               ? centroidOnFace[0] - width / 2
                               : (faceWidth - width) / 2);

                double v = centroidOnFace != null && centroidOnFace.Length >= 2 && distanceFromLeftEdge == null
                    ? centroidOnFace[1] - height / 2
                    : sillHeight;

                if (u < -tolerance || u + width > faceWidth + tolerance)
                {
                    double clamped = Math.Max(0, Math.Min(u, faceWidth - width));
                    notes.Add("The requested horizontal position put the opening off the edge of the face; " +
                              "it was moved to " + Round(clamped) + " from the left edge.");
                    u = clamped;
                }

                if (v < -tolerance || v + height > faceHeight + tolerance)
                {
                    double clamped = Math.Max(0, Math.Min(v, faceHeight - height));
                    notes.Add("The requested sill height put the opening off the face; it was moved to " +
                              Round(clamped) + " above the bottom of the face.");
                    v = clamped;
                }

                // A cutter that only just reaches the far side leaves slivers; overshoot both
                // ways so the boolean is clean.
                double cutDepth = depth ?? MassDepth(context, brep);
                double overshoot = Math.Max(tolerance * 100, Units.Length(0.5));

                var cutterOrigin = frame.Origin
                                   + frame.Horizontal * u
                                   + frame.Vertical * v
                                   - frame.Normal * overshoot;

                var cutterPlane = new Plane(cutterOrigin, frame.Horizontal, frame.Vertical);
                var cutterBox = new Box(cutterPlane,
                    new Interval(0, width),
                    new Interval(0, height),
                    new Interval(0, cutDepth + overshoot * 2));

                var cutter = cutterBox.ToBrep();
                if (cutter == null)
                    throw new InvalidOperationException("Could not build the cutter solid for that opening.");

                var results = Brep.CreateBooleanDifference(new[] { brep }, new[] { cutter }, tolerance);
                if (results == null || results.Length == 0)
                    throw new InvalidOperationException(
                        "The opening cut produced nothing. The cutter may not reach through the mass — pass " +
                        "an explicit depth, or check the face is on the outside of the solid.");

                var largest = results.OrderByDescending(Measure).First();
                if (!doc.Objects.Replace(context.ObjectId, largest))
                    throw new InvalidOperationException("Rhino refused to replace the mass with the cut Brep.");

                // The opening type cannot be read back off a hole, so record it on the mass,
                // keyed by where the hole sits. The analyzer reads these back.
                var openingKey = OpeningTagKey(frame.Origin + frame.Horizontal * (u + width / 2)
                                               + frame.Vertical * (v + height / 2));
                SetMassUserString(doc, context.ObjectId, openingKey, openingType);

                return (object)new Dictionary<string, object>
                {
                    { "massId", context.Mass.ElementId },
                    { "faceId", context.Face.FaceId },
                    { "openingType", openingType },
                    { "width", Round(width) },
                    { "height", Round(height) },
                    { "actualPosition", new Dictionary<string, object>
                        {
                            { "distanceFromLeftEdge", Round(u) },
                            { "sillHeight", Round(v) }
                        } },
                    { "openingArea", Round(width * height) },
                    { "depth", Round(cutDepth) },
                    { "notes", Join(notes,
                        "Face indices on this mass have changed — call get_mass_faces again before the next " +
                        "face operation. Call find_openings_in_face for the updated wall-window ratio.") }
                };
            });

            Invalidate();
            return result;
        }

        // ══ slice_mass_at_elevation ═══════════════════════════════════

        public object SliceMassAtElevation(string massId, double elevation, string mode)
        {
            var mass = RequireMass(massId);
            var notes = new List<string>();
            bool split = string.Equals(mode, "split-mass", StringComparison.OrdinalIgnoreCase);

            var result = _mutation.RunComposite("slice_mass_at_elevation", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;
                var obj = _query.RequireObject(mass.PrimaryObjectId);
                var brep = SemanticClassifier.AsBrep(obj.Geometry);
                if (brep == null) throw new ArgumentException("That mass has no Brep geometry to slice.");

                var box = brep.GetBoundingBox(true);
                if (elevation <= box.Min.Z + tolerance || elevation >= box.Max.Z - tolerance)
                    throw new ArgumentException(
                        "Elevation " + Round(elevation) + " is outside the mass, which spans " +
                        Round(box.Min.Z) + " to " + Round(box.Max.Z) + ".");

                var plane = new Plane(new Point3d(0, 0, elevation), Vector3d.ZAxis);

                if (!split)
                {
                    Curve[] sections;
                    Point3d[] points;
                    if (!Intersection.BrepPlane(brep, plane, tolerance, out sections, out points)
                        || sections == null || sections.Length == 0)
                        throw new InvalidOperationException(
                            "Rhino found no section at that elevation. The mass may not be closed there.");

                    var plates = Brep.CreatePlanarBreps(sections, tolerance);
                    if (plates == null || plates.Length == 0)
                    {
                        notes.Add("The section curves would not close into a planar plate; the section curves " +
                                  "were added instead.");
                    }

                    int layerIndex = EnsureLayerIndex(doc, "FLOOR_" + LevelLabel(elevation, box.Min.Z));
                    var attributes = doc.CreateDefaultAttributes();
                    attributes.LayerIndex = layerIndex;
                    attributes.Name = "Floor plate at " + Round(elevation);

                    var ids = new List<string>();
                    double area = 0;

                    if (plates != null && plates.Length > 0)
                    {
                        foreach (var plate in plates)
                        {
                            var id = doc.Objects.AddBrep(plate, attributes);
                            if (id != Guid.Empty) ids.Add(id.ToString());

                            var properties = AreaMassProperties.Compute(plate);
                            if (properties != null) area += Math.Abs(properties.Area);
                        }
                    }
                    else
                    {
                        foreach (var section in sections)
                        {
                            var id = doc.Objects.AddCurve(section, attributes);
                            if (id != Guid.Empty) ids.Add(id.ToString());
                        }
                    }

                    return (object)new Dictionary<string, object>
                    {
                        { "mode", "generate-floorplate" },
                        { "massId", mass.ElementId },
                        { "elevation", Round(elevation) },
                        { "floorPlateIds", ids },
                        { "netArea", Round(area) },
                        { "notes", Join(notes, "The mass itself was not changed — the plate is a new object " +
                                               "on a FLOOR_* layer, which the classifier keeps out of the " +
                                               "mass count.") }
                    };
                }

                var pieces = brep.Trim(plane, tolerance);
                var flipped = brep.Trim(new Plane(plane.Origin, -Vector3d.ZAxis), tolerance);

                var all = new List<Brep>();
                if (pieces != null) all.AddRange(pieces);
                if (flipped != null) all.AddRange(flipped);

                if (all.Count < 2)
                    throw new InvalidOperationException(
                        "The split produced fewer than two pieces. Try generate-floorplate mode, or split " +
                        "with the raw tools.");

                var capped = all.Select(p => p.CapPlanarHoles(tolerance) ?? p).ToList();
                var attributesSplit = obj.Attributes.Duplicate();

                var resultIds = new List<string>();
                var largestPiece = capped.OrderByDescending(Measure).First();
                if (!doc.Objects.Replace(obj.Id, largestPiece))
                    throw new InvalidOperationException("Rhino refused to replace the mass with the lower piece.");
                resultIds.Add(obj.Id.ToString());

                foreach (var piece in capped.Where(p => !ReferenceEquals(p, largestPiece)))
                {
                    var id = doc.Objects.AddBrep(piece, attributesSplit);
                    if (id != Guid.Empty) resultIds.Add(id.ToString());
                }

                if (capped.Any(p => !p.IsSolid))
                    notes.Add("At least one piece did not close into a solid; semantic queries on it will be " +
                              "approximate until it is repaired.");

                return (object)new Dictionary<string, object>
                {
                    { "mode", "split-mass" },
                    { "massId", mass.ElementId },
                    { "elevation", Round(elevation) },
                    { "resultMassIds", resultIds },
                    { "notes", Join(notes, "Both pieces inherit the original mass's layer and tags, so they " +
                                           "keep its function. Retag either one with ClaudeSetElement if the " +
                                           "programme differs.") }
                };
            });

            Invalidate();
            return result;
        }

        // ══ extrude_face_outward ══════════════════════════════════════

        /// <summary>
        /// Distinct from push_pull_face: this leaves the existing face where it is and creates
        /// a new solid projecting from it. That is what a canopy, a bump-out or a brise-soleil
        /// actually is.
        /// </summary>
        public object ExtrudeFaceOutward(string massId, FaceSelector selector, double distance, bool asOverhang)
        {
            var context = ResolveFace(massId, selector);
            var notes = new List<string>();

            if (distance <= 0)
                throw new ArgumentException("distance must be positive — extrude_face_outward projects outward. " +
                                            "To push a face inward use push_pull_face with a negative distance.");

            var result = _mutation.RunComposite("extrude_face_outward", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;
                var brep = RequireBrep(doc, context.ObjectId);

                if (context.Face.FaceIndex < 0 || context.Face.FaceIndex >= brep.Faces.Count)
                    throw new ArgumentException("That face index is no longer valid — call get_mass_faces again.");

                var faceBrep = brep.Faces[context.Face.FaceIndex].DuplicateFace(false);
                if (faceBrep == null)
                    throw new InvalidOperationException("Could not duplicate that face to extrude it.");

                var direction = SemanticClassifier.ToVector(context.Face.Normal.Unit()) * distance;
                var extruded = faceBrep.Faces.Count > 0
                    ? Brep.CreateFromOffsetFace(faceBrep.Faces[0], distance, tolerance, false, true)
                    : null;

                if (extruded == null || !extruded.IsSolid)
                {
                    // Offset-face is the clean route when it works; a straight extrusion of the
                    // face's outer boundary is the fallback that always does.
                    var boundary = faceBrep.Faces.Count > 0
                        ? faceBrep.Faces[0].OuterLoop?.To3dCurve()
                        : null;
                    if (boundary == null)
                        throw new InvalidOperationException("Could not build a projecting solid from that face.");

                    var surface = Surface.CreateExtrusion(boundary, direction);
                    extruded = surface?.ToBrep()?.CapPlanarHoles(tolerance);
                    if (extruded == null)
                        throw new InvalidOperationException("Rhino could not extrude that face outward.");

                    notes.Add("Built by extruding the face boundary rather than offsetting the face; the " +
                              "result follows the face outline exactly.");
                }

                string layer = asOverhang ? "OVERHANG_Canopy" : CanonicalConvention.LayerForFunction(context.Mass.Function);
                int layerIndex = EnsureLayerIndex(doc, layer);

                var attributes = doc.CreateDefaultAttributes();
                attributes.LayerIndex = layerIndex;
                attributes.Name = asOverhang
                    ? "Overhang off " + (context.Mass.Name ?? "mass")
                    : "Bump-out off " + (context.Mass.Name ?? "mass");

                if (asOverhang)
                {
                    attributes.SetUserString(SemanticVocabulary.KeyElementPrefix + SemanticVocabulary.Overhang, "Canopy");
                }
                else
                {
                    attributes.SetUserString(SemanticVocabulary.KeyElementPrefix + SemanticVocabulary.Mass, "1");
                    attributes.SetUserString(SemanticVocabulary.KeyMassFunction, context.Mass.Function);
                }

                var id = doc.Objects.AddBrep(extruded, attributes);
                if (id == Guid.Empty)
                    throw new InvalidOperationException("Rhino refused to add the projecting solid.");

                var box = SemanticClassifier.ToBox(extruded.GetBoundingBox(true));

                return (object)new Dictionary<string, object>
                {
                    { asOverhang ? "overhangId" : "newMassId", id.ToString() },
                    { "sourceMassId", context.Mass.ElementId },
                    { "sourceFaceId", context.Face.FaceId },
                    { "distance", Round(distance) },
                    { "layer", layer },
                    { "resultBbox", box.ToJson() },
                    { "volume", Round(Measure(extruded)) },
                    { "notes", Join(notes, asOverhang
                        ? "Tagged as an Overhang, so it is excluded from program area and mass counts."
                        : "Tagged as a Mass with the parent's function; boolean-union it with the parent " +
                          "using add_mass's unionWithExisting if it should read as one form.") }
                };
            });

            Invalidate();
            return result;
        }

        // ══ fillet_edges ══════════════════════════════════════════════

        public object FilletEdges(string massId, List<EdgeSelector> selectors, double radius)
        {
            var mass = RequireMass(massId);
            var geometry = _registry.GeometryFor(mass);
            if (geometry == null) throw new InvalidOperationException("Could not analyse that mass's geometry.");

            if (radius <= 0) throw new ArgumentException("radius must be positive.");

            var edges = EdgeSelector.ResolveAll(geometry, selectors);
            if (edges.Count == 0)
            {
                var available = geometry.Edges.Select(e => e.Role).Distinct().OrderBy(r => r, StringComparer.Ordinal);
                throw new ArgumentException(
                    "No edge matched " + string.Join(" or ", selectors.Select(s => s.ToString())) +
                    ". This mass has edges with roles [" + string.Join(", ", available) + "].");
            }

            var notes = new List<string>();

            var result = _mutation.RunComposite("fillet_edges", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;
                var brep = RequireBrep(doc, Guid.Parse(mass.PrimaryObjectId));

                var indices = edges.Select(e => e.EdgeIndex).Where(i => i >= 0 && i < brep.Edges.Count).ToList();
                if (indices.Count == 0)
                    throw new ArgumentException("Those edge indices are no longer valid — call get_mass_edges again.");

                var radii = indices.Select(_ => radius).ToList();

                var filleted = Brep.CreateFilletEdges(
                    brep, indices, radii, radii, BlendType.Fillet, RailType.RollingBall, tolerance);

                if (filleted == null || filleted.Length == 0)
                    throw new InvalidOperationException(
                        "Rhino could not fillet those edges at radius " + Round(radius) + ". A radius larger " +
                        "than the adjacent faces can absorb is the usual cause — try a smaller one.");

                if (filleted.Length > 1)
                    notes.Add("The fillet produced " + filleted.Length + " pieces; the largest was kept.");

                var largest = filleted.OrderByDescending(Measure).First();
                var massObjectId = Guid.Parse(mass.PrimaryObjectId);
                if (!doc.Objects.Replace(massObjectId, largest))
                    throw new InvalidOperationException("Rhino refused to replace the mass with the filleted Brep.");

                return (object)new Dictionary<string, object>
                {
                    { "resultMassId", mass.ElementId },
                    { "affectedEdgeCount", indices.Count },
                    { "radius", Round(radius) },
                    { "newVolume", Round(Measure(largest)) },
                    { "isSolid", largest.IsSolid },
                    { "notes", Join(notes, "Face and edge indices on this mass have all changed — call " +
                                           "get_mass_faces or get_mass_edges again before the next operation.") }
                };
            });

            Invalidate();
            return result;
        }

        // ══ promote_opening_to_entry ══════════════════════════════════

        /// <summary>
        /// Plan §3.8: Entry is a property on an Opening, not an element type. A hole in a face
        /// has no Rhino object of its own, so the flag is written on the parent mass keyed by
        /// where the hole sits — and read back by the geometry analyzer.
        /// </summary>
        public object PromoteOpeningToEntry(string openingId, string entryType)
        {
            entryType = SemanticVocabulary.Normalize(entryType, SemanticVocabulary.EntryTypes, "Main");

            var opening = _registry.View.LooseOpenings
                .FirstOrDefault(o => string.Equals(o.ElementId, openingId, StringComparison.OrdinalIgnoreCase));

            if (opening == null)
            {
                opening = _registry.AllGeometry().SelectMany(g => g.AllOpenings)
                    .FirstOrDefault(o => string.Equals(o.ElementId, openingId, StringComparison.OrdinalIgnoreCase));
            }

            if (opening == null)
                throw new ArgumentException(
                    "No Opening with id '" + openingId + "'. Call find_openings_in_face on the face it is on, " +
                    "or find_element(\"the main entry\").");

            var captured = opening;

            var result = _mutation.RunComposite("promote_opening_to_entry", doc =>
            {
                if (captured.RhinoObjectIds.Count > 0
                    && Guid.TryParse(captured.RhinoObjectIds[0], out var looseId))
                {
                    // A drawn opening object carries its own tag.
                    var obj = doc.Objects.FindId(looseId);
                    if (obj != null)
                    {
                        var attributes = obj.Attributes.Duplicate();
                        attributes.SetUserString(SemanticVocabulary.KeyEntryType, entryType);
                        doc.Objects.ModifyAttributes(obj, attributes, true);
                    }
                }
                else
                {
                    var massObjectId = _registry.View.FindMass(captured.MassId)?.PrimaryObjectId;
                    if (massObjectId == null)
                        throw new InvalidOperationException(
                            "The opening's parent mass could not be found, so the entry flag has nowhere to live.");

                    SetMassUserString(doc, Guid.Parse(massObjectId),
                        EntryTagKey(captured.Centroid), entryType);
                }

                return (object)new Dictionary<string, object>
                {
                    { "openingId", captured.ElementId },
                    { "isEntry", true },
                    { "entryType", entryType },
                    { "notes", "Recorded on the parent mass, keyed by where the opening sits, so it survives " +
                               "the face re-indexing that follows any further boolean." }
                };
            });

            Invalidate();
            return result;
        }

        // ══ Shared ════════════════════════════════════════════════════

        private sealed class FaceContext
        {
            public MassView Mass;
            public MassGeometryView Geometry;
            public FaceView Face;
            public Guid ObjectId;
            public string SelectorNote;
        }

        private FaceContext ResolveFace(string massId, FaceSelector selector)
        {
            var mass = RequireMass(massId);
            var geometry = _registry.GeometryFor(mass);
            if (geometry == null)
                throw new InvalidOperationException("Could not analyse the geometry of mass " + mass.ElementId + ".");

            var resolution = FaceSelectorResolver.Resolve(geometry, selector);
            if (!resolution.Resolved) throw new ArgumentException(resolution.Error);

            if (!Guid.TryParse(mass.PrimaryObjectId, out var objectId))
                throw new InvalidOperationException("That mass has no Rhino object behind it.");

            return new FaceContext
            {
                Mass = mass,
                Geometry = geometry,
                Face = resolution.Face,
                ObjectId = objectId,
                SelectorNote = resolution.Note
            };
        }

        private MassView RequireMass(string massId)
        {
            var mass = _registry.FindMass(massId);
            if (mass == null)
                throw new ArgumentException(
                    "No Mass with id or name '" + massId + "'. Call list_masses to see the current masses.");
            return mass;
        }

        private static Brep RequireBrep(RhinoDoc doc, Guid objectId)
        {
            var obj = doc.Objects.FindId(objectId);
            if (obj == null || obj.IsDeleted)
                throw new ArgumentException("The mass's Rhino object no longer exists — it may have been " +
                                            "replaced by an earlier operation in this turn. Call list_masses again.");

            var brep = SemanticClassifier.AsBrep(obj.Geometry);
            if (brep == null)
                throw new ArgumentException("That mass is a " + obj.ObjectType + ", not a Brep.");
            return brep;
        }

        /// <summary>
        /// A face's own coordinate frame: origin at its lower-left corner looking at it from
        /// outside, horizontal running left to right, vertical running up. Without this,
        /// "8 feet from the left edge" has no meaning on a face that is not aligned to world X.
        /// </summary>
        private struct Frame
        {
            public Point3d Origin;
            public Vector3d Horizontal;
            public Vector3d Vertical;
            public Vector3d Normal;
            public double Width;
            public double Height;
        }

        private static Frame FaceFrame(FaceView face)
        {
            var normal = SemanticClassifier.ToVector(face.Normal.Unit());

            var horizontal = Vector3d.CrossProduct(Vector3d.ZAxis, normal);
            if (horizontal.Length <= RhinoMath.ZeroTolerance)
            {
                // A horizontal face has no "up"; use world X and Y so the frame is still defined.
                horizontal = Vector3d.XAxis;
            }
            horizontal.Unitize();

            var vertical = Vector3d.CrossProduct(normal, horizontal);
            vertical.Unitize();

            // Face extents in that frame, from the elevation range and the face's area. The
            // vertical extent is exact; the horizontal is derived so a face of any shape gets a
            // usable width.
            double height = Math.Max(face.ElevationMax - face.ElevationMin, RhinoMath.ZeroTolerance);
            double width = height > RhinoMath.ZeroTolerance ? face.Area / height : Math.Sqrt(face.Area);

            var centroid = SemanticClassifier.ToPoint(face.Centroid);
            var origin = centroid - horizontal * (width / 2) - vertical * (height / 2);

            return new Frame
            {
                Origin = origin,
                Horizontal = horizontal,
                Vertical = vertical,
                Normal = normal,
                Width = width,
                Height = height
            };
        }

        /// <summary>How deep the mass is behind a face — the through-cut distance for an opening.</summary>
        private static double MassDepth(FaceContext context, Brep brep)
        {
            var box = brep.GetBoundingBox(true);
            if (!box.IsValid) return 1.0;

            var normal = context.Face.Normal.Unit();
            return Math.Abs(normal.X) * (box.Max.X - box.Min.X)
                 + Math.Abs(normal.Y) * (box.Max.Y - box.Min.Y)
                 + Math.Abs(normal.Z) * (box.Max.Z - box.Min.Z);
        }

        private static int EnsureLayerIndex(RhinoDoc doc, string fullPath)
        {
            int existing = doc.Layers.FindByFullPath(fullPath, -1);
            if (existing >= 0) return existing;

            string[] segments = fullPath.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
            Guid parentId = Guid.Empty;
            int index = -1;
            string accumulated = null;

            foreach (var segment in segments)
            {
                accumulated = accumulated == null ? segment : accumulated + "::" + segment;
                int found = doc.Layers.FindByFullPath(accumulated, -1);
                if (found >= 0)
                {
                    index = found;
                    parentId = doc.Layers[found].Id;
                    continue;
                }

                var layer = new Layer { Name = segment };
                if (parentId != Guid.Empty) layer.ParentLayerId = parentId;

                index = doc.Layers.Add(layer);
                if (index < 0) throw new InvalidOperationException("Rhino refused to create layer '" + accumulated + "'.");
                parentId = doc.Layers[index].Id;
            }

            return index;
        }

        private static void SetMassUserString(RhinoDoc doc, Guid objectId, string key, string value)
        {
            var obj = doc.Objects.FindId(objectId);
            if (obj == null) return;

            var attributes = obj.Attributes.Duplicate();
            attributes.SetUserString(key, value);
            doc.Objects.ModifyAttributes(obj, attributes, true);
        }

        /// <summary>
        /// Position-keyed tags. A hole has no object id, and face indices are destroyed by the
        /// next boolean — a rounded world position is the only handle that survives.
        /// </summary>
        public static string OpeningTagKey(Point3d centre) =>
            "RhinoClaude:OpeningType@" + Key(centre);

        public static string EntryTagKey(Vec3 centre) =>
            "RhinoClaude:Entry@" + Key(new Point3d(centre.X, centre.Y, centre.Z));

        /// <summary>A readable FLOOR_ layer suffix: L01 for the base, L02 one storey up, and so on.</summary>
        private string LevelLabel(double elevation, double massBase)
        {
            double floorToFloor = _registry.View.FloorToFloorDefault;
            if (floorToFloor > 0)
            {
                int level = (int)Math.Round((elevation - massBase) / floorToFloor) + 1;
                if (level >= 1) return "L" + level.ToString("00");
            }
            return "At" + Math.Round(elevation).ToString("0");
        }

        private static string Key(Point3d point) =>
            Math.Round(point.X, 2).ToString("0.##") + "," +
            Math.Round(point.Y, 2).ToString("0.##") + "," +
            Math.Round(point.Z, 2).ToString("0.##");

        private static double Measure(Brep brep)
        {
            if (brep == null || !brep.IsSolid) return 0;
            try
            {
                var properties = VolumeMassProperties.Compute(brep);
                return properties == null ? 0 : Math.Abs(properties.Volume);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private void Invalidate() => _registry.InvalidateAll();

        private static double Round(double value) => RhinoQueryService.Round(value);

        private static string Join(IEnumerable<string> notes, string trailing)
        {
            var all = (notes ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (!string.IsNullOrWhiteSpace(trailing)) all.Add(trailing);
            return all.Count == 0 ? null : string.Join(" ", all);
        }
    }
}
