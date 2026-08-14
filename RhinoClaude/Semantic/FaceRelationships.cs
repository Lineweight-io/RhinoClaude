using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    public sealed class FaceRelationshipReport
    {
        public List<List<string>> CoplanarGroups { get; } = new List<List<string>>();
        public List<ParallelPair> ParallelPairs { get; } = new List<ParallelPair>();
        public List<FacePair> PerpendicularPairs { get; } = new List<FacePair>();
        public List<FlushAlignment> FlushAlignments { get; } = new List<FlushAlignment>();
        public int FacesConsidered { get; set; }
        public List<string> Notes { get; } = new List<string>();

        public class FacePair
        {
            public string A { get; set; }
            public string B { get; set; }
        }

        public sealed class ParallelPair : FacePair
        {
            /// <summary>Perpendicular distance between the two planes, in model units.</summary>
            public double Offset { get; set; }
            /// <summary>True when the two faces look at each other rather than the same way.</summary>
            public bool FacingEachOther { get; set; }
        }

        public sealed class FlushAlignment
        {
            public List<string> Faces { get; } = new List<string>();
            public string Notes { get; set; }
        }
    }

    /// <summary>
    /// Plan §4.3's <c>check_face_relationships</c> — "does the office mass's north face align
    /// with the retail mass's north face?" A question about geometry, not about labels, so it
    /// keeps working on the curved and mislabelled faces where orientation does not (plan risk #3).
    /// </summary>
    public static class FaceRelationships
    {
        /// <summary>Normals within this dot product of each other count as parallel.</summary>
        public const double ParallelDot = 0.999;

        /// <summary>Normals whose dot product is within this of zero count as perpendicular.</summary>
        public const double PerpendicularDot = 0.02;

        public static FaceRelationshipReport Compute(
            IReadOnlyList<FaceView> faces, double tolerance, int maxFaces = 200)
        {
            var report = new FaceRelationshipReport();
            if (faces == null || faces.Count == 0)
            {
                report.Notes.Add("No faces were in scope.");
                return report;
            }

            var scoped = faces.Where(f => f.IsPlanar && f.Area > 0).ToList();

            if (faces.Count > scoped.Count)
            {
                report.Notes.Add((faces.Count - scoped.Count) + " non-planar face(s) were skipped — " +
                                 "coplanarity and offset are only meaningful between planes.");
            }

            if (scoped.Count > maxFaces)
            {
                report.Notes.Add("Scope held " + scoped.Count + " faces; only the " + maxFaces +
                                 " largest were compared. Narrow the scope with massIds for the rest.");
                scoped = scoped.OrderByDescending(f => f.Area).Take(maxFaces).ToList();
            }

            // Counted after both filters, so the number says what was actually compared rather
            // than what was offered — a truncated comparison must never read as a complete one.
            report.FacesConsidered = scoped.Count;

            var coplanarAssigned = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < scoped.Count; i++)
            {
                var a = scoped[i];
                var na = a.Normal.Unit();
                var coplanar = new List<string>();

                for (int j = i + 1; j < scoped.Count; j++)
                {
                    var b = scoped[j];
                    var nb = b.Normal.Unit();
                    double dot = Vec3.Dot(na, nb);

                    if (Math.Abs(dot) >= ParallelDot)
                    {
                        // Signed distance from a's plane to b's centroid.
                        double offset = Vec3.Dot(b.Centroid - a.Centroid, na);

                        if (Math.Abs(offset) <= tolerance && dot > 0)
                        {
                            if (!coplanarAssigned.Contains(b.FaceId))
                            {
                                coplanar.Add(b.FaceId);
                                coplanarAssigned.Add(b.FaceId);
                            }
                        }
                        else
                        {
                            report.ParallelPairs.Add(new FaceRelationshipReport.ParallelPair
                            {
                                A = a.FaceId,
                                B = b.FaceId,
                                Offset = Math.Round(Math.Abs(offset), 4),
                                FacingEachOther = dot < 0
                            });
                        }
                    }
                    else if (Math.Abs(dot) <= PerpendicularDot)
                    {
                        report.PerpendicularPairs.Add(new FaceRelationshipReport.FacePair
                        {
                            A = a.FaceId,
                            B = b.FaceId
                        });
                    }
                }

                if (coplanar.Count > 0 && !coplanarAssigned.Contains(a.FaceId))
                {
                    coplanar.Insert(0, a.FaceId);
                    coplanarAssigned.Add(a.FaceId);
                    report.CoplanarGroups.Add(coplanar);
                }
            }

            // A coplanar group spanning more than one mass is the flush alignment an architect
            // means by "do these two wings line up".
            foreach (var group in report.CoplanarGroups)
            {
                var masses = group.Select(id => faces.FirstOrDefault(f => f.FaceId == id)?.MassId)
                                  .Where(m => m != null)
                                  .Distinct()
                                  .ToList();
                if (masses.Count < 2) continue;

                var alignment = new FaceRelationshipReport.FlushAlignment
                {
                    Notes = "Flush across " + masses.Count + " masses — these faces sit in one plane."
                };
                alignment.Faces.AddRange(group);
                report.FlushAlignments.Add(alignment);
            }

            return report;
        }
    }
}
