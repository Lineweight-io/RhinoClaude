using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RhinoClaude.Tools
{
    /// <summary>
    /// Argument reading shared by the semantic tools. Same contract as the private helpers in
    /// <see cref="Phase1Tools"/>: a missing required argument throws with a message the model
    /// can act on, and an optional one falls back rather than failing.
    /// </summary>
    public static class ToolInput
    {
        public static JsonElement Require(JsonElement input, string name)
        {
            if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                throw new ArgumentException("Required parameter '" + name + "' is missing.");
            return value;
        }

        public static bool TryGet(JsonElement input, string name, out JsonElement value)
        {
            if (input.ValueKind != JsonValueKind.Object)
            {
                value = default;
                return false;
            }
            return input.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
        }

        public static string RequireString(JsonElement input, string name)
        {
            var value = Require(input, name);
            if (value.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Parameter '" + name + "' must be a string.");
            return value.GetString();
        }

        public static string String(JsonElement input, string name, string fallback = null) =>
            TryGet(input, name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : fallback;

        public static bool Bool(JsonElement input, string name, bool fallback)
        {
            if (!TryGet(input, name, out var value)) return fallback;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            return fallback;
        }

        public static int Int(JsonElement input, string name, int fallback) =>
            TryGet(input, name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int parsed)
                ? parsed
                : fallback;

        public static double RequireDouble(JsonElement input, string name)
        {
            var value = Require(input, name);
            if (value.ValueKind != JsonValueKind.Number)
                throw new ArgumentException("Parameter '" + name + "' must be a number.");
            return value.GetDouble();
        }

        public static double Double(JsonElement input, string name, double fallback) =>
            TryGet(input, name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : fallback;

        public static double? NullableDouble(JsonElement input, string name) =>
            TryGet(input, name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : (double?)null;

        public static List<string> StringList(JsonElement input, string name)
        {
            if (!TryGet(input, name, out var value) || value.ValueKind != JsonValueKind.Array)
                return new List<string>();

            return value.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString())
                        .ToList();
        }

        public static List<double> DoubleList(JsonElement input, string name)
        {
            if (!TryGet(input, name, out var value) || value.ValueKind != JsonValueKind.Array)
                return new List<double>();

            return value.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.Number)
                        .Select(e => e.GetDouble())
                        .ToList();
        }
    }
}
