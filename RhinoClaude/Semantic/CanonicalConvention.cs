using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>What a layer name resolved to. <c>ElementType</c> is null when nothing matched.</summary>
    public sealed class ConventionMatch
    {
        public string ElementType { get; set; }
        /// <summary>Mass function, Opening subtype, Overhang subtype, or Site subtype.</summary>
        public string Subtype { get; set; }
        /// <summary>Elevation carried in a LEVEL_ layer name, when one is encoded.</summary>
        public double? Elevation { get; set; }
        /// <summary>One of <see cref="SemanticVocabulary"/>'s classifiedBy values.</summary>
        public string ClassifiedBy { get; set; }
        /// <summary>The layer segment the match came from — useful in narratives and debugging.</summary>
        public string MatchedSegment { get; set; }

        public bool IsMatch => !string.IsNullOrEmpty(ElementType);

        public static ConventionMatch None => new ConventionMatch();
    }

    /// <summary>
    /// The shipped canonical layer convention from plan §5.3, and the parser that reads a
    /// layer full path through it.
    ///
    /// Format is <c>CATEGORY_Subcategory</c> with a PascalCase subcategory. Matching walks
    /// the layer path from leaf to root so a mass on <c>Building::MASS_Office</c> resolves
    /// the same as one on <c>MASS_Office</c>, and a child layer under <c>MASS_Office</c>
    /// inherits its parent's classification (plan §3.9's layer-parent grouping rule).
    /// </summary>
    public static class CanonicalConvention
    {
        public const string MassPrefix = "MASS_";
        public const string OpeningPrefix = "OPENING_";
        public const string OverhangPrefix = "OVERHANG_";
        public const string SitePrefix = "SITE_";
        public const string LevelPrefix = "LEVEL_";
        public const string FloorPrefix = "FLOOR_";

        /// <summary>Alternative prefixes the plan calls out for overhangs (§3.5 heuristic 2).</summary>
        private static readonly string[] OverhangAliases = { "OVERHANG_", "CANOPY_", "EAVE_", "BRISE_" };

        /// <summary>The full canonical layer list, exactly as shipped in LAYER_CONVENTIONS.md.</summary>
        public static readonly string[] CanonicalLayers =
        {
            "MASS_Office", "MASS_Residential", "MASS_Retail",
            "MASS_Institutional", "MASS_Common", "MASS_Other",
            "LEVEL_01_+0ft", "LEVEL_02_+12ft", "LEVEL_Roof_+36ft",
            "OPENING_Window", "OPENING_Door", "OPENING_Door_Entry",
            "OPENING_Storefront", "OPENING_Storefront_Entry",
            "OPENING_Curtain-Wall", "OPENING_Louver",
            "OVERHANG_Canopy", "OVERHANG_Eave", "OVERHANG_Brise-Soleil", "OVERHANG_Balcony",
            "SITE_Property-Line", "SITE_Topography", "SITE_Context-Building",
            "SITE_Street", "SITE_Curb", "SITE_Utility",
            "FLOOR_L01", "FLOOR_L02"
        };

        /// <summary>Layer-path separator Rhino uses between parent and child.</summary>
        public const string PathSeparator = "::";

        public static string[] Segments(string layerFullPath) =>
            string.IsNullOrWhiteSpace(layerFullPath)
                ? Array.Empty<string>()
                : layerFullPath.Split(new[] { PathSeparator }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>
        /// Resolve a layer path against the canonical convention. Leaf wins over parent —
        /// a `MASS_Office::OPENING_Window` child layer is an Opening, not a Mass.
        /// </summary>
        public static ConventionMatch Match(string layerFullPath)
        {
            var segments = Segments(layerFullPath);
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                var match = MatchSegment(segments[i]);
                if (match.IsMatch) return match;
            }
            return ConventionMatch.None;
        }

        /// <summary>Resolve one layer-name segment, ignoring its ancestry.</summary>
        public static ConventionMatch MatchSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment)) return ConventionMatch.None;
            string s = segment.Trim();

            if (StartsWith(s, MassPrefix))
            {
                return new ConventionMatch
                {
                    ElementType = SemanticVocabulary.Mass,
                    Subtype = NormalizeSubtype(Remainder(s, MassPrefix),
                                               SemanticVocabulary.MassFunctions,
                                               SemanticVocabulary.FunctionOther),
                    ClassifiedBy = SemanticVocabulary.ByCanonical,
                    MatchedSegment = s
                };
            }

            if (StartsWith(s, OpeningPrefix))
            {
                string tail = Remainder(s, OpeningPrefix);
                return new ConventionMatch
                {
                    ElementType = SemanticVocabulary.Opening,
                    // OPENING_Door_Entry and OPENING_Storefront_Entry carry the entry flag in the
                    // tail; the subtype is the part before it. The Entry promotion itself is a
                    // property on the Opening (plan §3.8), not a layer-driven element type.
                    Subtype = NormalizeSubtype(StripEntrySuffix(tail),
                                               SemanticVocabulary.OpeningTypes,
                                               SemanticVocabulary.OpeningOther),
                    ClassifiedBy = SemanticVocabulary.ByCanonical,
                    MatchedSegment = s
                };
            }

            foreach (var alias in OverhangAliases)
            {
                if (!StartsWith(s, alias)) continue;
                string tail = Remainder(s, alias);
                // CANOPY_/EAVE_/BRISE_ encode the subtype in the prefix itself.
                if (alias != OverhangPrefix)
                    tail = alias.TrimEnd('_');
                return new ConventionMatch
                {
                    ElementType = SemanticVocabulary.Overhang,
                    Subtype = NormalizeSubtype(tail, SemanticVocabulary.OverhangTypes, "Other"),
                    ClassifiedBy = SemanticVocabulary.ByCanonical,
                    MatchedSegment = s
                };
            }

            if (StartsWith(s, SitePrefix))
            {
                return new ConventionMatch
                {
                    ElementType = SemanticVocabulary.Site,
                    Subtype = NormalizeSubtype(Remainder(s, SitePrefix), SemanticVocabulary.SiteTypes, "Other"),
                    ClassifiedBy = SemanticVocabulary.ByCanonical,
                    MatchedSegment = s
                };
            }

            if (StartsWith(s, LevelPrefix))
            {
                return new ConventionMatch
                {
                    ElementType = SemanticVocabulary.Level,
                    Subtype = Remainder(s, LevelPrefix),
                    Elevation = ParseElevation(s),
                    ClassifiedBy = SemanticVocabulary.ByCanonical,
                    MatchedSegment = s
                };
            }

            // FLOOR_* layers hold derived floor plates. They are not a first-class type; the
            // classifier treats them as Level-adjacent context so they never read as Masses.
            if (StartsWith(s, FloorPrefix))
            {
                return new ConventionMatch
                {
                    ElementType = SemanticVocabulary.Level,
                    Subtype = Remainder(s, FloorPrefix),
                    ClassifiedBy = SemanticVocabulary.ByCanonical,
                    MatchedSegment = s
                };
            }

            return ConventionMatch.None;
        }

        /// <summary>True when any segment of the path is an OPENING_/SITE_/OVERHANG_ layer —
        /// the exclusions in the Mass geometry-inference fallback (plan §3.1 heuristic 4).</summary>
        public static bool IsNonMassCategory(string layerFullPath)
        {
            var match = Match(layerFullPath);
            return match.IsMatch && match.ElementType != SemanticVocabulary.Mass;
        }

        /// <summary>
        /// Pull an elevation out of a level layer name: <c>LEVEL_02_+12ft</c> → 12,
        /// <c>LEVEL_B1_-10ft</c> → -10. Returns null when nothing signed is present —
        /// a bare <c>LEVEL_02</c> has no elevation to read.
        /// </summary>
        public static double? ParseElevation(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment)) return null;

            foreach (var part in segment.Split('_'))
            {
                if (part.Length < 2) continue;
                if (part[0] != '+' && part[0] != '-') continue;

                var digits = new string(part.Skip(1)
                                            .TakeWhile(c => char.IsDigit(c) || c == '.')
                                            .ToArray());
                if (digits.Length == 0) continue;

                if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    return part[0] == '-' ? -value : value;
            }
            return null;
        }

        // ── internals ─────────────────────────────────────────────────

        private static bool StartsWith(string value, string prefix) =>
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        private static string Remainder(string value, string prefix) =>
            value.Length <= prefix.Length ? string.Empty : value.Substring(prefix.Length);

        private static string StripEntrySuffix(string tail)
        {
            if (string.IsNullOrEmpty(tail)) return tail;
            const string suffix = "_Entry";
            return tail.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? tail.Substring(0, tail.Length - suffix.Length)
                : tail;
        }

        /// <summary>True when the layer name marks the opening as an entry (OPENING_Door_Entry).</summary>
        public static bool IsEntryLayer(string layerFullPath)
        {
            foreach (var segment in Segments(layerFullPath))
                if (segment.EndsWith("_Entry", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Canonicalize a subtype token. Hyphenated canonical names (`Curtain-Wall`,
        /// `Property-Line`, `Context-Building`) map onto the PascalCase enum values the
        /// tools speak (`CurtainWall`, `PropertyLine`, `ContextBuilding`).
        /// </summary>
        public static string NormalizeSubtype(string token, string[] allowed, string fallback)
        {
            if (string.IsNullOrWhiteSpace(token)) return fallback;

            string compact = new string(token.Where(c => c != '-' && c != '_' && c != ' ').ToArray());

            foreach (var candidate in allowed)
            {
                string candidateCompact = new string(candidate.Where(c => c != '-' && c != '_' && c != ' ').ToArray());
                if (string.Equals(candidateCompact, compact, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return fallback;
        }

        /// <summary>Suggested layer for a newly created Mass of a given function.</summary>
        public static string LayerForFunction(string function) =>
            MassPrefix + (SemanticVocabulary.Normalize(function, SemanticVocabulary.MassFunctions,
                                                       SemanticVocabulary.FunctionOther));

        /// <summary>The canonical vocabulary rendered for the LearnNamingConvention prompt.</summary>
        public static string Describe()
        {
            var lines = new List<string>();
            foreach (var layer in CanonicalLayers)
            {
                var match = MatchSegment(layer);
                lines.Add("  " + layer + "  →  " + match.ElementType +
                          (string.IsNullOrEmpty(match.Subtype) ? "" : " / " + match.Subtype));
            }
            return string.Join("\n", lines);
        }
    }
}
