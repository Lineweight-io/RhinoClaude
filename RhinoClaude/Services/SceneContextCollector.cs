using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoClaude.Schema;

namespace RhinoClaude.Services
{
    /// <summary>
    /// Collects information about the active Rhino document and selected objects
    /// to provide as context to Claude. Includes semantic tag data when present.
    /// </summary>
    public static class SceneContextCollector
    {
        /// <summary>
        /// Gather a text summary of the current scene state.
        /// This gets sent to Claude so it understands what the user is working on.
        /// </summary>
        public static string CollectContext(RhinoDoc doc, bool includeSelectedOnly = false)
        {
            if (doc == null)
                return "No active Rhino document.";

            var sb = new StringBuilder();

            // Document info
            sb.AppendLine(string.Format("Document: {0}", string.IsNullOrEmpty(doc.Name) ? "(unsaved)" : doc.Name));
            sb.AppendLine(string.Format("Units: {0}", doc.ModelUnitSystem));
            sb.AppendLine(string.Format("Absolute tolerance: {0}", doc.ModelAbsoluteTolerance));
            sb.AppendLine();

            // Layer summary
            sb.AppendLine("--- Layers ---");
            var layers = doc.Layers.Where(l => !l.IsDeleted).ToList();
            foreach (var layer in layers)
            {
                int objectCount = doc.Objects.FindByLayer(layer).Length;
                string visibility = layer.IsVisible ? "visible" : "hidden";
                string locked = layer.IsLocked ? ", locked" : "";
                sb.AppendLine(string.Format("  {0} ({1} objects, {2}{3})",
                    layer.FullPath, objectCount, visibility, locked));
            }
            sb.AppendLine();

            // Object summary
            var allObjects = includeSelectedOnly
                ? doc.Objects.GetSelectedObjects(false, false).ToList()
                : doc.Objects.Where(o => !o.IsDeleted).ToList();

            string scope = includeSelectedOnly ? "Selected" : "All";
            sb.AppendLine(string.Format("--- {0} Objects ({1} total) ---", scope, allObjects.Count));

            // Group by object type for a clean summary
            var grouped = allObjects
                .GroupBy(o => o.ObjectType)
                .OrderByDescending(g => g.Count());

            foreach (var group in grouped)
            {
                sb.AppendLine(string.Format("  {0}: {1}", group.Key, group.Count()));
            }
            sb.AppendLine();

            // ── Semantic tag summary ─────────────────────────────────
            var taggedObjects = allObjects
                .Where(o => TagSchema.AllKeys.Any(k => !string.IsNullOrEmpty(o.Attributes.GetUserString(k))))
                .ToList();

            if (taggedObjects.Count > 0)
            {
                sb.AppendLine(string.Format("--- Semantic Tags ({0} tagged objects) ---", taggedObjects.Count));

                // Summarize by ElementType
                var byElementType = taggedObjects
                    .Select(o => o.Attributes.GetUserString(TagSchema.ElementType))
                    .Where(v => !string.IsNullOrEmpty(v))
                    .GroupBy(v => v)
                    .OrderByDescending(g => g.Count());

                if (byElementType.Any())
                {
                    sb.AppendLine("  By Element Type:");
                    foreach (var group in byElementType)
                    {
                        sb.AppendLine(string.Format("    {0}: {1}", group.Key, group.Count()));
                    }
                }

                // Summarize fire ratings in use
                var fireRatings = taggedObjects
                    .Select(o => o.Attributes.GetUserString(TagSchema.FireRating))
                    .Where(v => !string.IsNullOrEmpty(v) && v != "None")
                    .GroupBy(v => v)
                    .OrderByDescending(g => g.Count());

                if (fireRatings.Any())
                {
                    sb.AppendLine("  Fire Ratings:");
                    foreach (var group in fireRatings)
                    {
                        sb.AppendLine(string.Format("    {0}: {1} objects", group.Key, group.Count()));
                    }
                }

                // Summarize levels
                var levels = taggedObjects
                    .Select(o => o.Attributes.GetUserString(TagSchema.Level))
                    .Where(v => !string.IsNullOrEmpty(v))
                    .GroupBy(v => v)
                    .OrderBy(g => g.Key);

                if (levels.Any())
                {
                    sb.AppendLine("  Levels:");
                    foreach (var group in levels)
                    {
                        sb.AppendLine(string.Format("    {0}: {1} objects", group.Key, group.Count()));
                    }
                }

                int untaggedCount = allObjects.Count - taggedObjects.Count;
                if (untaggedCount > 0)
                {
                    sb.AppendLine(string.Format("  Untagged: {0} objects", untaggedCount));
                }

                sb.AppendLine();
            }

            // Detailed info for selected objects (up to 20, to avoid overwhelming Claude)
            var selectedObjects = doc.Objects.GetSelectedObjects(false, false).ToList();
            if (selectedObjects.Count > 0)
            {
                int limit = Math.Min(selectedObjects.Count, 20);
                sb.AppendLine(string.Format("--- Selected Object Details ({0} selected, showing {1}) ---",
                    selectedObjects.Count, limit));

                for (int i = 0; i < limit; i++)
                {
                    var obj = selectedObjects[i];
                    sb.AppendLine(DescribeObject(obj));
                }

                if (selectedObjects.Count > limit)
                    sb.AppendLine(string.Format("  ... and {0} more selected objects.", selectedObjects.Count - limit));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Get a brief summary suitable for a quick context check.
        /// </summary>
        public static string CollectBriefContext(RhinoDoc doc)
        {
            if (doc == null) return "No active document.";

            int totalObjects = doc.Objects.Count(o => !o.IsDeleted);
            int selectedCount = doc.Objects.GetSelectedObjects(false, false).Count();
            int layerCount = doc.Layers.Count(l => !l.IsDeleted);

            // Count tagged objects
            int taggedCount = doc.Objects
                .Where(o => !o.IsDeleted)
                .Count(o => TagSchema.AllKeys.Any(k => !string.IsNullOrEmpty(o.Attributes.GetUserString(k))));

            return string.Format(
                "Document: {0}, Units: {1}, {2} objects ({3} tagged), {4} selected, {5} layers.",
                doc.Name ?? "(unsaved)", doc.ModelUnitSystem,
                totalObjects, taggedCount, selectedCount, layerCount);
        }

        /// <summary>
        /// Describe a single Rhino object in human-readable text.
        /// Now includes semantic tag data when present.
        /// </summary>
        private static string DescribeObject(RhinoObject obj)
        {
            var sb = new StringBuilder();
            sb.Append(string.Format("  [{0}] ", obj.ObjectType));

            // Name and layer
            string name = obj.Name ?? "(unnamed)";
            string layer = obj.Document?.Layers[obj.Attributes.LayerIndex]?.FullPath ?? "?";
            sb.Append(string.Format("Name: {0}, Layer: {1}", name, layer));

            // Geometry-specific details
            var geom = obj.Geometry;
            if (geom != null)
            {
                var bbox = geom.GetBoundingBox(true);
                if (bbox.IsValid)
                {
                    var size = bbox.Max - bbox.Min;
                    sb.Append(string.Format(", BBox: ({0:F2} x {1:F2} x {2:F2})", size.X, size.Y, size.Z));
                }

                switch (geom)
                {
                    case Curve crv:
                        sb.Append(string.Format(", Length: {0:F2}", crv.GetLength()));
                        sb.Append(crv.IsClosed ? ", Closed" : ", Open");
                        if (crv.IsLinear()) sb.Append(", Linear");
                        else if (crv.IsArc()) sb.Append(", Arc");
                        else if (crv.IsCircle()) sb.Append(", Circle");
                        else if (crv.IsPolyline()) sb.Append(", Polyline");
                        break;

                    case Brep brep:
                        sb.Append(string.Format(", Faces: {0}, Edges: {1}", brep.Faces.Count, brep.Edges.Count));
                        sb.Append(brep.IsSolid ? ", Solid" : ", Open");
                        break;

                    case Mesh mesh:
                        sb.Append(string.Format(", Vertices: {0}, Faces: {1}", mesh.Vertices.Count, mesh.Faces.Count));
                        break;

                    case Extrusion ext:
                        sb.Append(string.Format(", Height: {0:F2}", ext.PathLineCurve().GetLength()));
                        sb.Append(ext.IsSolid ? ", Solid" : ", Open");
                        break;

                    case Surface srf:
                        sb.Append(", Surface");
                        break;

                    case Point pt:
                        var loc = pt.Location;
                        sb.Append(string.Format(", Location: ({0:F2}, {1:F2}, {2:F2})", loc.X, loc.Y, loc.Z));
                        break;
                }
            }

            // ── Semantic tags ────────────────────────────────────────
            var tagParts = new List<string>();
            foreach (var key in TagSchema.AllKeys)
            {
                string value = obj.Attributes.GetUserString(key);
                if (!string.IsNullOrEmpty(value))
                {
                    string shortKey = key.Replace(TagSchema.Prefix, "");
                    tagParts.Add(string.Format("{0}={1}", shortKey, value));
                }
            }

            if (tagParts.Count > 0)
            {
                sb.Append(string.Format(", Tags: [{0}]", string.Join(", ", tagParts)));
            }

            return sb.ToString();
        }
    }
}
