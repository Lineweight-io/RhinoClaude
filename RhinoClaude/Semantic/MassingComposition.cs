using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>
    /// The deterministic composition facts <c>check_massing_composition</c> returns, and that
    /// the reviewer reads before judging whether a massing "feels too squat".
    ///
    /// Plan §1.3's doc-engine principle, applied here: hand the reviewer numbers about how the
    /// form is proportioned and assembled. It decides; the numbers stop it guessing.
    /// </summary>
    public sealed class MassingCompositionReport
    {
        public BoxView OverallBbox { get; set; } = BoxView.Unset;
        /// <summary>[length:width, length:height, width:height] of the overall envelope.</summary>
        public double[] AspectRatios { get; set; }
        public string DominantAxis { get; set; }

        public double SymmetryAboutX { get; set; }
        public double SymmetryAboutY { get; set; }

        public List<MassRank> Ranked { get; } = new List<MassRank>();
        public string PrimaryMassId { get; set; }
        public double? RatioPrimaryToSecondary { get; set; }

        public int UnionCount { get; set; }
        public int DifferenceCount { get; set; }
        public double CutVolumeTotal { get; set; }
        public double AdditiveVolumeTotal { get; set; }

        public int InferredLevelCount { get; set; }
        /// <summary>1 when every mass's height is a clean multiple of the floor-to-floor; lower
        /// as the stack gets more irregular. Null when no floor-to-floor is configured.</summary>
        public double? FloorToFloorConsistency { get; set; }

        public List<string> Notes { get; } = new List<string>();

        public sealed class MassRank
        {
            public string MassId { get; set; }
            public string Name { get; set; }
            public string Function { get; set; }
            public double Volume { get; set; }
            public double PercentOfTotal { get; set; }
        }
    }

    /// <summary>Plan §4.4's <c>check_massing_composition</c>, with Rev 2's booleanComposition field.</summary>
    public static class MassingComposition
    {
        public static MassingCompositionReport Compute(MassingSnapshot snapshot)
        {
            var report = new MassingCompositionReport();
            if (snapshot == null || snapshot.Masses.Count == 0)
            {
                report.Notes.Add("No masses to analyse.");
                return report;
            }

            var view = snapshot.View;
            var masses = snapshot.Masses;

            // ── Proportion ────────────────────────────────────────────
            report.OverallBbox = view.BuildingExtents();
            if (report.OverallBbox.IsValid)
            {
                var size = report.OverallBbox.Size;
                var sorted = new[] { size.X, size.Y, size.Z }.OrderByDescending(v => v).ToArray();
                report.AspectRatios = new[]
                {
                    Round(GeometryMath.Ratio(sorted[0], sorted[1])),
                    Round(GeometryMath.Ratio(sorted[0], sorted[2])),
                    Round(GeometryMath.Ratio(sorted[1], sorted[2]))
                };
                report.DominantAxis = GeometryMath.DominantAxis(report.OverallBbox);
            }

            // ── Symmetry ──────────────────────────────────────────────
            var boxes = masses.Select(m => m.Bbox).Where(b => b != null && b.IsValid).ToList();
            report.SymmetryAboutX = GeometryMath.SymmetryScore(boxes, aboutX: true);
            report.SymmetryAboutY = GeometryMath.SymmetryScore(boxes, aboutX: false);

            // ── Hierarchy ─────────────────────────────────────────────
            double totalVolume = masses.Sum(m => m.Volume);
            foreach (var mass in masses.OrderByDescending(m => m.Volume))
            {
                report.Ranked.Add(new MassingCompositionReport.MassRank
                {
                    MassId = mass.ElementId,
                    Name = mass.Name,
                    Function = mass.Function,
                    Volume = Math.Round(mass.Volume, 4),
                    PercentOfTotal = totalVolume <= 1e-9 ? 0 : Math.Round(mass.Volume / totalVolume * 100, 2)
                });
            }

            report.PrimaryMassId = report.Ranked.FirstOrDefault()?.MassId;
            if (report.Ranked.Count > 1)
                report.RatioPrimaryToSecondary = GeometryMath.Ratio(report.Ranked[0].Volume, report.Ranked[1].Volume);

            // ── Boolean composition (Rev 2) ───────────────────────────
            // Adjacency stands in for the union count when history is off: masses that
            // interpenetrate read as one form whether or not the boolean has happened.
            report.UnionCount = CompositionAnalyzer.Relationships(masses, snapshot.Units)
                .Count(r => r.Type == SemanticVocabulary.RelWasUnionedWith);

            var cuts = snapshot.AllGeometry.SelectMany(g => g.Cuts).ToList();
            report.DifferenceCount = cuts.Count + snapshot.AllOpenings.Count();
            report.CutVolumeTotal = Math.Round(cuts.Sum(c => c.Volume), 4);
            report.AdditiveVolumeTotal = Math.Round(totalVolume, 4);

            var history = snapshot.AllGeometry.Where(g => g.HistoryAvailable).SelectMany(g => g.History).ToList();
            if (history.Count > 0)
            {
                report.UnionCount = Math.Max(report.UnionCount, history.Count(h => h.Kind == "union"));
                report.DifferenceCount = Math.Max(report.DifferenceCount, history.Count(h => h.Kind == "difference"));
                report.Notes.Add("Boolean counts include Rhino history where it was recorded.");
            }
            else
            {
                report.Notes.Add("Rhino history is not available on these masses, so boolean counts are " +
                                 "inferred from geometry: interpenetrating masses count as unions, and " +
                                 "voids plus face openings count as differences.");
            }

            // ── Vertical rhythm ───────────────────────────────────────
            double floorToFloor = view.FloorToFloorDefault;
            if (floorToFloor > 0 && report.OverallBbox.IsValid)
            {
                report.InferredLevelCount = (int)Math.Floor(report.OverallBbox.Height / floorToFloor);
                report.FloorToFloorConsistency = Consistency(masses, floorToFloor);
            }
            else
            {
                report.InferredLevelCount = view.Levels.Count;
                report.Notes.Add("No floor-to-floor default is configured, so vertical rhythm is not " +
                                 "measured. Set one with ClaudeLearnNamingConvention.");
            }

            return report;
        }

        /// <summary>
        /// How close each mass's height is to a whole number of storeys. 1 means every mass
        /// lands on the ladder; 0.5 means the average mass is half a storey out.
        /// </summary>
        public static double Consistency(IReadOnlyList<MassView> masses, double floorToFloor)
        {
            if (floorToFloor <= 0 || masses == null || masses.Count == 0) return 0;

            double total = 0;
            int counted = 0;

            foreach (var mass in masses)
            {
                if (mass.Bbox == null || !mass.Bbox.IsValid) continue;
                double storeys = mass.Bbox.Height / floorToFloor;
                double error = Math.Abs(storeys - Math.Round(storeys));
                total += 1.0 - Math.Min(1.0, error * 2);   // half a storey out scores zero
                counted++;
            }

            return counted == 0 ? 0 : Math.Round(total / counted, 4);
        }

        private static double Round(double? value) => value == null ? 0 : Math.Round(value.Value, 4);
    }
}
