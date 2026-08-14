using System.Linq;
using RhinoClaude.Semantic;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// find_element's rules-based parser (semantic plan §10.2 question 4) and
    /// describe_massing's narrative (§4.1, capped per question 5). Both are the agent's first
    /// contact with the model, so a misread here sends every later tool call to the wrong place.
    /// </summary>
    public class ElementQueryAndNarrativeTests
    {
        // ── The parser ────────────────────────────────────────────────

        [Fact]
        public void TheCanonicalExampleParsesIntoAllThreeParts()
        {
            // "the north face of the office mass" — orientation, type, function.
            var query = ElementQueryParser.Parse("the north face of the office mass");

            Assert.Equal("N", query.Orientation);
            Assert.Equal(SemanticVocabulary.Face, query.TargetType);
            Assert.Equal("Office", query.Function);
        }

        [Fact]
        public void AMassWordDoesNotOverrideAFaceWord()
        {
            // Both "face" and "mass" appear; the sentence is about the face.
            Assert.Equal(SemanticVocabulary.Face,
                ElementQueryParser.Parse("north face of the office mass").TargetType);
            Assert.Equal(SemanticVocabulary.Mass,
                ElementQueryParser.Parse("the office mass").TargetType);
        }

        [Theory]
        [InlineData("northern elevation", "N")]
        [InlineData("the south wall", "S")]
        [InlineData("east facade", "E")]
        [InlineData("the top face", "up")]
        [InlineData("the underside", "down")]
        public void SynonymsAnArchitectWouldActuallyTypeAreUnderstood(string text, string orientation)
        {
            Assert.Equal(orientation, ElementQueryParser.Parse(text).Orientation);
        }

        [Fact]
        public void RoofAndWallWordsImplyAFaceAndItsRole()
        {
            var roof = ElementQueryParser.Parse("the roof");
            Assert.Equal(SemanticVocabulary.Face, roof.TargetType);
            Assert.Equal(SemanticVocabulary.RoleRoof, roof.FaceRole);

            var wall = ElementQueryParser.Parse("the south wall");
            Assert.Equal(SemanticVocabulary.RoleFacade, wall.FaceRole);
        }

        [Fact]
        public void CornerWordsResolveToAnEdgeQuery()
        {
            var query = ElementQueryParser.Parse("all outside corners of the office mass");

            Assert.Equal(SemanticVocabulary.Edge, query.TargetType);
            Assert.Equal(SemanticVocabulary.EdgeOutsideCorner, query.EdgeRole);
        }

        [Fact]
        public void ParapetsAndRidgesAreEdgeRolesToo()
        {
            Assert.Equal(SemanticVocabulary.EdgeParapet, ElementQueryParser.Parse("the parapet").EdgeRole);
            Assert.Equal(SemanticVocabulary.EdgeRoofRidge, ElementQueryParser.Parse("the roof ridge").EdgeRole);
        }

        [Fact]
        public void TheMainEntryResolvesToAnOpeningWithTheEntryFlag()
        {
            var query = ElementQueryParser.Parse("the main entry");

            Assert.Equal(SemanticVocabulary.Opening, query.TargetType);
            Assert.True(query.WantsEntry);
            Assert.Equal("largest", query.Superlative);   // "main" reads as the principal one
        }

        [Fact]
        public void OpeningSubtypesAreRecognised()
        {
            Assert.Equal(SemanticVocabulary.OpeningStorefront,
                ElementQueryParser.Parse("the storefront on the south face").OpeningType);
            Assert.Equal(SemanticVocabulary.OpeningWindow,
                ElementQueryParser.Parse("the windows").OpeningType);
        }

        [Theory]
        [InlineData("the tallest mass", "tallest")]
        [InlineData("the largest mass", "largest")]
        [InlineData("the smallest volume", "smallest")]
        [InlineData("the primary mass", "largest")]
        public void SuperlativesAreUnderstood(string text, string superlative)
        {
            Assert.Equal(superlative, ElementQueryParser.Parse(text).Superlative);
        }

        [Fact]
        public void LeftoverWordsBecomeNameHints()
        {
            var query = ElementQueryParser.Parse("the Kendrick pavilion");

            Assert.Contains("kendrick", query.NameHints.Select(h => h.ToLowerInvariant()));
            Assert.Contains("pavilion", query.NameHints.Select(h => h.ToLowerInvariant()));
        }

        [Fact]
        public void StopWordsAreNotNameHints()
        {
            var query = ElementQueryParser.Parse("the north face of the office mass");

            Assert.Empty(query.NameHints);
        }

        [Fact]
        public void AHyphenatedCompoundReadsAsBothIdeas()
        {
            Assert.Equal("NE", ElementQueryParser.Parse("the north-east facade").Orientation);
        }

        [Fact]
        public void ContextAndSiteWordsResolveToSiteElements()
        {
            var query = ElementQueryParser.Parse("the context buildings");

            Assert.Equal(SemanticVocabulary.Site, query.TargetType);
            Assert.Equal("ContextBuilding", query.SiteType);
        }

        [Fact]
        public void AnUnrecognisableQueryIsEmptyRatherThanAWrongGuess()
        {
            var query = ElementQueryParser.Parse("");

            Assert.True(query.IsEmpty);
            Assert.Equal("(nothing recognised)", query.ToString());
        }

        [Fact]
        public void TheParsedQueryPrintsBackWhatItUnderstood()
        {
            // The tool returns this so the agent can check the reading before acting on it.
            var text = ElementQueryParser.Parse("the north facade of the office mass").ToString();

            Assert.Contains("N", text);
            Assert.Contains("Office", text);
            Assert.Contains("role:facade", text);
        }

        [Fact]
        public void NameScoreRewardsOverlapAndIgnoresTheRest()
        {
            var hints = new[] { "kendrick", "pavilion" };

            Assert.Equal(1.0, ElementQueryParser.NameScore("Kendrick Pavilion", hints), 4);
            Assert.Equal(0.5, ElementQueryParser.NameScore("Kendrick Hall", hints), 4);
            Assert.Equal(0.0, ElementQueryParser.NameScore("Office bar", hints), 4);
            Assert.Equal(0.0, ElementQueryParser.NameScore(null, hints), 4);
        }

        // ── The narrative ─────────────────────────────────────────────

        [Fact]
        public void AnEmptyModelExplainsHowToFixIt()
        {
            var narrative = MassingNarrator.Narrate(
                new MassingSnapshot(new SemanticView(), null, SemanticFixture.Feet),
                MassingNarrator.Standard);

            Assert.Contains("No Masses found", narrative);
            Assert.Contains("ClaudeLearnNamingConvention", narrative);
            Assert.Contains("LAYER_CONVENTIONS.md", narrative);
        }

        [Fact]
        public void TheNarrativeLeadsWithStoreysFunctionAndPosition()
        {
            var narrative = MassingNarrator.Narrate(SemanticFixture.MixedUse(), MassingNarrator.Standard);

            Assert.Contains("3-storey office mass", narrative);
            Assert.Contains("2-storey retail mass", narrative);
            Assert.Contains("side of the site", narrative);
        }

        [Fact]
        public void TheNarrativeDescribesHowTheMassesRelate()
        {
            // Rev 2's framing: composition, not assembly.
            var narrative = MassingNarrator.Narrate(SemanticFixture.MixedUse(), MassingNarrator.Standard);

            Assert.Contains("abuts", narrative);
        }

        [Fact]
        public void TheNarrativeCountsTheOpeningsInTheMassFaces()
        {
            var narrative = MassingNarrator.Narrate(SemanticFixture.MixedUse(), MassingNarrator.Standard);

            Assert.Contains("Openings in the mass faces", narrative);
            Assert.Contains("windows", narrative);
        }

        [Fact]
        public void BriefIsShorterThanStandardWhichIsShorterThanDetailed()
        {
            var snapshot = SemanticFixture.MixedUse();

            int brief = MassingNarrator.Narrate(snapshot, MassingNarrator.Brief).Length;
            int standard = MassingNarrator.Narrate(snapshot, MassingNarrator.Standard).Length;
            int detailed = MassingNarrator.Narrate(snapshot, MassingNarrator.Detailed).Length;

            Assert.True(brief < standard, "brief should be shorter than standard");
            Assert.True(standard <= detailed, "standard should not exceed detailed");
        }

        [Fact]
        public void DetailedAddsRoofFormAndEnvelopeNumbers()
        {
            var narrative = MassingNarrator.Narrate(SemanticFixture.MixedUse(), MassingNarrator.Detailed);

            Assert.Contains("Roof reads as flat", narrative);
            Assert.Contains("wall-window ratio", narrative);
        }

        [Fact]
        public void UnclassifiedObjectsAreSurfacedSoTheAgentKnowsToHedge()
        {
            var snapshot = SemanticFixture.MixedUse();
            snapshot.View.UnclassifiedCount = 4;
            snapshot.View.UnclassifiedLayers.Add("Working::Sketches");

            var narrative = MassingNarrator.Narrate(snapshot, MassingNarrator.Standard);

            Assert.Contains("4 object(s) did not classify", narrative);
            Assert.Contains("Working::Sketches", narrative);
        }

        [Fact]
        public void GeometryInferredMassesAreCalledOutAsGuesses()
        {
            var snapshot = SemanticFixture.MixedUse();
            snapshot.View.Masses[0].ClassifiedBy = SemanticVocabulary.ByGeometryInference;

            var narrative = MassingNarrator.Narrate(snapshot, MassingNarrator.Standard);

            Assert.Contains("classified from geometry alone", narrative);
            Assert.Contains("confirm before any destructive move", narrative);
        }

        [Fact]
        public void TheHardCapTruncatesAtASentenceBoundary()
        {
            string text = string.Concat(Enumerable.Repeat("This is a sentence about the massing. ", 500));

            string capped = MassingNarrator.Cap(text, MassingNarrator.Standard);

            Assert.True(capped.Length < text.Length);
            Assert.EndsWith("[…truncated]", capped);
            Assert.Contains(". […truncated]", capped);
        }

        [Fact]
        public void ShortNarrativesAreNotTouchedByTheCap()
        {
            const string text = "One 3-storey office mass.";

            Assert.Equal(text, MassingNarrator.Cap(text, MassingNarrator.Brief));
        }

        [Fact]
        public void TheDetailCapsMatchThePlansDecision()
        {
            // Plan §10.2 question 5: ~600-token target, 1500 hard cap, tighter at brief.
            Assert.Equal(600, MassingNarrator.TokenCap(MassingNarrator.Standard));
            Assert.Equal(1500, MassingNarrator.TokenCap(MassingNarrator.Detailed));
            Assert.True(MassingNarrator.TokenCap(MassingNarrator.Brief) < 600);
            Assert.Equal(600, MassingNarrator.TokenCap("nonsense"));
        }
    }
}
