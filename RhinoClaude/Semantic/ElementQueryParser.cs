using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>What a natural-language element query resolved to, before anything is matched.</summary>
    public sealed class ElementQuery
    {
        public string RawText { get; set; }

        /// <summary>Mass | Face | Edge | Opening | Overhang | Recess | Cut | MassGroup | Level | Site, or null.</summary>
        public string TargetType { get; set; }
        public string Function { get; set; }
        public string Orientation { get; set; }
        public string FaceRole { get; set; }
        public string EdgeRole { get; set; }
        public string OpeningType { get; set; }
        public string SiteType { get; set; }
        public bool WantsEntry { get; set; }

        /// <summary>largest | smallest | tallest | primary, or null.</summary>
        public string Superlative { get; set; }

        /// <summary>Words left over after the vocabulary was stripped — matched against names.</summary>
        public List<string> NameHints { get; } = new List<string>();

        public bool IsEmpty =>
            TargetType == null && Function == null && Orientation == null && FaceRole == null
            && EdgeRole == null && OpeningType == null && SiteType == null && Superlative == null
            && !WantsEntry && NameHints.Count == 0;

        public override string ToString()
        {
            var parts = new List<string>();
            if (Superlative != null) parts.Add(Superlative);
            if (Orientation != null) parts.Add(Orientation);
            if (Function != null) parts.Add(Function);
            if (FaceRole != null) parts.Add("role:" + FaceRole);
            if (EdgeRole != null) parts.Add("edge:" + EdgeRole);
            if (OpeningType != null) parts.Add("opening:" + OpeningType);
            if (TargetType != null) parts.Add("type:" + TargetType);
            return parts.Count == 0 ? "(nothing recognised)" : string.Join(" ", parts);
        }
    }

    /// <summary>
    /// The rules-based parser behind <c>find_element</c>. Plan §10.2 question 4 decided this:
    /// rules first, an LLM only as a fallback when the rules find nothing — "north face of the
    /// office mass" is orientation words plus role words plus function words, and a round trip
    /// to a model to discover that is a round trip wasted.
    ///
    /// Pure, so the whole vocabulary of synonyms an architect might type is pinned by tests
    /// rather than by hope.
    /// </summary>
    public static class ElementQueryParser
    {
        private static readonly Dictionary<string, string> OrientationWords =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "north", "N" }, { "northern", "N" }, { "n", "N" },
                { "northeast", "NE" }, { "ne", "NE" },
                { "east", "E" }, { "eastern", "E" }, { "e", "E" },
                { "southeast", "SE" }, { "se", "SE" },
                { "south", "S" }, { "southern", "S" }, { "s", "S" },
                { "southwest", "SW" }, { "sw", "SW" },
                { "west", "W" }, { "western", "W" }, { "w", "W" },
                { "northwest", "NW" }, { "nw", "NW" },
                { "top", SemanticVocabulary.OrientationUp },
                { "upper", SemanticVocabulary.OrientationUp },
                { "up", SemanticVocabulary.OrientationUp },
                { "bottom", SemanticVocabulary.OrientationDown },
                { "underside", SemanticVocabulary.OrientationDown },
                { "down", SemanticVocabulary.OrientationDown }
            };

        private static readonly Dictionary<string, string> FaceRoleWords =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "facade", SemanticVocabulary.RoleFacade },
                { "facades", SemanticVocabulary.RoleFacade },
                { "elevation", SemanticVocabulary.RoleFacade },
                { "wall", SemanticVocabulary.RoleFacade },
                { "walls", SemanticVocabulary.RoleFacade },
                { "roof", SemanticVocabulary.RoleRoof },
                { "roofs", SemanticVocabulary.RoleRoof },
                { "floor", SemanticVocabulary.RoleFloor },
                { "base", SemanticVocabulary.RoleFloor },
                { "party", SemanticVocabulary.RolePartyWall },
                { "interior", SemanticVocabulary.RoleInterior }
            };

        private static readonly Dictionary<string, string> EdgeRoleWords =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "parapet", SemanticVocabulary.EdgeParapet },
                { "parapets", SemanticVocabulary.EdgeParapet },
                { "corner", SemanticVocabulary.EdgeOutsideCorner },
                { "corners", SemanticVocabulary.EdgeOutsideCorner },
                { "ridge", SemanticVocabulary.EdgeRoofRidge },
                { "ridges", SemanticVocabulary.EdgeRoofRidge },
                { "eave", SemanticVocabulary.EdgeEave },
                { "eaves", SemanticVocabulary.EdgeEave }
            };

        private static readonly Dictionary<string, string> TypeWords =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "mass", SemanticVocabulary.Mass },
                { "masses", SemanticVocabulary.Mass },
                { "massing", SemanticVocabulary.Mass },
                { "volume", SemanticVocabulary.Mass },
                { "building", SemanticVocabulary.Mass },
                { "block", SemanticVocabulary.Mass },
                { "bar", SemanticVocabulary.Mass },
                { "tower", SemanticVocabulary.Mass },
                { "face", SemanticVocabulary.Face },
                { "faces", SemanticVocabulary.Face },
                { "edge", SemanticVocabulary.Edge },
                { "edges", SemanticVocabulary.Edge },
                { "opening", SemanticVocabulary.Opening },
                { "openings", SemanticVocabulary.Opening },
                { "window", SemanticVocabulary.Opening },
                { "windows", SemanticVocabulary.Opening },
                { "door", SemanticVocabulary.Opening },
                { "doors", SemanticVocabulary.Opening },
                { "storefront", SemanticVocabulary.Opening },
                { "entry", SemanticVocabulary.Opening },
                { "entrance", SemanticVocabulary.Opening },
                { "overhang", SemanticVocabulary.Overhang },
                { "canopy", SemanticVocabulary.Overhang },
                { "balcony", SemanticVocabulary.Overhang },
                { "recess", SemanticVocabulary.Recess },
                { "loggia", SemanticVocabulary.Recess },
                { "cut", SemanticVocabulary.Cut },
                { "void", SemanticVocabulary.Cut },
                { "atrium", SemanticVocabulary.Cut },
                { "courtyard", SemanticVocabulary.Cut },
                { "wing", SemanticVocabulary.MassGroup },
                { "group", SemanticVocabulary.MassGroup },
                { "level", SemanticVocabulary.Level },
                { "storey", SemanticVocabulary.Level },
                { "story", SemanticVocabulary.Level },
                { "site", SemanticVocabulary.Site },
                { "context", SemanticVocabulary.Site },
                { "street", SemanticVocabulary.Site },
                { "topography", SemanticVocabulary.Site }
            };

        private static readonly Dictionary<string, string> OpeningTypeWords =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "window", SemanticVocabulary.OpeningWindow },
                { "windows", SemanticVocabulary.OpeningWindow },
                { "door", SemanticVocabulary.OpeningDoor },
                { "doors", SemanticVocabulary.OpeningDoor },
                { "storefront", SemanticVocabulary.OpeningStorefront },
                { "curtain", SemanticVocabulary.OpeningCurtainWall },
                { "louver", SemanticVocabulary.OpeningLouver }
            };

        private static readonly Dictionary<string, string> SiteTypeWords =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "property", "PropertyLine" },
                { "topography", "Topography" },
                { "context", "ContextBuilding" },
                { "street", "Street" },
                { "curb", "Curb" },
                { "utility", "Utility" }
            };

        private static readonly Dictionary<string, string> SuperlativeWords =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "largest", "largest" }, { "biggest", "largest" }, { "main", "largest" },
                { "primary", "largest" }, { "dominant", "largest" },
                { "smallest", "smallest" }, { "secondary", "smallest" },
                { "tallest", "tallest" }, { "highest", "tallest" }
            };

        private static readonly HashSet<string> StopWords =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "a", "an", "of", "on", "in", "at", "to", "and", "or", "my", "our",
                "this", "that", "these", "those", "is", "are", "with", "for", "its", "it"
            };

        public static ElementQuery Parse(string text)
        {
            var query = new ElementQuery { RawText = text };
            if (string.IsNullOrWhiteSpace(text)) return query;

            var tokens = Tokenize(text);

            foreach (var token in tokens)
            {
                if (StopWords.Contains(token)) continue;

                bool consumed = false;

                if (query.Orientation == null && Lookup(OrientationWords, token, out var orientation))
                {
                    query.Orientation = orientation;
                    consumed = true;
                }

                if (query.FaceRole == null && FaceRoleWords.TryGetValue(token, out var faceRole))
                {
                    query.FaceRole = faceRole;
                    // "roof", "wall" and "floor" name a face role, and imply a Face unless the
                    // sentence already named a different type.
                    if (query.TargetType == null) query.TargetType = SemanticVocabulary.Face;
                    consumed = true;
                }

                if (query.EdgeRole == null && EdgeRoleWords.TryGetValue(token, out var edgeRole))
                {
                    query.EdgeRole = edgeRole;
                    if (query.TargetType == null || query.TargetType == SemanticVocabulary.Face)
                        query.TargetType = SemanticVocabulary.Edge;
                    consumed = true;
                }

                if (OpeningTypeWords.TryGetValue(token, out var openingType))
                {
                    query.OpeningType = openingType;
                    consumed = true;
                }

                if (SiteTypeWords.TryGetValue(token, out var siteType) && query.SiteType == null)
                {
                    query.SiteType = siteType;
                    consumed = true;
                }

                if (TypeWords.TryGetValue(token, out var type))
                {
                    // An explicit type word wins over one implied by a role word: "the roof
                    // face of the office mass" is a Face, "the office mass" is a Mass.
                    query.TargetType = PreferType(query.TargetType, type);
                    consumed = true;
                }

                if (string.Equals(token, "entry", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(token, "entrance", StringComparison.OrdinalIgnoreCase))
                {
                    query.WantsEntry = true;
                    consumed = true;
                }

                if (query.Function == null)
                {
                    var function = SemanticVocabulary.Normalize(token, SemanticVocabulary.MassFunctions);
                    if (function != null && function != SemanticVocabulary.FunctionOther)
                    {
                        query.Function = function;
                        consumed = true;
                    }
                }

                if (query.Superlative == null && SuperlativeWords.TryGetValue(token, out var superlative))
                {
                    query.Superlative = superlative;
                    consumed = true;
                }

                if (!consumed && token.Length > 2) query.NameHints.Add(token);
            }

            // "the north face" with no type word still means a Face.
            if (query.TargetType == null && query.Orientation != null) query.TargetType = SemanticVocabulary.Face;

            return query;
        }

        /// <summary>
        /// Dictionary lookup that also tries the token with its hyphens removed, so
        /// "north-east" finds "northeast". Architects write compass directions both ways and
        /// the parser should not care which.
        /// </summary>
        private static bool Lookup(Dictionary<string, string> words, string token, out string value)
        {
            if (words.TryGetValue(token, out value)) return true;
            if (token.IndexOf('-') < 0) return false;
            return words.TryGetValue(token.Replace("-", string.Empty), out value);
        }

        /// <summary>
        /// Later, more specific type words win — except that a Mass word never overrides a Face
        /// or Edge word, because "the north face of the office mass" names both and means the face.
        /// </summary>
        private static string PreferType(string existing, string incoming)
        {
            if (existing == null) return incoming;
            if (existing == incoming) return existing;

            bool existingIsSubElement = existing == SemanticVocabulary.Face || existing == SemanticVocabulary.Edge
                                        || existing == SemanticVocabulary.Opening;
            if (existingIsSubElement && incoming == SemanticVocabulary.Mass) return existing;

            return incoming;
        }

        public static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();

            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c) || c == '-')
                {
                    current.Append(c);
                }
                else if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            if (current.Length > 0) tokens.Add(current.ToString());

            // Split hyphenated compounds too, so "north-east facade" reads as two ideas.
            return tokens.SelectMany(t => t.Contains("-")
                    ? new[] { t }.Concat(t.Split('-')).Where(p => p.Length > 0)
                    : new[] { t })
                .ToList();
        }

        /// <summary>
        /// How well a candidate's name matches the leftover words. 0 means no overlap; 1 means
        /// every hint appears. Used to rank matches, never to filter them out entirely.
        /// </summary>
        public static double NameScore(string candidateName, IReadOnlyList<string> hints)
        {
            if (hints == null || hints.Count == 0) return 0;
            if (string.IsNullOrWhiteSpace(candidateName)) return 0;

            int hits = hints.Count(h => candidateName.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0);
            return (double)hits / hints.Count;
        }
    }
}
