using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RhinoClaude.Semantic
{
    /// <summary>
    /// The narrative half of <c>describe_massing</c>. Rev 2 changed what this says: not "three
    /// volumes assembled" but the boolean and mass composition — "one 3-storey office mass on
    /// the north half; a smaller 2-storey retail mass unioned into its south face; a light well
    /// cut through the office mass at the centre."
    ///
    /// Length is capped per plan §10.2 question 5: a ~600-token target, a 1500-token hard cap,
    /// tighter still at brief. Tokens are approximated at four characters, which is close enough
    /// for a budget and cheap enough to run on every call.
    /// </summary>
    public static class MassingNarrator
    {
        public const string Brief = "brief";
        public const string Standard = "standard";
        public const string Detailed = "detailed";

        private const int CharactersPerToken = 4;

        public static int TokenCap(string levelOfDetail)
        {
            switch (levelOfDetail)
            {
                case Brief: return 200;
                case Detailed: return 1500;
                default: return 600;
            }
        }

        public static string Narrate(MassingSnapshot snapshot, string levelOfDetail)
        {
            levelOfDetail = Normalize(levelOfDetail);
            var view = snapshot?.View ?? new SemanticView();
            var units = snapshot?.Units ?? UnitContext.Feet();

            var sb = new StringBuilder();

            if (view.Masses.Count == 0)
            {
                sb.Append("No Masses found. If your layers use a different naming convention, run " +
                          "ClaudeLearnNamingConvention, or use ClaudeSetElement to tag your solid Breps " +
                          "as Masses — see LAYER_CONVENTIONS.md. ");
                if (view.UnclassifiedCount > 0)
                {
                    sb.Append("There are " + view.UnclassifiedCount + " unclassified object(s) in the " +
                              "document, on " + Join(view.UnclassifiedLayers.Take(4)) + ".");
                }
                return Cap(sb.ToString(), levelOfDetail);
            }

            // ── The masses ────────────────────────────────────────────
            var ranked = view.Masses.OrderByDescending(m => m.Volume).ToList();
            var primary = ranked[0];

            sb.Append(DescribeMass(primary, view, units, isPrimary: true));

            foreach (var mass in ranked.Skip(1).Take(levelOfDetail == Brief ? 2 : 8))
            {
                sb.Append(" ");
                sb.Append(DescribeMass(mass, view, units, isPrimary: false));
            }

            if (ranked.Count > (levelOfDetail == Brief ? 3 : 9))
                sb.Append(" Plus " + (ranked.Count - (levelOfDetail == Brief ? 3 : 9)) + " smaller mass(es).");

            // ── How they relate ───────────────────────────────────────
            if (levelOfDetail != Brief)
            {
                var relationships = CompositionAnalyzer.Relationships(view.Masses, units);
                if (relationships.Count > 0)
                {
                    sb.Append(" ");
                    sb.Append(string.Join(" ", relationships.Take(6).Select(r => r.Notes)));
                }
                else if (view.Masses.Count > 1)
                {
                    sb.Append(" The masses are freestanding — none of them touch.");
                }
            }

            // ── Cuts, openings, overhangs ─────────────────────────────
            if (snapshot != null && levelOfDetail != Brief)
            {
                var cuts = snapshot.AllGeometry.SelectMany(g => g.Cuts).ToList();
                if (cuts.Count > 0)
                {
                    sb.Append(" " + Count(cuts.Count, "void") + " cut into the massing, totalling " +
                              Feet(cuts.Sum(c => c.Volume), units, cubic: true) + ".");
                }

                var openings = snapshot.AllOpenings.ToList();
                if (openings.Count > 0)
                {
                    var byType = openings.GroupBy(o => o.OpeningType)
                                         .OrderByDescending(g => g.Count())
                                         .Select(g => g.Count() + " " + g.Key.ToLowerInvariant() +
                                                      (g.Count() == 1 ? "" : "s"));
                    sb.Append(" Openings in the mass faces: " + Join(byType) + ".");
                }

                var overhangs = snapshot.AllGeometry.SelectMany(g => g.Overhangs).ToList();
                if (overhangs.Count > 0)
                    sb.Append(" " + Count(overhangs.Count, "projecting element") + " detected.");
            }

            // ── Roof and envelope, detailed only ──────────────────────
            if (snapshot != null && levelOfDetail == Detailed)
            {
                var roof = RoofAnalysis.Compute(snapshot);
                if (roof.RoofFaces.Count > 0)
                {
                    sb.Append(" Roof reads as " + roof.PredominantForm + " across " +
                              Count(roof.RoofFaces.Count, "face") + ", " +
                              Feet(roof.TotalRoofArea, units, square: true) + " total.");
                }

                var wwr = WallWindowRatio.Compute(snapshot, WallWindowRatio.ScopeWhole);
                if (wwr.TotalFacadeArea > 0)
                    sb.Append(" Overall wall-window ratio " + Percent(wwr.OverallRatio) + ".");
            }

            // ── Context ───────────────────────────────────────────────
            if (view.SiteElements.Count > 0 && levelOfDetail != Brief)
            {
                var byType = view.SiteElements.GroupBy(s => s.SiteType)
                                              .OrderBy(g => g.Key, StringComparer.Ordinal)
                                              .Select(g => g.Count() + " " + Humanize(g.Key));
                sb.Append(" Site context: " + Join(byType) + ".");
            }

            // ── What the classifier could not see (plan §5.7) ─────────
            if (view.UnclassifiedCount > 0)
            {
                sb.Append(" " + view.UnclassifiedCount + " object(s) did not classify and are absent from " +
                          "every semantic result");
                if (view.UnclassifiedLayers.Count > 0)
                    sb.Append(" (on " + Join(view.UnclassifiedLayers.Take(3)) + ")");
                sb.Append(".");
            }

            int inferred = view.Masses.Count(m => m.ClassifiedBy == SemanticVocabulary.ByGeometryInference);
            if (inferred > 0)
            {
                sb.Append(" " + inferred + " of these mass(es) were classified from geometry alone rather " +
                          "than a tag or layer — treat them as a guess and confirm before any destructive move.");
            }

            return Cap(sb.ToString(), levelOfDetail);
        }

        private static string DescribeMass(MassView mass, SemanticView view, UnitContext units, bool isPrimary)
        {
            var sb = new StringBuilder();

            double storeys = view.FloorToFloorDefault > 0 && mass.Bbox.IsValid
                ? Math.Round(mass.Bbox.Height / view.FloorToFloorDefault)
                : 0;

            sb.Append(isPrimary ? "One " : "A ");
            if (storeys >= 1) sb.Append(storeys + "-storey ");
            else if (mass.Bbox.IsValid) sb.Append(Feet(mass.Bbox.Height, units) + "-tall ");

            sb.Append(mass.Function == SemanticVocabulary.FunctionOther
                ? "mass"
                : mass.Function.ToLowerInvariant() + " mass");

            if (mass.Bbox.IsValid)
            {
                sb.Append(" of " + Feet(mass.FootprintArea, units, square: true) + " footprint");
                sb.Append(", " + Bearing(mass, view) + " of the site");
            }

            sb.Append(".");
            return sb.ToString();
        }

        /// <summary>Where a mass sits relative to the whole building's centre, in plain compass terms.</summary>
        private static string Bearing(MassView mass, SemanticView view)
        {
            var extents = view.BuildingExtents();
            if (!extents.IsValid || !mass.Bbox.IsValid) return "on the site";

            var offset = mass.Bbox.Center - extents.Center;
            var size = extents.Size;

            double threshold = Math.Max(size.X, size.Y) * 0.15;
            if (Math.Abs(offset.X) < threshold && Math.Abs(offset.Y) < threshold) return "at the centre";

            return "on the " + FaceClassifier.CompassSector(offset.X, offset.Y).ToLowerInvariant() + " side";
        }

        // ── Formatting ────────────────────────────────────────────────

        private static string Feet(double modelValue, UnitContext units, bool square = false, bool cubic = false)
        {
            double feet = square ? units.AreaToSquareFeet(modelValue)
                        : cubic ? units.VolumeToCubicFeet(modelValue)
                        : units.ToFeet(modelValue);

            string suffix = square ? " ft²" : cubic ? " ft³" : " ft";
            return Math.Round(feet).ToString("#,0") + suffix;
        }

        private static string Percent(double ratio) => Math.Round(ratio * 100, 1).ToString("0.#") + "%";

        private static string Count(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

        private static string Join(IEnumerable<string> values)
        {
            var list = values?.ToList() ?? new List<string>();
            if (list.Count == 0) return "none";
            if (list.Count == 1) return list[0];
            return string.Join(", ", list.Take(list.Count - 1)) + " and " + list[list.Count - 1];
        }

        private static string Humanize(string pascalCase)
        {
            if (string.IsNullOrEmpty(pascalCase)) return pascalCase;
            var sb = new StringBuilder();
            foreach (char c in pascalCase)
            {
                if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        public static string Normalize(string levelOfDetail)
        {
            if (string.Equals(levelOfDetail, Brief, StringComparison.OrdinalIgnoreCase)) return Brief;
            if (string.Equals(levelOfDetail, Detailed, StringComparison.OrdinalIgnoreCase)) return Detailed;
            return Standard;
        }

        /// <summary>Truncate at a sentence boundary so a capped narrative still reads as prose.</summary>
        public static string Cap(string text, string levelOfDetail)
        {
            if (string.IsNullOrEmpty(text)) return text;

            int limit = TokenCap(levelOfDetail) * CharactersPerToken;
            if (text.Length <= limit) return text;

            int cut = text.LastIndexOf(". ", Math.Min(limit, text.Length - 1), StringComparison.Ordinal);
            if (cut < limit / 2) cut = limit;

            return text.Substring(0, Math.Min(cut + 1, text.Length)).TrimEnd() + " […truncated]";
        }
    }
}
