using System.Text.RegularExpressions;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// Regression guard on the prompt's load-bearing paragraphs.
    ///
    /// The prompt is one long string literal, so a paragraph can be dropped by an unrelated edit
    /// and nothing fails — the loss only shows up later as a bad turn on a real drawing, which is
    /// how the 8/19 floor-plan session went. These tests pin the guidance whose absence cost
    /// something, and the tool names it points at, because guidance that names a tool that is not
    /// registered is worse than no guidance at all.
    /// </summary>
    public class SystemPromptTests
    {
        /// <summary>
        /// The prompt is a hard-wrapped literal, so a phrase that reads as one sentence can be
        /// split across a line break. Assert against collapsed whitespace: the wording is what
        /// matters here, not where the paragraph happens to wrap.
        /// </summary>
        private static string Flat(string prompt) => Regex.Replace(prompt, @"\s+", " ");

        private static string Base => Flat(SystemPrompt.Build(scriptToolEnabled: false));

        private static string Semantic =>
            Flat(SystemPrompt.Build(scriptToolEnabled: false, semanticToolsEnabled: true));

        // ── Precision over screenshot inspection ──────────────────────

        [Fact]
        public void Prompt_TellsTheAgentNotToMeasureFromScreenshots()
        {
            Assert.Contains("Precision over screenshot inspection", Base);
            Assert.Contains("Do NOT infer measurements from screenshots", Base);
            Assert.Contains("dimensions, coordinates, or vertex counts", Base);
        }

        [Fact]
        public void PrecisionParagraph_NamesTheQueryToolsThatAreAlwaysRegistered()
        {
            Assert.Contains("get_object with includeSubobjects=true", Base);
            Assert.Contains("list_objects", Base);
            Assert.Contains("get_selection", Base);
        }

        [Fact]
        public void PrecisionParagraph_SaysToConfirmADimensionAScreenshotSuggests()
        {
            Assert.Contains("confirm it with get_object before acting on it", Base);
        }

        /// <summary>
        /// The semantic query tools only exist when the semantic set is switched on, so their
        /// half of the rule lives in that block. Naming them unconditionally would point the
        /// agent at tools the registry does not have.
        /// </summary>
        [Fact]
        public void SemanticPrompt_NamesTheSemanticQueryToolsForDimensions()
        {
            Assert.Contains("Dimensions come from the queries, never from the pixels", Semantic);
            Assert.Contains("list_masses", Semantic);
            Assert.Contains("describe_massing", Semantic);
            Assert.Contains("get_mass_faces", Semantic);
        }

        [Fact]
        public void SemanticToolNames_StayOutOfTheBasePrompt()
        {
            Assert.DoesNotContain("list_masses", Base);
            Assert.DoesNotContain("get_mass_faces", Base);
        }

        // ── Clarify selection intent ──────────────────────────────────

        [Fact]
        public void Prompt_TellsTheAgentToStateItsReadingBeforeBuilding()
        {
            Assert.Contains("Clarify selection intent before creating geometry", Base);
            Assert.Contains("before you create anything", Base);
            Assert.Contains("Say in one sentence what you believe the target is", Base);
        }

        /// <summary>
        /// The specific failure: a leftover mass on a MASS_* layer read as the thing the user
        /// pointed at, when the user had actually selected 1,918 curves.
        /// </summary>
        [Fact]
        public void ClarifyParagraph_WarnsAgainstAssumingAPreExistingMassIsTheTarget()
        {
            Assert.Contains("MASS_*", Base);
            Assert.Contains("left over from an earlier session", Base);
            Assert.Contains("more than about 50", Base);
        }

        [Fact]
        public void ClarifyParagraph_NamesTheToolsThatConfirmTheTarget()
        {
            Assert.Contains("confirm its real geometry", Base);
            Assert.Contains("get_selection and get_object before extruding", Base);
        }

        [Fact]
        public void ClarifyParagraph_PointsLineworkSelectionsAtTheFootprintTool()
        {
            Assert.Contains("extract_footprint_from_curves", Base);
            Assert.Contains("not the selection's bounding box", Base);
        }

        // ── Both paragraphs survive every configuration ───────────────

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void BothParagraphs_SurviveEveryToolConfiguration(bool script, bool semantic)
        {
            var prompt = Flat(SystemPrompt.Build(script, semantic));

            Assert.Contains("Precision over screenshot inspection", prompt);
            Assert.Contains("Clarify selection intent before creating geometry", prompt);
        }
    }
}
