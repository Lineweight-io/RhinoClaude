using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RhinoClaude.Semantic;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The solid-preserving massing moves, tested where the decisions actually live.
    ///
    /// RhinoCommon is a compile-only reference, so a Brep cannot be built at test runtime and
    /// the split itself cannot be exercised here (see the test project's own note). What can be
    /// exercised is everything that decides what the split does: where the cut line lands on a
    /// face, whether it crosses at all, and which of the resulting edges is the ridge — which is
    /// where the failures the tools exist to prevent would come from.
    /// </summary>
    public class FaceFrameTests
    {
        /// <summary>A 100 × 60 roof at z = 36, the fixture's office bar seen from above.</summary>
        private static FaceView Roof() => new FaceView
        {
            FaceId = "office:face:4",
            MassId = "office",
            FaceIndex = 4,
            Orientation = SemanticVocabulary.OrientationUp,
            Normal = new Vec3(0, 0, 1),
            Centroid = new Vec3(50, 30, 36),
            Area = 100 * 60,
            IsPlanar = true,
            ElevationMin = 36,
            ElevationMax = 36,
            Bbox = BoxView.From(new Vec3(0, 0, 36), new Vec3(100, 60, 36))
        };

        [Fact]
        public void HorizontalFaceGetsWorldAlignedFrame()
        {
            var frame = FaceFrame.For(Roof());

            Assert.True(frame.IsValid);
            Assert.Equal(100, frame.Width, 6);
            Assert.Equal(60, frame.Height, 6);

            // u runs east, v runs north, origin at the south-west corner.
            Assert.Equal(1, frame.Horizontal.X, 6);
            Assert.Equal(1, frame.Vertical.Y, 6);
            Assert.Equal(0, frame.Origin.X, 6);
            Assert.Equal(0, frame.Origin.Y, 6);
            Assert.Equal(36, frame.Origin.Z, 6);
        }

        [Fact]
        public void PointsOffThePlaneAreProjectedOntoIt()
        {
            var frame = FaceFrame.For(Roof());
            var projected = frame.Project(new Vec3(20, 10, 500));

            Assert.Equal(20, projected.X, 6);
            Assert.Equal(10, projected.Y, 6);
            Assert.Equal(36, projected.Z, 6);
        }

        [Fact]
        public void VerticalFaceFallsBackToAreaAndElevationWithoutABox()
        {
            var south = new FaceView
            {
                FaceId = "office:face:1",
                Normal = new Vec3(0, -1, 0),
                Centroid = new Vec3(50, 0, 18),
                Area = 100 * 36,
                IsPlanar = true,
                ElevationMin = 0,
                ElevationMax = 36
            };

            var frame = FaceFrame.For(south);

            Assert.True(frame.IsValid);
            Assert.Equal(36, frame.Height, 6);
            Assert.Equal(100, frame.Width, 6);
        }
    }

    public class FaceCutPlannerTests
    {
        private const double Tolerance = 0.001;

        private static FaceView Roof() => new FaceView
        {
            FaceId = "office:face:4",
            MassId = "office",
            FaceIndex = 4,
            Orientation = SemanticVocabulary.OrientationUp,
            Normal = new Vec3(0, 0, 1),
            Centroid = new Vec3(50, 30, 36),
            Area = 100 * 60,
            IsPlanar = true,
            ElevationMin = 36,
            ElevationMax = 36,
            Bbox = BoxView.From(new Vec3(0, 0, 36), new Vec3(100, 60, 36))
        };

        [Fact]
        public void RidgeLineIsProjectedOntoTheFaceAndExtendedPastIt()
        {
            var cut = new FaceCut
            {
                LineStart = new double[] { 20, 30 },
                LineEnd = new double[] { 80, 30 }
            };

            var plan = FaceCutPlanner.Plan(Roof(), cut, Tolerance, 1.0);

            Assert.True(plan.Resolved);
            Assert.Equal(36, plan.Start.Z, 6);
            Assert.Equal(36, plan.End.Z, 6);
            Assert.Equal(30, plan.Start.Y, 6);
            Assert.Equal(30, plan.End.Y, 6);

            // Extended to the roof's east and west edges plus the overshoot, so the split
            // reaches both sides rather than stopping where the caller's points did.
            Assert.Equal(-1, plan.Start.X, 6);
            Assert.Equal(101, plan.End.X, 6);
        }

        [Fact]
        public void DiagonalCutIsPlannedFromCornerToCorner()
        {
            var cut = new FaceCut
            {
                LineStart = new double[] { 0, 0 },
                LineEnd = new double[] { 100, 60 }
            };

            var plan = FaceCutPlanner.Plan(Roof(), cut, Tolerance, 0.5);

            Assert.True(plan.Resolved);
            Assert.True(plan.Start.X < 0 && plan.Start.Y < 0);
            Assert.True(plan.End.X > 100 && plan.End.Y > 60);
        }

        [Fact]
        public void ALineThatMissesTheFaceIsRefusedWithAnActionableError()
        {
            var cut = new FaceCut
            {
                LineStart = new double[] { 0, 200 },
                LineEnd = new double[] { 100, 200 }
            };

            var plan = FaceCutPlanner.Plan(Roof(), cut, Tolerance, 1.0);

            Assert.False(plan.Resolved);
            Assert.Contains("does not cross", plan.Error);
            Assert.Contains("office:face:4", plan.Error);
        }

        [Fact]
        public void MidlineSplitRatioCutsAcrossTheFacesUAxis()
        {
            var cut = new FaceCut { SplitRatio = 0.5, Direction = "u" };
            var plan = FaceCutPlanner.Plan(Roof(), cut, Tolerance, 1.0);

            Assert.True(plan.Resolved);
            Assert.Equal(50, plan.Start.X, 6);
            Assert.Equal(50, plan.End.X, 6);
            Assert.Equal(-1, plan.Start.Y, 6);
            Assert.Equal(61, plan.End.Y, 6);
        }

        [Fact]
        public void SplitRatioAlongVRunsTheOtherWay()
        {
            var cut = new FaceCut { SplitRatio = 0.25, Direction = "v" };
            var plan = FaceCutPlanner.Plan(Roof(), cut, Tolerance, 1.0);

            Assert.True(plan.Resolved);
            Assert.Equal(15, plan.Start.Y, 6);
            Assert.Equal(15, plan.End.Y, 6);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        [InlineData(1.5)]
        public void ASplitRatioOnOrPastTheEdgeIsRefused(double ratio)
        {
            var plan = FaceCutPlanner.Plan(Roof(), new FaceCut { SplitRatio = ratio }, Tolerance, 1.0);

            Assert.False(plan.Resolved);
            Assert.Contains("splitRatio", plan.Error);
        }

        [Fact]
        public void TwoPointsThatProjectToTheSameSpotAreRefused()
        {
            var cut = new FaceCut
            {
                LineStart = new double[] { 40, 30, 0 },
                LineEnd = new double[] { 40, 30, 90 }
            };

            var plan = FaceCutPlanner.Plan(Roof(), cut, Tolerance, 1.0);

            Assert.False(plan.Resolved);
            Assert.Contains("same spot", plan.Error);
        }

        [Fact]
        public void ACurvedFaceIsRefusedRatherThanCutBlind()
        {
            var face = Roof();
            face.IsPlanar = false;

            var plan = FaceCutPlanner.Plan(
                face, new FaceCut { SplitRatio = 0.5 }, Tolerance, 1.0);

            Assert.False(plan.Resolved);
            Assert.Contains("not planar", plan.Error);
        }

        [Fact]
        public void AnEmptyCutSaysWhatTheThreeShapesAre()
        {
            var plan = FaceCutPlanner.Plan(Roof(), new FaceCut(), Tolerance, 1.0);

            Assert.False(plan.Resolved);
            Assert.Contains("cuttingCurveId", plan.Error);
            Assert.Contains("splitRatio", plan.Error);
        }

        [Fact]
        public void ACuttingCurveIdPassesStraightThrough()
        {
            var plan = FaceCutPlanner.Plan(
                Roof(), new FaceCut { CuttingCurveId = "abc" }, Tolerance, 1.0);

            Assert.True(plan.Resolved);
            Assert.True(plan.UsesCurve);
            Assert.Equal("abc", plan.CuttingCurveId);
        }

        [Fact]
        public void TheJsonUnionParsesAllThreeShapes()
        {
            var line = JsonDocument.Parse(
                @"{""line"": {""startPoint"": [0, 30], ""endPoint"": [100, 30, 36]}}").RootElement;
            var parsedLine = FaceCut.Parse(line);
            Assert.Equal(new double[] { 0, 30 }, parsedLine.LineStart);
            Assert.Equal(new double[] { 100, 30, 36 }, parsedLine.LineEnd);

            var ratio = JsonDocument.Parse(@"{""splitRatio"": 0.5, ""direction"": ""v""}").RootElement;
            var parsedRatio = FaceCut.Parse(ratio);
            Assert.Equal(0.5, parsedRatio.SplitRatio);
            Assert.Equal("v", parsedRatio.Direction);

            var curve = JsonDocument.Parse(@"{""cuttingCurveId"": ""guid-here""}").RootElement;
            Assert.Equal("guid-here", FaceCut.Parse(curve).CuttingCurveId);

            Assert.True(FaceCut.Parse(JsonDocument.Parse("{}").RootElement).IsEmpty);
        }
    }

    public class MoveDirectionTests
    {
        [Theory]
        [InlineData("+z", 0, 0, 1)]
        [InlineData("up", 0, 0, 1)]
        [InlineData("-z", 0, 0, -1)]
        [InlineData("down", 0, 0, -1)]
        [InlineData("north", 0, 1, 0)]
        [InlineData("south", 0, -1, 0)]
        [InlineData("east", 1, 0, 0)]
        [InlineData("west", -1, 0, 0)]
        [InlineData("-X", -1, 0, 0)]
        public void NamedAxesResolve(string name, double x, double y, double z)
        {
            Assert.True(MoveDirection.TryParseNamed(name, out var direction));
            Assert.Equal(x, direction.X, 6);
            Assert.Equal(y, direction.Y, 6);
            Assert.Equal(z, direction.Z, 6);
        }

        [Theory]
        [InlineData("sideways")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("outward")]
        public void AnythingElseIsNotANamedAxis(string name)
        {
            Assert.False(MoveDirection.TryParseNamed(name, out _));
        }

        [Fact]
        public void FaceRelativeNamesAreRecognisedAndSigned()
        {
            Assert.True(MoveDirection.IsFaceRelative("outward"));
            Assert.True(MoveDirection.IsFaceRelative("Inward"));
            Assert.False(MoveDirection.IsFaceRelative("+z"));

            Assert.True(MoveDirection.IsInward("inward"));
            Assert.False(MoveDirection.IsInward("outward"));
        }
    }

    /// <summary>
    /// The before/after edge comparison that turns a split into the edge id the agent moves
    /// next. Boxes here are written out as their twelve edges, because that is exactly what the
    /// mutation service hands this code after reading a Brep.
    /// </summary>
    public class EdgeTopologyDiffTests
    {
        private const double Tolerance = 0.001;

        /// <summary>The twelve edges of a 100 × 60 × 36 box based at the origin.</summary>
        private static List<EdgeSegment> Box()
        {
            var corners = new[]
            {
                new Vec3(0, 0, 0), new Vec3(100, 0, 0), new Vec3(100, 60, 0), new Vec3(0, 60, 0),
                new Vec3(0, 0, 36), new Vec3(100, 0, 36), new Vec3(100, 60, 36), new Vec3(0, 60, 36)
            };

            return new List<EdgeSegment>
            {
                new EdgeSegment(corners[0], corners[1]),
                new EdgeSegment(corners[1], corners[2]),
                new EdgeSegment(corners[2], corners[3]),
                new EdgeSegment(corners[3], corners[0]),
                new EdgeSegment(corners[4], corners[5]),
                new EdgeSegment(corners[5], corners[6]),
                new EdgeSegment(corners[6], corners[7]),
                new EdgeSegment(corners[7], corners[4]),
                new EdgeSegment(corners[0], corners[4]),
                new EdgeSegment(corners[1], corners[5]),
                new EdgeSegment(corners[2], corners[6]),
                new EdgeSegment(corners[3], corners[7])
            };
        }

        [Fact]
        public void ADiagonalCutOnTheTopFaceAddsExactlyOneEdge()
        {
            var before = Box();

            // Corner to corner across the roof: the four top edges survive untouched and the
            // diagonal is the only thing that was not there before.
            var after = new List<EdgeSegment>(before)
            {
                new EdgeSegment(new Vec3(0, 0, 36), new Vec3(100, 60, 36))
            };

            var diff = EdgeTopologyDiff.Compare(before, after, Tolerance);

            Assert.Single(diff.NewIndices);
            Assert.Equal(12, diff.NewIndices[0]);
            Assert.Empty(diff.FragmentIndices);
            Assert.Equal(12, diff.UnchangedIndices.Count);
        }

        [Fact]
        public void AMidlineRidgeCutSeparatesTheRidgeFromTheEdgesItSplit()
        {
            var before = Box();

            // The ridge runs east-west at y = 30, so it crosses the two top edges running
            // north-south and halves each of them.
            var after = before.Where((_, i) => i != 5 && i != 7).ToList();
            after.Add(new EdgeSegment(new Vec3(100, 0, 36), new Vec3(100, 30, 36)));
            after.Add(new EdgeSegment(new Vec3(100, 30, 36), new Vec3(100, 60, 36)));
            after.Add(new EdgeSegment(new Vec3(0, 60, 36), new Vec3(0, 30, 36)));
            after.Add(new EdgeSegment(new Vec3(0, 30, 36), new Vec3(0, 0, 36)));
            after.Add(new EdgeSegment(new Vec3(0, 30, 36), new Vec3(100, 30, 36)));

            var diff = EdgeTopologyDiff.Compare(before, after, Tolerance);

            // One genuinely new edge — the ridge — and four halves of edges that already existed.
            Assert.Single(diff.NewIndices);
            Assert.Equal(4, diff.FragmentIndices.Count);
            Assert.Equal(10, diff.UnchangedIndices.Count);

            var ridge = after[diff.NewIndices[0]];
            Assert.Equal(100, ridge.Length, 6);
            Assert.Equal(30, ridge.Midpoint.Y, 6);
        }

        [Fact]
        public void ReversedEndpointsStillCountAsTheSameEdge()
        {
            var before = Box();
            var after = before.Select(e => new EdgeSegment(e.End, e.Start)).ToList();

            var diff = EdgeTopologyDiff.Compare(before, after, Tolerance);

            Assert.Empty(diff.NewIndices);
            Assert.Equal(12, diff.UnchangedIndices.Count);
        }

        [Fact]
        public void TheRidgeIsTheNewEdgeLyingOnTheCutLine()
        {
            var segments = new List<EdgeSegment>
            {
                new EdgeSegment(new Vec3(0, 0, 36), new Vec3(100, 0, 36)),      // off the line
                new EdgeSegment(new Vec3(0, 30, 36), new Vec3(100, 30, 36)),    // on it
                new EdgeSegment(new Vec3(0, 60, 36), new Vec3(100, 60, 36))     // off the line
            };

            var index = EdgeTopologyDiff.NearestToLine(
                segments, new[] { 0, 1, 2 }, new Vec3(-1, 30, 36), new Vec3(101, 30, 36));

            Assert.Equal(1, index);
        }

        [Fact]
        public void NoCandidatesMeansNoRidge()
        {
            Assert.Null(EdgeTopologyDiff.NearestToLine(
                Box(), new int[0], new Vec3(0, 30, 36), new Vec3(100, 30, 36)));
        }

        [Fact]
        public void PointToSegmentDistanceClampsToTheEnds()
        {
            var segment = new EdgeSegment(new Vec3(0, 0, 0), new Vec3(10, 0, 0));

            Assert.Equal(0, EdgeTopologyDiff.DistanceToSegment(new Vec3(5, 0, 0), segment), 6);
            Assert.Equal(3, EdgeTopologyDiff.DistanceToSegment(new Vec3(5, 3, 0), segment), 6);
            Assert.Equal(5, EdgeTopologyDiff.DistanceToSegment(new Vec3(15, 0, 0), segment), 6);
        }
    }

    /// <summary>
    /// The gable, end to end at the level this harness can reach: plan the ridge cut on a box's
    /// roof, then pick the ridge out of the edges the cut would produce.
    /// </summary>
    public class GablePlanningTests
    {
        [Fact]
        public void AMidlineRidgePlansAndResolvesToTheNewEdge()
        {
            var roof = new FaceView
            {
                FaceId = "house:face:4",
                MassId = "house",
                FaceIndex = 4,
                Orientation = SemanticVocabulary.OrientationUp,
                Normal = new Vec3(0, 0, 1),
                Centroid = new Vec3(15, 20, 10),
                Area = 30 * 40,
                IsPlanar = true,
                ElevationMin = 10,
                ElevationMax = 10,
                Bbox = BoxView.From(new Vec3(0, 0, 10), new Vec3(30, 40, 10))
            };
            roof.Roles.Add(SemanticVocabulary.RoleRoof);

            var plan = FaceCutPlanner.Plan(
                roof,
                new FaceCut { LineStart = new double[] { 15, 0 }, LineEnd = new double[] { 15, 40 } },
                0.001, 1.0);

            Assert.True(plan.Resolved);
            Assert.Equal(15, plan.Start.X, 6);
            Assert.Equal(10, plan.Start.Z, 6);

            // What the split would leave behind: the ridge, plus the halves of the two eaves
            // it crossed.
            var candidates = new List<EdgeSegment>
            {
                new EdgeSegment(new Vec3(0, 0, 10), new Vec3(15, 0, 10)),
                new EdgeSegment(new Vec3(15, 0, 10), new Vec3(15, 40, 10)),
                new EdgeSegment(new Vec3(15, 40, 10), new Vec3(30, 40, 10))
            };

            var ridge = EdgeTopologyDiff.NearestToLine(
                candidates, new[] { 0, 1, 2 }, plan.Start, plan.End);

            Assert.Equal(1, ridge);
            Assert.Equal(40, candidates[ridge.Value].Length, 6);
        }

        [Fact]
        public void ARidgeThatStopsShortOfTheRoofIsStillPlanned()
        {
            // Short of the edges is fine — the planner extends it. Off the roof entirely is not,
            // and that is the case the gable tool has to report rather than silently do nothing.
            var roof = new FaceView
            {
                FaceId = "house:face:4",
                Normal = new Vec3(0, 0, 1),
                Centroid = new Vec3(15, 20, 10),
                Area = 30 * 40,
                IsPlanar = true,
                ElevationMin = 10,
                ElevationMax = 10,
                Bbox = BoxView.From(new Vec3(0, 0, 10), new Vec3(30, 40, 10))
            };

            var shortRidge = FaceCutPlanner.Plan(
                roof,
                new FaceCut { LineStart = new double[] { 15, 12 }, LineEnd = new double[] { 15, 28 } },
                0.001, 1.0);

            Assert.True(shortRidge.Resolved);
            Assert.Equal(-1, shortRidge.Start.Y, 6);
            Assert.Equal(41, shortRidge.End.Y, 6);

            var offRoof = FaceCutPlanner.Plan(
                roof,
                new FaceCut { LineStart = new double[] { 60, 0 }, LineEnd = new double[] { 60, 40 } },
                0.001, 1.0);

            Assert.False(offRoof.Resolved);
            Assert.Contains("does not cross", offRoof.Error);
        }
    }

    /// <summary>
    /// The multi-cut form, which exists because a single straight line cannot describe the roof
    /// of anything but a box.
    ///
    /// The behaviour under test was measured against Rhino rather than assumed — see
    /// tools/rhino-headless/probes/lgable.py. Three findings drive these tests: one curve that
    /// stops inside a face divides nothing; two curves that each stop inside but meet each other
    /// and reach the boundary between them divide it in one Split call; and the resulting ridge
    /// edges have to move in a single transform or the faces spanning them warp.
    /// </summary>
    public class MultiCutTests
    {
        /// <summary>
        /// The L from the probe: 60 x 30 east-west, 30 x 50 north-south. Its top face is
        /// re-entrant, so the frame is the bounding box and the real boundary is smaller.
        /// </summary>
        private static FaceView LRoof() => new FaceView
        {
            FaceId = "house:face:6",
            MassId = "house",
            FaceIndex = 6,
            Orientation = SemanticVocabulary.OrientationUp,
            Normal = new Vec3(0, 0, 1),
            Centroid = new Vec3(25, 20, 12),
            Area = 2400,
            IsPlanar = true,
            ElevationMin = 12,
            ElevationMax = 12,
            Bbox = BoxView.From(new Vec3(0, 0, 12), new Vec3(60, 50, 12))
        };

        private static FaceCut LGableCut() => new FaceCut
        {
            // Two ridge segments through the turning point at (15, 15)...
            PolylinePoints = new List<double[]>
            {
                new double[] { 60, 15 },
                new double[] { 15, 15 },
                new double[] { 15, 50 }
            },
            // ...plus the hip out to the outside corner and the valley in to the inside one.
            Lines = new List<double[][]>
            {
                new[] { new double[] { 15, 15 }, new double[] { 0, 0 } },
                new[] { new double[] { 15, 15 }, new double[] { 30, 30 } }
            }
        };

        [Fact]
        public void MultiSegmentCutParsesFromBothListForms()
        {
            var element = JsonDocument.Parse(@"{
                ""polyline"": [[60, 15], [15, 15], [15, 50]],
                ""lines"": [
                  { ""startPoint"": [15, 15], ""endPoint"": [0, 0] },
                  { ""startPoint"": [15, 15], ""endPoint"": [30, 30] }
                ]
            }").RootElement;

            var cut = FaceCut.Parse(element);

            Assert.True(cut.IsMultiSegment);
            Assert.False(cut.IsEmpty);
            Assert.Equal(3, cut.PolylinePoints.Count);
            Assert.Equal(2, cut.Lines.Count);

            // Two polyline segments plus two lines, whichever order they come out in.
            Assert.Equal(4, cut.AllSegments().Count());
        }

        [Fact]
        public void CuttingCurveIdsParseAsAList()
        {
            var element = JsonDocument.Parse(
                @"{""cuttingCurveId"": ""a"", ""cuttingCurveIds"": [""b"", ""c""]}").RootElement;

            Assert.Equal(new[] { "a", "b", "c" }, FaceCut.Parse(element).AllCurveIds().ToArray());
        }

        [Fact]
        public void SeveralCurvesAllReachThePlan()
        {
            var cut = new FaceCut { CuttingCurveIds = new List<string> { "one", "two" } };
            var plan = FaceCutPlanner.Plan(LRoof(), cut, 0.001, 1.0);

            Assert.True(plan.Resolved);
            Assert.True(plan.UsesCurve);
            Assert.Equal(2, plan.CuttingCurveIds.Count);
        }

        [Fact]
        public void EverySegmentSurvivesPlanningAsItsOwnCut()
        {
            var plan = FaceCutPlanner.Plan(LRoof(), LGableCut(), 0.001, 1.0);

            Assert.True(plan.Resolved);
            Assert.Equal(4, plan.Segments.Count);
        }

        [Fact]
        public void SegmentsAreNotStretchedAcrossTheFace()
        {
            var plan = FaceCutPlanner.Plan(LRoof(), LGableCut(), 0.001, 1.0);

            // The single-line form extends to the bounding box; this one must not. A ridge
            // stretched to the frame would saw straight through the gable wall it stops at.
            var valley = plan.Segments.Single(
                s => s.Start.DistanceTo(new Vec3(15, 15, 12)) < 1e-6
                     && s.End.DistanceTo(new Vec3(30, 30, 12)) < 1e-6);

            Assert.Equal(15, valley.Start.X, 6);
            Assert.Equal(30, valley.End.X, 6);
        }

        [Fact]
        public void LooseEndsOnTheBoundaryAreNudgedPastIt()
        {
            var plan = FaceCutPlanner.Plan(LRoof(), LGableCut(), 0.001, 1.0);

            // (60, 15) and (15, 50) are free ends sitting on the frame boundary, so they run a
            // little past it — a cut landing a hair short leaves a sliver and fails the split.
            Assert.Contains(plan.Segments, s => s.Start.X > 60 || s.End.X > 60);
            Assert.Contains(plan.Segments, s => s.Start.Y > 50 || s.End.Y > 50);

            // All four cuts meet at the turning point (15, 15), so no one of them owns it alone
            // and it is never nudged — the ridge stays connected to the hip and the valley.
            Assert.Equal(4, plan.Segments.Count(
                s => s.Start.DistanceTo(new Vec3(15, 15, 12)) < 1e-6
                     || s.End.DistanceTo(new Vec3(15, 15, 12)) < 1e-6));
        }

        [Fact]
        public void InsideCornerIsLeftAloneBecauseItIsNotOnTheFrame()
        {
            var plan = FaceCutPlanner.Plan(LRoof(), LGableCut(), 0.001, 1.0);

            // (30, 30) is a real corner of an L but sits inside the bounding box, so the nudge
            // rule does not apply and the valley stops exactly where it was asked to.
            Assert.Contains(plan.Segments, s => s.End.DistanceTo(new Vec3(30, 30, 12)) < 1e-6);
        }

        [Fact]
        public void MultiSegmentPlanSaysTheRuleAppliesToTheSet()
        {
            var plan = FaceCutPlanner.Plan(LRoof(), LGableCut(), 0.001, 1.0);

            Assert.Contains("together they have to divide it", plan.Note);
        }

        [Fact]
        public void DegenerateSegmentsAreDroppedAndAnAllEmptyCutFails()
        {
            var withStub = new FaceCut
            {
                Lines = new List<double[][]>
                {
                    new[] { new double[] { 0, 0 }, new double[] { 30, 30 } },
                    new[] { new double[] { 10, 10 }, new double[] { 10, 10 } }
                }
            };

            Assert.Single(FaceCutPlanner.Plan(LRoof(), withStub, 0.001, 1.0).Segments);

            var allStubs = new FaceCut
            {
                Lines = new List<double[][]>
                {
                    new[] { new double[] { 10, 10 }, new double[] { 10, 10 } }
                }
            };

            var failed = FaceCutPlanner.Plan(LRoof(), allStubs, 0.001, 1.0);
            Assert.False(failed.Resolved);
            Assert.Contains("any length", failed.Error);
        }

        [Fact]
        public void SingleLineFormStillFillsSegmentsSoCallersCanReadOneList()
        {
            var plan = FaceCutPlanner.Plan(
                LRoof(),
                new FaceCut { LineStart = new double[] { 0, 15 }, LineEnd = new double[] { 60, 15 } },
                0.001, 1.0);

            Assert.True(plan.Resolved);
            Assert.Single(plan.Segments);
            Assert.Equal(plan.Start.X, plan.Segments[0].Start.X, 6);
            Assert.Equal(plan.End.X, plan.Segments[0].End.X, 6);
        }

        [Fact]
        public void RidgeEdgesAreToldApartFromTheHipAndValleyTheSameCutMade()
        {
            // What the Brep hands back after the four-curve split: the two ridge segments, the
            // hip, and the valley. Only the ridge pair may be raised — lifting the hip would
            // pull the outside corner into the air.
            var after = new List<EdgeSegment>
            {
                new EdgeSegment(new Vec3(15, 15, 12), new Vec3(60, 15, 12)),   // ridge A
                new EdgeSegment(new Vec3(15, 15, 12), new Vec3(15, 50, 12)),   // ridge B
                new EdgeSegment(new Vec3(15, 15, 12), new Vec3(0, 0, 12)),     // hip
                new EdgeSegment(new Vec3(15, 15, 12), new Vec3(30, 30, 12)),   // valley
                new EdgeSegment(new Vec3(0, 0, 12), new Vec3(60, 0, 12))       // an eave, unchanged
            };

            var ridge = new List<EdgeSegment>
            {
                new EdgeSegment(new Vec3(60, 15, 12), new Vec3(15, 15, 12)),
                new EdgeSegment(new Vec3(15, 15, 12), new Vec3(15, 50, 12))
            };

            var found = EdgeTopologyDiff.OnAnyLine(
                after, new[] { 0, 1, 2, 3 }, ridge, 0.001);

            Assert.Equal(new[] { 0, 1 }, found);
        }
    }
}
