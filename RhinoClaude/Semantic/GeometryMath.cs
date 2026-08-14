using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>
    /// Small numerical helpers the semantic layer needs and RhinoCommon does not hand over in
    /// a usable shape: principal axes of a point cloud, and the plane-reflection test behind
    /// <c>check_massing_composition</c>'s symmetry field.
    ///
    /// Pure by design — every one of these is a rule the reviewer will see numbers from, and
    /// numbers that cannot be tested are numbers that cannot be trusted.
    /// </summary>
    public static class GeometryMath
    {
        /// <summary>
        /// Principal axes of a point cloud, longest first. A PCA over the Brep's vertices:
        /// axis 0 is the direction the mass is most extended in, which is what "dominant axis"
        /// means to an architect looking at a bar building.
        ///
        /// Returns the world axes for a degenerate cloud rather than NaNs.
        /// </summary>
        public static Vec3[] PrincipalAxes(IReadOnlyList<Vec3> points)
        {
            var fallback = new[] { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) };
            if (points == null || points.Count < 3) return fallback;

            var mean = Centroid(points);

            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p in points)
            {
                var d = p - mean;
                xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z;
                yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
            }

            double n = points.Count;
            var covariance = new[,]
            {
                { xx / n, xy / n, xz / n },
                { xy / n, yy / n, yz / n },
                { xz / n, yz / n, zz / n }
            };

            if (!Jacobi(covariance, out double[] values, out double[,] vectors)) return fallback;

            var order = Enumerable.Range(0, 3).OrderByDescending(i => values[i]).ToArray();
            var axes = order.Select(i => new Vec3(vectors[0, i], vectors[1, i], vectors[2, i]).Unit()).ToArray();

            return axes.Any(a => a.Length < 0.5) ? fallback : axes;
        }

        public static Vec3 Centroid(IReadOnlyList<Vec3> points)
        {
            if (points == null || points.Count == 0) return Vec3.Zero;
            double x = 0, y = 0, z = 0;
            foreach (var p in points) { x += p.X; y += p.Y; z += p.Z; }
            return new Vec3(x / points.Count, y / points.Count, z / points.Count);
        }

        /// <summary>
        /// Cyclic Jacobi eigenvalue decomposition of a symmetric 3×3. Overkill for three
        /// dimensions in theory; in practice it is the version that does not fall over on the
        /// degenerate matrices a cube's vertex cloud produces.
        /// </summary>
        private static bool Jacobi(double[,] input, out double[] eigenvalues, out double[,] eigenvectors)
        {
            var a = (double[,])input.Clone();
            var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
                if (off < 1e-12) break;

                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-15) continue;

                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1);
                        double s = t * c;

                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }

            eigenvalues = new[] { a[0, 0], a[1, 1], a[2, 2] };
            eigenvectors = v;
            return eigenvalues.All(value => !double.IsNaN(value) && !double.IsInfinity(value));
        }

        /// <summary>
        /// How symmetric a set of masses is about a vertical plane through the overall centre,
        /// normal to X or Y. 1 is perfectly symmetric, 0 is not symmetric at all.
        ///
        /// Measured on bounding boxes, weighted by volume: mirror each box across the plane and
        /// score how much of it lands on another box. That is the question "does this massing
        /// read as symmetric" answered the way you would answer it from an elevation.
        /// </summary>
        public static double SymmetryScore(IReadOnlyList<BoxView> boxes, bool aboutX)
        {
            var valid = boxes?.Where(b => b != null && b.IsValid).ToList();
            if (valid == null || valid.Count == 0) return 0;

            var extents = valid.Aggregate(BoxView.Unset, (acc, b) => acc.Union(b));
            if (!extents.IsValid) return 0;

            double axis = aboutX ? extents.Center.X : extents.Center.Y;

            double total = 0, matched = 0;
            foreach (var box in valid)
            {
                double volume = CompositionAnalyzer.Volume(box);
                if (volume <= 0) continue;
                total += volume;

                var mirrored = Mirror(box, axis, aboutX);
                double best = valid.Max(other => CompositionAnalyzer.OverlapVolume(mirrored, other));
                matched += Math.Min(volume, best);
            }

            return total <= 0 ? 0 : Math.Round(matched / total, 4);
        }

        private static BoxView Mirror(BoxView box, double axis, bool aboutX)
        {
            if (aboutX)
            {
                double minX = 2 * axis - box.Max.X;
                double maxX = 2 * axis - box.Min.X;
                return BoxView.From(new Vec3(minX, box.Min.Y, box.Min.Z), new Vec3(maxX, box.Max.Y, box.Max.Z));
            }

            double minY = 2 * axis - box.Max.Y;
            double maxY = 2 * axis - box.Min.Y;
            return BoxView.From(new Vec3(box.Min.X, minY, box.Min.Z), new Vec3(box.Max.X, maxY, box.Max.Z));
        }

        /// <summary>Which world axis a box is most extended along, as a readable label.</summary>
        public static string DominantAxis(BoxView box)
        {
            if (box == null || !box.IsValid) return "none";
            var size = box.Size;
            if (size.X >= size.Y && size.X >= size.Z) return "X (east-west)";
            if (size.Y >= size.X && size.Y >= size.Z) return "Y (north-south)";
            return "Z (vertical)";
        }

        /// <summary>Safe ratio — returns null rather than infinity when the denominator vanishes.</summary>
        public static double? Ratio(double numerator, double denominator) =>
            Math.Abs(denominator) <= 1e-12 ? (double?)null : Math.Round(numerator / denominator, 4);
    }
}
