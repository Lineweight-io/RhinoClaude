using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RhinoClaude.Semantic
{
    /// <summary>One learned layer → element mapping, as produced by ClaudeLearnNamingConvention.</summary>
    public sealed class LayerConventionEntry
    {
        /// <summary>Layer full path or a single segment. Matching is exact on either.</summary>
        public string Layer { get; set; }
        /// <summary>One of <see cref="SemanticVocabulary.AllTypes"/>, or null for "not architectural".</summary>
        public string ElementType { get; set; }
        public string Subtype { get; set; }
        public double? Elevation { get; set; }
        /// <summary>Free text from the learn pass. Surfaced in the confirm dialog, never to the model.</summary>
        public string Note { get; set; }
    }

    /// <summary>
    /// A firm's own layer convention, learned once and reused. Step 2 of the plan's four-step
    /// resolution rule (§5.2) — trumped only by an explicit user-data tag on the object.
    ///
    /// Persisted as JSON under <c>RhinoClaude:LayerConvention:v1</c> in <c>RhinoDoc.Strings</c>
    /// (doc scope) and/or plugin settings (firm scope). Rhino-free so the round-trip is testable.
    /// </summary>
    public sealed class LayerConventionMap
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>Firm-standard floor-to-floor, used when Levels are inferred rather than drawn
        /// (plan §5.6's Rev 2 addition). Zero or negative means "not configured".</summary>
        public double FloorToFloorDefault { get; set; }

        public List<LayerConventionEntry> Entries { get; } = new List<LayerConventionEntry>();

        /// <summary>Where this map came from — shown in the classifier's debugging surface.</summary>
        public string Source { get; set; }

        public bool IsEmpty => Entries.Count == 0;

        public void Add(string layer, string elementType, string subtype = null, double? elevation = null, string note = null)
        {
            if (string.IsNullOrWhiteSpace(layer)) return;
            var existing = Entries.FirstOrDefault(e => Same(e.Layer, layer));
            if (existing != null) Entries.Remove(existing);
            Entries.Add(new LayerConventionEntry
            {
                Layer = layer.Trim(),
                ElementType = elementType,
                Subtype = subtype,
                Elevation = elevation,
                Note = note
            });
        }

        /// <summary>
        /// Resolve a layer path. Exact full-path entries win; then leaf-segment entries; then
        /// any ancestor segment, so a child layer inherits its parent's meaning. A matched
        /// entry with a null ElementType is a deliberate "not architectural" — it returns a
        /// non-match, but stops the search rather than falling through to canonical.
        /// </summary>
        public ConventionMatch Match(string layerFullPath)
        {
            if (string.IsNullOrWhiteSpace(layerFullPath) || Entries.Count == 0)
                return ConventionMatch.None;

            var direct = Entries.FirstOrDefault(e => Same(e.Layer, layerFullPath));
            if (direct != null) return ToMatch(direct);

            var segments = CanonicalConvention.Segments(layerFullPath);
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                var entry = Entries.FirstOrDefault(e => Same(e.Layer, segments[i]));
                if (entry != null) return ToMatch(entry);
            }

            return ConventionMatch.None;
        }

        /// <summary>True when the map has an opinion about this layer, including "not architectural".</summary>
        public bool Covers(string layerFullPath)
        {
            if (string.IsNullOrWhiteSpace(layerFullPath) || Entries.Count == 0) return false;
            if (Entries.Any(e => Same(e.Layer, layerFullPath))) return true;
            return CanonicalConvention.Segments(layerFullPath).Any(s => Entries.Any(e => Same(e.Layer, s)));
        }

        private static ConventionMatch ToMatch(LayerConventionEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.ElementType)) return ConventionMatch.None;
            return new ConventionMatch
            {
                ElementType = SemanticVocabulary.Normalize(entry.ElementType, SemanticVocabulary.AllTypes),
                Subtype = entry.Subtype,
                Elevation = entry.Elevation,
                ClassifiedBy = SemanticVocabulary.ByLearnedConvention,
                MatchedSegment = entry.Layer
            };
        }

        private static bool Same(string a, string b) =>
            string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        // ── JSON round-trip ───────────────────────────────────────────

        public string ToJson()
        {
            var payload = new Dictionary<string, object>
            {
                { "version", Version },
                { "floorToFloorDefault", FloorToFloorDefault },
                { "source", Source },
                { "entries", Entries.Select(e => new Dictionary<string, object>
                    {
                        { "layer", e.Layer },
                        { "elementType", e.ElementType },
                        { "subtype", e.Subtype },
                        { "elevation", e.Elevation },
                        { "note", e.Note }
                    }).ToList() }
            };
            return JsonSerializer.Serialize(payload);
        }

        /// <summary>Parse a persisted map. Never throws — a corrupt or future-version string
        /// yields null and the caller falls through to the canonical convention.</summary>
        public static LayerConventionMap FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;

                    var map = new LayerConventionMap();

                    if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number)
                        map.Version = version.GetInt32();
                    if (map.Version > CurrentVersion) return null;

                    if (root.TryGetProperty("floorToFloorDefault", out var ftf) && ftf.ValueKind == JsonValueKind.Number)
                        map.FloorToFloorDefault = ftf.GetDouble();
                    if (root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String)
                        map.Source = source.GetString();

                    if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in entries.EnumerateArray())
                        {
                            if (element.ValueKind != JsonValueKind.Object) continue;
                            string layer = ReadString(element, "layer");
                            if (string.IsNullOrWhiteSpace(layer)) continue;

                            map.Entries.Add(new LayerConventionEntry
                            {
                                Layer = layer,
                                ElementType = ReadString(element, "elementType"),
                                Subtype = ReadString(element, "subtype"),
                                Elevation = ReadDouble(element, "elevation"),
                                Note = ReadString(element, "note")
                            });
                        }
                    }

                    return map;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static double? ReadDouble(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : (double?)null;
    }

    /// <summary>
    /// Steps 2 and 3 of the resolution rule in one place: learned convention first, shipped
    /// canonical second. Step 1 (explicit tag) and step 4 (geometry inference) live in the
    /// classifier because they need the object, not just its layer.
    /// </summary>
    public sealed class ConventionResolver
    {
        private readonly LayerConventionMap _docMap;
        private readonly LayerConventionMap _firmMap;

        public ConventionResolver(LayerConventionMap docMap, LayerConventionMap firmMap = null)
        {
            _docMap = docMap;
            _firmMap = firmMap;
        }

        /// <summary>Floor-to-floor for inferred Levels: doc setting beats firm setting.</summary>
        public double FloorToFloorDefault
        {
            get
            {
                if (_docMap != null && _docMap.FloorToFloorDefault > 0) return _docMap.FloorToFloorDefault;
                if (_firmMap != null && _firmMap.FloorToFloorDefault > 0) return _firmMap.FloorToFloorDefault;
                return 0;
            }
        }

        public ConventionMatch Resolve(string layerFullPath)
        {
            // Doc-level learned map is the most specific statement about this file.
            if (_docMap != null)
            {
                var match = _docMap.Match(layerFullPath);
                if (match.IsMatch) return match;
                if (_docMap.Covers(layerFullPath)) return ConventionMatch.None;   // explicit "not architectural"
            }

            if (_firmMap != null)
            {
                var match = _firmMap.Match(layerFullPath);
                if (match.IsMatch) return match;
                if (_firmMap.Covers(layerFullPath)) return ConventionMatch.None;
            }

            return CanonicalConvention.Match(layerFullPath);
        }
    }
}
