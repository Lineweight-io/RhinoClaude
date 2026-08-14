using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using RhinoClaude.Agent;

namespace RhinoClaude.Services.Agent
{
    /// <summary>
    /// The rest of the Tier 1 write surface: primitives beyond the box, the full transform
    /// set, booleans, curve/surface modification, blocks and materials.
    ///
    /// Same contract as the rest of the service — one undo record per public method,
    /// UI-thread only, structured results with an actionable <c>notes</c> field when Rhino
    /// does something the agent should know about.
    /// </summary>
    public sealed partial class RhinoMutationService
    {
        // ── Create: primitives ────────────────────────────────────────

        public object CreatePoint(Point3d location, string layer, string name)
        {
            return InUndoRecord("create_point", doc =>
            {
                var id = doc.Objects.AddPoint(location, BuildAttributes(doc, layer, name));
                if (id == Guid.Empty)
                    throw new InvalidOperationException("Rhino refused to add the point to the document.");

                return (object)new Dictionary<string, object>
                {
                    { "id", id.ToString() },
                    { "location", RhinoQueryService.Pt(location) }
                };
            });
        }

        public object CreateCircle(Point3d center, double radius, Vector3d? normal, string layer, string name)
        {
            if (radius <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("radius must be greater than zero.");

            return InUndoRecord("create_circle", doc =>
            {
                var plane = PlaneAt(center, normal);
                var circle = new Circle(plane, radius);
                var curve = circle.ToNurbsCurve();

                var id = doc.Objects.AddCurve(curve, BuildAttributes(doc, layer, name));
                if (id == Guid.Empty)
                    throw new InvalidOperationException("Rhino refused to add the circle to the document.");

                return (object)new Dictionary<string, object>
                {
                    { "id", id.ToString() },
                    { "circumference", RhinoQueryService.Round(circle.Circumference) },
                    { "bbox", RhinoQueryService.Bbox(curve.GetBoundingBox(true)) }
                };
            });
        }

        public object CreateRectangle(Point3d corner, double width, double depth, Vector3d? normal, string layer, string name)
        {
            if (Math.Abs(width) <= RhinoMath.ZeroTolerance || Math.Abs(depth) <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("width and depth must both be non-zero.");

            return InUndoRecord("create_rectangle", doc =>
            {
                // The rectangle grows from the given corner along the plane's axes, which is
                // what "corner + width + depth" reads as to anyone placing a room.
                var plane = PlaneAt(corner, normal);
                var rectangle = new Rectangle3d(plane, width, depth);
                var curve = rectangle.ToNurbsCurve();
                if (curve == null)
                    throw new InvalidOperationException("Rhino could not build a rectangle from those values.");

                var id = doc.Objects.AddCurve(curve, BuildAttributes(doc, layer, name));
                if (id == Guid.Empty)
                    throw new InvalidOperationException("Rhino refused to add the rectangle to the document.");

                return (object)new Dictionary<string, object>
                {
                    { "id", id.ToString() },
                    { "area", RhinoQueryService.Round(Math.Abs(width * depth)) },
                    { "bbox", RhinoQueryService.Bbox(curve.GetBoundingBox(true)) }
                };
            });
        }

        public object CreateArcCurve(string mode, Point3d? start, Point3d? through, Point3d? end,
                                     Point3d? center, double radius, double angleDegrees,
                                     Vector3d? normal, string layer, string name)
        {
            return InUndoRecord("create_arc_curve", doc =>
            {
                Arc arc;

                if (string.Equals(mode, "centerRadius", StringComparison.OrdinalIgnoreCase))
                {
                    if (!center.HasValue)
                        throw new ArgumentException("centerRadius mode requires 'center'.");
                    if (radius <= RhinoMath.ZeroTolerance)
                        throw new ArgumentException("centerRadius mode requires a positive 'radius'.");
                    if (Math.Abs(angleDegrees) <= RhinoMath.ZeroTolerance)
                        throw new ArgumentException("centerRadius mode requires a non-zero 'angleDegrees'.");

                    arc = new Arc(PlaneAt(center.Value, normal), radius, RhinoMath.ToRadians(angleDegrees));
                }
                else
                {
                    if (!start.HasValue || !through.HasValue || !end.HasValue)
                        throw new ArgumentException("threePoint mode requires 'start', 'through' and 'end'.");

                    arc = new Arc(start.Value, through.Value, end.Value);
                }

                if (!arc.IsValid)
                    throw new ArgumentException(
                        "Those values do not describe a valid arc — collinear points, or a zero radius or angle.");

                var curve = arc.ToNurbsCurve();
                var id = doc.Objects.AddCurve(curve, BuildAttributes(doc, layer, name));
                if (id == Guid.Empty)
                    throw new InvalidOperationException("Rhino refused to add the arc to the document.");

                return (object)new Dictionary<string, object>
                {
                    { "id", id.ToString() },
                    { "length", RhinoQueryService.Round(arc.Length) },
                    { "radius", RhinoQueryService.Round(arc.Radius) },
                    { "bbox", RhinoQueryService.Bbox(curve.GetBoundingBox(true)) }
                };
            });
        }

        // ── Transform ─────────────────────────────────────────────────

        public object RotateObjects(List<string> ids, Point3d center, Vector3d axis, double angleDegrees, bool copy)
        {
            if (axis.Length <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("axis must be a non-zero vector.");

            return ApplyTransform("rotate_objects", ids, copy,
                Transform.Rotation(RhinoMath.ToRadians(angleDegrees), axis, center),
                new Dictionary<string, object> { { "angleDegrees", angleDegrees } });
        }

        public object ScaleObjects(List<string> ids, Point3d center, double[] factors, bool copy)
        {
            if (factors == null || factors.Length == 0)
                throw new ArgumentException("factor must be a number or [x, y, z].");

            double fx, fy, fz;
            if (factors.Length == 1) { fx = fy = fz = factors[0]; }
            else if (factors.Length == 3) { fx = factors[0]; fy = factors[1]; fz = factors[2]; }
            else throw new ArgumentException("factor must be a single number or exactly three numbers.");

            if (Math.Abs(fx) <= RhinoMath.ZeroTolerance ||
                Math.Abs(fy) <= RhinoMath.ZeroTolerance ||
                Math.Abs(fz) <= RhinoMath.ZeroTolerance)
            {
                throw new ArgumentException("Scale factors must all be non-zero — a zero factor collapses the geometry.");
            }

            var plane = new Plane(center, Vector3d.XAxis, Vector3d.YAxis);
            return ApplyTransform("scale_objects", ids, copy,
                Transform.Scale(plane, fx, fy, fz),
                new Dictionary<string, object> { { "factors", new[] { fx, fy, fz } } });
        }

        /// <summary>
        /// One-directional scale to a target length, along the axis from base to reference —
        /// the RhinoCommon equivalent of the Scale1D command.
        /// </summary>
        public object Scale1D(List<string> ids, Point3d basePoint, Point3d referencePoint, double targetLength, bool copy)
        {
            var direction = referencePoint - basePoint;
            double current = direction.Length;

            if (current <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("basePoint and referencePoint are the same — the scaling direction is undefined.");
            if (Math.Abs(targetLength) <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("targetLength must be non-zero.");

            double factor = targetLength / current;

            // A plane whose X axis is the scaling direction, so scaling X alone is a 1-D scale.
            direction.Unitize();
            var scalePlane = new Plane(basePoint, direction, PerpendicularTo(direction));

            return ApplyTransform("scale_1d", ids, copy,
                Transform.Scale(scalePlane, factor, 1.0, 1.0),
                new Dictionary<string, object>
                {
                    { "computedScaleFactor", RhinoQueryService.Round(factor) },
                    { "currentLength", RhinoQueryService.Round(current) },
                    { "targetLength", RhinoQueryService.Round(targetLength) }
                });
        }

        public object MirrorObjects(List<string> ids, Point3d planeOrigin, Vector3d planeNormal, bool copy)
        {
            if (planeNormal.Length <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("planeNormal must be a non-zero vector.");

            return ApplyTransform("mirror_objects", ids, copy,
                Transform.Mirror(planeOrigin, planeNormal), null);
        }

        /// <summary>Shared body for every transform tool: resolve, transform, report new boxes.</summary>
        private object ApplyTransform(string toolName, List<string> ids, bool copy,
                                      Transform transform, Dictionary<string, object> extra)
        {
            if (ids == null || ids.Count == 0)
                throw new ArgumentException("At least one object id is required.");
            if (!transform.IsValid)
                throw new ArgumentException("The requested transform is not valid.");

            var objects = ids.Select(_query.RequireObject).ToList();

            return InUndoRecord(toolName, doc =>
            {
                var updatedIds = new List<string>();
                var newBboxes = new Dictionary<string, object>();

                foreach (var obj in objects)
                {
                    var newId = doc.Objects.Transform(obj.Id, transform, !copy);
                    if (newId == Guid.Empty)
                        throw new InvalidOperationException("Rhino refused to transform object " + obj.Id + ".");

                    updatedIds.Add(newId.ToString());
                    var moved = doc.Objects.FindId(newId);
                    newBboxes[newId.ToString()] = RhinoQueryService.Bbox(
                        moved?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset);
                }

                var result = new Dictionary<string, object>
                {
                    { "updatedIds", updatedIds },
                    { "newBboxes", newBboxes },
                    { "copied", copy }
                };
                if (extra != null)
                    foreach (var kv in extra) result[kv.Key] = kv.Value;

                return (object)result;
            });
        }

        // ── Boolean ───────────────────────────────────────────────────

        public object BooleanOperation(string operation, List<string> firstIds, List<string> secondIds, bool deleteInputs)
        {
            if (firstIds == null || firstIds.Count == 0)
                throw new ArgumentException("At least one input object is required.");

            var firstObjects = firstIds.Select(_query.RequireObject).ToList();
            var secondObjects = (secondIds ?? new List<string>()).Select(_query.RequireObject).ToList();

            return InUndoRecord("boolean_" + operation, doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;

                var notes = new List<string>();
                var firstBreps = ToBreps(firstObjects, notes);
                var secondBreps = ToBreps(secondObjects, notes);

                if (firstBreps.Count == 0)
                    throw new ArgumentException(
                        "None of the first set could be used as a solid. Booleans need Breps, extrusions or surfaces; " +
                        "curves and points cannot take part.");

                foreach (var brep in firstBreps.Concat(secondBreps))
                    if (!brep.IsSolid)
                        notes.Add("At least one input is an open surface rather than a closed solid — " +
                                  "Rhino's boolean may fail or return the inputs unchanged.");

                Brep[] results;
                switch (operation)
                {
                    case "union":
                        results = Brep.CreateBooleanUnion(firstBreps, tolerance);
                        break;
                    case "difference":
                        if (secondBreps.Count == 0)
                            throw new ArgumentException("boolean_difference needs at least one subtrahend.");
                        results = Brep.CreateBooleanDifference(firstBreps, secondBreps, tolerance);
                        break;
                    case "intersection":
                        if (secondBreps.Count == 0)
                            throw new ArgumentException("boolean_intersection needs objects in both sets.");
                        results = Brep.CreateBooleanIntersection(firstBreps, secondBreps, tolerance);
                        break;
                    default:
                        throw new ArgumentException("Unknown boolean operation '" + operation + "'.");
                }

                if (results == null || results.Length == 0)
                {
                    return (object)new Dictionary<string, object>
                    {
                        { "resultIds", new List<string>() },
                        { "notes", "Rhino's boolean produced no result — the solids may not intersect, or one of " +
                                   "them is not closed. Nothing was changed. " + string.Join(" ", notes.Distinct()) }
                    };
                }

                var resultIds = new List<string>();
                foreach (var brep in results)
                {
                    var id = doc.Objects.AddBrep(brep, firstObjects[0].Attributes.Duplicate());
                    if (id != Guid.Empty) resultIds.Add(id.ToString());
                }

                if (deleteInputs)
                {
                    foreach (var obj in firstObjects.Concat(secondObjects))
                        doc.Objects.Delete(obj.Id, true);
                }

                if (results.Length > 1)
                    notes.Add("The operation produced " + results.Length + " separate pieces.");

                return (object)new Dictionary<string, object>
                {
                    { "resultIds", resultIds },
                    { "deletedInputs", deleteInputs },
                    { "notes", notes.Count == 0 ? "Boolean completed cleanly." : string.Join(" ", notes.Distinct()) }
                };
            });
        }

        private static List<Brep> ToBreps(IEnumerable<RhinoObject> objects, List<string> notes)
        {
            var breps = new List<Brep>();
            foreach (var obj in objects)
            {
                switch (obj.Geometry)
                {
                    case Brep brep:
                        breps.Add(brep.DuplicateBrep());
                        break;
                    case Extrusion extrusion:
                        var converted = extrusion.ToBrep();
                        if (converted != null) breps.Add(converted);
                        break;
                    case Surface surface:
                        var surfaceBrep = surface.ToBrep();
                        if (surfaceBrep != null) breps.Add(surfaceBrep);
                        break;
                    default:
                        notes.Add("Object " + obj.Id + " is a " + obj.ObjectType +
                                  " and was skipped — booleans only apply to solids and surfaces.");
                        break;
                }
            }
            return breps;
        }

        // ── Curve / surface modification ──────────────────────────────

        public object OffsetCurve(string id, double distance, Vector3d? normal, string layer)
        {
            var obj = _query.RequireObject(id);
            var curve = obj.Geometry as Curve;
            if (curve == null)
                throw new ArgumentException("Object " + id + " is a " + obj.ObjectType + ", not a curve.");

            if (Math.Abs(distance) <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("distance must be non-zero.");

            return InUndoRecord("offset_curve", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;

                Plane plane;
                if (normal.HasValue && normal.Value.Length > RhinoMath.ZeroTolerance)
                {
                    plane = new Plane(curve.PointAtStart, normal.Value);
                }
                else if (!curve.TryGetPlane(out plane, tolerance))
                {
                    throw new ArgumentException(
                        "The curve is not planar, so the offset direction is ambiguous. Supply 'normal' to " +
                        "pick the plane, or use run_rhinocommon_script for a non-planar offset.");
                }

                var offsets = curve.Offset(plane, distance, tolerance, CurveOffsetCornerStyle.Sharp);
                if (offsets == null || offsets.Length == 0)
                    throw new InvalidOperationException(
                        "Rhino could not offset this curve by " + distance +
                        ". A distance larger than the curve's inner radius will fail.");

                var attributes = string.IsNullOrWhiteSpace(layer)
                    ? obj.Attributes.Duplicate()
                    : BuildAttributes(doc, layer, null);

                var resultIds = new List<string>();
                foreach (var offset in offsets)
                {
                    var newId = doc.Objects.AddCurve(offset, attributes);
                    if (newId != Guid.Empty) resultIds.Add(newId.ToString());
                }

                return (object)new Dictionary<string, object>
                {
                    { "resultIds", resultIds },
                    { "notes", offsets.Length > 1
                        ? "The offset produced " + offsets.Length + " curve segments."
                        : "Offset completed." }
                };
            });
        }

        public object ExtrudeCurve(string id, Vector3d direction, double distance, bool cap, bool deleteInput, string layer)
        {
            var obj = _query.RequireObject(id);
            var curve = obj.Geometry as Curve;
            if (curve == null)
                throw new ArgumentException("Object " + id + " is a " + obj.ObjectType + ", not a curve.");

            if (direction.Length <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("direction must be a non-zero vector.");
            if (Math.Abs(distance) <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("distance must be non-zero.");

            return InUndoRecord("extrude_curve", doc =>
            {
                var vector = direction;
                vector.Unitize();
                vector *= distance;

                var surface = Surface.CreateExtrusion(curve, vector);
                if (surface == null)
                    throw new InvalidOperationException("Rhino could not extrude this curve.");

                var brep = surface.ToBrep();
                if (brep == null)
                    throw new InvalidOperationException("The extrusion could not be converted to a Brep.");

                var notes = new List<string>();

                if (cap)
                {
                    if (!curve.IsClosed)
                    {
                        notes.Add("The input curve is open, so the extrusion is a surface rather than a solid — " +
                                  "capping was skipped.");
                    }
                    else
                    {
                        var capped = brep.CapPlanarHoles(doc.ModelAbsoluteTolerance);
                        if (capped != null) brep = capped;
                        else notes.Add("Rhino could not cap the extrusion; the result is an open surface.");
                    }
                }

                var attributes = string.IsNullOrWhiteSpace(layer)
                    ? obj.Attributes.Duplicate()
                    : BuildAttributes(doc, layer, null);

                var resultId = doc.Objects.AddBrep(brep, attributes);
                if (resultId == Guid.Empty)
                    throw new InvalidOperationException("Rhino refused to add the extrusion to the document.");

                if (deleteInput) doc.Objects.Delete(obj.Id, true);

                var volume = brep.IsSolid ? VolumeMassProperties.Compute(brep) : null;

                return (object)new Dictionary<string, object>
                {
                    { "resultId", resultId.ToString() },
                    { "isSolid", brep.IsSolid },
                    { "volume", volume == null ? (object)null : RhinoQueryService.Round(volume.Volume) },
                    { "bbox", RhinoQueryService.Bbox(brep.GetBoundingBox(true)) },
                    { "notes", notes.Count == 0 ? "Extrusion completed." : string.Join(" ", notes) }
                };
            });
        }

        /// <summary>
        /// Push/pull one Brep face or edge. Indices come from get_object with
        /// includeSubobjects, and are validated against the current Brep — a stale index
        /// after an earlier edit is the most likely way to get this wrong.
        /// </summary>
        public object MoveSubObject(bool isFace, string brepId, int index, Vector3d direction, double distance)
        {
            var obj = _query.RequireObject(brepId);

            Brep source = obj.Geometry as Brep;
            if (source == null && obj.Geometry is Extrusion extrusion) source = extrusion.ToBrep();
            if (source == null)
                throw new ArgumentException("Object " + brepId + " is a " + obj.ObjectType +
                    ", not a Brep. move_face and move_edge only apply to solids and polysurfaces.");

            if (direction.Length <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("direction must be a non-zero vector.");

            int count = isFace ? source.Faces.Count : source.Edges.Count;
            string what = isFace ? "face" : "edge";
            if (index < 0 || index >= count)
                throw new ArgumentException(string.Format(
                    "{0} index {1} is out of range — this Brep has {2} {0}s (valid indices 0..{3}). " +
                    "Call get_object with includeSubobjects to get current indices.",
                    what, index, count, count - 1));

            return InUndoRecord(isFace ? "move_face" : "move_edge", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;
                var brep = source.DuplicateBrep();
                var notes = new List<string>();

                if (isFace && !brep.Faces[index].IsPlanar(tolerance))
                {
                    notes.Add("This face is not planar. Push/pull on free-form surfaces needs control-point " +
                              "editing — use run_rhinocommon_script if the result looks wrong.");
                }

                var vector = direction;
                vector.Unitize();
                vector *= distance;

                var component = isFace
                    ? brep.Faces[index].ComponentIndex()
                    : brep.Edges[index].ComponentIndex();

                bool moved = brep.TransformComponent(
                    new[] { component },
                    Transform.Translation(vector),
                    tolerance,
                    0.0,
                    false);

                if (!moved)
                    throw new InvalidOperationException(
                        "Rhino could not move that " + what + ". Adjacent faces may not be able to follow the " +
                        "move — try a smaller distance, or use run_rhinocommon_script for a rebuild.");

                if (!brep.IsValid)
                {
                    brep.Repair(tolerance);
                    if (!brep.IsValid)
                        notes.Add("The result is not a valid Brep; the geometry may be self-intersecting.");
                }

                var newId = doc.Objects.Replace(obj.Id, brep)
                    ? obj.Id
                    : Guid.Empty;

                if (newId == Guid.Empty)
                    throw new InvalidOperationException("Rhino refused to replace the object with the edited Brep.");

                return (object)new Dictionary<string, object>
                {
                    { "resultId", newId.ToString() },
                    { "newBbox", RhinoQueryService.Bbox(brep.GetBoundingBox(true)) },
                    { "isSolid", brep.IsSolid },
                    { "notes", notes.Count == 0 ? "Moved cleanly." : string.Join(" ", notes) }
                };
            });
        }

        // ── Blocks ────────────────────────────────────────────────────

        public object InsertBlock(string blockName, Point3d location, double rotationDegrees, double scale, string layer)
        {
            if (string.IsNullOrWhiteSpace(blockName))
                throw new ArgumentException("blockName is required.");
            if (Math.Abs(scale) <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("scale must be non-zero.");

            return InUndoRecord("insert_block", doc =>
            {
                var definition = doc.InstanceDefinitions.Find(blockName);
                if (definition == null)
                {
                    var available = doc.InstanceDefinitions
                        .Where(d => d != null && !d.IsDeleted)
                        .Select(d => d.Name)
                        .ToList();
                    throw new ArgumentException("No block definition named '" + blockName + "'. Available: " +
                        (available.Count == 0 ? "(none)" : string.Join(", ", available)));
                }

                var transform = Transform.Translation(location.X, location.Y, location.Z)
                              * Transform.Rotation(RhinoMath.ToRadians(rotationDegrees), Vector3d.ZAxis, Point3d.Origin)
                              * Transform.Scale(Point3d.Origin, scale);

                var id = doc.Objects.AddInstanceObject(definition.Index, transform, BuildAttributes(doc, layer, null));
                if (id == Guid.Empty)
                    throw new InvalidOperationException("Rhino refused to insert the block instance.");

                var inserted = doc.Objects.FindId(id);
                return (object)new Dictionary<string, object>
                {
                    { "id", id.ToString() },
                    { "blockName", ToolJson.Safe(definition.Name) },
                    { "bbox", RhinoQueryService.Bbox(inserted?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset) }
                };
            });
        }

        public object Import3dmAsBlock(string path, string blockName)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path is required.");
            if (!File.Exists(path))
                throw new ArgumentException("No file at '" + path + "'.");
            if (!string.Equals(Path.GetExtension(path), ".3dm", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("import_3dm_as_block only reads .3dm files.");

            return InUndoRecord("import_3dm_as_block", doc =>
            {
                string name = string.IsNullOrWhiteSpace(blockName)
                    ? Path.GetFileNameWithoutExtension(path)
                    : blockName;

                var existing = doc.InstanceDefinitions.Find(name);
                if (existing != null)
                {
                    return (object)new Dictionary<string, object>
                    {
                        { "blockName", ToolJson.Safe(existing.Name) },
                        { "definitionId", existing.Id.ToString() },
                        { "created", false },
                        { "notes", "A block with this name already exists; it was reused rather than re-imported." }
                    };
                }

                var file = File3dm.Read(path);
                if (file == null)
                    throw new InvalidOperationException("Rhino could not read '" + path + "'.");

                var geometry = new List<GeometryBase>();
                var attributes = new List<ObjectAttributes>();

                // Objects that only exist inside the file's own block definitions must not be
                // added twice — they arrive via their instance references instead.
                var nested = new HashSet<Guid>();
                foreach (var idef in file.AllInstanceDefinitions)
                    foreach (var objId in idef.GetObjectIds())
                        nested.Add(objId);

                foreach (var fileObject in file.Objects)
                {
                    if (fileObject?.Geometry == null) continue;
                    if (nested.Contains(fileObject.Attributes.ObjectId)) continue;
                    if (fileObject.Geometry is InstanceReferenceGeometry) continue;

                    geometry.Add(fileObject.Geometry);
                    attributes.Add(fileObject.Attributes);
                }

                if (geometry.Count == 0)
                    throw new InvalidOperationException(
                        "'" + Path.GetFileName(path) + "' contains no top-level geometry to make a block from.");

                int definitionIndex = doc.InstanceDefinitions.Add(
                    name,
                    "Imported by RhinoClaude from " + Path.GetFileName(path),
                    Point3d.Origin,
                    geometry,
                    attributes);

                if (definitionIndex < 0)
                    throw new InvalidOperationException("Rhino refused to create the block definition.");

                var definition = doc.InstanceDefinitions[definitionIndex];
                return (object)new Dictionary<string, object>
                {
                    { "blockName", ToolJson.Safe(definition.Name) },
                    { "definitionId", definition.Id.ToString() },
                    { "created", true },
                    { "objectCount", geometry.Count },
                    { "notes", "The definition was created but not placed. Call insert_block to put an instance in the model." }
                };
            });
        }

        // ── Materials ─────────────────────────────────────────────────

        public object AssignMaterial(List<string> ids, string materialName, string diffuseHex, double? transparency)
        {
            if (ids == null || ids.Count == 0)
                throw new ArgumentException("At least one object id is required.");
            if (string.IsNullOrWhiteSpace(materialName))
                throw new ArgumentException("materialName is required.");

            var objects = ids.Select(_query.RequireObject).ToList();

            return InUndoRecord("assign_material", doc =>
            {
                // Idempotent by name, like ensure_layer: reuse a matching material rather than
                // creating a duplicate every time the agent mentions "concrete".
                int index = -1;
                for (int i = 0; i < doc.Materials.Count; i++)
                {
                    var candidate = doc.Materials[i];
                    if (candidate != null && !candidate.IsDeleted &&
                        string.Equals(candidate.Name, materialName, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }

                bool created = false;
                if (index < 0)
                {
                    index = doc.Materials.Add();
                    if (index < 0)
                        throw new InvalidOperationException("Rhino refused to create the material.");
                    created = true;
                }

                var material = doc.Materials[index];
                material.Name = materialName;
                if (!string.IsNullOrWhiteSpace(diffuseHex))
                    material.DiffuseColor = ParseColor(diffuseHex);
                if (transparency.HasValue)
                    material.Transparency = Math.Max(0.0, Math.Min(1.0, transparency.Value));
                doc.Materials.Modify(material, index, true);

                var updatedIds = new List<string>();
                foreach (var obj in objects)
                {
                    var objectAttributes = obj.Attributes.Duplicate();
                    objectAttributes.MaterialIndex = index;
                    objectAttributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                    if (!doc.Objects.ModifyAttributes(obj, objectAttributes, true))
                        throw new InvalidOperationException("Rhino refused to set the material on object " + obj.Id + ".");
                    updatedIds.Add(obj.Id.ToString());
                }

                return (object)new Dictionary<string, object>
                {
                    { "updatedIds", updatedIds },
                    { "materialIndex", index },
                    { "materialName", ToolJson.Safe(materialName) },
                    { "created", created }
                };
            });
        }

        // ── helpers ───────────────────────────────────────────────────

        /// <summary>A construction plane at <paramref name="origin"/>, world XY unless a normal is given.</summary>
        private static Plane PlaneAt(Point3d origin, Vector3d? normal)
        {
            if (!normal.HasValue || normal.Value.Length <= RhinoMath.ZeroTolerance)
                return new Plane(origin, Vector3d.ZAxis);
            return new Plane(origin, normal.Value);
        }

        /// <summary>Any unit vector perpendicular to <paramref name="v"/>.</summary>
        private static Vector3d PerpendicularTo(Vector3d v)
        {
            var candidate = Math.Abs(v.Z) < 0.9 ? Vector3d.ZAxis : Vector3d.XAxis;
            var perpendicular = Vector3d.CrossProduct(v, candidate);
            perpendicular.Unitize();
            return perpendicular;
        }
    }
}
