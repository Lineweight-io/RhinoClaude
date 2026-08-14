using System.Linq;
using System.Text.Json;
using RhinoClaude.Semantic;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The FaceSelector union (semantic plan §4 preamble) is how every write tool picks the
    /// face it operates on. "push_pull_face(mass, {role: roof}, 6)" has to land on the roof
    /// and nothing else — selection is where a wrong answer becomes a wrong edit.
    /// </summary>
    public class FaceSelectorTests
    {
        /// <summary>
        /// A stepped office bar: four walls, a big north face and a smaller setback north face
        /// above it, a roof, and a base.
        /// </summary>
        private static MassGeometryView SteppedMass()
        {
            var view = new MassGeometryView { MassId = "mass-1", SourceObjectId = "obj-1" };

            void Add(int index, string orientation, string role, double area, double zMin, double zMax, Vec3 normal)
            {
                var face = new FaceView
                {
                    FaceId = "mass-1:face:" + index,
                    MassId = "mass-1",
                    FaceIndex = index,
                    Orientation = orientation,
                    Area = area,
                    ElevationMin = zMin,
                    ElevationMax = zMax,
                    Normal = normal,
                    IsPlanar = true,
                    Centroid = new Vec3(0, 0, (zMin + zMax) / 2)
                };
                face.Roles.Add(role);
                view.Faces.Add(face);
            }

            Add(0, "N", SemanticVocabulary.RoleFacade, 720, 0, 24, new Vec3(0, 1, 0));    // main north wall
            Add(1, "N", SemanticVocabulary.RoleFacade, 240, 24, 36, new Vec3(0, 1, 0));   // setback above
            Add(2, "S", SemanticVocabulary.RoleFacade, 900, 0, 36, new Vec3(0, -1, 0));
            Add(3, "E", SemanticVocabulary.RoleFacade, 600, 0, 36, new Vec3(1, 0, 0));
            Add(4, "W", SemanticVocabulary.RoleFacade, 600, 0, 36, new Vec3(-1, 0, 0));
            Add(5, SemanticVocabulary.OrientationUp, SemanticVocabulary.RoleRoof, 1500, 36, 36, new Vec3(0, 0, 1));
            Add(6, SemanticVocabulary.OrientationDown, SemanticVocabulary.RoleFloor, 1500, 0, 0, new Vec3(0, 0, -1));

            for (int i = 0; i < view.Faces.Count; i++)
            {
                view.Edges.Add(new EdgeView
                {
                    EdgeId = "mass-1:edge:" + i,
                    MassId = "mass-1",
                    EdgeIndex = i,
                    Role = i < 4 ? SemanticVocabulary.EdgeOutsideCorner : SemanticVocabulary.EdgeParapet,
                    Length = 36
                });
            }

            return view;
        }

        // ── Parsing ───────────────────────────────────────────────────

        private static FaceSelector Parse(string json) =>
            FaceSelector.Parse(JsonDocument.Parse(json).RootElement);

        [Fact]
        public void EachArmOfTheUnionParses()
        {
            Assert.Equal("f-1", Parse("{\"faceId\":\"f-1\"}").FaceId);
            Assert.Equal(3, Parse("{\"faceIndex\":3}").FaceIndex);
            Assert.Equal("N", Parse("{\"orientation\":\"N\"}").Orientation);
            Assert.Equal(SemanticVocabulary.RoleRoof, Parse("{\"role\":\"roof\"}").Role);

            var compound = Parse("{\"role\":\"facade\",\"orientation\":\"N\"}");
            Assert.Equal(SemanticVocabulary.RoleFacade, compound.Role);
            Assert.Equal("N", compound.Orientation);
        }

        [Fact]
        public void AnOutOfVocabularyValueIsDroppedRatherThanPassedThrough()
        {
            // "gable" is not a role. Silently keeping it would produce zero matches with a
            // confusing message; dropping it makes the selector empty and says so.
            Assert.Null(Parse("{\"role\":\"gable\"}").Role);
            Assert.True(Parse("{\"role\":\"gable\"}").IsEmpty);
        }

        [Fact]
        public void AnElevationRangeIsNormalisedLowToHigh()
        {
            var selector = Parse("{\"orientation\":\"S\",\"elevationRange\":[12,0]}");

            Assert.Equal(new[] { 0.0, 12.0 }, selector.ElevationRange);
        }

        [Fact]
        public void AnEmptySelectorIsRejectedWithAUsefulMessage()
        {
            var result = FaceSelectorResolver.Resolve(SteppedMass(), new FaceSelector());

            Assert.False(result.Resolved);
            Assert.Contains("faceSelector is required", result.Error);
        }

        // ── Resolution ────────────────────────────────────────────────

        [Fact]
        public void AFaceIdResolvesExactly()
        {
            var result = FaceSelectorResolver.Resolve(SteppedMass(), new FaceSelector { FaceId = "mass-1:face:5" });

            Assert.True(result.Resolved);
            Assert.Equal(5, result.Face.FaceIndex);
        }

        [Fact]
        public void AStaleFaceIdSaysWhyItIsStale()
        {
            var result = FaceSelectorResolver.Resolve(SteppedMass(), new FaceSelector { FaceId = "mass-1:face:99" });

            Assert.False(result.Resolved);
            Assert.Contains("they change when the Brep does", result.Error);
        }

        [Fact]
        public void AnOutOfRangeIndexReportsTheValidRange()
        {
            var result = FaceSelectorResolver.Resolve(SteppedMass(), new FaceSelector { FaceIndex = 42 });

            Assert.False(result.Resolved);
            Assert.Contains("valid indices 0..6", result.Error);
        }

        [Fact]
        public void TheRoofRoleResolvesToTheRoofFace()
        {
            var result = FaceSelectorResolver.Resolve(
                SteppedMass(), new FaceSelector { Role = SemanticVocabulary.RoleRoof });

            Assert.True(result.Resolved);
            Assert.Equal(5, result.Face.FaceIndex);
        }

        [Fact]
        public void AmbiguousMatchesPickTheLargestAndSayThatTheyDid()
        {
            // Two north faces on a stepped mass. "The north facade" means the big one.
            var result = FaceSelectorResolver.Resolve(SteppedMass(), new FaceSelector { Orientation = "N" });

            Assert.True(result.Resolved);
            Assert.Equal(0, result.Face.FaceIndex);
            Assert.Equal(2, result.Candidates.Count);
            Assert.Contains("2 faces matched", result.Note);
            Assert.Contains("Pass a faceId", result.Note);
        }

        [Fact]
        public void TheCompoundSelectorNarrowsByBothRoleAndOrientation()
        {
            var matches = FaceSelectorResolver.Filter(
                SteppedMass().Faces,
                new FaceSelector { Role = SemanticVocabulary.RoleFacade, Orientation = "S" }).ToList();

            Assert.Single(matches);
            Assert.Equal(2, matches[0].FaceIndex);
        }

        [Fact]
        public void AnElevationBandPicksTheGroundFloorPortionOfAsteppedFacade()
        {
            // Plan §11 example 15: "recess the ground-floor south face inward by 8 feet".
            var matches = FaceSelectorResolver.Filter(
                SteppedMass().Faces,
                new FaceSelector { Orientation = "N", ElevationRange = new[] { 0.0, 12.0 } }).ToList();

            Assert.Single(matches);
            Assert.Equal(0, matches[0].FaceIndex);
        }

        [Fact]
        public void TheElevationBandOverlapsRatherThanContains()
        {
            // A face spanning 0-24 must be caught by a 0-12 band; requiring containment would
            // silently miss every full-height wall.
            var matches = FaceSelectorResolver.Filter(
                SteppedMass().Faces,
                new FaceSelector { Role = SemanticVocabulary.RoleFacade, ElevationRange = new[] { 0.0, 6.0 } }).ToList();

            Assert.Equal(4, matches.Count);
            Assert.DoesNotContain(matches, f => f.FaceIndex == 1);   // the setback starts at 24
        }

        [Fact]
        public void NoMatchExplainsWhatTheMassActuallyHas()
        {
            var result = FaceSelectorResolver.Resolve(
                SteppedMass(), new FaceSelector { Orientation = "NE" });

            Assert.False(result.Resolved);
            Assert.Contains("faces oriented", result.Error);
            Assert.Contains("roles", result.Error);
        }

        [Fact]
        public void AMissingGeometryViewFailsCleanly()
        {
            var result = FaceSelectorResolver.Resolve(null, new FaceSelector { Role = "roof" });

            Assert.False(result.Resolved);
            Assert.Contains("No geometry", result.Error);
        }

        // ── Edge selectors ────────────────────────────────────────────

        [Fact]
        public void OneRoleSelectorCoversEveryEdgeWithThatRole()
        {
            // Plan §11 example 14: "fillet all outside corners" is one selector value.
            var edges = EdgeSelector.ResolveAll(
                SteppedMass(),
                new[] { new EdgeSelector { Role = SemanticVocabulary.EdgeOutsideCorner } });

            Assert.Equal(4, edges.Count);
        }

        [Fact]
        public void OverlappingSelectorsDoNotFilletAnEdgeTwice()
        {
            var edges = EdgeSelector.ResolveAll(SteppedMass(), new[]
            {
                new EdgeSelector { Role = SemanticVocabulary.EdgeOutsideCorner },
                new EdgeSelector { EdgeIndex = 0 },
                new EdgeSelector { EdgeId = "mass-1:edge:1" }
            });

            Assert.Equal(4, edges.Count);
            Assert.Equal(4, edges.Select(e => e.EdgeIndex).Distinct().Count());
        }

        [Fact]
        public void AnEmptyEdgeSelectorContributesNothing()
        {
            Assert.Empty(EdgeSelector.ResolveAll(SteppedMass(), new[] { new EdgeSelector() }));
            Assert.Empty(EdgeSelector.ResolveAll(SteppedMass(), null));
        }

        [Fact]
        public void EdgeSelectorsParseFromJson()
        {
            var byRole = EdgeSelector.Parse(JsonDocument.Parse("{\"role\":\"outside-corner\"}").RootElement);
            Assert.Equal(SemanticVocabulary.EdgeOutsideCorner, byRole.Role);

            var byIndex = EdgeSelector.Parse(JsonDocument.Parse("{\"edgeIndex\":7}").RootElement);
            Assert.Equal(7, byIndex.EdgeIndex);
        }
    }
}
