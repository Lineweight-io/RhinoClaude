using System.Collections.Generic;
using System.Linq;
using RhinoClaude.Semantic;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The CAD-linework path of <c>extract_footprint_from_curves</c>.
    ///
    /// RhinoCommon is a compile-only reference, so the tool's own <c>Curve.JoinCurves</c> pass
    /// cannot run here (see the test project's note). What can run — and what actually went
    /// wrong in the 8/19 live test — is the part that decides the answer: whether loose segments
    /// chain into a ring at all, which of several rings is the building outline, and whether a
    /// selection that cannot close says so instead of silently producing a rectangle.
    /// </summary>
    public class FootprintExtractorTests
    {
        private const double Tolerance = 0.001;

        private static IReadOnlyList<Vec3> Segment(double x1, double y1, double x2, double y2) =>
            new List<Vec3> { new Vec3(x1, y1, 0), new Vec3(x2, y2, 0) };

        private static FootprintExtraction Assemble(params IReadOnlyList<Vec3>[] segments) =>
            FootprintExtractor.Assemble(segments, Tolerance);

        // ── The square ────────────────────────────────────────────────

        /// <summary>
        /// Four disjoint lines, handed over in scrambled order with two of them drawn backwards —
        /// which is what a selection off a real drawing looks like, because the objects arrive in
        /// document order and nobody drew them all clockwise.
        /// </summary>
        [Fact]
        public void FourLoosePlines_BecomeOneClosedRectangle()
        {
            var result = Assemble(
                Segment(10, 10, 10, 0),   // east, drawn downward
                Segment(0, 0, 10, 0),     // south
                Segment(10, 10, 0, 10),   // north, also backwards
                Segment(0, 10, 0, 0));    // west

            Assert.True(result.Success, result.Error);
            Assert.Empty(result.Inner);
            Assert.Equal(0, result.OpenChainCount);

            Assert.Equal(4, result.Outer.VertexCount);
            Assert.Equal(100, result.Outer.Area, 6);
            Assert.Equal(40, result.Outer.Perimeter, 6);
        }

        [Fact]
        public void ClosedRectangle_ReportsItsBounds()
        {
            var result = Assemble(
                Segment(0, 0, 10, 0),
                Segment(10, 0, 10, 10),
                Segment(10, 10, 0, 10),
                Segment(0, 10, 0, 0));

            Assert.True(result.Outer.Bounds.IsValid);
            Assert.Equal(0, result.Outer.Bounds.Min.X, 6);
            Assert.Equal(0, result.Outer.Bounds.Min.Y, 6);
            Assert.Equal(10, result.Outer.Bounds.Max.X, 6);
            Assert.Equal(10, result.Outer.Bounds.Max.Y, 6);
        }

        /// <summary>The regression the tool exists for: the plan is not its own bounding box.</summary>
        [Fact]
        public void LShapedPlan_IsNotItsBoundingBox()
        {
            var result = LShape();

            var bounds = result.Outer.Bounds;
            double boundingBoxArea = (bounds.Max.X - bounds.Min.X) * (bounds.Max.Y - bounds.Min.Y);

            Assert.Equal(600, boundingBoxArea, 6);  // 30 × 20, what a bbox extrusion would give
            Assert.Equal(400, result.Outer.Area, 6);  // what the plan actually encloses
        }

        // ── The L ─────────────────────────────────────────────────────

        /// <summary>
        /// Six lines around an L: a 30 × 10 bar with a 10 × 10 wing standing on its west end.
        /// </summary>
        private static FootprintExtraction LShape() => Assemble(
            Segment(10, 20, 0, 20),
            Segment(30, 0, 30, 10),
            Segment(0, 0, 30, 0),
            Segment(0, 20, 0, 0),
            Segment(30, 10, 10, 10),
            Segment(10, 10, 10, 20));

        [Fact]
        public void SixLoosePlines_BecomeOneClosedL()
        {
            var result = LShape();

            Assert.True(result.Success, result.Error);
            Assert.Empty(result.Inner);
            Assert.Equal(0, result.OpenChainCount);

            Assert.Equal(6, result.Outer.VertexCount);
            Assert.Equal(400, result.Outer.Area, 6);
            Assert.Equal(100, result.Outer.Perimeter, 6);
        }

        [Fact]
        public void LShape_KeepsBothReentrantCorners()
        {
            var vertices = LShape().Outer.Vertices;

            Assert.Contains(vertices, v => v.X == 10 && v.Y == 10);  // the inside corner
            Assert.Contains(vertices, v => v.X == 30 && v.Y == 10);
        }

        // ── Malformed input ───────────────────────────────────────────

        [Fact]
        public void ThreeSidesOfASquare_FailWithAnInformativeError()
        {
            var result = Assemble(
                Segment(0, 0, 10, 0),
                Segment(10, 0, 10, 10),
                Segment(10, 10, 0, 10));

            Assert.False(result.Success);
            Assert.Null(result.Outer);
            Assert.Contains("No closed loop", result.Error);
            Assert.Contains("10", result.Error);          // the size of the gap left open
            Assert.Contains("try again", result.Error);   // says what to do about it
        }

        /// <summary>
        /// Two runs whose ends nearly meet, but not within tolerance — an imported plan that looks
        /// closed on screen. The error has to name the gap, because that is the number the agent
        /// needs to decide whether to widen the tolerance or tell the user the drawing is dirty.
        /// </summary>
        [Fact]
        public void GapWiderThanTolerance_IsReportedAsAMeasuredDistance()
        {
            var result = FootprintExtractor.Assemble(
                new[]
                {
                    Segment(0, 0, 10, 0),
                    Segment(10, 0, 10, 10),
                    Segment(10, 10, 0, 10),
                    Segment(0, 10, 0, 0.25)   // stops a quarter unit short of the start
                },
                Tolerance);

            Assert.False(result.Success);
            Assert.Equal(0.25, result.SmallestUnbridgedGap, 6);
            Assert.Contains("0.25", result.Error);
            Assert.Contains("does not actually meet", result.Error);
        }

        [Fact]
        public void GapInsideTolerance_IsBridged()
        {
            var result = FootprintExtractor.Assemble(
                new[]
                {
                    Segment(0, 0, 10, 0),
                    Segment(10, 0, 10, 10),
                    Segment(10, 10, 0, 10),
                    Segment(0, 10, 0, 0.0005)   // half a tolerance short: the same corner, really
                },
                Tolerance);

            Assert.True(result.Success, result.Error);
            Assert.Equal(4, result.Outer.VertexCount);
        }

        [Fact]
        public void NoCurves_SaysSoRatherThanThrowing()
        {
            var result = FootprintExtractor.Assemble(new List<IReadOnlyList<Vec3>>(), Tolerance);

            Assert.False(result.Success);
            Assert.Contains("No curves were supplied", result.Error);
        }

        [Fact]
        public void DegenerateCurves_AreReportedAsCollapsed()
        {
            var result = Assemble(
                Segment(5, 5, 5, 5),
                Segment(7, 7, 7, 7));

            Assert.False(result.Success);
            Assert.Contains("collapsed to a point", result.Error);
        }

        // ── Choosing the outer loop ───────────────────────────────────

        /// <summary>
        /// The real floor-plan case: a perimeter with rooms inside it. The outer wall wins because
        /// it encloses the most area, and the inner loops are counted, not silently dropped.
        /// </summary>
        [Fact]
        public void PerimeterWithInteriorRooms_TakesTheLargestLoop()
        {
            var result = Assemble(
                // Outer wall, 40 × 30.
                Segment(0, 0, 40, 0), Segment(40, 0, 40, 30),
                Segment(40, 30, 0, 30), Segment(0, 30, 0, 0),
                // A room, 10 × 10.
                Segment(5, 5, 15, 5), Segment(15, 5, 15, 15),
                Segment(15, 15, 5, 15), Segment(5, 15, 5, 5),
                // Another room, 8 × 8.
                Segment(20, 5, 28, 5), Segment(28, 5, 28, 13),
                Segment(28, 13, 20, 13), Segment(20, 13, 20, 5));

            Assert.True(result.Success, result.Error);
            Assert.Equal(1200, result.Outer.Area, 6);
            Assert.Equal(2, result.Inner.Count);
            Assert.Equal(new[] { 100.0, 64.0 }, result.Inner.Select(l => l.Area).ToArray());
            Assert.Contains("discarded 2 inner loops", result.Notes);
        }

        /// <summary>Dimension strings and door swings in the selection are dropped, not fatal.</summary>
        [Fact]
        public void StrayOpenLineworkIsIgnoredOnceALoopCloses()
        {
            var result = Assemble(
                Segment(0, 0, 10, 0), Segment(10, 0, 10, 10),
                Segment(10, 10, 0, 10), Segment(0, 10, 0, 0),
                Segment(-5, -5, -5, 20),    // a dimension line, off on its own
                Segment(20, 3, 26, 3));     // and another

            Assert.True(result.Success, result.Error);
            Assert.Equal(100, result.Outer.Area, 6);
            Assert.Equal(2, result.OpenChainCount);
            Assert.Contains("2 open chains could not be closed", result.Notes);
        }

        [Fact]
        public void AlreadyClosedPolyline_IsTakenAsIs()
        {
            var square = new List<Vec3>
            {
                new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 10, 0),
                new Vec3(0, 10, 0), new Vec3(0, 0, 0)
            };

            var result = FootprintExtractor.Assemble(new[] { square }, Tolerance);

            Assert.True(result.Success, result.Error);
            Assert.Equal(4, result.Outer.VertexCount);   // the repeated closing point is dropped
            Assert.Equal(100, result.Outer.Area, 6);
        }

        // ── The shared decisions ──────────────────────────────────────

        [Fact]
        public void ChooseOuterIndex_PicksTheLargestArea()
        {
            Assert.Equal(1, FootprintExtractor.ChooseOuterIndex(new[] { 100.0, 900.0, 400.0 }));
            Assert.Equal(0, FootprintExtractor.ChooseOuterIndex(new[] { 5.0 }));
            Assert.Equal(-1, FootprintExtractor.ChooseOuterIndex(new double[0]));
            Assert.Equal(-1, FootprintExtractor.ChooseOuterIndex(null));
        }

        [Fact]
        public void ChooseOuterIndex_KeepsTheFirstOnATie()
        {
            Assert.Equal(0, FootprintExtractor.ChooseOuterIndex(new[] { 100.0, 100.0 }));
        }

        /// <summary>
        /// The notes are the tool's only chance to say the outer loop was a guess, so the wording
        /// is worth pinning: an agent that reads "discarded 12 inner loops" knows to look twice.
        /// </summary>
        [Fact]
        public void BuildNotes_SaysWhatWasJoinedAndWhatWasThrownAway()
        {
            var notes = FootprintExtractor.BuildNotes(
                inputCurveCount: 47, skippedCount: 0, closedLoopCount: 13,
                openChainCount: 0, viaFallback: false);

            Assert.Contains("Joined 47 curves into 13 closed loops", notes);
            Assert.Contains("largest-area loop as the outer boundary", notes);
            Assert.Contains("discarded 12 inner loops", notes);
        }

        [Fact]
        public void BuildNotes_MentionsSkippedNonCurvesAndTheFallback()
        {
            var notes = FootprintExtractor.BuildNotes(
                inputCurveCount: 1, skippedCount: 1, closedLoopCount: 1,
                openChainCount: 1, viaFallback: true);

            Assert.Contains("Joined 1 curve into 1 closed loop", notes);
            Assert.Contains("1 open chain could not be closed and was ignored", notes);
            Assert.Contains("skipped 1 non-curve object", notes);
            Assert.Contains("Rhino's own join left nothing closed", notes);
            Assert.DoesNotContain("inner loop", notes);
        }

        [Fact]
        public void RingArea_IsIndependentOfWinding()
        {
            var clockwise = new List<Vec3>
            {
                new Vec3(0, 0, 0), new Vec3(0, 10, 0), new Vec3(10, 10, 0), new Vec3(10, 0, 0)
            };
            var counterClockwise = Enumerable.Reverse(clockwise).ToList();

            Assert.Equal(100, FootprintExtractor.RingArea(clockwise), 6);
            Assert.Equal(100, FootprintExtractor.RingArea(counterClockwise), 6);
        }

        [Fact]
        public void RingBounds_CarryTheZSpreadOfTheLinework()
        {
            var tilted = new List<Vec3>
            {
                new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 10, 3), new Vec3(0, 10, 3)
            };

            var bounds = FootprintExtractor.RingBounds(tilted);

            Assert.Equal(0, bounds.Min.Z, 6);
            Assert.Equal(3, bounds.Max.Z, 6);
        }
    }
}
