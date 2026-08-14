using System.Collections.Generic;
using System.Linq;
using RhinoClaude.Semantic;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// How masses relate (semantic plan §3.10) and how they group (§3.9). These feed
    /// describe_massing's narrative and check_massing_composition's numbers, both of which the
    /// reviewer reads as fact — so they have to be right about the easy cases and honest about
    /// the hard ones.
    /// </summary>
    public class CompositionAnalyzerTests
    {
        private static readonly UnitContext Feet = UnitContext.Feet();

        private static MassView Mass(
            string id, Vec3 min, Vec3 max, string function = "Office", string layer = "MASS_Office")
        {
            var mass = new MassView
            {
                ElementId = id,
                Name = id,
                Function = function,
                Layer = layer,
                Bbox = BoxView.From(min, max),
                IsSolid = true
            };
            mass.Volume = CompositionAnalyzer.Volume(mass.Bbox);
            mass.FootprintArea = mass.Bbox.FootprintArea;
            mass.Centroid = mass.Bbox.Center;
            mass.RhinoObjectIds.Add(id);
            return mass;
        }

        // ── Adjacency ─────────────────────────────────────────────────

        [Fact]
        public void AMassRestingOnAnotherSitsOnIt()
        {
            var plinth = Mass("plinth", new Vec3(0, 0, 0), new Vec3(100, 60, 24));
            var tower = Mass("tower", new Vec3(20, 10, 24), new Vec3(60, 50, 120));

            CompositionAnalyzer.FillAdjacencies(new[] { plinth, tower }, Feet);

            Assert.Equal(SemanticVocabulary.RelSitsOn, tower.AdjacentMasses.Single().Relationship);
            Assert.Equal(SemanticVocabulary.RelSitsUnder, plinth.AdjacentMasses.Single().Relationship);
        }

        [Fact]
        public void TwoMassesSideBySideAbut()
        {
            var office = Mass("office", new Vec3(0, 0, 0), new Vec3(50, 60, 36));
            var retail = Mass("retail", new Vec3(50, 0, 0), new Vec3(90, 60, 24), "Retail", "MASS_Retail");

            CompositionAnalyzer.FillAdjacencies(new[] { office, retail }, Feet);

            Assert.Equal(SemanticVocabulary.RelAbuts, office.AdjacentMasses.Single().Relationship);
        }

        [Fact]
        public void InterpenetratingMassesReadAsOneForm()
        {
            // Two Breps that overlap have not been boolean-unioned yet, but the architect has
            // already decided they are one building.
            var a = Mass("a", new Vec3(0, 0, 0), new Vec3(50, 60, 36));
            var b = Mass("b", new Vec3(40, 0, 0), new Vec3(90, 60, 24));

            CompositionAnalyzer.FillAdjacencies(new[] { a, b }, Feet);

            Assert.Equal(SemanticVocabulary.RelWasUnionedWith, a.AdjacentMasses.Single().Relationship);
        }

        [Fact]
        public void MassesAcrossTheSiteFromEachOtherAreNotRelated()
        {
            var a = Mass("a", new Vec3(0, 0, 0), new Vec3(50, 60, 36));
            var b = Mass("b", new Vec3(400, 0, 0), new Vec3(450, 60, 36));

            CompositionAnalyzer.FillAdjacencies(new[] { a, b }, Feet);

            Assert.Empty(a.AdjacentMasses);
            Assert.Empty(CompositionAnalyzer.Relationships(new[] { a, b }, Feet));
        }

        [Fact]
        public void RelationshipsAreReportedOncePerPairWithReadableNotes()
        {
            var plinth = Mass("plinth", new Vec3(0, 0, 0), new Vec3(100, 60, 24));
            var tower = Mass("tower", new Vec3(20, 10, 24), new Vec3(60, 50, 120));

            var edges = CompositionAnalyzer.Relationships(new[] { plinth, tower }, Feet);

            Assert.Single(edges);
            Assert.Contains("sits atop", edges[0].Notes);
        }

        [Fact]
        public void StackingIsDetectedAcrossASmallModellingGap()
        {
            // Architects do not snap to six decimal places; a hairline gap must still read as
            // "sits on" rather than "unrelated".
            var plinth = Mass("plinth", new Vec3(0, 0, 0), new Vec3(100, 60, 24));
            var tower = Mass("tower", new Vec3(20, 10, 24.02), new Vec3(60, 50, 120));

            CompositionAnalyzer.FillAdjacencies(new[] { plinth, tower }, Feet);

            Assert.Equal(SemanticVocabulary.RelSitsOn, tower.AdjacentMasses.Single().Relationship);
        }

        // ── Grouping ──────────────────────────────────────────────────

        [Fact]
        public void MassesUnderACommonParentLayerGroupTogether()
        {
            var a = Mass("a", new Vec3(0, 0, 0), new Vec3(50, 60, 36), "Office", "Office Wing::MASS_Office");
            var b = Mass("b", new Vec3(50, 0, 0), new Vec3(90, 60, 24), "Common", "Office Wing::MASS_Common");
            var c = Mass("c", new Vec3(0, 200, 0), new Vec3(50, 260, 36), "Residential", "MASS_Residential");

            var groups = CompositionAnalyzer.DeriveGroups(new[] { a, b, c }, null, null);

            var group = Assert.Single(groups);
            Assert.Equal("Office Wing", group.Name);
            Assert.Equal(new[] { "a", "b" }, group.MassIds);
            Assert.Equal("Office", group.DominantFunction);
            Assert.Null(c.MassGroupId);
        }

        [Fact]
        public void AnExplicitGroupTagBeatsTheLayerParent()
        {
            var a = Mass("a", new Vec3(0, 0, 0), new Vec3(50, 60, 36), layer: "Wing A::MASS_Office");
            var b = Mass("b", new Vec3(0, 200, 0), new Vec3(50, 260, 36), layer: "Wing B::MASS_Office");

            var explicitNames = new Dictionary<string, string> { { "a", "North Building" }, { "b", "North Building" } };
            var groups = CompositionAnalyzer.DeriveGroups(new[] { a, b }, explicitNames, null);

            var group = Assert.Single(groups);
            Assert.Equal("North Building", group.Name);
            Assert.Equal(SemanticVocabulary.ByUserData, group.ClassifiedBy);
        }

        [Fact]
        public void AGroupOfOneIsNotAGroup()
        {
            var a = Mass("a", new Vec3(0, 0, 0), new Vec3(50, 60, 36), layer: "Wing A::MASS_Office");

            Assert.Empty(CompositionAnalyzer.DeriveGroups(new[] { a }, null, null));
        }

        [Fact]
        public void GroupTotalsAreTheSumOfTheirMembers()
        {
            var a = Mass("a", new Vec3(0, 0, 0), new Vec3(50, 60, 36), layer: "Wing::MASS_Office");
            var b = Mass("b", new Vec3(50, 0, 0), new Vec3(90, 60, 24), layer: "Wing::MASS_Retail");

            var group = CompositionAnalyzer.DeriveGroups(new[] { a, b }, null, null).Single();

            Assert.Equal(a.Volume + b.Volume, group.CombinedVolume, 6);
            Assert.Equal(a.FootprintArea + b.FootprintArea, group.CombinedFootprintArea, 6);
            Assert.Equal(90, group.Bbox.Max.X);
        }

        [Fact]
        public void TheDominantFunctionIsTheOneCarryingTheMostVolume()
        {
            var big = Mass("big", new Vec3(0, 0, 0), new Vec3(100, 100, 100), "Residential");
            var small = Mass("small", new Vec3(0, 0, 100), new Vec3(20, 20, 110), "Retail");

            Assert.Equal("Residential", CompositionAnalyzer.DominantFunction(new[] { big, small }));
        }

        [Fact]
        public void ParentLayerIsNullForATopLevelLayer()
        {
            Assert.Null(CompositionAnalyzer.ParentLayer("MASS_Office"));
            Assert.Equal("Building", CompositionAnalyzer.ParentLayer("Building::MASS_Office"));
            Assert.Equal("Site::Existing", CompositionAnalyzer.ParentLayer("Site::Existing::SITE_Street"));
        }

        // ── Principal axes and symmetry ───────────────────────────────

        [Fact]
        public void ABarBuildingsFirstPrincipalAxisRunsAlongIt()
        {
            var points = new List<Vec3>();
            foreach (double x in new[] { 0.0, 200.0 })
                foreach (double y in new[] { 0.0, 40.0 })
                    foreach (double z in new[] { 0.0, 36.0 })
                        points.Add(new Vec3(x, y, z));

            var axes = GeometryMath.PrincipalAxes(points);

            Assert.True(System.Math.Abs(axes[0].X) > 0.9);
        }

        [Fact]
        public void DegeneratePointCloudsFallBackToWorldAxes()
        {
            Assert.Equal(1, GeometryMath.PrincipalAxes(new List<Vec3> { Vec3.Zero }) [0].X);
            Assert.Equal(1, GeometryMath.PrincipalAxes(null)[0].X);
        }

        [Fact]
        public void AMirroredPairOfWingsScoresAsSymmetric()
        {
            var west = BoxView.From(new Vec3(0, 0, 0), new Vec3(40, 60, 36));
            var east = BoxView.From(new Vec3(60, 0, 0), new Vec3(100, 60, 36));

            Assert.Equal(1.0, GeometryMath.SymmetryScore(new[] { west, east }, aboutX: true), 3);
        }

        [Fact]
        public void AnAsymmetricMassingScoresLow()
        {
            var big = BoxView.From(new Vec3(0, 0, 0), new Vec3(100, 60, 36));
            var spur = BoxView.From(new Vec3(100, 0, 0), new Vec3(140, 20, 12));

            Assert.True(GeometryMath.SymmetryScore(new[] { big, spur }, aboutX: true) < 0.9);
        }

        [Fact]
        public void DominantAxisNamesTheDirectionInPlainLanguage()
        {
            Assert.Equal("X (east-west)",
                GeometryMath.DominantAxis(BoxView.From(new Vec3(0, 0, 0), new Vec3(200, 40, 36))));
            Assert.Equal("Z (vertical)",
                GeometryMath.DominantAxis(BoxView.From(new Vec3(0, 0, 0), new Vec3(40, 40, 400))));
            Assert.Equal("none", GeometryMath.DominantAxis(BoxView.Unset));
        }

        [Fact]
        public void RatioRefusesToDivideByZero()
        {
            Assert.Null(GeometryMath.Ratio(5, 0));
            Assert.Equal(2.5, GeometryMath.Ratio(5, 2).Value, 6);
        }
    }
}
