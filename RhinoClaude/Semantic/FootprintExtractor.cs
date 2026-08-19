using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>
    /// One closed ring recovered from a set of loose segments. Vertices are distinct and in
    /// order — the closing vertex is implied, not repeated, so a rectangle is four points.
    /// </summary>
    public sealed class FootprintLoop
    {
        public List<Vec3> Vertices { get; } = new List<Vec3>();
        public double Area { get; set; }
        public double Perimeter { get; set; }
        public BoxView Bounds { get; set; } = BoxView.Unset;
        public int VertexCount => Vertices.Count;
    }

    /// <summary>The outcome of assembling loose linework into a footprint.</summary>
    public sealed class FootprintExtraction
    {
        public bool Success => Error == null && Outer != null;
        /// <summary>Null on success; otherwise a message that says what to change.</summary>
        public string Error { get; set; }
        public FootprintLoop Outer { get; set; }
        public List<FootprintLoop> Inner { get; } = new List<FootprintLoop>();
        /// <summary>Chains that never closed. Dropped rather than fatal once an outer loop is found.</summary>
        public int OpenChainCount { get; set; }
        /// <summary>Smallest gap that would have to be bridged to join two of the leftover open chains.</summary>
        public double SmallestUnbridgedGap { get; set; }
        public int InputCount { get; set; }
        public string Notes { get; set; }
    }

    /// <summary>
    /// Turns a pile of CAD floor-plan linework into a single closed outer boundary.
    ///
    /// Deliberately free of RhinoCommon — the RhinoCommon side of
    /// <c>extract_footprint_from_curves</c> does the curve joining Rhino is good at (arcs,
    /// splines, polycurves) and calls in here for the two decisions that actually go wrong:
    /// which of several closed loops is the building outline, and how to bridge the small
    /// gaps in imported linework that <c>Curve.JoinCurves</c> leaves alone.
    ///
    /// Everything is treated as planar in XY. A plan whose linework sits at varying Z still
    /// produces the right footprint; the Z spread comes back in the bounds so the caller can
    /// say so.
    /// </summary>
    public static class FootprintExtractor
    {
        /// <summary>
        /// Chain the supplied polylines end-to-end within <paramref name="tolerance"/>, keep every
        /// ring that closes, and return the largest-area one as the footprint.
        ///
        /// The largest-area rule is the tie-break for genuinely ambiguous input: a floor plan's
        /// outer wall encloses its rooms, so it encloses the most area. Where that guess is load
        /// bearing, the caller reports it in <c>notes</c>.
        /// </summary>
        public static FootprintExtraction Assemble(
            IEnumerable<IReadOnlyList<Vec3>> polylines, double tolerance)
        {
            if (tolerance <= 0) tolerance = 1e-6;

            var result = new FootprintExtraction();
            var chains = new List<List<Vec3>>();

            foreach (var polyline in polylines ?? Enumerable.Empty<IReadOnlyList<Vec3>>())
            {
                result.InputCount++;
                var cleaned = Clean(polyline, tolerance);
                if (cleaned.Count >= 2) chains.Add(cleaned);
            }

            if (chains.Count == 0)
            {
                result.Error = result.InputCount == 0
                    ? "No curves were supplied."
                    : "None of the " + result.InputCount + " curves had two distinct points — " +
                      "every one collapsed to a point at the model tolerance.";
                return result;
            }

            var rings = new List<List<Vec3>>();
            var open = new List<List<Vec3>>();

            // Curves that already close on their own are loops, whatever else is in the selection.
            for (int i = chains.Count - 1; i >= 0; i--)
            {
                if (!ClosesOnItself(chains[i], tolerance)) continue;
                rings.Add(AsRing(chains[i], tolerance));
                chains.RemoveAt(i);
            }

            while (chains.Count > 0)
            {
                var current = chains[0];
                chains.RemoveAt(0);

                // Grow from both ends: starting mid-run is normal, because the selection arrives
                // in whatever order the objects happen to sit in the document.
                while (!ClosesOnItself(current, tolerance) &&
                       (GrowTail(current, chains, tolerance) || GrowHead(current, chains, tolerance)))
                {
                }

                if (ClosesOnItself(current, tolerance)) rings.Add(AsRing(current, tolerance));
                else open.Add(current);
            }

            result.OpenChainCount = open.Count;
            result.SmallestUnbridgedGap = SmallestGap(open);

            var loops = rings.Where(r => r.Count >= 3)
                             .Select(Measure)
                             .OrderByDescending(l => l.Area)
                             .ToList();

            if (loops.Count == 0)
            {
                result.Error = DescribeFailure(result.InputCount, open.Count, result.SmallestUnbridgedGap, tolerance);
                return result;
            }

            result.Outer = loops[0];
            result.Inner.AddRange(loops.Skip(1));
            result.Notes = BuildNotes(result.InputCount, 0, loops.Count, open.Count, viaFallback: true);
            return result;
        }

        /// <summary>
        /// Index of the largest value, or -1 when there is nothing to choose from. The
        /// RhinoCommon path picks its outer loop from measured curve areas through here, so
        /// both paths make the same choice the same way.
        /// </summary>
        public static int ChooseOuterIndex(IReadOnlyList<double> areas)
        {
            if (areas == null || areas.Count == 0) return -1;

            int best = 0;
            for (int i = 1; i < areas.Count; i++)
                if (areas[i] > areas[best]) best = i;
            return best;
        }

        /// <summary>
        /// The <c>notes</c> string both paths return. Says what was joined, what was thrown
        /// away, and — when more than one loop closed — that the outer boundary was a
        /// largest-area choice rather than a certainty.
        /// </summary>
        public static string BuildNotes(
            int inputCurveCount, int skippedCount, int closedLoopCount, int openChainCount, bool viaFallback)
        {
            var parts = new List<string>
            {
                "Joined " + inputCurveCount + " " + Plural(inputCurveCount, "curve") +
                " into " + closedLoopCount + " closed " + Plural(closedLoopCount, "loop")
            };

            int inner = Math.Max(0, closedLoopCount - 1);
            if (inner > 0)
                parts.Add("took the largest-area loop as the outer boundary and discarded " +
                          inner + " inner " + Plural(inner, "loop"));

            if (openChainCount > 0)
                parts.Add(openChainCount + " open " + Plural(openChainCount, "chain") +
                          " could not be closed and " + (openChainCount == 1 ? "was" : "were") + " ignored");

            if (skippedCount > 0)
                parts.Add("skipped " + skippedCount + " non-curve " + Plural(skippedCount, "object"));

            if (viaFallback)
                parts.Add("gaps were bridged at the model tolerance because Rhino's own join left nothing closed");

            return string.Join("; ", parts) + ".";
        }

        /// <summary>Signed-area magnitude in XY. Rings only; the closing segment is implied.</summary>
        public static double RingArea(IReadOnlyList<Vec3> ring)
        {
            if (ring == null || ring.Count < 3) return 0;

            double twice = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                twice += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(twice) / 2.0;
        }

        /// <summary>Perimeter of a ring, including the implied closing segment.</summary>
        public static double RingPerimeter(IReadOnlyList<Vec3> ring)
        {
            if (ring == null || ring.Count < 2) return 0;

            double total = 0;
            for (int i = 0; i < ring.Count; i++)
                total += ring[i].DistanceTo(ring[(i + 1) % ring.Count]);
            return total;
        }

        public static BoxView RingBounds(IReadOnlyList<Vec3> ring)
        {
            if (ring == null || ring.Count == 0) return BoxView.Unset;

            double minX = ring[0].X, minY = ring[0].Y, minZ = ring[0].Z;
            double maxX = minX, maxY = minY, maxZ = minZ;
            foreach (var p in ring)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.Z > maxZ) maxZ = p.Z;
            }
            return BoxView.From(new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ));
        }

        // ── Internals ─────────────────────────────────────────────────

        private static FootprintLoop Measure(List<Vec3> ring)
        {
            var loop = new FootprintLoop
            {
                Area = RingArea(ring),
                Perimeter = RingPerimeter(ring),
                Bounds = RingBounds(ring)
            };
            loop.Vertices.AddRange(ring);
            return loop;
        }

        /// <summary>Drop repeated points, so a tolerance comparison cannot match a vertex to itself.</summary>
        private static List<Vec3> Clean(IReadOnlyList<Vec3> polyline, double tolerance)
        {
            var cleaned = new List<Vec3>();
            if (polyline == null) return cleaned;

            foreach (var p in polyline)
            {
                if (cleaned.Count > 0 && cleaned[cleaned.Count - 1].DistanceTo(p) <= tolerance) continue;
                cleaned.Add(p);
            }
            return cleaned;
        }

        private static bool ClosesOnItself(List<Vec3> chain, double tolerance) =>
            chain.Count >= 4 && chain[0].DistanceTo(chain[chain.Count - 1]) <= tolerance;

        /// <summary>A closed chain as distinct vertices, with the duplicated closing point removed.</summary>
        private static List<Vec3> AsRing(List<Vec3> chain, double tolerance)
        {
            var ring = new List<Vec3>(chain);
            while (ring.Count > 1 && ring[0].DistanceTo(ring[ring.Count - 1]) <= tolerance)
                ring.RemoveAt(ring.Count - 1);
            return ring;
        }

        private static bool GrowTail(List<Vec3> current, List<List<Vec3>> pool, double tolerance)
        {
            int index = FindNearest(current[current.Count - 1], pool, tolerance, out bool matchedTail);
            if (index < 0) return false;

            var next = pool[index];
            pool.RemoveAt(index);
            if (matchedTail) next.Reverse();
            for (int i = 1; i < next.Count; i++) current.Add(next[i]);
            return true;
        }

        private static bool GrowHead(List<Vec3> current, List<List<Vec3>> pool, double tolerance)
        {
            int index = FindNearest(current[0], pool, tolerance, out bool matchedTail);
            if (index < 0) return false;

            var next = pool[index];
            pool.RemoveAt(index);
            // The matching end becomes the join, so orient the chain to finish at current's head.
            if (!matchedTail) next.Reverse();
            for (int i = next.Count - 2; i >= 0; i--) current.Insert(0, next[i]);
            return true;
        }

        /// <summary>
        /// Closest chain end within tolerance. <paramref name="matchedTail"/> comes back true when
        /// it was the candidate's own last point that matched, so the caller knows to flip it.
        /// </summary>
        private static int FindNearest(Vec3 point, List<List<Vec3>> pool, double tolerance, out bool matchedTail)
        {
            int best = -1;
            double bestDistance = double.MaxValue;
            matchedTail = false;

            for (int i = 0; i < pool.Count; i++)
            {
                double toStart = point.DistanceTo(pool[i][0]);
                if (toStart <= tolerance && toStart < bestDistance)
                {
                    bestDistance = toStart;
                    best = i;
                    matchedTail = false;
                }

                double toEnd = point.DistanceTo(pool[i][pool[i].Count - 1]);
                if (toEnd <= tolerance && toEnd < bestDistance)
                {
                    bestDistance = toEnd;
                    best = i;
                    matchedTail = true;
                }
            }
            return best;
        }

        /// <summary>
        /// Closest approach between the loose ends left over — the gap the user would have to
        /// close, or raise the tolerance past, for the loop to complete.
        /// </summary>
        private static double SmallestGap(List<List<Vec3>> open)
        {
            double smallest = double.PositiveInfinity;

            for (int i = 0; i < open.Count; i++)
            {
                var ends = new[] { open[i][0], open[i][open[i].Count - 1] };

                for (int j = i + 1; j < open.Count; j++)
                {
                    var others = new[] { open[j][0], open[j][open[j].Count - 1] };
                    foreach (var a in ends)
                        foreach (var b in others)
                            smallest = Math.Min(smallest, a.DistanceTo(b));
                }

                // A single run can also be one gap away from closing on itself.
                if (open[i].Count >= 3)
                    smallest = Math.Min(smallest, ends[0].DistanceTo(ends[1]));
            }

            return double.IsPositiveInfinity(smallest) ? 0 : smallest;
        }

        private static string DescribeFailure(int inputCount, int openChains, double gap, double tolerance)
        {
            var message = "No closed loop could be extracted from these " + inputCount + " " +
                          Plural(inputCount, "curve") + ". " + openChains + " open " +
                          Plural(openChains, "chain") + " remain";

            if (gap > tolerance)
                message += ", and the smallest gap between two loose ends is " + Vec3.Round(gap) +
                           ", wider than the model tolerance of " + Vec3.Round(tolerance) +
                           " — the linework does not actually meet";
            else
                message += " that do not form a ring — the selection is probably a set of open runs " +
                           "(walls drawn as separate strokes, or a plan with a doorway gap) rather " +
                           "than a perimeter";

            return message + ". Select the perimeter curves only, or close the gap, and try again.";
        }

        private static string Plural(int count, string word) => count == 1 ? word : word + "s";
    }
}
