using System.Linq;
using RhinoClaude.Semantic;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// Face and edge labelling — semantic plan §3.2, §3.3, §5.5. These are the rules that turn
    /// "a Brep with six faces" into "the north facade of the office mass", so they carry most
    /// of the plan's weight and all of its risk.
    /// </summary>
    public class FaceAndEdgeClassifierTests
    {
        private static readonly UnitContext Feet = UnitContext.Feet();

        // ── Orientation ───────────────────────────────────────────────

        [Theory]
        [InlineData(0, 1, 0, "N")]
        [InlineData(1, 0, 0, "E")]
        [InlineData(0, -1, 0, "S")]
        [InlineData(-1, 0, 0, "W")]
        [InlineData(1, 1, 0, "NE")]
        [InlineData(1, -1, 0, "SE")]
        [InlineData(-1, -1, 0, "SW")]
        [InlineData(-1, 1, 0, "NW")]
        public void AVerticalFaceTakesTheCompassSectorOfItsNormal(double x, double y, double z, string expected)
        {
            Assert.Equal(expected, FaceClassifier.Orientation(new Vec3(x, y, z)));
        }

        [Fact]
        public void ASectorSpansFortyFiveDegreesCentredOnItsCardinal()
        {
            // 20° east of north is still north; 30° is not.
            Assert.Equal("N", FaceClassifier.CompassSector(0.34, 0.94));
            Assert.Equal("NE", FaceClassifier.CompassSector(0.5, 0.87));
        }

        [Fact]
        public void UpAndDownFacingFacesGetVerticalOrientations()
        {
            Assert.Equal(SemanticVocabulary.OrientationUp, FaceClassifier.Orientation(new Vec3(0, 0, 1)));
            Assert.Equal(SemanticVocabulary.OrientationDown, FaceClassifier.Orientation(new Vec3(0, 0, -1)));
        }

        [Fact]
        public void ASlopedRoofStillReadsAsUpFacing()
        {
            // 4:12 pitch. Vertical component dominates, so it is a roof, not a facade.
            Assert.Equal(SemanticVocabulary.OrientationUp, FaceClassifier.Orientation(new Vec3(0, 0.32, 0.95)));
        }

        [Fact]
        public void ACurvedSidewaysFaceHasNoSingleOrientation()
        {
            // Plan risk #3: a cylindrical mass's exterior is one Brep face wrapping every
            // compass point. Calling it "N" would be a lie the agent would act on.
            Assert.Equal(SemanticVocabulary.OrientationOther,
                FaceClassifier.Orientation(new Vec3(1, 0, 0), isPlanar: false));
        }

        [Fact]
        public void ACurvedButDecisivelyUpwardFaceIsStillUp()
        {
            // A domed roof has one curved face, and it is unambiguously a roof.
            Assert.Equal(SemanticVocabulary.OrientationUp,
                FaceClassifier.Orientation(new Vec3(0, 0, 1), isPlanar: false));
        }

        [Fact]
        public void ADegenerateNormalIsOtherRatherThanAnArbitrarySector()
        {
            Assert.Equal(SemanticVocabulary.OrientationOther, FaceClassifier.Orientation(Vec3.Zero));
        }

        // ── Roles ─────────────────────────────────────────────────────

        private static FaceFacts Facts(Vec3 normal, double zMin = 0, double zMax = 36, double massBase = 0)
        {
            return new FaceFacts
            {
                Normal = normal,
                IsPlanar = true,
                ElevationMin = zMin,
                ElevationMax = zMax,
                MassBaseElevation = massBase,
                BaseTolerance = 0.01
            };
        }

        [Fact]
        public void AVerticalExteriorFaceIsAFacade()
        {
            var roles = FaceClassifier.Roles(Facts(new Vec3(0, 1, 0)), out _);

            Assert.Contains(SemanticVocabulary.RoleFacade, roles);
            Assert.Single(roles);
        }

        [Fact]
        public void AnUpFacingExteriorFaceIsARoof()
        {
            var roles = FaceClassifier.Roles(Facts(new Vec3(0, 0, 1), zMin: 36, zMax: 36), out _);

            Assert.Contains(SemanticVocabulary.RoleRoof, roles);
        }

        [Fact]
        public void TheDownFacingFaceAtTheMassBaseIsAFloor()
        {
            var roles = FaceClassifier.Roles(Facts(new Vec3(0, 0, -1), zMin: 0, zMax: 0), out _);

            Assert.Contains(SemanticVocabulary.RoleFloor, roles);
        }

        [Fact]
        public void ADownFacingFaceHighAboveTheBaseIsASoffitNotAFloor()
        {
            var roles = FaceClassifier.Roles(Facts(new Vec3(0, 0, -1), zMin: 24, zMax: 24), out string note);

            Assert.DoesNotContain(SemanticVocabulary.RoleFloor, roles);
            Assert.Contains(SemanticVocabulary.RoleUnclassified, roles);
            Assert.Contains("soffit", note);
        }

        [Fact]
        public void AFaceAgainstAnotherMassIsAPartyWallNotAFacade()
        {
            var facts = Facts(new Vec3(0, 1, 0));
            facts.CoincidentWithAnotherMass = true;

            var roles = FaceClassifier.Roles(facts, out _);

            Assert.Equal(new[] { SemanticVocabulary.RolePartyWall }, roles);
        }

        [Fact]
        public void AFaceBoundingAVoidIsInteriorWhateverItsNormal()
        {
            var facts = Facts(new Vec3(0, 0, 1));
            facts.BoundsInteriorVoid = true;

            Assert.Equal(new[] { SemanticVocabulary.RoleInterior }, FaceClassifier.Roles(facts, out _));
        }

        [Fact]
        public void AFaceNearTheTiltThresholdCarriesBothReadings()
        {
            // Plan §5.5: hand the agent both labels rather than being confidently wrong about
            // a sloped-roof-meets-wall face.
            var roles = FaceClassifier.Roles(Facts(new Vec3(0, 0.955, 0.295)), out string note);

            Assert.Contains(SemanticVocabulary.RoleFacade, roles);
            Assert.Contains(SemanticVocabulary.RoleRoof, roles);
            Assert.Contains("both readings", note);
        }

        [Fact]
        public void RolesAreNeverEmpty()
        {
            var roles = FaceClassifier.Roles(Facts(Vec3.Zero), out _);

            Assert.Contains(SemanticVocabulary.RoleUnclassified, roles);
        }

        // ── Slope and drainage ────────────────────────────────────────

        [Fact]
        public void AFlatRoofHasZeroSlopeAndNoDrainageDirection()
        {
            Assert.Equal(0, FaceClassifier.SlopePercent(new Vec3(0, 0, 1)));
            Assert.Null(FaceClassifier.DrainageDirection(new Vec3(0, 0, 1)));
        }

        [Fact]
        public void AFortyFiveDegreeRoofIsOneHundredPercentSlope()
        {
            Assert.Equal(100, FaceClassifier.SlopePercent(new Vec3(0, 1, 1)), 6);
        }

        [Fact]
        public void WaterRunsOffInTheDirectionTheRoofNormalLeans()
        {
            // A roof tilted so its normal leans south sheds water to the south.
            Assert.Equal("S", FaceClassifier.DrainageDirection(new Vec3(0, -1, 2)));
            Assert.Equal("E", FaceClassifier.DrainageDirection(new Vec3(1, 0, 2)));
        }

        [Fact]
        public void OppositeFlipsACompassSectorByHalfATurn()
        {
            Assert.Equal("S", FaceClassifier.Opposite("N"));
            Assert.Equal("NW", FaceClassifier.Opposite("SE"));
        }

        // ── Edges ─────────────────────────────────────────────────────

        /// <summary>
        /// Two faces of a box meeting at the north-east vertical edge, with the solid to the
        /// south-west of it. The north face is at y = +50, the east face at x = +30.
        /// </summary>
        private static EdgeFacts BoxCorner()
        {
            return new EdgeFacts
            {
                AdjacentFaceCount = 2,
                FaceARoles = { SemanticVocabulary.RoleFacade },
                FaceBRoles = { SemanticVocabulary.RoleFacade },
                FaceANormal = new Vec3(0, 1, 0),
                FaceBNormal = new Vec3(1, 0, 0),
                FaceACentroid = new Vec3(15, 50, 18),
                FaceBCentroid = new Vec3(30, 25, 18),
                Midpoint = new Vec3(30, 50, 18),
                Length = 36,
                IsLinear = true
            };
        }

        [Fact]
        public void TwoFacadesMeetingConvexlyAreAnOutsideCorner()
        {
            Assert.Equal(SemanticVocabulary.EdgeOutsideCorner, EdgeClassifier.Role(BoxCorner()));
            Assert.True(EdgeClassifier.IsConvex(BoxCorner()));
        }

        [Fact]
        public void TheReenteringCornerOfAnLPlanIsAnInsideCorner()
        {
            // Solid fills the L; the notch is at high x, high y. At the re-entrant edge the two
            // wall faces look away from each other.
            var facts = new EdgeFacts
            {
                AdjacentFaceCount = 2,
                FaceARoles = { SemanticVocabulary.RoleFacade },
                FaceBRoles = { SemanticVocabulary.RoleFacade },
                FaceANormal = new Vec3(1, 0, 0),
                FaceBNormal = new Vec3(0, 1, 0),
                FaceACentroid = new Vec3(20, 35, 10),
                FaceBCentroid = new Vec3(35, 20, 10),
                Midpoint = new Vec3(20, 20, 10)
            };

            Assert.False(EdgeClassifier.IsConvex(facts));
            Assert.Equal(SemanticVocabulary.EdgeInsideCorner, EdgeClassifier.Role(facts));
        }

        [Fact]
        public void ARoofAboveAWallMeetsItAtAParapet()
        {
            var facts = new EdgeFacts
            {
                AdjacentFaceCount = 2,
                FaceARoles = { SemanticVocabulary.RoleRoof },
                FaceBRoles = { SemanticVocabulary.RoleFacade },
                FaceANormal = new Vec3(0, 0, 1),
                FaceBNormal = new Vec3(0, 1, 0),
                FaceACentroid = new Vec3(15, 25, 40),
                FaceBCentroid = new Vec3(15, 50, 18),
                Midpoint = new Vec3(15, 50, 40)
            };

            Assert.Equal(SemanticVocabulary.EdgeParapet, EdgeClassifier.Role(facts));
        }

        [Fact]
        public void ANakedRoofEdgeIsAnEave()
        {
            var facts = new EdgeFacts
            {
                AdjacentFaceCount = 1,
                FaceARoles = { SemanticVocabulary.RoleRoof },
                FaceANormal = new Vec3(0, 0, 1),
                FaceACentroid = new Vec3(15, 25, 40),
                Midpoint = new Vec3(15, 50, 40)
            };

            Assert.Equal(SemanticVocabulary.EdgeEave, EdgeClassifier.Role(facts));
        }

        [Fact]
        public void TwoRoofPlanesMeetingConvexlyMakeARidge()
        {
            var facts = new EdgeFacts
            {
                AdjacentFaceCount = 2,
                FaceARoles = { SemanticVocabulary.RoleRoof },
                FaceBRoles = { SemanticVocabulary.RoleRoof },
                FaceANormal = new Vec3(0, -0.7, 0.7),
                FaceBNormal = new Vec3(0, 0.7, 0.7),
                FaceACentroid = new Vec3(15, 12, 36),
                FaceBCentroid = new Vec3(15, 38, 36),
                Midpoint = new Vec3(15, 25, 42)
            };

            Assert.Equal(SemanticVocabulary.EdgeRoofRidge, EdgeClassifier.Role(facts));
        }

        [Fact]
        public void ASeamBetweenCoplanarFacesIsNotACorner()
        {
            // Two Brep faces of the same wall plane, split by an earlier operation. An agent
            // told to "fillet all outside corners" must not round a seam.
            var facts = new EdgeFacts
            {
                AdjacentFaceCount = 2,
                FaceARoles = { SemanticVocabulary.RoleFacade },
                FaceBRoles = { SemanticVocabulary.RoleFacade },
                FaceANormal = new Vec3(0, 1, 0),
                FaceBNormal = new Vec3(0, 1, 0),
                FaceACentroid = new Vec3(5, 50, 18),
                FaceBCentroid = new Vec3(25, 50, 18),
                Midpoint = new Vec3(15, 50, 18)
            };

            Assert.True(EdgeClassifier.SameDirection(facts));
            Assert.Equal(SemanticVocabulary.EdgeOther, EdgeClassifier.Role(facts));
        }

        [Fact]
        public void AnEdgeWithNoRecognisedFacesIsOther()
        {
            Assert.Equal(SemanticVocabulary.EdgeOther, EdgeClassifier.Role(new EdgeFacts()));
            Assert.Equal(SemanticVocabulary.EdgeOther, EdgeClassifier.Role(null));
        }

        // ── Opening subtype inference ─────────────────────────────────

        [Fact]
        public void AFullHeightNarrowOpeningAtFloorLevelIsADoor()
        {
            var facts = new OpeningFacts { Width = 3.5, Height = 7, SillHeight = 0, Area = 24.5 };

            Assert.Equal(SemanticVocabulary.OpeningDoor, OpeningClassifier.InferType(facts, Feet, out _));
        }

        [Fact]
        public void AWideFullHeightOpeningAtFloorLevelIsAStorefront()
        {
            var facts = new OpeningFacts { Width = 20, Height = 10, SillHeight = 0, Area = 200 };

            Assert.Equal(SemanticVocabulary.OpeningStorefront, OpeningClassifier.InferType(facts, Feet, out _));
        }

        [Fact]
        public void AnOpeningWithATypicalSillIsAWindow()
        {
            var facts = new OpeningFacts { Width = 5, Height = 4, SillHeight = 2.5, Area = 20 };

            Assert.Equal(SemanticVocabulary.OpeningWindow, OpeningClassifier.InferType(facts, Feet, out _));
        }

        [Fact]
        public void AWholeBayOfGlassIsACurtainWall()
        {
            var facts = new OpeningFacts { Width = 30, Height = 24, SillHeight = 0, Area = 720 };

            Assert.Equal(SemanticVocabulary.OpeningCurtainWall, OpeningClassifier.InferType(facts, Feet, out _));
        }

        [Fact]
        public void EveryInferredSubtypeSaysItWasInferred()
        {
            // The agent has to be able to tell a measured fact from a rule of thumb.
            OpeningClassifier.InferType(
                new OpeningFacts { Width = 5, Height = 4, SillHeight = 2.5, Area = 20 }, Feet, out string note);

            Assert.StartsWith("Inferred", note);
        }

        [Fact]
        public void ASliverLoopIsNotAnOpening()
        {
            Assert.False(OpeningClassifier.IsSignificant(0.2, Feet));
            Assert.True(OpeningClassifier.IsSignificant(18, Feet));
        }

        [Fact]
        public void TheOpeningThresholdConvertsWithTheDocumentUnits()
        {
            var millimetres = new UnitContext(304.8, "Millimeters");

            // 1 ft² is roughly 92 900 mm².
            Assert.False(OpeningClassifier.IsSignificant(1000, millimetres));
            Assert.True(OpeningClassifier.IsSignificant(2000000, millimetres));
        }

        [Fact]
        public void SubtypeInferenceHoldsInAMillimetreDocument()
        {
            var millimetres = new UnitContext(304.8, "Millimeters");
            var door = new OpeningFacts
            {
                Width = 1067,          // 3'-6"
                Height = 2134,         // 7'-0"
                SillHeight = 0,
                Area = 1067.0 * 2134.0
            };

            Assert.Equal(SemanticVocabulary.OpeningDoor, OpeningClassifier.InferType(door, millimetres, out _));
        }
    }
}
