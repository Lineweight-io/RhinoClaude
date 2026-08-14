using System.Linq;
using RhinoClaude.Semantic;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The analytical read tools (semantic plan §4.4, §4.5). These produce the numbers the
    /// reviewer treats as fact — plan §1.3's "facts to reviewer" principle — so a wrong one
    /// here is worse than no number at all.
    /// </summary>
    public class SemanticAnalyticsTests
    {
        private static readonly UnitContext Feet = UnitContext.Feet();

        // ── Wall-window ratio ─────────────────────────────────────────

        [Fact]
        public void WwrByOrientationReportsOneRowPerCompassSector()
        {
            var snapshot = new MassingSnapshot(
                Single(), new[] { SemanticFixture.OfficeBarGeometry() }, Feet);

            var report = WallWindowRatio.Compute(snapshot, WallWindowRatio.ScopeByOrientation);

            Assert.Equal(4, report.Results.Count);
            Assert.Equal(new[] { "N", "E", "S", "W" }, report.Results.Select(r => r.Key));
        }

        [Fact]
        public void OnlyTheGlazedFaceHasARatio()
        {
            var snapshot = new MassingSnapshot(
                Single(), new[] { SemanticFixture.OfficeBarGeometry() }, Feet);

            var report = WallWindowRatio.Compute(snapshot, WallWindowRatio.ScopeByOrientation);
            var south = report.Results.Single(r => r.Key == "S");
            var north = report.Results.Single(r => r.Key == "N");

            Assert.Equal(0.08, south.Ratio, 4);      // 12 windows × 24 ft² of 3600 ft²
            Assert.Equal(0, north.Ratio);
        }

        [Fact]
        public void RoofAndFloorFacesAreExcludedFromTheRatio()
        {
            // Counting the roof would drag the denominator up and give a smaller, wronger
            // number that the agent would then act on.
            var snapshot = new MassingSnapshot(
                Single(), new[] { SemanticFixture.OfficeBarGeometry() }, Feet);

            var report = WallWindowRatio.Compute(snapshot, WallWindowRatio.ScopeWhole);

            Assert.Equal(2 * 100 * 36 + 2 * 60 * 36, report.TotalFacadeArea, 4);
        }

        [Fact]
        public void TheOverallRatioIsOpeningAreaOverFacadeArea()
        {
            var snapshot = new MassingSnapshot(
                Single(), new[] { SemanticFixture.OfficeBarGeometry() }, Feet);

            var report = WallWindowRatio.Compute(snapshot, WallWindowRatio.ScopeWhole);

            Assert.Equal(288, report.TotalOpeningArea, 4);
            Assert.Equal(288.0 / 11520.0, report.OverallRatio, 4);
        }

        [Fact]
        public void GlazingIsBrokenDownByOpeningType()
        {
            var geometry = SemanticFixture.OfficeBarGeometry();
            var south = geometry.FaceAt(1);
            SemanticFixture.Opening(south, 99, SemanticVocabulary.OpeningStorefront, 20, 12, 0);

            var report = WallWindowRatio.Compute(
                new MassingSnapshot(Single(), new[] { geometry }, Feet), WallWindowRatio.ScopeByOrientation);

            var row = report.Results.Single(r => r.Key == "S");
            Assert.Equal(288, row.GlazingByType[SemanticVocabulary.OpeningWindow], 4);
            Assert.Equal(240, row.GlazingByType[SemanticVocabulary.OpeningStorefront], 4);
        }

        [Fact]
        public void UnclassifiedFaceAreaIsReportedRatherThanQuietlyDropped()
        {
            // Plan §5.7: the caller has to be able to say what the number excludes.
            var geometry = SemanticFixture.OfficeBarGeometry();
            geometry.Faces.Add(SemanticFixture.Face(
                "office", 6, "other", new Vec3(0.2, 0.2, 0.4), 500, 0, 36, new Vec3(50, 30, 18),
                SemanticVocabulary.RoleUnclassified));

            var report = WallWindowRatio.Compute(
                new MassingSnapshot(Single(), new[] { geometry }, Feet), WallWindowRatio.ScopeWhole);

            Assert.Equal(500, report.SkippedUnclassifiedArea, 4);
            Assert.Contains("could not be classified", string.Join(" ", report.Notes));
        }

        [Fact]
        public void NoFacadesGivesAnExplanationNotAZero()
        {
            var report = WallWindowRatio.Compute(
                new MassingSnapshot(new SemanticView(), null, Feet), WallWindowRatio.ScopeWhole);

            Assert.Empty(report.Results);
            Assert.Contains("no wall-window ratio to report", string.Join(" ", report.Notes));
        }

        [Fact]
        public void ShadingIsRefusedHonestlyRatherThanSilentlyIgnored()
        {
            var snapshot = new MassingSnapshot(
                Single(), new[] { SemanticFixture.OfficeBarGeometry() }, Feet);

            var report = WallWindowRatio.Compute(snapshot, WallWindowRatio.ScopeWhole, includeOverhangsAsShading: true);

            Assert.Contains("not modelled", string.Join(" ", report.Notes));
        }

        // ── Roof analysis ─────────────────────────────────────────────

        [Fact]
        public void AFlatRoofReportsZeroSlopeAndNoDrainageDirection()
        {
            var snapshot = new MassingSnapshot(
                Single(), new[] { SemanticFixture.OfficeBarGeometry() }, Feet);

            var report = RoofAnalysis.Compute(snapshot);

            var roof = Assert.Single(report.RoofFaces);
            Assert.Equal(0, roof.SlopePercent);
            Assert.Null(roof.DrainageDirection);
            Assert.Equal("flat", report.PredominantForm);
            Assert.Equal(6000, report.TotalRoofArea, 4);
        }

        [Fact]
        public void ParapetAndRidgeLengthsAreTotalledSeparately()
        {
            var snapshot = new MassingSnapshot(
                Single(), new[] { SemanticFixture.OfficeBarGeometry() }, Feet);

            var report = RoofAnalysis.Compute(snapshot);

            Assert.Equal(200, report.ParapetLength, 4);
            Assert.Equal(0, report.RidgeLength);
            Assert.Contains("drainage is internal", string.Join(" ", report.Notes));
        }

        [Fact]
        public void AGableReadsAsSlopedWithADrainageDirectionPerPlane()
        {
            var geometry = new MassGeometryView { MassId = "gable" };
            geometry.Faces.Add(SemanticFixture.Face("gable", 0, SemanticVocabulary.OrientationUp,
                new Vec3(0, -0.7, 0.7), 800, 20, 36, new Vec3(50, 15, 28), SemanticVocabulary.RoleRoof));
            geometry.Faces.Add(SemanticFixture.Face("gable", 1, SemanticVocabulary.OrientationUp,
                new Vec3(0, 0.7, 0.7), 800, 20, 36, new Vec3(50, 45, 28), SemanticVocabulary.RoleRoof));

            var report = RoofAnalysis.Compute(new MassingSnapshot(Single(), new[] { geometry }, Feet));

            Assert.Equal("sloped", report.PredominantForm);
            Assert.Equal("S", report.RoofFaces[0].DrainageDirection);
            Assert.Equal("N", report.RoofFaces[1].DrainageDirection);
        }

        [Fact]
        public void ACurvedRoofReadsAsComplex()
        {
            var geometry = new MassGeometryView { MassId = "shell" };
            var face = SemanticFixture.Face("shell", 0, SemanticVocabulary.OrientationUp,
                new Vec3(0, 0, 1), 2000, 20, 40, new Vec3(50, 30, 34), SemanticVocabulary.RoleRoof);
            face.IsPlanar = false;
            geometry.Faces.Add(face);

            Assert.Equal("complex", RoofAnalysis.Compute(new MassingSnapshot(Single(), new[] { geometry }, Feet))
                .PredominantForm);
        }

        // ── Program allocation ────────────────────────────────────────

        [Fact]
        public void ProgramAllocationSplitsVolumeByFunction()
        {
            var snapshot = SemanticFixture.MixedUse();

            var byFunction = ProgramAllocation.Compute(snapshot.View, out double total);

            Assert.Equal(216000 + 86400, total, 4);
            Assert.Equal(216000, byFunction["Office"].TotalVolume, 4);
            Assert.Equal(86400, byFunction["Retail"].TotalVolume, 4);
            Assert.Equal(100, byFunction.Values.Sum(v => v.PercentOfTotal), 1);
        }

        [Fact]
        public void AnEmptyModelAllocatesNothingRatherThanThrowing()
        {
            var byFunction = ProgramAllocation.Compute(new SemanticView(), out double total);

            Assert.Empty(byFunction);
            Assert.Equal(0, total);
        }

        // ── Massing composition ───────────────────────────────────────

        [Fact]
        public void CompositionRanksMassesAndNamesThePrimary()
        {
            var report = MassingComposition.Compute(SemanticFixture.MixedUse());

            Assert.Equal("office", report.PrimaryMassId);
            Assert.Equal(2, report.Ranked.Count);
            Assert.Equal(216000.0 / 86400.0, report.RatioPrimaryToSecondary.Value, 3);
        }

        [Fact]
        public void CompositionReportsProportionsAndTheDominantAxis()
        {
            var report = MassingComposition.Compute(SemanticFixture.MixedUse());

            Assert.Equal(160, report.OverallBbox.Size.X, 4);
            Assert.Equal("X (east-west)", report.DominantAxis);
            Assert.Equal(3, report.AspectRatios.Length);
        }

        [Fact]
        public void BooleanCompositionSaysWhereItsNumbersCameFromWhenHistoryIsOff()
        {
            // Plan §1.3: the reviewer gets numbers, and it also gets told how solid they are.
            var report = MassingComposition.Compute(SemanticFixture.MixedUse());

            Assert.Contains("history is not available", string.Join(" ", report.Notes));
            Assert.True(report.DifferenceCount > 0);   // the windows count as differences
        }

        [Fact]
        public void VerticalRhythmScoresACleanStackAtOne()
        {
            // 36 ft and 24 ft masses on a 12 ft floor-to-floor: three storeys and two, exactly.
            var report = MassingComposition.Compute(SemanticFixture.MixedUse(floorToFloor: 12));

            Assert.Equal(3, report.InferredLevelCount);
            Assert.Equal(1.0, report.FloorToFloorConsistency.Value, 4);
        }

        [Fact]
        public void AnIrregularStackScoresLower()
        {
            var report = MassingComposition.Compute(SemanticFixture.MixedUse(floorToFloor: 16));

            Assert.True(report.FloorToFloorConsistency.Value < 1.0);
        }

        [Fact]
        public void WithoutAFloorToFloorVerticalRhythmSaysSoRatherThanGuessing()
        {
            var report = MassingComposition.Compute(SemanticFixture.MixedUse(floorToFloor: 0));

            Assert.Null(report.FloorToFloorConsistency);
            Assert.Contains("No floor-to-floor default", string.Join(" ", report.Notes));
        }

        [Fact]
        public void AnEmptyModelSaysThereIsNothingToAnalyse()
        {
            var report = MassingComposition.Compute(new MassingSnapshot(new SemanticView(), null, Feet));

            Assert.Contains("No masses", string.Join(" ", report.Notes));
        }

        // ── Zoning envelope ───────────────────────────────────────────

        private static MassingSnapshot WithLot(double lotMaxX = 200, double lotMaxY = 100)
        {
            var snapshot = SemanticFixture.MixedUse();
            snapshot.View.SiteElements.Add(SemanticFixture.PropertyLine(
                new Vec3(-20, -20, 0), new Vec3(lotMaxX, lotMaxY, 0), (lotMaxX + 20) * (lotMaxY + 20)));
            return snapshot;
        }

        [Fact]
        public void ACompliantBuildingReportsNoViolations()
        {
            var report = ZoningEnvelope.Compute(WithLot(), new ZoningParameters
            {
                MaxHeight = 60,
                SetbackNorth = 10, SetbackEast = 10, SetbackSouth = 10, SetbackWest = 10
            });

            Assert.Empty(report.Violations);
            Assert.Equal("compliant", report.ComplianceStatus);
            Assert.Equal(36, report.CurrentHeight, 4);
        }

        [Fact]
        public void AHeightBreachNamesTheMassesResponsible()
        {
            var report = ZoningEnvelope.Compute(WithLot(), new ZoningParameters
            {
                MaxHeight = 30,
                SetbackNorth = 10, SetbackEast = 10, SetbackSouth = 10, SetbackWest = 10
            });

            var violation = Assert.Single(report.Violations.Where(v => v.Type == "height"));
            Assert.Equal(6, violation.Amount, 4);
            Assert.Contains("office", violation.Ids);
            Assert.DoesNotContain("retail", violation.Ids);   // the 24 ft plinth is under the limit
            Assert.Equal("violations", report.ComplianceStatus);
        }

        [Fact]
        public void ASetbackBreachReportsTheSideAndTheOverrun()
        {
            // Lot runs to x = 120 with a 10 ft east setback, so the buildable edge is x = 110;
            // the retail plinth runs to x = 160.
            var report = ZoningEnvelope.Compute(WithLot(lotMaxX: 120), new ZoningParameters
            {
                MaxHeight = 60, SetbackEast = 10
            });

            var violation = Assert.Single(report.Violations.Where(v => v.Type == "setback" && v.Side == "E"));
            Assert.Equal(50, violation.Amount, 4);
            Assert.Contains("retail", violation.Ids);
        }

        [Fact]
        public void MultiplePropertyLinesAreNeverPickedBetweenSilently()
        {
            // Plan §10.2 question 6.
            var snapshot = WithLot();
            var second = SemanticFixture.PropertyLine(new Vec3(0, 0, 0), new Vec3(50, 50, 0), 2500);
            second.ElementId = "lot-2";
            snapshot.View.SiteElements.Add(second);

            var report = ZoningEnvelope.Compute(snapshot, new ZoningParameters { MaxHeight = 60 });

            Assert.NotNull(report.Error);
            Assert.Contains("propertyLineElementId", report.Error);
        }

        [Fact]
        public void NamingThePropertyLineResolvesTheAmbiguity()
        {
            var snapshot = WithLot();
            var second = SemanticFixture.PropertyLine(new Vec3(0, 0, 0), new Vec3(50, 50, 0), 2500);
            second.ElementId = "lot-2";
            snapshot.View.SiteElements.Add(second);

            var report = ZoningEnvelope.Compute(snapshot, new ZoningParameters
            {
                MaxHeight = 60,
                PropertyLineElementId = "lot"
            });

            Assert.Null(report.Error);
        }

        [Fact]
        public void WithoutAPropertyLineSetbacksAreSkippedWithAWarningNotAFailure()
        {
            var report = ZoningEnvelope.Compute(
                SemanticFixture.MixedUse(), new ZoningParameters { MaxHeight = 60, SetbackNorth = 10 });

            Assert.Null(report.Error);
            Assert.Empty(report.Violations.Where(v => v.Type == "setback"));
            Assert.Contains("No property line", string.Join(" ", report.Notes));
            Assert.Equal("warnings", report.ComplianceStatus);
        }

        [Fact]
        public void FarIsComputedFromGrossFloorAreaOverLotArea()
        {
            // Volume 302 400 ÷ 12 ft floor-to-floor = 25 200 ft² gross, over a 26 400 ft² lot.
            var report = ZoningEnvelope.Compute(WithLot(), new ZoningParameters
            {
                MaxHeight = 60, FarMax = 3.0
            });

            Assert.Equal(0.9545, report.Far.Value, 4);
            Assert.Empty(report.Violations.Where(v => v.Type == "far"));
        }

        [Fact]
        public void AnFarBreachIsReportedWithTheOverrunInFarPoints()
        {
            var report = ZoningEnvelope.Compute(WithLot(), new ZoningParameters
            {
                MaxHeight = 60, FarMax = 0.5
            });

            var violation = Assert.Single(report.Violations.Where(v => v.Type == "far"));
            Assert.True(violation.Amount > 0.4);
        }

        [Fact]
        public void AnOpenPropertyLineIsFlaggedAsApproximate()
        {
            var snapshot = SemanticFixture.MixedUse();
            snapshot.View.SiteElements.Add(SemanticFixture.PropertyLine(
                new Vec3(-20, -20, 0), new Vec3(200, 100, 0), 26400, closed: false));

            var report = ZoningEnvelope.Compute(snapshot, new ZoningParameters { MaxHeight = 60 });

            Assert.Contains("only right for a rectangular lot", string.Join(" ", report.Notes));
        }

        [Fact]
        public void AModelWithNoMassesCannotBeCheckedAndSaysSo()
        {
            var report = ZoningEnvelope.Compute(
                new MassingSnapshot(new SemanticView(), null, Feet), new ZoningParameters { MaxHeight = 60 });

            Assert.Contains("no masses", report.Error);
        }

        // ── Face relationships ────────────────────────────────────────

        [Fact]
        public void OppositeWallsOfABoxAreParallelAndFacingEachOther()
        {
            var geometry = SemanticFixture.OfficeBarGeometry();

            var report = FaceRelationships.Compute(geometry.Faces, 0.01);

            var pair = report.ParallelPairs.Single(p =>
                (p.A.EndsWith(":0") && p.B.EndsWith(":1")) || (p.A.EndsWith(":1") && p.B.EndsWith(":0")));
            Assert.True(pair.FacingEachOther);
            Assert.Equal(60, pair.Offset, 4);
        }

        [Fact]
        public void AdjacentWallsOfABoxArePerpendicular()
        {
            var report = FaceRelationships.Compute(SemanticFixture.OfficeBarGeometry().Faces, 0.01);

            // Four wall-to-neighbouring-wall pairs, plus each of the four walls against the
            // roof and against the floor.
            Assert.Equal(12, report.PerpendicularPairs.Count);
        }

        [Fact]
        public void FacesInOnePlaneAcrossTwoMassesReportAsFlush()
        {
            var a = SemanticFixture.OfficeBarGeometry("a");
            var b = SemanticFixture.OfficeBarGeometry("b");

            var faces = a.Faces.Concat(b.Faces).ToList();
            var report = FaceRelationships.Compute(faces, 0.01);

            Assert.NotEmpty(report.FlushAlignments);
            Assert.Contains(report.FlushAlignments, f => f.Notes.Contains("2 masses"));
        }

        [Fact]
        public void CurvedFacesAreSkippedAndTheSkipIsReported()
        {
            var geometry = SemanticFixture.OfficeBarGeometry();
            var curved = SemanticFixture.Face("office", 6, "other", new Vec3(1, 0, 0), 400, 0, 36,
                new Vec3(120, 30, 18), SemanticVocabulary.RoleFacade);
            curved.IsPlanar = false;
            geometry.Faces.Add(curved);

            var report = FaceRelationships.Compute(geometry.Faces, 0.01);

            Assert.Equal(6, report.FacesConsidered);
            Assert.Contains("non-planar", string.Join(" ", report.Notes));
        }

        [Fact]
        public void ALargeScopeIsCappedAndTheCapIsReported()
        {
            // Plan's "no silent caps" instinct: a truncated comparison must not read as complete.
            var geometry = new MassGeometryView { MassId = "big" };
            for (int i = 0; i < 30; i++)
                geometry.Faces.Add(SemanticFixture.Face("big", i, "N", new Vec3(0, 1, 0), 100 + i, 0, 10,
                    new Vec3(i * 10, 50, 5), SemanticVocabulary.RoleFacade));

            var report = FaceRelationships.Compute(geometry.Faces, 0.01, maxFaces: 10);

            Assert.Equal(10, report.FacesConsidered);
            Assert.Contains("only the 10 largest", string.Join(" ", report.Notes));
        }

        private static SemanticView Single()
        {
            var view = new SemanticView { UnitSystem = "Feet", FloorToFloorDefault = 12 };
            view.Masses.Add(SemanticFixture.Mass("office", new Vec3(0, 0, 0), new Vec3(100, 60, 36)));
            return view;
        }
    }
}
