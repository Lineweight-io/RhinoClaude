using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Schema
{
    /// <summary>
    /// Defines the RhinoClaude semantic tagging schema.
    /// All tag keys use the "RC:" namespace prefix to avoid conflicts
    /// with other plugins or user data.
    /// 
    /// Tags are stored as Rhino User Text (key-value string pairs) on objects.
    /// </summary>
    public static class TagSchema
    {
        // ── Namespace prefix ──────────────────────────────────────────
        public const string Prefix = "RC:";

        // ── Core tag keys ─────────────────────────────────────────────
        public const string ElementType     = "RC:ElementType";
        public const string AssemblyType    = "RC:AssemblyType";
        public const string FireRating      = "RC:FireRating";
        public const string IntExt          = "RC:IntExt";
        public const string Level           = "RC:Level";
        public const string MaterialLayers  = "RC:MaterialLayers";

        // ── Extended tag keys ─────────────────────────────────────────
        public const string Description     = "RC:Description";
        public const string Mark            = "RC:Mark";          // unique instance mark (e.g., W-01, D-14)
        public const string Zone            = "RC:Zone";          // building zone / area (e.g., Core, Wing A)
        public const string SystemType      = "RC:SystemType";    // structural, mechanical, plumbing, etc.

        /// <summary>
        /// All recognized tag keys in display order.
        /// </summary>
        public static readonly string[] AllKeys = new[]
        {
            ElementType,
            AssemblyType,
            FireRating,
            IntExt,
            Level,
            MaterialLayers,
            Description,
            Mark,
            Zone,
            SystemType
        };

        /// <summary>
        /// Human-readable display names for each key (without the RC: prefix).
        /// </summary>
        public static readonly Dictionary<string, string> DisplayNames = new Dictionary<string, string>
        {
            { ElementType,    "Element Type" },
            { AssemblyType,   "Assembly Type" },
            { FireRating,     "Fire Rating" },
            { IntExt,         "Interior/Exterior" },
            { Level,          "Level" },
            { MaterialLayers, "Material Layers" },
            { Description,    "Description" },
            { Mark,           "Mark" },
            { Zone,           "Zone" },
            { SystemType,     "System Type" }
        };

        // ── Allowed values (where constrained) ───────────────────────

        /// <summary>
        /// Allowed values for RC:ElementType.
        /// </summary>
        public static readonly string[] ElementTypes = new[]
        {
            "Wall", "Floor", "Roof", "Ceiling",
            "Door", "Window", "Column", "Beam",
            "Stair", "Ramp", "Railing", "Curtain Wall",
            "Slab", "Foundation", "Footing",
            "Parapet", "Soffit", "Canopy",
            "Casework", "Furniture", "Equipment",
            "Site", "Other"
        };

        /// <summary>
        /// Allowed values for RC:FireRating.
        /// </summary>
        public static readonly string[] FireRatings = new[]
        {
            "None", "20-min", "30-min", "45-min",
            "1-hr", "1.5-hr", "2-hr", "3-hr", "4-hr"
        };

        /// <summary>
        /// Allowed values for RC:IntExt.
        /// </summary>
        public static readonly string[] IntExtValues = new[]
        {
            "Interior", "Exterior", "Both"
        };

        /// <summary>
        /// Allowed values for RC:SystemType.
        /// </summary>
        public static readonly string[] SystemTypes = new[]
        {
            "Architectural", "Structural", "Mechanical",
            "Electrical", "Plumbing", "Fire Protection",
            "Civil", "Landscape", "Other"
        };

        /// <summary>
        /// Map of keys that have constrained value sets.
        /// Keys not in this map accept freeform string values.
        /// </summary>
        public static readonly Dictionary<string, string[]> ConstrainedValues = new Dictionary<string, string[]>
        {
            { ElementType, ElementTypes },
            { FireRating,  FireRatings },
            { IntExt,      IntExtValues },
            { SystemType,  SystemTypes }
        };

        // ── Validation ────────────────────────────────────────────────

        /// <summary>
        /// Check whether a key is a recognized RC: tag key.
        /// </summary>
        public static bool IsValidKey(string key)
        {
            return AllKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validate a value for a given key. Returns null if valid,
        /// or an error message string if invalid.
        /// </summary>
        public static string ValidateValue(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Value cannot be empty.";

            // For constrained keys, check against allowed values (case-insensitive)
            if (ConstrainedValues.TryGetValue(key, out string[] allowed))
            {
                string match = allowed.FirstOrDefault(a =>
                    string.Equals(a, value, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    return string.Format("Invalid value '{0}' for {1}. Allowed: {2}",
                        value, DisplayNames.GetValueOrDefault(key, key),
                        string.Join(", ", allowed));
                }
            }

            return null; // valid
        }

        /// <summary>
        /// Normalize a value for a constrained key to its canonical casing.
        /// Returns the original value for freeform keys.
        /// </summary>
        public static string NormalizeValue(string key, string value)
        {
            if (ConstrainedValues.TryGetValue(key, out string[] allowed))
            {
                string match = allowed.FirstOrDefault(a =>
                    string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
            return value.Trim();
        }

        /// <summary>
        /// Try to resolve a user-typed key to the canonical RC: key.
        /// Accepts "ElementType", "RC:ElementType", "element type", "element_type", etc.
        /// Returns null if no match found.
        /// </summary>
        public static string ResolveKey(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            input = input.Trim();

            // Direct match
            if (IsValidKey(input))
                return AllKeys.First(k => string.Equals(k, input, StringComparison.OrdinalIgnoreCase));

            // Try adding prefix
            string withPrefix = Prefix + input;
            if (IsValidKey(withPrefix))
                return AllKeys.First(k => string.Equals(k, withPrefix, StringComparison.OrdinalIgnoreCase));

            // Fuzzy match against display names (case-insensitive, ignore spaces/underscores)
            string normalized = input.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
            foreach (var kvp in DisplayNames)
            {
                string displayNorm = kvp.Value.Replace(" ", "").Replace("/", "").ToLowerInvariant();
                string keyNorm = kvp.Key.Replace(Prefix, "").ToLowerInvariant();

                if (normalized == displayNorm || normalized == keyNorm)
                    return kvp.Key;
            }

            return null;
        }

        /// <summary>
        /// Returns a formatted string describing the full schema for use in prompts to Claude.
        /// </summary>
        public static string GetSchemaDescription()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("RhinoClaude Semantic Tag Schema (stored as Rhino User Text):");
            sb.AppendLine();

            foreach (var key in AllKeys)
            {
                string display = DisplayNames.GetValueOrDefault(key, key);
                sb.Append(string.Format("  {0} ({1})", key, display));

                if (ConstrainedValues.TryGetValue(key, out string[] allowed))
                {
                    sb.Append(string.Format(" — allowed values: [{0}]", string.Join(", ", allowed)));
                }
                else
                {
                    sb.Append(" — freeform text");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }

    // ── Extension helper for .NET Framework compatibility ─────────
    internal static class DictionaryExtensions
    {
        public static TValue GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> dict, TKey key, TValue defaultValue = default)
        {
            return dict.TryGetValue(key, out TValue value) ? value : defaultValue;
        }
    }
}
