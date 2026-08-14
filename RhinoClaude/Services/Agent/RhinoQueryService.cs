using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoClaude.Agent;
using RhinoClaude.Schema;

namespace RhinoClaude.Services.Agent
{
    /// <summary>
    /// Read-only document access for the query tools. Nothing here opens an undo record —
    /// that is the boundary between this service and <see cref="RhinoMutationService"/>.
    ///
    /// UI-thread only; the dispatcher guarantees that.
    /// </summary>
    public sealed class RhinoQueryService
    {
        private readonly uint _docSerialNumber;

        public RhinoQueryService(RhinoDoc doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _docSerialNumber = doc.RuntimeSerialNumber;
        }

        public RhinoDoc Doc
        {
            get
            {
                var doc = RhinoDoc.FromRuntimeSerialNumber(_docSerialNumber);
                if (doc == null) throw new InvalidOperationException("The Rhino document is no longer open.");
                return doc;
            }
        }

        // ── Shared serialization helpers ──────────────────────────────

        public static double[] Pt(Point3d p) => new[] { Round(p.X), Round(p.Y), Round(p.Z) };
        public static double[] Vec(Vector3d v) => new[] { Round(v.X), Round(v.Y), Round(v.Z) };

        public static double Round(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : Math.Round(v, 6);

        public static object Bbox(BoundingBox box)
        {
            if (!box.IsValid) return null;
            return new Dictionary<string, object>
            {
                { "min", Pt(box.Min) },
                { "max", Pt(box.Max) },
                { "size", new[] { Round(box.Max.X - box.Min.X), Round(box.Max.Y - box.Min.Y), Round(box.Max.Z - box.Min.Z) } }
            };
        }

        public static Point3d ReadPoint(System.Text.Json.JsonElement element)
        {
            if (element.ValueKind != System.Text.Json.JsonValueKind.Array || element.GetArrayLength() < 3)
                throw new ArgumentException("Expected a coordinate as [x, y, z].");
            var values = element.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            return new Point3d(values[0], values[1], values[2]);
        }

        public static Vector3d ReadVector(System.Text.Json.JsonElement element)
        {
            var p = ReadPoint(element);
            return new Vector3d(p.X, p.Y, p.Z);
        }

        /// <summary>Resolve an object by GUID string, with a message the model can act on.</summary>
        public RhinoObject RequireObject(string idText)
        {
            if (!Guid.TryParse(idText, out var id))
                throw new ArgumentException("'" + idText + "' is not a valid object id (expected a GUID).");

            var obj = Doc.Objects.FindId(id);
            if (obj == null || obj.IsDeleted)
                throw new ArgumentException("No object with id " + id + " exists in the document.");

            return obj;
        }

        // ── describe_document ─────────────────────────────────────────

        public object DescribeDocument()
        {
            var doc = Doc;
            var view = doc.Views.ActiveView;
            var viewport = view?.ActiveViewport;

            return new Dictionary<string, object>
            {
                { "name", string.IsNullOrEmpty(doc.Name) ? "(unsaved)" : ToolJson.Safe(doc.Name) },
                { "path", ToolJson.Safe(doc.Path ?? string.Empty) },
                { "units", doc.ModelUnitSystem.ToString() },
                { "tolerance", Round(doc.ModelAbsoluteTolerance) },
                { "angleToleranceDegrees", Round(doc.ModelAngleToleranceDegrees) },
                { "layerCount", doc.Layers.Count(l => !l.IsDeleted) },
                { "objectCount", doc.Objects.Count(o => !o.IsDeleted) },
                { "selectionCount", doc.Objects.GetSelectedObjects(false, false).Count() },
                { "activeViewName", viewport == null ? null : ToolJson.Safe(viewport.Name) },
                { "activeViewCameraLocation", viewport == null ? null : Pt(viewport.CameraLocation) },
                { "activeViewCameraTarget", viewport == null ? null : Pt(viewport.CameraTarget) }
            };
        }

        // ── list_layers ───────────────────────────────────────────────

        public object ListLayers(bool includeHidden, int limit)
        {
            var doc = Doc;
            if (limit <= 0) limit = PayloadCaps.ListLayersDefaultLimit;

            var matched = doc.Layers
                .Where(l => !l.IsDeleted)
                .Where(l => includeHidden || l.IsVisible)
                .ToList();

            var layers = matched.Take(limit).Select(layer => (object)new Dictionary<string, object>
            {
                { "id", layer.Id.ToString() },
                { "fullPath", ToolJson.Safe(layer.FullPath) },
                { "visible", layer.IsVisible },
                { "locked", layer.IsLocked },
                { "objectCount", doc.Objects.FindByLayer(layer)?.Length ?? 0 },
                { "colorHex", ColorHex(layer.Color) }
            }).ToList();

            var result = new Dictionary<string, object>
            {
                { "layers", layers },
                { "totalLayers", matched.Count },
                { "truncated", matched.Count > layers.Count }
            };

            if (matched.Count > layers.Count)
            {
                result["truncationNote"] =
                    "omitted " + (matched.Count - layers.Count) + " of " + matched.Count +
                    " layers — raise 'limit' if you need the rest.";
            }

            return result;
        }

        public static string ColorHex(System.Drawing.Color c) =>
            string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);

        // ── list_objects ──────────────────────────────────────────────

        public object ListObjects(string layerFullPath, string objectType, string hasTagKey, int limit)
        {
            var doc = Doc;
            var tagService = RhinoClaudePlugin.Instance?.TagService;

            IEnumerable<RhinoObject> query = doc.Objects.Where(o => !o.IsDeleted);

            if (!string.IsNullOrWhiteSpace(layerFullPath))
            {
                int layerIndex = doc.Layers.FindByFullPath(layerFullPath, -1);
                if (layerIndex < 0)
                    throw new ArgumentException("No layer named '" + layerFullPath +
                        "'. Use list_layers to see the exact full paths.");
                query = query.Where(o => o.Attributes.LayerIndex == layerIndex);
            }

            if (!string.IsNullOrWhiteSpace(objectType))
            {
                query = query.Where(o =>
                    string.Equals(o.ObjectType.ToString(), objectType, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(hasTagKey))
            {
                string resolved = TagSchema.ResolveKey(hasTagKey);
                if (resolved == null)
                    throw new ArgumentException("Unknown tag key '" + hasTagKey + "'.");
                query = query.Where(o => !string.IsNullOrEmpty(o.Attributes.GetUserString(resolved)));
            }

            var matched = query.ToList();
            if (limit <= 0) limit = PayloadCaps.ListObjectsDefaultLimit;
            if (limit > PayloadCaps.ListObjectsMaxLimit) limit = PayloadCaps.ListObjectsMaxLimit;

            var objects = matched.Take(limit).Select(o => new Dictionary<string, object>
            {
                { "id", o.Id.ToString() },
                { "type", o.ObjectType.ToString() },
                { "layer", ToolJson.Safe(doc.Layers[o.Attributes.LayerIndex]?.FullPath ?? "?") },
                { "name", ToolJson.Safe(o.Name ?? string.Empty) },
                { "bbox", Bbox(o.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset) },
                { "tags", tagService == null
                    ? new Dictionary<string, string>()
                    : tagService.GetAllTags(o).ToDictionary(kv => kv.Key, kv => ToolJson.Safe(kv.Value)) }
            }).ToList();

            var result = new Dictionary<string, object>
            {
                { "objects", objects },
                { "truncated", matched.Count > limit },
                { "totalMatched", matched.Count }
            };

            if (matched.Count > limit)
            {
                result["truncationNote"] =
                    "showing the first " + limit + " of " + matched.Count +
                    " matches — narrow the filters, or raise 'limit', if you need more.";
            }

            return result;
        }

        // ── get_object ────────────────────────────────────────────────

        /// <summary>
        /// <paramref name="facesRange"/> and <paramref name="edgesRange"/> are optional
        /// inclusive <c>[start, end]</c> pairs. Both lists are capped either way — a Brep with
        /// a few hundred faces would otherwise put ~19,000 tokens into the conversation from
        /// one call, and then re-send them on every later iteration of the turn.
        /// </summary>
        public object GetObject(string idText, bool includeSubobjects, int[] facesRange, int[] edgesRange)
        {
            var doc = Doc;
            var obj = RequireObject(idText);
            var tagService = RhinoClaudePlugin.Instance?.TagService;

            var result = new Dictionary<string, object>
            {
                { "id", obj.Id.ToString() },
                { "type", obj.ObjectType.ToString() },
                { "layer", ToolJson.Safe(doc.Layers[obj.Attributes.LayerIndex]?.FullPath ?? "?") },
                { "name", ToolJson.Safe(obj.Name ?? string.Empty) },
                { "bbox", Bbox(obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset) },
                { "tags", tagService == null
                    ? new Dictionary<string, string>()
                    : tagService.GetAllTags(obj).ToDictionary(kv => kv.Key, kv => ToolJson.Safe(kv.Value)) },
                { "geometrySummary", SummarizeGeometry(obj.Geometry) }
            };

            if (includeSubobjects && obj.Geometry is Brep brep)
            {
                var faceWindow = PayloadCaps.Resolve(brep.Faces.Count, facesRange, PayloadCaps.FacesPerCall);
                var edgeWindow = PayloadCaps.Resolve(brep.Edges.Count, edgesRange, PayloadCaps.EdgesPerCall);

                var faces = new List<object>(faceWindow.Count);
                for (int i = faceWindow.Start; i <= faceWindow.End; i++)
                {
                    var face = brep.Faces[i];
                    var area = AreaMassProperties.Compute(face);
                    var centroid = area?.Centroid ?? face.GetBoundingBox(true).Center;
                    Vector3d normal = Vector3d.Unset;
                    double u, v;
                    if (face.ClosestPoint(centroid, out u, out v))
                        normal = face.NormalAt(u, v);

                    faces.Add(new Dictionary<string, object>
                    {
                        { "index", i },
                        { "area", area == null ? (object)null : Round(area.Area) },
                        { "centroid", Pt(centroid) },
                        { "normal", normal.IsValid ? Vec(normal) : null },
                        { "isPlanar", face.IsPlanar(doc.ModelAbsoluteTolerance) }
                    });
                }

                var edges = new List<object>(edgeWindow.Count);
                for (int i = edgeWindow.Start; i <= edgeWindow.End; i++)
                {
                    var edge = brep.Edges[i];
                    edges.Add(new Dictionary<string, object>
                    {
                        { "index", i },
                        { "length", Round(edge.GetLength()) },
                        { "startPoint", Pt(edge.PointAtStart) },
                        { "endPoint", Pt(edge.PointAtEnd) },
                        { "isLinear", edge.IsLinear(doc.ModelAbsoluteTolerance) }
                    });
                }

                result["faces"] = faces;
                result["edges"] = edges;
                result["totalFaces"] = faceWindow.Total;
                result["totalEdges"] = edgeWindow.Total;
                result["facesReturned"] = faceWindow.AsRange();
                result["edgesReturned"] = edgeWindow.AsRange();
                result["truncated"] = faceWindow.Truncated || edgeWindow.Truncated;

                string note = PayloadCaps.CombineNotes(
                    PayloadCaps.NoteFor("faces", "facesRange", faceWindow, PayloadCaps.FacesPerCall),
                    PayloadCaps.NoteFor("edges", "edgesRange", edgeWindow, PayloadCaps.EdgesPerCall));
                if (note != null) result["truncationNote"] = note;
            }

            return result;
        }

        private object SummarizeGeometry(GeometryBase geometry)
        {
            if (geometry == null) return null;
            var doc = Doc;

            switch (geometry)
            {
                case Curve curve:
                    return new Dictionary<string, object>
                    {
                        { "kind", "curve" },
                        { "length", Round(curve.GetLength()) },
                        { "closed", curve.IsClosed },
                        { "planar", curve.IsPlanar(doc.ModelAbsoluteTolerance) },
                        { "degree", curve.Degree },
                        { "startPoint", Pt(curve.PointAtStart) },
                        { "endPoint", Pt(curve.PointAtEnd) }
                    };

                case Brep brep:
                    {
                        var volume = brep.IsSolid ? VolumeMassProperties.Compute(brep) : null;
                        var area = AreaMassProperties.Compute(brep);
                        return new Dictionary<string, object>
                        {
                            { "kind", "brep" },
                            { "faceCount", brep.Faces.Count },
                            { "edgeCount", brep.Edges.Count },
                            { "isSolid", brep.IsSolid },
                            { "isValid", brep.IsValid },
                            { "area", area == null ? (object)null : Round(area.Area) },
                            { "volume", volume == null ? (object)null : Round(volume.Volume) }
                        };
                    }

                case Extrusion extrusion:
                    return new Dictionary<string, object>
                    {
                        { "kind", "extrusion" },
                        { "isSolid", extrusion.IsSolid },
                        { "isValid", extrusion.IsValid }
                    };

                case Mesh mesh:
                    return new Dictionary<string, object>
                    {
                        { "kind", "mesh" },
                        { "vertexCount", mesh.Vertices.Count },
                        { "faceCount", mesh.Faces.Count },
                        { "isClosed", mesh.IsClosed }
                    };

                case Rhino.Geometry.Point point:
                    return new Dictionary<string, object>
                    {
                        { "kind", "point" },
                        { "location", Pt(point.Location) }
                    };

                case InstanceReferenceGeometry instance:
                    return new Dictionary<string, object>
                    {
                        { "kind", "blockInstance" },
                        { "definitionId", instance.ParentIdefId.ToString() }
                    };

                default:
                    return new Dictionary<string, object> { { "kind", geometry.GetType().Name } };
            }
        }

        // ── get_selection ─────────────────────────────────────────────

        public object GetSelection()
        {
            var selected = Doc.Objects.GetSelectedObjects(false, false).ToList();
            return new Dictionary<string, object>
            {
                { "ids", selected.Select(o => o.Id.ToString()).ToList() },
                { "count", selected.Count }
            };
        }

        // ── list_named_views ──────────────────────────────────────────

        public object ListNamedViews()
        {
            var doc = Doc;
            var views = new List<object>();

            for (int i = 0; i < doc.NamedViews.Count; i++)
            {
                var info = doc.NamedViews[i];
                var viewport = info.Viewport;
                views.Add(new Dictionary<string, object>
                {
                    { "name", ToolJson.Safe(info.Name) },
                    { "cameraLocation", Pt(viewport.CameraLocation) },
                    { "cameraTarget", Pt(viewport.TargetPoint) },
                    { "isParallel", viewport.IsParallelProjection }
                });
            }

            return new Dictionary<string, object> { { "views", views } };
        }

        // ── list_blocks ───────────────────────────────────────────────

        public object ListBlocks()
        {
            var doc = Doc;
            var blocks = new List<object>();
            int total = 0;

            foreach (var definition in doc.InstanceDefinitions)
            {
                if (definition == null || definition.IsDeleted) continue;
                total++;
                if (blocks.Count >= PayloadCaps.ListBlocksDefaultLimit) continue;

                blocks.Add(new Dictionary<string, object>
                {
                    { "name", ToolJson.Safe(definition.Name) },
                    { "id", definition.Id.ToString() },
                    { "instanceCount", definition.GetReferences(0)?.Length ?? 0 },
                    { "objectCount", definition.GetObjectIds()?.Length ?? 0 }
                });
            }

            return new Dictionary<string, object>
            {
                { "blocks", blocks },
                { "totalBlocks", total },
                { "truncated", total > blocks.Count }
            };
        }

        /// <summary>Union bbox of a set of ids — used to frame view captures on the work area.</summary>
        public BoundingBox BoundingBoxOf(IEnumerable<Guid> ids)
        {
            var doc = Doc;
            var box = BoundingBox.Unset;
            foreach (var id in ids)
            {
                var obj = doc.Objects.FindId(id);
                if (obj?.Geometry == null) continue;
                var b = obj.Geometry.GetBoundingBox(true);
                if (b.IsValid) box.Union(b);
            }
            return box;
        }

        public BoundingBox DocumentExtents()
        {
            var box = BoundingBox.Unset;
            foreach (var obj in Doc.Objects.Where(o => !o.IsDeleted))
            {
                if (obj.Geometry == null) continue;
                var b = obj.Geometry.GetBoundingBox(true);
                if (b.IsValid) box.Union(b);
            }
            return box;
        }
    }
}
