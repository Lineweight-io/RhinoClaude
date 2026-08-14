using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RhinoClaude.Semantic
{
    /// <summary>
    /// The solid-preserving massing moves, expressed as pure geometry: where a cut line lands
    /// on a face, which direction a named axis means, and which edges a subdivision actually
    /// created.
    ///
    /// Same split as the rest of the semantic layer — <see cref="FaceClassifier"/> holds the
    /// rules and the analyzer measures the Brep. Here the planner decides the cut and the
    /// mutation service performs it, so the arithmetic that decides whether a ridge line
    /// crosses a roof is testable without a Rhino document.
    /// </summary>
    public struct FaceFrame
    {
        /// <summary>Lower-left corner of the face's extent, looking at it from outside.</summary>
        public Vec3 Origin;
        public Vec3 Horizontal;
        public Vec3 Vertical;
        public Vec3 Normal;
        public double Width;
        public double Height;
        public bool IsValid;

        public Vec3 At(double u, double v) => Origin + Horizontal * u + Vertical * v;

        public double U(Vec3 point) => Vec3.Dot(point - Origin, Horizontal);
        public double V(Vec3 point) => Vec3.Dot(point - Origin, Vertical);

        /// <summary>Drop a point onto the face's plane along the face normal.</summary>
        public Vec3 Project(Vec3 point) => point - Normal * Vec3.Dot(point - Origin, Normal);

        /// <summary>The four corners of the face's extent, in order round the rectangle.</summary>
        public Vec3[] Corners() => new[]
        {
            At(0, 0), At(Width, 0), At(Width, Height), At(0, Height)
        };

        /// <summary>
        /// A face's own coordinate frame. Built from the face's bounding box when it has one —
        /// exact for the planar, box-aligned faces massing is made of — and from its area and
        /// elevation range otherwise, so a hand-built view still yields a usable frame.
        ///
        /// A horizontal face has no "up", so world X and Y stand in: on a roof, u runs east and
        /// v runs north, which is what "split the top face down the middle" has to mean before
        /// it can mean anything.
        /// </summary>
        public static FaceFrame For(FaceView face)
        {
            var frame = new FaceFrame { IsValid = false };
            if (face == null) return frame;

            var normal = face.Normal.Unit();
            if (normal.Length < 0.5) return frame;

            var horizontal = Vec3.Cross(new Vec3(0, 0, 1), normal);
            if (horizontal.Length <= 1e-9) horizontal = new Vec3(1, 0, 0);
            horizontal = horizontal.Unit();

            var vertical = Vec3.Cross(normal, horizontal).Unit();

            frame.Normal = normal;
            frame.Horizontal = horizontal;
            frame.Vertical = vertical;

            var centroid = face.Centroid;

            if (face.Bbox != null && face.Bbox.IsValid && face.Bbox.Size.Length > 1e-9)
            {
                double uMin = double.MaxValue, uMax = double.MinValue;
                double vMin = double.MaxValue, vMax = double.MinValue;

                foreach (var corner in BoxCorners(face.Bbox))
                {
                    double u = Vec3.Dot(corner - centroid, horizontal);
                    double v = Vec3.Dot(corner - centroid, vertical);
                    uMin = Math.Min(uMin, u); uMax = Math.Max(uMax, u);
                    vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v);
                }

                frame.Width = uMax - uMin;
                frame.Height = vMax - vMin;
                frame.Origin = centroid + horizontal * uMin + vertical * vMin;
            }
            else
            {
                double height = face.ElevationMax - face.ElevationMin;
                double width;

                if (height <= 1e-9 || Math.Abs(normal.Z) > 0.9)
                {
                    // A horizontal face has no elevation range to measure; assume it is roughly
                    // square, which is the best a single area number supports.
                    width = height = Math.Sqrt(Math.Max(face.Area, 0));
                }
                else
                {
                    width = face.Area / height;
                }

                frame.Width = width;
                frame.Height = height;
                frame.Origin = centroid - horizontal * (width / 2) - vertical * (height / 2);
            }

            frame.IsValid = frame.Width > 1e-9 && frame.Height > 1e-9;
            return frame;
        }

        private static IEnumerable<Vec3> BoxCorners(BoxView box)
        {
            var min = box.Min;
            var max = box.Max;
            yield return new Vec3(min.X, min.Y, min.Z);
            yield return new Vec3(max.X, min.Y, min.Z);
            yield return new Vec3(min.X, max.Y, min.Z);
            yield return new Vec3(max.X, max.Y, min.Z);
            yield return new Vec3(min.X, min.Y, max.Z);
            yield return new Vec3(max.X, min.Y, max.Z);
            yield return new Vec3(min.X, max.Y, max.Z);
            yield return new Vec3(max.X, max.Y, max.Z);
        }
    }

    /// <summary>
    /// How <c>subdivide_face</c> is asked to divide a face. Three shapes, one union, same
    /// pattern as <see cref="FaceSelector"/>: a cutting line given by two points, an existing
    /// curve in the document, or a proportional split along the face's own axes.
    /// </summary>
    public sealed class FaceCut
    {
        /// <summary>[x, y] or [x, y, z]. A missing z is filled from the face's plane.</summary>
        public double[] LineStart { get; set; }
        public double[] LineEnd { get; set; }

        public string CuttingCurveId { get; set; }

        /// <summary>0..1 along the face's own axis; 0.5 is the midline.</summary>
        public double? SplitRatio { get; set; }

        /// <summary>"u" cuts at constant u (the line runs along v); "v" is the other way.</summary>
        public string Direction { get; set; }

        public bool IsEmpty =>
            LineStart == null && LineEnd == null
            && string.IsNullOrWhiteSpace(CuttingCurveId) && SplitRatio == null;

        public override string ToString()
        {
            if (!string.IsNullOrWhiteSpace(CuttingCurveId)) return "cuttingCurveId=" + CuttingCurveId;
            if (SplitRatio != null) return "splitRatio=" + SplitRatio + " along " + (Direction ?? "u");
            if (LineStart != null && LineEnd != null)
                return "line=[" + string.Join(",", LineStart) + "]→[" + string.Join(",", LineEnd) + "]";
            return "(empty)";
        }

        /// <summary>Parse the union out of a tool's JSON input. Unknown keys are ignored.</summary>
        public static FaceCut Parse(JsonElement element)
        {
            var cut = new FaceCut();
            if (element.ValueKind != JsonValueKind.Object) return cut;

            if (element.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Object)
            {
                cut.LineStart = Numbers(line, "startPoint");
                cut.LineEnd = Numbers(line, "endPoint");
            }

            if (element.TryGetProperty("cuttingCurveId", out var curveId) && curveId.ValueKind == JsonValueKind.String)
                cut.CuttingCurveId = curveId.GetString();

            if (element.TryGetProperty("splitRatio", out var ratio) && ratio.ValueKind == JsonValueKind.Number)
                cut.SplitRatio = ratio.GetDouble();

            if (element.TryGetProperty("direction", out var direction) && direction.ValueKind == JsonValueKind.String)
                cut.Direction = direction.GetString();

            return cut;
        }

        private static double[] Numbers(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                return null;

            var numbers = value.EnumerateArray()
                               .Where(e => e.ValueKind == JsonValueKind.Number)
                               .Select(e => e.GetDouble())
                               .ToArray();

            return numbers.Length >= 2 ? numbers : null;
        }
    }

    /// <summary>The outcome of planning a cut — a line on the face's plane, or a reason there isn't one.</summary>
    public sealed class CutLinePlan
    {
        public Vec3 Start { get; set; }
        public Vec3 End { get; set; }
        /// <summary>Set instead of Start/End when the caller supplied a curve to cut with.</summary>
        public string CuttingCurveId { get; set; }
        public string Error { get; set; }
        public string Note { get; set; }

        public bool Resolved => Error == null;
        public bool UsesCurve => !string.IsNullOrWhiteSpace(CuttingCurveId);

        public static CutLinePlan Failed(string error) => new CutLinePlan { Error = error };
    }

    /// <summary>
    /// Turns a <see cref="FaceCut"/> into the line the Brep split actually needs: on the face's
    /// plane, crossing the face from edge to edge, and overshooting both ends so the split is
    /// clean rather than leaving a sliver.
    ///
    /// The crossing test is the one that matters. A ridge line that stops short of the roof's
    /// far edge splits nothing, and Rhino reports that as "the face came back in one piece" —
    /// which tells the agent nothing about what to fix. Catching it here means the error can
    /// say where the face is and what a line across it would look like.
    /// </summary>
    public static class FaceCutPlanner
    {
        public static CutLinePlan Plan(FaceView face, FaceCut cut, double tolerance, double overshoot)
        {
            if (face == null) return CutLinePlan.Failed("No face to subdivide.");
            if (cut == null || cut.IsEmpty)
                return CutLinePlan.Failed(
                    "A cut is required. Give one of: {line: {startPoint, endPoint}}, {cuttingCurveId}, " +
                    "or {splitRatio, direction}.");

            if (!face.IsPlanar)
                return CutLinePlan.Failed(
                    "Face " + face.FaceId + " is not planar, so a straight cut line cannot be placed on it. " +
                    "Use {cuttingCurveId} with a curve you have drawn on the face, or pick a planar face " +
                    "with get_mass_faces.");

            if (!string.IsNullOrWhiteSpace(cut.CuttingCurveId))
                return new CutLinePlan
                {
                    CuttingCurveId = cut.CuttingCurveId,
                    Note = "The curve was projected onto the face along its normal before splitting."
                };

            var frame = FaceFrame.For(face);
            if (!frame.IsValid)
                return CutLinePlan.Failed(
                    "Face " + face.FaceId + " has no measurable extent, so there is nowhere to put a cut line.");

            if (tolerance <= 0) tolerance = 1e-6;
            if (overshoot <= 0) overshoot = Math.Max(frame.Width, frame.Height) * 0.05;

            Vec3 start, end;
            string note = null;

            if (cut.SplitRatio != null)
            {
                double ratio = cut.SplitRatio.Value;
                if (ratio <= 1e-6 || ratio >= 1 - 1e-6)
                    return CutLinePlan.Failed(
                        "splitRatio must be strictly between 0 and 1 — 0.5 is the midline. " +
                        ratio + " would put the cut on the face's own edge, which divides nothing.");

                string direction = string.Equals(cut.Direction, "v", StringComparison.OrdinalIgnoreCase) ? "v" : "u";

                if (direction == "u")
                {
                    start = frame.At(frame.Width * ratio, 0);
                    end = frame.At(frame.Width * ratio, frame.Height);
                }
                else
                {
                    start = frame.At(0, frame.Height * ratio);
                    end = frame.At(frame.Width, frame.Height * ratio);
                }

                note = "Cut at " + Math.Round(ratio, 3) + " along the face's " + direction + " axis (" +
                       Math.Round(direction == "u" ? frame.Width : frame.Height, 2) + " model units across).";
            }
            else
            {
                if (cut.LineStart == null || cut.LineEnd == null)
                    return CutLinePlan.Failed(
                        "A line cut needs both startPoint and endPoint, each [x, y] or [x, y, z].");

                start = frame.Project(ToVec(cut.LineStart, frame.Origin.Z));
                end = frame.Project(ToVec(cut.LineEnd, frame.Origin.Z));

                if (start.DistanceTo(end) <= tolerance)
                    return CutLinePlan.Failed(
                        "startPoint and endPoint land on the same spot once projected onto the face, so " +
                        "there is no cut line. Give two points that are apart in plan.");
            }

            var direction3d = (end - start).Unit();
            var corners = frame.Corners();

            // Does the line actually cross the face, or does it run past one side of it? A line
            // that misses leaves every corner on the same side.
            int positive = 0, negative = 0;
            foreach (var corner in corners)
            {
                double side = Vec3.Dot(Vec3.Cross(direction3d, corner - start), frame.Normal);
                if (side > tolerance) positive++;
                else if (side < -tolerance) negative++;
            }

            if (positive == 0 || negative == 0)
                return CutLinePlan.Failed(
                    "That line does not cross face " + face.FaceId + ", so it would divide nothing. The face " +
                    "spans " + Describe(frame) + ". Give two points on opposite sides of it — the line is " +
                    "extended to the face's edges automatically, so the points do not have to sit exactly on " +
                    "the boundary.");

            // Extend to the face's extent plus a margin, so the split reaches both edges.
            double tMin = double.MaxValue, tMax = double.MinValue;
            foreach (var corner in corners)
            {
                double t = Vec3.Dot(corner - start, direction3d);
                tMin = Math.Min(tMin, t);
                tMax = Math.Max(tMax, t);
            }

            return new CutLinePlan
            {
                Start = start + direction3d * (tMin - overshoot),
                End = start + direction3d * (tMax + overshoot),
                Note = note
            };
        }

        /// <summary>Where a face sits, in the terms the error messages use.</summary>
        public static string Describe(FaceFrame frame)
        {
            var a = frame.At(0, 0);
            var b = frame.At(frame.Width, frame.Height);
            return "from (" + Round(a.X) + ", " + Round(a.Y) + ", " + Round(a.Z) + ") to (" +
                   Round(b.X) + ", " + Round(b.Y) + ", " + Round(b.Z) + ")";
        }

        private static Vec3 ToVec(double[] values, double fallbackZ) =>
            new Vec3(values[0], values[1], values.Length >= 3 ? values[2] : fallbackZ);

        private static double Round(double value) => Math.Round(value, 2);
    }

    /// <summary>
    /// Named directions for <c>move_face</c> and <c>move_edge</c>. "+z" is what an architect
    /// types when they mean "up"; making them spell it as a vector is friction with no payoff.
    /// </summary>
    public static class MoveDirection
    {
        /// <summary>Values that mean "along the face's own normal" and so need a face to resolve.</summary>
        public static readonly string[] FaceRelative = { "normal", "outward", "inward", "-normal" };

        public static bool IsFaceRelative(string name) =>
            !string.IsNullOrWhiteSpace(name)
            && FaceRelative.Contains(name.Trim().ToLowerInvariant(), StringComparer.Ordinal);

        /// <summary>True when the face-relative name points into the mass rather than out of it.</summary>
        public static bool IsInward(string name) =>
            !string.IsNullOrWhiteSpace(name)
            && (string.Equals(name.Trim(), "inward", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name.Trim(), "-normal", StringComparison.OrdinalIgnoreCase));

        public static bool TryParseNamed(string name, out Vec3 direction)
        {
            direction = Vec3.Zero;
            if (string.IsNullOrWhiteSpace(name)) return false;

            switch (name.Trim().ToLowerInvariant())
            {
                case "+z": case "z": case "up": direction = new Vec3(0, 0, 1); return true;
                case "-z": case "down": direction = new Vec3(0, 0, -1); return true;
                case "+x": case "x": case "east": direction = new Vec3(1, 0, 0); return true;
                case "-x": case "west": direction = new Vec3(-1, 0, 0); return true;
                case "+y": case "y": case "north": direction = new Vec3(0, 1, 0); return true;
                case "-y": case "south": direction = new Vec3(0, -1, 0); return true;
                default: return false;
            }
        }

        /// <summary>The names a caller may use, for an error message.</summary>
        public static string Names =>
            "+x, -x, +y, -y, +z, -z, up, down, north, south, east, west, outward, inward";
    }

    /// <summary>One Brep edge reduced to its endpoints — the only thing a topology diff needs.</summary>
    public struct EdgeSegment
    {
        public Vec3 Start;
        public Vec3 End;

        public EdgeSegment(Vec3 start, Vec3 end) { Start = start; End = end; }

        public Vec3 Midpoint => new Vec3(
            (Start.X + End.X) / 2, (Start.Y + End.Y) / 2, (Start.Z + End.Z) / 2);

        public double Length => Start.DistanceTo(End);
    }

    /// <summary>What a subdivision did to a solid's edges.</summary>
    public sealed class EdgeDiff
    {
        /// <summary>Edges that did not exist before in any form — the cut itself.</summary>
        public List<int> NewIndices { get; } = new List<int>();

        /// <summary>Pieces of an edge the cut landed on. Not new geometry, just renumbered.</summary>
        public List<int> FragmentIndices { get; } = new List<int>();

        public List<int> UnchangedIndices { get; } = new List<int>();
    }

    /// <summary>
    /// Which edges a face subdivision actually created. Brep edge indices are renumbered by any
    /// topology change, so "the new edge" cannot be read off an index — it has to be found by
    /// comparing the edge set before and after.
    ///
    /// The distinction that matters to the caller is between a genuinely new edge and a
    /// fragment. Cutting a box's top face down the middle creates one ridge, but it also splits
    /// the two top edges it crosses into halves; those halves are new *indices* and not new
    /// *edges*, and telling the agent to move one of them would tear the roof rather than raise
    /// it.
    /// </summary>
    public static class EdgeTopologyDiff
    {
        public static EdgeDiff Compare(
            IReadOnlyList<EdgeSegment> before, IReadOnlyList<EdgeSegment> after, double tolerance)
        {
            var diff = new EdgeDiff();
            if (after == null) return diff;
            if (tolerance <= 0) tolerance = 1e-6;

            before = before ?? new List<EdgeSegment>();

            for (int i = 0; i < after.Count; i++)
            {
                var candidate = after[i];

                if (before.Any(b => SameSegment(b, candidate, tolerance)))
                {
                    diff.UnchangedIndices.Add(i);
                    continue;
                }

                if (before.Any(b => LiesAlong(b, candidate, tolerance)))
                {
                    diff.FragmentIndices.Add(i);
                    continue;
                }

                diff.NewIndices.Add(i);
            }

            return diff;
        }

        /// <summary>
        /// The candidate closest to the line the caller asked to cut along. A subdivision can
        /// produce more than one new edge; the ridge is the one lying on the cut.
        /// </summary>
        public static int? NearestToLine(
            IReadOnlyList<EdgeSegment> segments, IEnumerable<int> candidates, Vec3 lineStart, Vec3 lineEnd)
        {
            if (segments == null || candidates == null) return null;

            var direction = (lineEnd - lineStart).Unit();
            if (direction.Length < 0.5) return null;

            int? best = null;
            double bestDistance = double.MaxValue;

            foreach (int index in candidates)
            {
                if (index < 0 || index >= segments.Count) continue;

                var midpoint = segments[index].Midpoint;
                double distance = Vec3.Cross(direction, midpoint - lineStart).Length;

                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = index;
            }

            return best;
        }

        private static bool SameSegment(EdgeSegment a, EdgeSegment b, double tolerance) =>
            (a.Start.DistanceTo(b.Start) <= tolerance && a.End.DistanceTo(b.End) <= tolerance)
            || (a.Start.DistanceTo(b.End) <= tolerance && a.End.DistanceTo(b.Start) <= tolerance);

        /// <summary>True when <paramref name="piece"/> sits along <paramref name="whole"/>.</summary>
        private static bool LiesAlong(EdgeSegment whole, EdgeSegment piece, double tolerance) =>
            DistanceToSegment(piece.Start, whole) <= tolerance
            && DistanceToSegment(piece.End, whole) <= tolerance
            && piece.Length <= whole.Length + tolerance;

        public static double DistanceToSegment(Vec3 point, EdgeSegment segment)
        {
            var span = segment.End - segment.Start;
            double lengthSquared = Vec3.Dot(span, span);
            if (lengthSquared <= 1e-18) return point.DistanceTo(segment.Start);

            double t = Vec3.Dot(point - segment.Start, span) / lengthSquared;
            t = Math.Max(0, Math.Min(1, t));

            return point.DistanceTo(segment.Start + span * t);
        }
    }
}
