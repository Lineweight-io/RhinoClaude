using System.Text.Json;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    public class CostBudgetTests
    {
        [Fact]
        public void PicksSonnetRatesForTheDatedSonnet45Id()
        {
            var pricing = CostBudget.PricingFor("claude-sonnet-4-5-20250929");

            Assert.Equal(3.00, pricing.InputPerMTok);
            Assert.Equal(15.00, pricing.OutputPerMTok);
            Assert.Equal(3.75, pricing.CacheWritePerMTok);
            Assert.Equal(0.30, pricing.CacheReadPerMTok, 6);
        }

        [Fact]
        public void LongestPrefixWins()
        {
            // "claude-sonnet-4-5" and "claude-sonnet-5" must not be confused with each other.
            Assert.Equal(3.00, CostBudget.PricingFor("claude-sonnet-5").InputPerMTok);
            Assert.Equal(5.00, CostBudget.PricingFor("claude-opus-4-8").InputPerMTok);
            Assert.Equal(1.00, CostBudget.PricingFor("claude-haiku-4-5").InputPerMTok);
        }

        [Fact]
        public void UnknownModelFallsBackRatherThanThrowing()
        {
            var pricing = CostBudget.PricingFor("some-future-model");
            Assert.True(pricing.InputPerMTok > 0);
        }

        [Fact]
        public void CostIsComputedAcrossAllFourTokenBuckets()
        {
            var pricing = CostBudget.PricingFor("claude-sonnet-4-5-20250929");
            var usage = new TokenUsage
            {
                InputTokens = 1_000_000,
                OutputTokens = 1_000_000,
                CacheCreationInputTokens = 1_000_000,
                CacheReadInputTokens = 1_000_000
            };

            Assert.Equal(3.00 + 15.00 + 3.75 + 0.30, pricing.CostOf(usage), 6);
        }

        [Fact]
        public void SpendAccumulatesAcrossIterations()
        {
            var budget = new CostBudget("claude-sonnet-4-5-20250929", 0.50, 25);

            budget.RecordIteration(new TokenUsage { InputTokens = 10_000, OutputTokens = 1_000 });
            budget.RecordIteration(new TokenUsage { InputTokens = 20_000, OutputTokens = 2_000 });

            Assert.Equal(2, budget.Iterations);
            Assert.Equal(30_000, budget.TotalUsage.InputTokens);
            Assert.Equal(3_000, budget.TotalUsage.OutputTokens);
            Assert.Equal(0.03 + 0.045, budget.SpentUsd, 6);
            Assert.False(budget.Exceeded);
        }

        [Fact]
        public void CostCeilingTrips()
        {
            var budget = new CostBudget("claude-sonnet-4-5-20250929", 0.50, 25);

            // 200k output tokens at $15/MTok is $3.00 — well past the $0.50 ceiling.
            budget.RecordIteration(new TokenUsage { OutputTokens = 200_000 });

            Assert.True(budget.CostExceeded);
            Assert.True(budget.Exceeded);
            Assert.Equal(0.0, budget.RemainingUsd);
            Assert.Equal(1.0, budget.FractionSpent);
        }

        [Fact]
        public void IterationCeilingTripsIndependentlyOfCost()
        {
            var budget = new CostBudget("claude-sonnet-4-5-20250929", 100.0, 3);

            for (int i = 0; i < 3; i++)
                budget.RecordIteration(new TokenUsage { InputTokens = 10 });

            Assert.False(budget.CostExceeded);
            Assert.True(budget.IterationsExceeded);
            Assert.True(budget.Exceeded);
        }

        [Fact]
        public void RecordingASnapshotDoesNotAliasTheCallersUsage()
        {
            var budget = new CostBudget("claude-sonnet-4-5-20250929", 0.50, 25);
            var usage = new TokenUsage { InputTokens = 100 };

            budget.RecordIteration(usage);
            usage.InputTokens = 999_999;   // the accumulator keeps mutating its own object

            Assert.Equal(100, budget.TotalUsage.InputTokens);
        }

        [Fact]
        public void NullUsageIsToleratedAsAZeroCostIteration()
        {
            var budget = new CostBudget("claude-sonnet-4-5-20250929", 0.50, 25);
            budget.RecordIteration(null);

            Assert.Equal(1, budget.Iterations);
            Assert.Equal(0.0, budget.SpentUsd);
        }

        [Fact]
        public void DescribeAndBreakdownAreRenderable()
        {
            var budget = new CostBudget("claude-sonnet-4-5-20250929", 0.50, 25);
            budget.RecordIteration(new TokenUsage { InputTokens = 5_000, OutputTokens = 500 });

            Assert.Contains("/ $0.50", budget.Describe());
            Assert.Contains("iter 1/25", budget.Describe());
            Assert.Contains("claude-sonnet-4-5-20250929", budget.Breakdown());
            Assert.Contains("TOTAL", budget.Breakdown());
        }
    }

    public class ToolResultTests
    {
        [Fact]
        public void SuccessResultMergesTheEnvelopeWithThePayload()
        {
            var result = ToolResult.Ok(new System.Collections.Generic.Dictionary<string, object>
            {
                { "id", "abc" },
                { "volume", 1000.0 }
            });

            var block = result.ToBlock("toolu_1");
            var text = Assert.IsType<TextBlock>(Assert.Single(block.Content));

            using var doc = JsonDocument.Parse(text.Text);
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("error").ValueKind);
            Assert.Equal("abc", doc.RootElement.GetProperty("id").GetString());
            Assert.Equal(1000.0, doc.RootElement.GetProperty("volume").GetDouble());
        }

        [Fact]
        public void FailureResultSetsIsErrorOnTheBlock()
        {
            var block = ToolResult.Fail("No layer named 'Walls'.").ToBlock("toolu_1");

            Assert.True(block.IsError);
            var text = Assert.IsType<TextBlock>(Assert.Single(block.Content));

            using var doc = JsonDocument.Parse(text.Text);
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("No layer named 'Walls'.", doc.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public void ImagesFollowTheJsonSummaryInTheContentArray()
        {
            var result = ToolResult.Ok(new System.Collections.Generic.Dictionary<string, object> { { "shots", 2 } });
            result.Images.Add(new ToolImage { MediaType = "image/png", Base64 = "AAAA" });
            result.Images.Add(new ToolImage { MediaType = "image/jpeg", Base64 = "BBBB" });

            var block = result.ToBlock("toolu_1");

            Assert.Equal(3, block.Content.Count);
            Assert.IsType<TextBlock>(block.Content[0]);
            Assert.Equal("image/png", Assert.IsType<ImageBlock>(block.Content[1]).MediaType);
            Assert.Equal("image/jpeg", Assert.IsType<ImageBlock>(block.Content[2]).MediaType);
        }

        [Fact]
        public void SafeStripsControlCharactersFromUserAuthoredText()
        {
            // A layer name is user-authored; control characters must not reach the model.
            string cleaned = ToolJson.Safe("Walls\u0007\u001BInterior\n");

            Assert.DoesNotContain('\u0007', cleaned);
            Assert.DoesNotContain('\u001B', cleaned);
            Assert.Contains("WallsInterior", cleaned);
            Assert.Contains("\n", cleaned);   // newlines are legitimate content
        }
    }

    public class ToolRegistryTests
    {
        private static ToolDefinition Stub(string name) => new ToolDefinition
        {
            Name = name,
            Description = name + " does something.",
            Handler = (input, ct) => ToolResult.Ok()
        };

        [Fact]
        public void SpecOrderMatchesRegistrationOrder()
        {
            // Tools render first in the prompt, so a shuffled order would break prompt caching.
            var registry = new ToolRegistry();
            registry.RegisterAll(new[] { Stub("a"), Stub("b"), Stub("c") });

            Assert.Equal(new[] { "a", "b", "c" }, registry.ToSpecs().ConvertAll(s => s.Name));
        }

        [Fact]
        public void ReRegisteringReplacesWithoutDuplicatingTheOrderEntry()
        {
            var registry = new ToolRegistry();
            registry.Register(Stub("a"));
            registry.Register(Stub("a"));

            Assert.Equal(1, registry.Count);
            Assert.Single(registry.ToSpecs());
        }

        [Fact]
        public void LookupIsExactAndMissesReturnFalse()
        {
            var registry = new ToolRegistry();
            registry.Register(Stub("create_box"));

            Assert.True(registry.TryGet("create_box", out _));
            Assert.False(registry.TryGet("Create_Box", out _));
            Assert.False(registry.TryGet(null, out _));
        }

        [Fact]
        public void AToolWithoutAHandlerIsRejected()
        {
            var registry = new ToolRegistry();
            Assert.Throws<System.ArgumentException>(() =>
                registry.Register(new ToolDefinition { Name = "x", Description = "y" }));
        }
    }
}
