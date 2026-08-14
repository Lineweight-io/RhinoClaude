using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using RhinoClaude.Schema;

namespace RhinoClaude.Services
{
    /// <summary>
    /// Service for reading, writing, querying, and validating
    /// RC: semantic tags on Rhino objects via User Text.
    /// </summary>
    public class TagService
    {
        // ── Write ─────────────────────────────────────────────────────

        /// <summary>
        /// Set a single tag on a Rhino object. Validates the key and value.
        /// Returns null on success, or an error message on failure.
        /// </summary>
        public string SetTag(RhinoDoc doc, RhinoObject obj, string key, string value)
        {
            string resolvedKey = TagSchema.ResolveKey(key);
            if (resolvedKey == null)
                return string.Format("Unknown tag key: '{0}'", key);

            string error = TagSchema.ValidateValue(resolvedKey, value);
            if (error != null)
                return error;

            string normalizedValue = TagSchema.NormalizeValue(resolvedKey, value);

            obj.Attributes.SetUserString(resolvedKey, normalizedValue);
            obj.CommitChanges();
            return null;
        }

        /// <summary>
        /// Set multiple tags on a Rhino object at once.
        /// Returns a list of error messages (empty if all succeeded).
        /// </summary>
        public List<string> SetTags(RhinoDoc doc, RhinoObject obj, Dictionary<string, string> tags)
        {
            var errors = new List<string>();

            foreach (var kvp in tags)
            {
                string resolvedKey = TagSchema.ResolveKey(kvp.Key);
                if (resolvedKey == null)
                {
                    errors.Add(string.Format("Unknown tag key: '{0}'", kvp.Key));
                    continue;
                }

                string error = TagSchema.ValidateValue(resolvedKey, kvp.Value);
                if (error != null)
                {
                    errors.Add(error);
                    continue;
                }

                string normalizedValue = TagSchema.NormalizeValue(resolvedKey, kvp.Value);
                obj.Attributes.SetUserString(resolvedKey, normalizedValue);
            }

            obj.CommitChanges();
            return errors;
        }

        /// <summary>
        /// Remove a specific tag from an object.
        /// </summary>
        public bool RemoveTag(RhinoDoc doc, RhinoObject obj, string key)
        {
            string resolvedKey = TagSchema.ResolveKey(key);
            if (resolvedKey == null)
                return false;

            obj.Attributes.SetUserString(resolvedKey, null);
            obj.CommitChanges();
            return true;
        }

        /// <summary>
        /// Remove all RC: tags from an object.
        /// </summary>
        public int ClearAllTags(RhinoDoc doc, RhinoObject obj)
        {
            int count = 0;
            foreach (var key in TagSchema.AllKeys)
            {
                string existing = obj.Attributes.GetUserString(key);
                if (existing != null)
                {
                    obj.Attributes.SetUserString(key, null);
                    count++;
                }
            }
            if (count > 0)
                obj.CommitChanges();
            return count;
        }

        // ── Read ──────────────────────────────────────────────────────

        /// <summary>
        /// Get a single tag value from an object. Returns null if not set.
        /// </summary>
        public string GetTag(RhinoObject obj, string key)
        {
            string resolvedKey = TagSchema.ResolveKey(key);
            if (resolvedKey == null)
                return null;

            string value = obj.Attributes.GetUserString(resolvedKey);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>
        /// Get all RC: tags on an object as a dictionary.
        /// Only returns keys that have values set.
        /// </summary>
        public Dictionary<string, string> GetAllTags(RhinoObject obj)
        {
            var tags = new Dictionary<string, string>();
            foreach (var key in TagSchema.AllKeys)
            {
                string value = obj.Attributes.GetUserString(key);
                if (!string.IsNullOrEmpty(value))
                    tags[key] = value;
            }
            return tags;
        }

        /// <summary>
        /// Check whether an object has any RC: tags.
        /// </summary>
        public bool HasAnyTags(RhinoObject obj)
        {
            foreach (var key in TagSchema.AllKeys)
            {
                string value = obj.Attributes.GetUserString(key);
                if (!string.IsNullOrEmpty(value))
                    return true;
            }
            return false;
        }

        // ── Query ─────────────────────────────────────────────────────

        /// <summary>
        /// Find all objects in the document that have a specific tag value.
        /// </summary>
        public List<RhinoObject> FindByTag(RhinoDoc doc, string key, string value)
        {
            string resolvedKey = TagSchema.ResolveKey(key);
            if (resolvedKey == null)
                return new List<RhinoObject>();

            string normalizedValue = TagSchema.NormalizeValue(resolvedKey, value);

            return doc.Objects
                .Where(o => !o.IsDeleted)
                .Where(o =>
                {
                    string v = o.Attributes.GetUserString(resolvedKey);
                    return string.Equals(v, normalizedValue, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }

        /// <summary>
        /// Find all objects that have ANY value set for a given key.
        /// </summary>
        public List<RhinoObject> FindByKey(RhinoDoc doc, string key)
        {
            string resolvedKey = TagSchema.ResolveKey(key);
            if (resolvedKey == null)
                return new List<RhinoObject>();

            return doc.Objects
                .Where(o => !o.IsDeleted)
                .Where(o => !string.IsNullOrEmpty(o.Attributes.GetUserString(resolvedKey)))
                .ToList();
        }

        /// <summary>
        /// Find all objects that match ALL of the specified tag criteria.
        /// </summary>
        public List<RhinoObject> FindByMultipleTags(RhinoDoc doc, Dictionary<string, string> criteria)
        {
            var resolved = new Dictionary<string, string>();
            foreach (var kvp in criteria)
            {
                string resolvedKey = TagSchema.ResolveKey(kvp.Key);
                if (resolvedKey == null)
                    continue;
                resolved[resolvedKey] = TagSchema.NormalizeValue(resolvedKey, kvp.Value);
            }

            if (resolved.Count == 0)
                return new List<RhinoObject>();

            return doc.Objects
                .Where(o => !o.IsDeleted)
                .Where(o => resolved.All(kvp =>
                    string.Equals(
                        o.Attributes.GetUserString(kvp.Key),
                        kvp.Value,
                        StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        /// <summary>
        /// Get distinct values used for a specific tag key across the document.
        /// </summary>
        public List<string> GetDistinctValues(RhinoDoc doc, string key)
        {
            string resolvedKey = TagSchema.ResolveKey(key);
            if (resolvedKey == null)
                return new List<string>();

            return doc.Objects
                .Where(o => !o.IsDeleted)
                .Select(o => o.Attributes.GetUserString(resolvedKey))
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v)
                .ToList();
        }

        // ── Validation / Audit ────────────────────────────────────────

        /// <summary>
        /// Audit all objects in the document for tagging completeness.
        /// Returns a structured report.
        /// </summary>
        public TagAuditReport AuditDocument(RhinoDoc doc)
        {
            var report = new TagAuditReport();

            var allObjects = doc.Objects.Where(o => !o.IsDeleted).ToList();
            report.TotalObjects = allObjects.Count;

            foreach (var obj in allObjects)
            {
                var tags = GetAllTags(obj);

                if (tags.Count == 0)
                {
                    report.UntaggedObjects.Add(obj);
                    continue;
                }

                report.TaggedObjects++;

                // Check for minimum required tags (ElementType is essential)
                if (!tags.ContainsKey(TagSchema.ElementType))
                {
                    report.MissingElementType.Add(obj);
                }

                // Track tag usage counts
                foreach (var kvp in tags)
                {
                    if (!report.TagUsageCounts.ContainsKey(kvp.Key))
                        report.TagUsageCounts[kvp.Key] = 0;
                    report.TagUsageCounts[kvp.Key]++;

                    // Track value distribution
                    string valueKey = kvp.Key + "=" + kvp.Value;
                    if (!report.ValueDistribution.ContainsKey(valueKey))
                        report.ValueDistribution[valueKey] = 0;
                    report.ValueDistribution[valueKey]++;
                }
            }

            return report;
        }

        // ── Formatting ────────────────────────────────────────────────

        /// <summary>
        /// Format an object's tags as a readable string for display.
        /// </summary>
        public string FormatObjectTags(RhinoObject obj)
        {
            var tags = GetAllTags(obj);
            if (tags.Count == 0)
                return "  (no RC: tags)";

            var sb = new StringBuilder();
            foreach (var kvp in tags)
            {
                string displayName = TagSchema.DisplayNames.GetValueOrDefault(kvp.Key, kvp.Key);
                sb.AppendLine(string.Format("  {0}: {1}", displayName, kvp.Value));
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Format tags as a compact single-line summary.
        /// </summary>
        public string FormatTagsSummary(RhinoObject obj)
        {
            var tags = GetAllTags(obj);
            if (tags.Count == 0)
                return "(untagged)";

            var parts = new List<string>();
            // Show the most important tags first
            string[] priorityKeys = { TagSchema.ElementType, TagSchema.AssemblyType, TagSchema.FireRating, TagSchema.IntExt, TagSchema.Level };

            foreach (var key in priorityKeys)
            {
                if (tags.TryGetValue(key, out string val))
                    parts.Add(val);
            }

            return string.Join(" | ", parts);
        }
    }

    /// <summary>
    /// Result of a document-wide tag audit.
    /// </summary>
    public class TagAuditReport
    {
        public int TotalObjects { get; set; }
        public int TaggedObjects { get; set; }
        public List<RhinoObject> UntaggedObjects { get; set; } = new List<RhinoObject>();
        public List<RhinoObject> MissingElementType { get; set; } = new List<RhinoObject>();
        public Dictionary<string, int> TagUsageCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> ValueDistribution { get; set; } = new Dictionary<string, int>();

        public string ToReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine("  RhinoClaude Tag Audit Report");
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine(string.Format("  Total objects:    {0}", TotalObjects));
            sb.AppendLine(string.Format("  Tagged:           {0}", TaggedObjects));
            sb.AppendLine(string.Format("  Untagged:         {0}", UntaggedObjects.Count));
            sb.AppendLine(string.Format("  Missing type:     {0}  (tagged but no ElementType)", MissingElementType.Count));
            sb.AppendLine();

            if (TagUsageCounts.Count > 0)
            {
                sb.AppendLine("  ── Tag Usage ──");
                foreach (var kvp in TagUsageCounts.OrderByDescending(x => x.Value))
                {
                    string display = TagSchema.DisplayNames.GetValueOrDefault(kvp.Key, kvp.Key);
                    sb.AppendLine(string.Format("    {0}: {1} objects", display, kvp.Value));
                }
                sb.AppendLine();
            }

            if (ValueDistribution.Count > 0)
            {
                sb.AppendLine("  ── Value Distribution ──");
                foreach (var kvp in ValueDistribution.OrderByDescending(x => x.Value))
                {
                    sb.AppendLine(string.Format("    {0}: {1}", kvp.Key, kvp.Value));
                }
                sb.AppendLine();
            }

            if (UntaggedObjects.Count > 0)
            {
                int show = Math.Min(UntaggedObjects.Count, 20);
                sb.AppendLine(string.Format("  ── Untagged Objects (showing {0} of {1}) ──", show, UntaggedObjects.Count));
                for (int i = 0; i < show; i++)
                {
                    var obj = UntaggedObjects[i];
                    string name = obj.Name ?? "(unnamed)";
                    string layer = obj.Document?.Layers[obj.Attributes.LayerIndex]?.FullPath ?? "?";
                    sb.AppendLine(string.Format("    [{0}] {1} on layer '{2}'", obj.ObjectType, name, layer));
                }
                if (UntaggedObjects.Count > show)
                    sb.AppendLine(string.Format("    ... and {0} more.", UntaggedObjects.Count - show));
            }

            sb.AppendLine("═══════════════════════════════════════════");
            return sb.ToString();
        }
    }
}
