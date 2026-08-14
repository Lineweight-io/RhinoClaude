using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>One row of a wall-window-ratio report.</summary>
    public sealed class WwrResult
    {
        /// <summary>Compass sector, face id, or "whole" — whatever the scope grouped by.</summary>
        public string Key { get; set; }
        public double Area { get; set; }
        public double OpeningArea { get; set; }
        public double Ratio { get; set; }
        public int FaceCount { get; set; }
        public Dictionary<string, double> GlazingByType { get; } =
            new Dictionary<string, double>(StringComparer.Ordinal);
    }

    /// <summary>Everything check_wall_window_ratio reports, including what it had to skip.</summary>
    public sealed class WwrReport
    {
        public List<WwrResult> Results { get; } = new List<WwrResult>();
        public double OverallRatio { get; set; }
        public double TotalFacadeArea { get; set; }
        public double TotalOpeningArea { get; set; }
        /// <summary>Plan §5.7: how much envelope the classifier could not label, so the caller
        /// can say what the number excludes rather than implying full coverage.</summary>
        public double SkippedUnclassifiedArea { get; set; }
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// Plan §4.4's <c>check_wall_window_ratio</c>. Only facade-role faces count: a WWR that
    /// included roofs and undersides would be a smaller, wronger number, and the agent would
    /// act on it.
    /// </summary>
    public static class WallWindowRatio
    {
        public const string ScopeByOrientation = "byOrientation";
        public const string ScopeByFace = "byFace";
        public const string ScopeWhole = "whole";

        public static WwrReport Compute(MassingSnapshot snapshot, string scope, bool includeOverhangsAsShading = false)
        {
            var report = new WwrReport();
            if (snapshot == null) return report;

            var facades = snapshot.AllFaces
                .Where(f => f.HasRole(SemanticVocabulary.RoleFacade))
                .ToList();

            report.SkippedUnclassifiedArea = snapshot.AllFaces
                .Where(f => f.HasRole(SemanticVocabulary.RoleUnclassified))
                .Sum(f => f.Area);

            if (facades.Count == 0)
            {
                report.Notes.Add("No facade-role faces were found, so there is no wall-window ratio to report. " +
                                 "Either the model has no masses or their faces did not classify — " +
                                 "check describe_massing first.");
                return report;
            }

            switch (scope)
            {
                case ScopeByFace:
                    foreach (var face in facades.OrderByDescending(f => f.Area))
                        report.Results.Add(Row(face.FaceId, new[] { face }));
                    break;

                case ScopeWhole:
                    report.Results.Add(Row("whole", facades));
                    break;

                default:
                    foreach (var group in facades.GroupBy(f => f.Orientation)
                                                 .OrderBy(g => SectorOrder(g.Key)))
                        report.Results.Add(Row(group.Key, group));
                    break;
            }

            report.TotalFacadeArea = facades.Sum(f => f.Area);
            report.TotalOpeningArea = facades.Sum(f => f.OpeningArea);
            report.OverallRatio = report.TotalFacadeArea <= 1e-9
                ? 0
                : Math.Round(report.TotalOpeningArea / report.TotalFacadeArea, 4);

            if (report.SkippedUnclassifiedArea > 0)
            {
                report.Notes.Add(
                    Math.Round(report.SkippedUnclassifiedArea, 1) + " of face area could not be " +
                    "classified and is excluded from these ratios.");
            }

            if (includeOverhangsAsShading)
            {
                report.Notes.Add("Overhang shading was requested but is not modelled: the ratios below are " +
                                 "unshaded. Daylight and shading simulation is out of scope for this plugin " +
                                 "(see LAYER_CONVENTIONS.md §7).");
            }

            return report;
        }

        private static WwrResult Row(string key, IEnumerable<FaceView> faces)
        {
            var list = faces.ToList();
            var row = new WwrResult
            {
                Key = key,
                Area = list.Sum(f => f.Area),
                OpeningArea = list.Sum(f => f.OpeningArea),
                FaceCount = list.Count
            };
            row.Ratio = row.Area <= 1e-9 ? 0 : Math.Round(row.OpeningArea / row.Area, 4);

            foreach (var byType in list.SelectMany(f => f.Openings).GroupBy(o => o.OpeningType))
                row.GlazingByType[byType.Key] = Math.Round(byType.Sum(o => o.Area), 4);

            return row;
        }

        /// <summary>Compass order for stable reporting: N, NE, E, … then up/down/other.</summary>
        public static int SectorOrder(string orientation)
        {
            int index = Array.IndexOf(SemanticVocabulary.CompassSectors, orientation);
            return index >= 0 ? index : 100 + (orientation ?? string.Empty).Length;
        }
    }

    /// <summary>One roof face in the roof-form breakdown.</summary>
    public sealed class RoofFaceResult
    {
        public string FaceId { get; set; }
        public string MassId { get; set; }
        public double Area { get; set; }
        public double SlopePercent { get; set; }
        public string DrainageDirection { get; set; }
        public bool IsPlanar { get; set; }
        public double[] ElevationRange { get; set; }
        public List<EdgeView> AdjacentEdges { get; } = new List<EdgeView>();
    }

    public sealed class RoofReport
    {
        public List<RoofFaceResult> RoofFaces { get; } = new List<RoofFaceResult>();
        public double TotalRoofArea { get; set; }
        /// <summary>flat | sloped | complex.</summary>
        public string PredominantForm { get; set; } = "flat";
        public double RidgeLength { get; set; }
        public double EaveLength { get; set; }
        public double ParapetLength { get; set; }
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>Plan §4.4's <c>get_roof_analysis</c>: roof form, drainage, and edge treatment.</summary>
    public static class RoofAnalysis
    {
        /// <summary>Below this slope a roof is flat in every sense an architect cares about.</summary>
        public const double FlatSlopeCutoffPercent = 2.0;

        public static RoofReport Compute(MassingSnapshot snapshot)
        {
            var report = new RoofReport();
            if (snapshot == null) return report;

            foreach (var geometry in snapshot.AllGeometry)
            {
                foreach (var face in geometry.FacesWithRole(SemanticVocabulary.RoleRoof))
                {
                    var result = new RoofFaceResult
                    {
                        FaceId = face.FaceId,
                        MassId = face.MassId,
                        Area = face.Area,
                        SlopePercent = Math.Round(FaceClassifier.SlopePercent(face.Normal), 2),
                        DrainageDirection = FaceClassifier.DrainageDirection(face.Normal, FlatSlopeCutoffPercent),
                        IsPlanar = face.IsPlanar,
                        ElevationRange = new[] { face.ElevationMin, face.ElevationMax }
                    };

                    foreach (int edgeIndex in face.BoundingEdgeIndices)
                    {
                        var edge = geometry.Edges.FirstOrDefault(e => e.EdgeIndex == edgeIndex);
                        if (edge != null && edge.Role != SemanticVocabulary.EdgeOther)
                            result.AdjacentEdges.Add(edge);
                    }

                    report.RoofFaces.Add(result);
                }

                report.RidgeLength += geometry.Edges
                    .Where(e => e.Role == SemanticVocabulary.EdgeRoofRidge).Sum(e => e.Length);
                report.EaveLength += geometry.Edges
                    .Where(e => e.Role == SemanticVocabulary.EdgeEave).Sum(e => e.Length);
                report.ParapetLength += geometry.Edges
                    .Where(e => e.Role == SemanticVocabulary.EdgeParapet).Sum(e => e.Length);
            }

            report.TotalRoofArea = report.RoofFaces.Sum(f => f.Area);

            if (report.RoofFaces.Count == 0)
            {
                report.Notes.Add("No roof-role faces were found.");
                return report;
            }

            int sloped = report.RoofFaces.Count(f => f.SlopePercent >= FlatSlopeCutoffPercent);
            bool anyCurved = report.RoofFaces.Any(f => !f.IsPlanar);

            if (anyCurved || report.RoofFaces.Count > 6) report.PredominantForm = "complex";
            else if (sloped == 0) report.PredominantForm = "flat";
            else if (sloped == report.RoofFaces.Count) report.PredominantForm = "sloped";
            else report.PredominantForm = "complex";

            if (report.PredominantForm == "flat" && report.ParapetLength > 0)
                report.Notes.Add("Flat roof with parapet edges — drainage is internal or to scuppers; " +
                                 "the drainage direction fields are null by design.");

            return report;
        }
    }

    /// <summary>Plan §4.4's <c>get_program_allocation</c>: area and volume by mass function.</summary>
    public static class ProgramAllocation
    {
        public sealed class FunctionTotals
        {
            public double TotalVolume { get; set; }
            public double FootprintArea { get; set; }
            public double PercentOfTotal { get; set; }
            public int MassCount { get; set; }
        }

        public static Dictionary<string, FunctionTotals> Compute(SemanticView view, out double totalVolume)
        {
            var result = new Dictionary<string, FunctionTotals>(StringComparer.Ordinal);
            totalVolume = view?.Masses.Sum(m => m.Volume) ?? 0;
            if (view == null) return result;

            foreach (var group in view.Masses.GroupBy(m => m.Function ?? SemanticVocabulary.FunctionOther))
            {
                double volume = group.Sum(m => m.Volume);
                result[group.Key] = new FunctionTotals
                {
                    TotalVolume = Math.Round(volume, 4),
                    FootprintArea = Math.Round(group.Sum(m => m.FootprintArea), 4),
                    PercentOfTotal = totalVolume <= 1e-9 ? 0 : Math.Round(volume / totalVolume * 100, 2),
                    MassCount = group.Count()
                };
            }

            return result;
        }
    }
}
