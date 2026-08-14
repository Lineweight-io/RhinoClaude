using RhinoClaude.Agent;
using RhinoClaude.Semantic;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// Semantic plan phase E: composition facts feed the self-review the same way phase 1's
    /// deterministic checks do. The reviewer is judging whether the massing works, and a
    /// screenshot alone makes that a guess.
    /// </summary>
    public class ReviewCompositionTests
    {
        [Fact]
        public void TheSummaryCarriesProportionsHierarchyAndBooleanComposition()
        {
            var report = MassingComposition.Compute(SemanticFixture.MixedUse());

            string summary = MassingComposition.Summarize(report, SemanticFixture.Feet);

            Assert.Contains("Envelope:", summary);
            Assert.Contains("Aspect ratios", summary);
            Assert.Contains("Symmetry:", summary);
            Assert.Contains("Mass hierarchy:", summary);
            Assert.Contains("Boolean composition:", summary);
            Assert.Contains("Vertical rhythm:", summary);
        }

        [Fact]
        public void TheSummaryNamesMassesTheWayTheUserWould()
        {
            string summary = MassingComposition.Summarize(
                MassingComposition.Compute(SemanticFixture.MixedUse()), SemanticFixture.Feet);

            Assert.Contains("Office bar", summary);
            Assert.Contains("Retail plinth", summary);
        }

        [Fact]
        public void AnEmptyModelSummarisesToNullSoThePromptStaysByteStable()
        {
            // A reviewer prompt that grows an empty section on every mass-less document would
            // break the cached prefix for no information.
            var report = MassingComposition.Compute(
                new MassingSnapshot(new SemanticView(), null, SemanticFixture.Feet));

            Assert.Null(MassingComposition.Summarize(report, SemanticFixture.Feet));
            Assert.Null(MassingComposition.Summarize(null, SemanticFixture.Feet));
        }

        [Fact]
        public void LengthsInTheSummaryAreInFeetWhateverTheDocumentUnits()
        {
            var millimetres = new UnitContext(304.8, "Millimeters");

            var view = new SemanticView { UnitSystem = "Millimeters", FloorToFloorDefault = 3658 };
            view.Masses.Add(SemanticFixture.Mass("m",
                new Vec3(0, 0, 0), new Vec3(30480, 18288, 10973)));   // 100 × 60 × 36 ft

            string summary = MassingComposition.Summarize(
                MassingComposition.Compute(new MassingSnapshot(view, null, millimetres)), millimetres);

            Assert.Contains("100 × 60 × 36 ft", summary);
        }

        [Fact]
        public void TheReviewerPromptIncludesTheCompositionSectionWhenThereIsOne()
        {
            var facts = new ReviewFacts
            {
                UserRequest = "make the office mass taller",
                AgentSummary = "pushed the roof up 12 feet",
                Units = "Feet",
                MassingComposition = "Envelope: 160 × 60 × 36 ft, dominant axis X (east-west)."
            };

            string text = ReviewPrompt.BuildUserText(facts);

            Assert.Contains("<massing_composition>", text);
            Assert.Contains("dominant axis X", text);
        }

        [Fact]
        public void TheReviewerPromptIsUnchangedWhenTheSemanticLayerIsOff()
        {
            var facts = new ReviewFacts
            {
                UserRequest = "make a box",
                AgentSummary = "made a box",
                Units = "Feet"
            };

            string text = ReviewPrompt.BuildUserText(facts);

            Assert.DoesNotContain("massing_composition", text);
        }
    }
}
