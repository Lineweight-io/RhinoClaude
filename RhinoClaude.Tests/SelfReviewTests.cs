using System.Linq;
using System.Text.Json;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The reviewer's answer decides whether a turn ends or goes round again, so an
    /// unexpected response has to degrade into a usable verdict rather than throw
    /// mid-turn — after the geometry is already in the document.
    /// </summary>
    public class ReviewParsingTests
    {
        [Theory]
        [InlineData("ship", ReviewVerdict.Ship)]
        [InlineData("iterate", ReviewVerdict.Iterate)]
        [InlineData("ask_user", ReviewVerdict.AskUser)]
        public void ParsesStructuredOutput(string verdict, ReviewVerdict expected)
        {
            string json = "{\"verdict\":\"" + verdict + "\",\"notes\":\"Looks right.\"}";

            var outcome = ReviewPrompt.Parse(json);

            Assert.Equal(expected, outcome.Verdict);
            Assert.Equal("Looks right.", outcome.Notes);
        }

        [Fact]
        public void ParsesAQuestionOnAskUser()
        {
            const string json =
                "{\"verdict\":\"ask_user\",\"notes\":\"Ambiguous.\",\"questionForUser\":\"Interior or exterior wall?\"}";

            var outcome = ReviewPrompt.Parse(json);

            Assert.Equal(ReviewVerdict.AskUser, outcome.Verdict);
            Assert.Equal("Interior or exterior wall?", outcome.QuestionForUser);
        }

        [Fact]
        public void UnwrapsAFencedCodeBlock()
        {
            const string fenced = "```json\n{\"verdict\":\"ship\",\"notes\":\"fine\"}\n```";
            Assert.Equal(ReviewVerdict.Ship, ReviewPrompt.Parse(fenced).Verdict);
        }

        [Fact]
        public void FindsJsonEmbeddedInProse()
        {
            const string messy = "Here is my assessment:\n{\"verdict\":\"iterate\",\"notes\":\"one wall missing\"}\nHope that helps.";

            var outcome = ReviewPrompt.Parse(messy);

            Assert.Equal(ReviewVerdict.Iterate, outcome.Verdict);
            Assert.Equal("one wall missing", outcome.Notes);
        }

        [Fact]
        public void FallsBackToProseWhenThereIsNoJson()
        {
            var outcome = ReviewPrompt.Parse("I would iterate on this — the north wall is missing.");

            Assert.Equal(ReviewVerdict.Iterate, outcome.Verdict);
            Assert.Contains("north wall", outcome.Notes);
        }

        [Fact]
        public void ProseFallbackTakesTheFirstVerdictMentioned()
        {
            // "ship" appears later; the reviewer's actual call is iterate.
            var outcome = ReviewPrompt.Parse("My verdict is iterate, it is not ready to ship.");
            Assert.Equal(ReviewVerdict.Iterate, outcome.Verdict);
        }

        [Fact]
        public void AnEmptyResponseIsUnavailableRatherThanAThrow()
        {
            var outcome = ReviewPrompt.Parse("   ");

            Assert.Equal(ReviewVerdict.Unavailable, outcome.Verdict);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Notes));
        }

        [Fact]
        public void AnUnrecognisableResponseIsUnavailable()
        {
            Assert.Equal(ReviewVerdict.Unavailable, ReviewPrompt.Parse("¯\\_(ツ)_/¯").Verdict);
        }

        [Fact]
        public void MalformedJsonDoesNotThrow()
        {
            var outcome = ReviewPrompt.Parse("{\"verdict\": \"ship\", oops");
            Assert.Equal(ReviewVerdict.Ship, outcome.Verdict);   // prose fallback finds it
        }

        [Fact]
        public void TheOutputSchemaIsValidJsonAndPinsTheThreeVerdicts()
        {
            using var doc = JsonDocument.Parse(ReviewPrompt.OutputSchema);
            var values = doc.RootElement
                .GetProperty("properties").GetProperty("verdict").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetString()).ToList();

            Assert.Equal(new[] { "ship", "iterate", "ask_user" }, values);
        }

        [Fact]
        public void ToToolPayloadUsesTheWireVerdictStrings()
        {
            var payload = new ReviewOutcome
            {
                Verdict = ReviewVerdict.AskUser,
                Notes = "n",
                QuestionForUser = "q"
            }.ToToolPayload();

            Assert.Equal("ask_user", payload["reviewVerdict"]);
            Assert.Equal("q", payload["questionForUser"]);
        }
    }

    public class ReviewPromptTests
    {
        private static ReviewFacts Facts()
        {
            var facts = new ReviewFacts
            {
                UserRequest = "Build a 10x12 office",
                AgentSummary = "Made four walls",
                AgentExpectedOutcome = "A closed room on layer Walls",
                Units = "Inches",
                ObjectsCreated = 4,
                ObjectsDeleted = 0,
                ShotCount = 3
            };
            facts.LayersTouched.Add("Walls");
            facts.Checks.Add(CheckResult.Pass("bbox_sanity", "all plausible"));
            facts.Checks.Add(CheckResult.Fail("layer_assignment", "1 object on the default layer"));
            return facts;
        }

        [Fact]
        public void PromptCarriesTheRequestSummaryAndChecks()
        {
            string text = ReviewPrompt.BuildUserText(Facts());

            Assert.Contains("Build a 10x12 office", text);
            Assert.Contains("Made four walls", text);
            Assert.Contains("[pass] bbox_sanity", text);
            Assert.Contains("[FAIL] layer_assignment", text);
            Assert.Contains("Inches", text);
        }

        [Fact]
        public void PromptSaysSoWhenThereAreNoImages()
        {
            var facts = Facts();
            facts.ShotCount = 0;

            Assert.Contains("No screenshots", ReviewPrompt.BuildUserText(facts));
        }

        [Fact]
        public void FactsExposeWhetherEverythingPassed()
        {
            var facts = Facts();
            Assert.False(facts.AllChecksPassed);
            Assert.Single(facts.Failures);
        }
    }

    /// <summary>
    /// The mutation log is what turns "the agent says it built four walls" into a checkable
    /// claim, so its bookkeeping across create/delete has to be right.
    /// </summary>
    public class SessionMutationLogTests
    {
        private static SessionMutation Mutation(string tool, string[] created = null, string[] deleted = null,
                                                string layer = null, double[] box = null)
        {
            var m = new SessionMutation { ToolName = tool, AffectedBox = box };
            if (created != null) m.CreatedIds.AddRange(created);
            if (deleted != null) m.DeletedIds.AddRange(deleted);
            if (layer != null) m.LayersTouched.Add(layer);
            return m;
        }

        [Fact]
        public void SurvivingIdsExcludeWhatWasLaterDeleted()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("create_box", created: new[] { "a", "b", "c" }));
            log.Add(Mutation("delete_objects", deleted: new[] { "b" }));

            Assert.Equal(new[] { "a", "c" }, log.SurvivingCreatedIds());
        }

        [Fact]
        public void NetDeltaCountsCreatesMinusDeletes()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("create_box", created: new[] { "a", "b", "c" }));
            log.Add(Mutation("delete_objects", deleted: new[] { "b" }));

            Assert.Equal(2, log.NetObjectDelta());
        }

        [Fact]
        public void LayersAreDeduplicatedCaseInsensitively()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("ensure_layer", layer: "Walls"));
            log.Add(Mutation("create_box", created: new[] { "a" }, layer: "walls"));

            Assert.Single(log.LayersTouched());
        }

        [Fact]
        public void AffectedBoxIsTheUnionOfEveryMutation()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("create_box", box: new double[] { 0, 0, 0, 10, 10, 10 }));
            log.Add(Mutation("create_box", box: new double[] { -5, 0, 0, 4, 20, 3 }));

            Assert.Equal(new double[] { -5, 0, 0, 10, 20, 10 }, log.AffectedBox());
        }

        [Fact]
        public void AffectedBoxIsNullWhenNothingHasABox()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("deselect_all"));

            Assert.Null(log.AffectedBox());
        }

        [Fact]
        public void MarkScopesQueriesToWorkDoneSinceThatPoint()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("create_box", created: new[] { "old" }));

            int mark = log.Mark;
            log.Add(Mutation("create_box", created: new[] { "new" }));

            Assert.Equal(new[] { "new" }, log.SurvivingCreatedIds(mark));
            Assert.Equal(new[] { "old", "new" }, log.SurvivingCreatedIds());
        }

        [Fact]
        public void AMarkPastTheEndYieldsNothingRatherThanThrowing()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("create_box", created: new[] { "a" }));

            Assert.Empty(log.Since(99));
            Assert.Empty(log.SurvivingCreatedIds(99));
        }
    }

    /// <summary>Self-review's own model call has to be priced separately — it runs on Opus
    /// while the loop runs on Sonnet.</summary>
    public class SideCallCostTests
    {
        [Fact]
        public void AReviewCallIsPricedOnItsOwnModel()
        {
            var budget = new CostBudget("claude-sonnet-5", 0.50, 25);
            budget.RecordIteration(new TokenUsage { InputTokens = 10_000, OutputTokens = 1_000 });

            double loopOnly = budget.SpentUsd;

            budget.RecordSideCall("review", "claude-opus-5",
                new TokenUsage { InputTokens = 20_000, OutputTokens = 500 });

            // Opus rates: 20k in at $5/MTok = $0.10, 500 out at $25/MTok = $0.0125.
            Assert.Equal(loopOnly + 0.10 + 0.0125, budget.SpentUsd, 6);
        }

        [Fact]
        public void ASideCallDoesNotAdvanceTheIterationCounter()
        {
            var budget = new CostBudget("claude-sonnet-5", 0.50, 25);
            budget.RecordSideCall("review", "claude-opus-5", new TokenUsage { InputTokens = 100 });

            Assert.Equal(0, budget.Iterations);
            Assert.Single(budget.SideCalls);
        }

        [Fact]
        public void SideSpendCountsTowardTheCeiling()
        {
            var budget = new CostBudget("claude-sonnet-5", 0.05, 25);
            budget.RecordSideCall("review", "claude-opus-5",
                new TokenUsage { InputTokens = 100_000, OutputTokens = 5_000 });

            Assert.True(budget.CostExceeded);
        }

        [Fact]
        public void BreakdownListsSideCallsSeparately()
        {
            var budget = new CostBudget("claude-sonnet-5", 0.50, 25);
            budget.RecordIteration(new TokenUsage { InputTokens = 1_000 });
            budget.RecordSideCall("review", "claude-opus-5", new TokenUsage { InputTokens = 1_000 });

            string text = budget.Breakdown();
            Assert.Contains("review (claude-opus-5)", text);
        }
    }
}
