using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoClaude.Agent;

namespace RhinoClaude.Services.Agent
{
    /// <summary>
    /// Every document write the agent can make. Each public method opens an undo record,
    /// applies, and closes it in a try/finally — the plan's non-negotiable risk #4.
    ///
    /// UI-thread only; the dispatcher guarantees that.
    /// </summary>
    public sealed partial class RhinoMutationService
    {
        private readonly RhinoQueryService _query;
        private readonly SessionSnapshotService _snapshots;

        public RhinoMutationService(RhinoQueryService query, SessionSnapshotService snapshots)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        }

        private RhinoDoc Doc => _query.Doc;

        /// <summary>What this session has changed. Feeds the self-review's checks and framing.</summary>
        public SessionMutationLog MutationLog { get; } = new SessionMutationLog();

        /// <summary>
        /// Run a mutation inside its own undo record. The <c>using</c> is what guarantees
        /// EndUndoRecord runs even when the body throws.
        ///
        /// The object-id delta is measured around the body rather than reported by each tool:
        /// tools already return their own ids, but Rhino also replaces ids behind the scenes
        /// (a transform, a Brep replace), and self-review needs the truth rather than the
        /// tool's view of it.
        /// </summary>
        private T InUndoRecord<T>(string toolName, Func<RhinoDoc, T> body)
        {
            var doc = Doc;
            var before = SnapshotIds(doc);

            using (_snapshots.BeginMutation(toolName))
            {
                var result = body(doc);
                doc.Views.Redraw();
                RecordMutation(doc, toolName, before);
                return result;
            }
        }

        /// <summary>
        /// Run a composite mutation — several document edits that must undo as one move —
        /// inside a single undo record, with the same id-delta bookkeeping every other tool
        /// gets.
        ///
        /// This is what the semantic write tools use. A semantic move like cut_opening is a
        /// cutter solid, a boolean, a tag and a delete; running each through its own public
        /// method here would leave four undo records for one architectural action, and the
        /// user would have to press undo four times to take back one window. Keeping the
        /// wrapper in this class is also what stops a second, parallel mutation pipeline from
        /// growing next to this one.
        /// </summary>
        public T RunComposite<T>(string toolName, Func<RhinoDoc, T> body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            return InUndoRecord(toolName, body);
        }

        private static HashSet<Guid> SnapshotIds(RhinoDoc doc)
        {
            var ids = new HashSet<Guid>();
            foreach (var obj in doc.Objects)
                if (!obj.IsDeleted) ids.Add(obj.Id);
            return ids;
        }

        private void RecordMutation(RhinoDoc doc, string toolName, HashSet<Guid> before)
        {
            var after = SnapshotIds(doc);

            var mutation = new SessionMutation { ToolName = toolName };
            mutation.CreatedIds.AddRange(after.Except(before).Select(g => g.ToString()));
            mutation.DeletedIds.AddRange(before.Except(after).Select(g => g.ToString()));

            // Ids the tool edited in place. The id delta cannot see these — the object is the
            // same object before and after — so the tool has to say so itself.
            foreach (var id in DrainPendingModified())
            {
                if (mutation.CreatedIds.Contains(id) || mutation.DeletedIds.Contains(id)) continue;
                if (!mutation.ModifiedIds.Contains(id)) mutation.ModifiedIds.Add(id);
            }

            var box = BoundingBox.Unset;
            foreach (var id in after.Except(before))
            {
                var obj = doc.Objects.FindId(id);
                if (obj?.Geometry == null) continue;

                var objectBox = obj.Geometry.GetBoundingBox(true);
                if (objectBox.IsValid) box.Union(objectBox);

                var layer = doc.Layers[obj.Attributes.LayerIndex];
                if (layer != null && !mutation.LayersTouched.Contains(layer.FullPath))
                    mutation.LayersTouched.Add(layer.FullPath);
            }

            foreach (var idText in mutation.ModifiedIds)
            {
                if (!Guid.TryParse(idText, out var id)) continue;
                var obj = doc.Objects.FindId(id);
                if (obj?.Geometry == null) continue;

                var objectBox = obj.Geometry.GetBoundingBox(true);
                if (objectBox.IsValid) box.Union(objectBox);

                var layer = doc.Layers[obj.Attributes.LayerIndex];
                if (layer != null && !mutation.LayersTouched.Contains(layer.FullPath))
                    mutation.LayersTouched.Add(layer.FullPath);
            }

            if (box.IsValid)
                mutation.AffectedBox = new[] { box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z };

            MutationLog.Add(mutation);
        }

        // ── In-place edits ────────────────────────────────────────────
        //
        // A tool that changes an object without replacing it — a layer assignment, a tag, a
        // material, an in-place transform — leaves the id table untouched, so the delta around
        // the undo record reports nothing. Those tools call NoteModified so the session log
        // still knows the object is part of the agent's work, which is what the "export result"
        // button writes out and what self-review counts.

        private readonly List<string> _pendingModifiedIds = new List<string>();

        private void NoteModified(IEnumerable<string> ids)
        {
            if (ids == null) return;
            foreach (var id in ids)
                if (!string.IsNullOrWhiteSpace(id)) _pendingModifiedIds.Add(id);
        }

        private List<string> DrainPendingModified()
        {
            var drained = _pendingModifiedIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _pendingModifiedIds.Clear();
            return drained;
        }

        /// <summary>
        /// ensure_layer creates a layer without creating any object, so the layer would not
        /// otherwise appear in the log — and the "did you leave a layer empty" check depends on it.
        /// </summary>
        private void NoteLayerTouched(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return;
            var mutation = new SessionMutation { ToolName = "ensure_layer" };
            mutation.LayersTouched.Add(fullPath);
            MutationLog.Add(mutation);
        }

        // ── ensure_layer ──────────────────────────────────────────────

        public object EnsureLayer(string fullPath, string colorHex)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                throw new ArgumentException("fullPath is required.");

            NoteLayerTouched(fullPath);

            return InUndoRecord("ensure_layer", doc =>
            {
                int existing = doc.Layers.FindByFullPath(fullPath, -1);
                if (existing >= 0)
                {
                    var found = doc.Layers[existing];
                    return (object)new Dictionary<string, object>
                    {
                        { "id", found.Id.ToString() },
                        { "created", false },
                        { "fullPath", ToolJson.Safe(found.FullPath) },
                        { "index", found.Index }
                    };
                }

                // Walk the path so "Walls::Interior" creates the parent too.
                string[] segments = fullPath.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
                Guid parentId = Guid.Empty;
                Layer created = null;
                string accumulated = null;

                foreach (var segment in segments)
                {
                    accumulated = accumulated == null ? segment : accumulated + "::" + segment;
                    int index = doc.Layers.FindByFullPath(accumulated, -1);
                    if (index >= 0)
                    {
                        created = doc.Layers[index];
                        parentId = created.Id;
                        continue;
                    }

                    var layer = new Layer { Name = segment };
                    if (parentId != Guid.Empty) layer.ParentLayerId = parentId;
                    if (!string.IsNullOrWhiteSpace(colorHex) && accumulated == fullPath)
                        layer.Color = ParseColor(colorHex);

                    int newIndex = doc.Layers.Add(layer);
                    if (newIndex < 0)
                        throw new InvalidOperationException("Rhino refused to create layer '" + accumulated + "'.");

                    created = doc.Layers[newIndex];
                    parentId = created.Id;
                }

                if (created == null)
                    throw new InvalidOperationException("Could not create layer '" + fullPath + "'.");

                return new Dictionary<string, object>
                {
                    { "id", created.Id.ToString() },
                    { "created", true },
                    { "fullPath", ToolJson.Safe(created.FullPath) },
                    { "index", created.Index }
                };
            });
        }

        public static Color ParseColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Color.Black;
            string h = hex.TrimStart('#');
            if (h.Length == 6 &&
                int.TryParse(h.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out int r) &&
                int.TryParse(h.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out int g) &&
                int.TryParse(h.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out int b))
            {
                return Color.FromArgb(r, g, b);
            }
            throw new ArgumentException("colorHex must be a 6-digit hex string like '#8B4513'.");
        }

        // ── create_box ────────────────────────────────────────────────

        public object CreateBox(Point3d corner1, Point3d corner2, string layer, string name)
        {
            var box = new BoundingBox(
                new Point3d(Math.Min(corner1.X, corner2.X), Math.Min(corner1.Y, corner2.Y), Math.Min(corner1.Z, corner2.Z)),
                new Point3d(Math.Max(corner1.X, corner2.X), Math.Max(corner1.Y, corner2.Y), Math.Max(corner1.Z, corner2.Z)));

            if (!box.IsValid || box.Diagonal.Length < RhinoMath.ZeroTolerance)
                throw new ArgumentException("The two corners are coincident or degenerate — the box would have zero size.");

            return InUndoRecord("create_box", doc =>
            {
                var brep = Brep.CreateFromBox(box);
                if (brep == null)
                    throw new InvalidOperationException("Rhino could not build a box from those corners.");

                var attributes = BuildAttributes(doc, layer, name);
                var id = doc.Objects.AddBrep(brep, attributes);
                if (id == Guid.Empty)
                    throw new InvalidOperationException("Rhino refused to add the box to the document.");

                var volume = VolumeMassProperties.Compute(brep);
                return (object)new Dictionary<string, object>
                {
                    { "id", id.ToString() },
                    { "bbox", RhinoQueryService.Bbox(box) },
                    { "volume", volume == null ? (object)null : RhinoQueryService.Round(volume.Volume) }
                };
            });
        }

        // ── create_line_curve ─────────────────────────────────────────

        public object CreateLineCurve(List<Point3d> points, int degree, bool closed, string layer, string name)
        {
            if (points == null || points.Count < 2)
                throw new ArgumentException("At least two points are required.");

            return InUndoRecord("create_line_curve", doc =>
            {
                var pts = new List<Point3d>(points);
                if (closed && pts[0].DistanceTo(pts[pts.Count - 1]) > doc.ModelAbsoluteTolerance)
                    pts.Add(pts[0]);

                Curve curve;
                if (degree <= 1)
                {
                    var polyline = new Polyline(pts);
                    if (!polyline.IsValid)
                        throw new ArgumentException("The points do not form a valid polyline (duplicates or too few distinct points).");
                    curve = polyline.ToNurbsCurve();
                }
                else
                {
                    curve = Curve.CreateInterpolatedCurve(pts, degree);
                    if (curve == null)
                        throw new InvalidOperationException(
                            "Rhino could not interpolate a degree-" + degree + " curve through those points. " +
                            "Try degree 1 (polyline) or supply more points.");
                }

                if (curve.GetLength() <= doc.ModelAbsoluteTolerance)
                    throw new ArgumentException("The resulting curve has zero length.");

                var attributes = BuildAttributes(doc, layer, name);
                var id = doc.Objects.AddCurve(curve, attributes);
                if (id == Guid.Empty)
                    throw new InvalidOperationException("Rhino refused to add the curve to the document.");

                return (object)new Dictionary<string, object>
                {
                    { "id", id.ToString() },
                    { "length", RhinoQueryService.Round(curve.GetLength()) },
                    { "closed", curve.IsClosed },
                    { "degree", curve.Degree },
                    { "bbox", RhinoQueryService.Bbox(curve.GetBoundingBox(true)) }
                };
            });
        }

        // ── translate_objects ─────────────────────────────────────────

        public object TranslateObjects(List<string> ids, Vector3d vector, bool copy)
        {
            if (ids == null || ids.Count == 0)
                throw new ArgumentException("At least one object id is required.");

            var objects = ids.Select(_query.RequireObject).ToList();

            return InUndoRecord("translate_objects", doc =>
            {
                var transform = Transform.Translation(vector);
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

                NoteModified(updatedIds);

                return (object)new Dictionary<string, object>
                {
                    { "updatedIds", updatedIds },
                    { "newBboxes", newBboxes },
                    { "copied", copy }
                };
            });
        }

        // ── assign_objects_to_layer ───────────────────────────────────

        public object AssignObjectsToLayer(List<string> ids, string layerFullPath)
        {
            if (ids == null || ids.Count == 0)
                throw new ArgumentException("At least one object id is required.");
            if (string.IsNullOrWhiteSpace(layerFullPath))
                throw new ArgumentException("layerFullPath is required.");

            var objects = ids.Select(_query.RequireObject).ToList();
            NoteLayerTouched(layerFullPath);

            return InUndoRecord("assign_objects_to_layer", doc =>
            {
                int layerIndex = doc.Layers.FindByFullPath(layerFullPath, -1);
                if (layerIndex < 0)
                    throw new ArgumentException("No layer named '" + layerFullPath +
                        "'. Create it first with ensure_layer.");

                var updatedIds = new List<string>();
                foreach (var obj in objects)
                {
                    var attributes = obj.Attributes.Duplicate();
                    attributes.LayerIndex = layerIndex;
                    if (!doc.Objects.ModifyAttributes(obj, attributes, true))
                        throw new InvalidOperationException("Rhino refused to change the layer of object " + obj.Id + ".");
                    updatedIds.Add(obj.Id.ToString());
                }

                NoteModified(updatedIds);

                return (object)new Dictionary<string, object>
                {
                    { "updatedIds", updatedIds },
                    { "layerFullPath", ToolJson.Safe(layerFullPath) }
                };
            });
        }

        // ── delete_objects ────────────────────────────────────────────

        public object DeleteObjects(List<string> ids)
        {
            if (ids == null || ids.Count == 0)
                throw new ArgumentException("At least one object id is required.");

            var objects = ids.Select(_query.RequireObject).ToList();

            return InUndoRecord("delete_objects", doc =>
            {
                var deleted = new List<string>();
                var failed = new List<string>();

                foreach (var obj in objects)
                {
                    if (doc.Objects.Delete(obj.Id, true)) deleted.Add(obj.Id.ToString());
                    else failed.Add(obj.Id.ToString());
                }

                return (object)new Dictionary<string, object>
                {
                    { "deletedIds", deleted },
                    { "failedIds", failed },
                    { "notes", failed.Count == 0
                        ? "All requested objects were deleted."
                        : "Some objects could not be deleted — they may be locked or on a locked layer." }
                };
            });
        }

        // ── set_object_tags ───────────────────────────────────────────

        public object SetObjectTags(List<string> ids, Dictionary<string, string> tags)
        {
            if (ids == null || ids.Count == 0)
                throw new ArgumentException("At least one object id is required.");
            if (tags == null || tags.Count == 0)
                throw new ArgumentException("At least one tag is required.");

            var objects = ids.Select(_query.RequireObject).ToList();
            var tagService = RhinoClaudePlugin.Instance?.TagService;
            if (tagService == null)
                throw new InvalidOperationException("TagService is unavailable.");

            return InUndoRecord("set_object_tags", doc =>
            {
                var updatedIds = new List<string>();
                var errors = new List<string>();

                foreach (var obj in objects)
                {
                    var objErrors = tagService.SetTags(doc, obj, tags);
                    if (objErrors.Count == 0) updatedIds.Add(obj.Id.ToString());
                    else errors.AddRange(objErrors);
                }

                NoteModified(updatedIds);

                return (object)new Dictionary<string, object>
                {
                    { "updatedIds", updatedIds },
                    { "errors", errors.Distinct().ToList() }
                };
            });
        }

        // ── shared ────────────────────────────────────────────────────

        private static ObjectAttributes BuildAttributes(RhinoDoc doc, string layerFullPath, string name)
        {
            var attributes = doc.CreateDefaultAttributes();

            if (!string.IsNullOrWhiteSpace(layerFullPath))
            {
                int index = doc.Layers.FindByFullPath(layerFullPath, -1);
                if (index < 0)
                    throw new ArgumentException("No layer named '" + layerFullPath +
                        "'. Create it first with ensure_layer.");
                attributes.LayerIndex = index;
            }

            if (!string.IsNullOrWhiteSpace(name))
                attributes.Name = name;

            return attributes;
        }
    }
}
