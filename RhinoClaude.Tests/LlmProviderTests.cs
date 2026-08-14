using System;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The provider catalog, the settings that select one, and the rates that price it.
    /// </summary>
    public class LlmProviderTests
    {
        // ── Catalog ───────────────────────────────────────────────────

        [Fact]
        public void EveryProviderIsDescribed()
        {
            foreach (LlmProvider provider in Enum.GetValues(typeof(LlmProvider)))
            {
                var info = LlmProviderCatalog.Get(provider);
                Assert.Equal(provider, info.Provider);
                Assert.False(string.IsNullOrWhiteSpace(info.DisplayName), provider + " has no display name.");
                Assert.False(string.IsNullOrWhiteSpace(info.ApiKeySettingsKey), provider + " has no settings key.");

                // Only the custom provider is allowed to ship without an endpoint.
                if (!info.NeedsCustomEndpoint)
                {
                    Assert.False(string.IsNullOrWhiteSpace(info.BaseUrl), provider + " has no base URL.");
                    Assert.False(string.IsNullOrWhiteSpace(info.DefaultLoopModel), provider + " has no default model.");
                }
            }
        }

        [Fact]
        public void ApiKeySettingsKeysAreDistinctAndAnthropicKeepsItsOldOne()
        {
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in LlmProviderCatalog.Providers)
                Assert.True(seen.Add(info.ApiKeySettingsKey), "Duplicate settings key: " + info.ApiKeySettingsKey);

            // Changing this would silently orphan every key already saved by ClaudeSetKey.
            Assert.Equal("AnthropicApiKey", LlmProviderCatalog.Get(LlmProvider.Anthropic).ApiKeySettingsKey);
        }

        [Theory]
        [InlineData("DeepSeek", LlmProvider.DeepSeek)]
        [InlineData("deepseek", LlmProvider.DeepSeek)]
        [InlineData("Kimi", LlmProvider.Kimi)]
        [InlineData("", LlmProvider.Anthropic)]
        [InlineData("nonsense", LlmProvider.Anthropic)]
        public void PersistedProviderNamesParseBack(string saved, LlmProvider expected)
        {
            Assert.Equal(expected, LlmProviderCatalog.Parse(saved));
        }

        // ── Settings ──────────────────────────────────────────────────

        [Fact]
        public void ANewSettingsObjectIsUnchangedForAnthropicUsers()
        {
            var settings = new AgentSettings();

            Assert.Equal(LlmProvider.Anthropic, settings.Provider);
            Assert.Equal(AgentSettings.DefaultLoopModel, settings.LoopModel);
            Assert.Equal(AgentSettings.DefaultReviewerModel, settings.ReviewerModel);
            Assert.Equal("https://api.anthropic.com/v1", settings.ActiveEndpoint);
        }

        [Fact]
        public void SwitchingProviderMovesTheModelsWithIt()
        {
            var settings = new AgentSettings();
            settings.SelectProvider(LlmProvider.DeepSeek);

            Assert.Equal("deepseek-v4-flash", settings.LoopModel);
            Assert.Equal("https://api.deepseek.com/v1", settings.ActiveEndpoint);

            // …and switching back restores what Anthropic had, rather than leaving a DeepSeek id.
            settings.SelectProvider(LlmProvider.Anthropic);
            Assert.Equal(AgentSettings.DefaultLoopModel, settings.LoopModel);
        }

        [Fact]
        public void AHandPickedModelIsRememberedPerProvider()
        {
            var settings = new AgentSettings();
            settings.SelectProvider(LlmProvider.Kimi);
            settings.LoopModel = "kimi-k2.5";

            settings.SelectProvider(LlmProvider.Anthropic);
            settings.SelectProvider(LlmProvider.Kimi);

            Assert.Equal("kimi-k2.5", settings.LoopModel);
        }

        [Fact]
        public void KeysAreHeldPerProvider()
        {
            var settings = new AgentSettings();
            settings.SetApiKey(LlmProvider.Anthropic, "sk-ant-x");
            settings.SetApiKey(LlmProvider.DeepSeek, "sk-ds-y");

            Assert.Equal("sk-ant-x", settings.ActiveApiKey);
            settings.SelectProvider(LlmProvider.DeepSeek);
            Assert.Equal("sk-ds-y", settings.ActiveApiKey);
            Assert.Equal("sk-ant-x", settings.ApiKeyFor(LlmProvider.Anthropic));
        }

        [Fact]
        public void TheCustomProviderUsesTheTypedEndpoint()
        {
            var settings = new AgentSettings();
            settings.SelectProvider(LlmProvider.OpenAiCompatibleCustom);
            settings.CustomEndpoint = "  http://localhost:8080/v1  ";

            Assert.Equal("http://localhost:8080/v1", settings.ActiveEndpoint);
        }

        [Fact]
        public void CloneAndAdoptCarryTheWholeProviderBlock()
        {
            var settings = new AgentSettings();
            settings.SetApiKey(LlmProvider.Kimi, "sk-moon");
            settings.SelectProvider(LlmProvider.Kimi);
            settings.LoopModel = "kimi-k2.5";

            var clone = settings.Clone();
            Assert.Equal(LlmProvider.Kimi, clone.Provider);
            Assert.Equal("sk-moon", clone.ActiveApiKey);
            Assert.Equal("kimi-k2.5", clone.LoopModel);

            // Editing the clone must not reach back into the original — the settings dialog
            // relies on that for its cancel button.
            clone.SetApiKey(LlmProvider.Kimi, "sk-changed");
            Assert.Equal("sk-moon", settings.ApiKeyFor(LlmProvider.Kimi));

            var target = new AgentSettings();
            target.AdoptProviderSettings(clone);
            Assert.Equal(LlmProvider.Kimi, target.Provider);
            Assert.Equal("sk-changed", target.ActiveApiKey);
        }

        // ── Pricing ───────────────────────────────────────────────────

        [Fact]
        public void DeepSeekIsPricedAtItsPublishedRates()
        {
            var flash = CostBudget.PricingFor("deepseek-v4-flash");
            Assert.Equal(0.14, flash.InputPerMTok);
            Assert.Equal(0.28, flash.OutputPerMTok);
            Assert.Equal(0.0028, flash.CacheReadPerMTok);

            // No cache-write premium: these providers cache implicitly and bill the write at
            // the plain input rate.
            Assert.Equal(flash.InputPerMTok, flash.CacheWritePerMTok);

            var pro = CostBudget.PricingFor("deepseek-v4-pro");
            Assert.Equal(0.435, pro.InputPerMTok);
            Assert.Equal(0.87, pro.OutputPerMTok);
        }

        [Fact]
        public void KimiModelsAreEachPricedOnTheirOwnLine()
        {
            Assert.Equal(0.60, CostBudget.PricingFor("kimi-k2.5").InputPerMTok);
            Assert.Equal(0.95, CostBudget.PricingFor("kimi-k2.6").InputPerMTok);
            Assert.Equal(4.00, CostBudget.PricingFor("kimi-k2.6").OutputPerMTok);

            // K3 is not a cost move: it lists at Sonnet 5's own rate.
            var k3 = CostBudget.PricingFor("kimi-k3");
            Assert.Equal(3.00, k3.InputPerMTok);
            Assert.Equal(15.00, k3.OutputPerMTok);
        }

        [Fact]
        public void QwenAndOllamaArePriced()
        {
            Assert.Equal(0.40, CostBudget.PricingFor("qwen-plus").InputPerMTok);
            Assert.Equal(0.03, CostBudget.PricingFor("qwen3.7-flash").InputPerMTok);
            Assert.Equal(0.00, CostBudget.PricingFor("ollama/anything").InputPerMTok);
        }

        [Fact]
        public void AnUnknownIdOnAKnownProviderFallsBackToThatProvider()
        {
            // Longest-prefix wins, so a family-level entry catches a model id we have not seen.
            Assert.Equal(0.435, CostBudget.PricingFor("deepseek-v5-experimental").InputPerMTok);
            Assert.Equal(3.00, CostBudget.PricingFor("kimi-k4").InputPerMTok);
            Assert.Equal(2.00, CostBudget.PricingFor("qwen4-max").InputPerMTok);
        }

        [Fact]
        public void AnEntirelyUnknownIdStillFallsBackToSonnetRates()
        {
            // Over-reporting is the safe direction for a spend ceiling.
            var unknown = CostBudget.PricingFor("some-model-nobody-has-heard-of");
            Assert.Equal(3.00, unknown.InputPerMTok);
            Assert.Equal(15.00, unknown.OutputPerMTok);
        }

        [Fact]
        public void ClaudeRatesAreUntouchedByTheNewEntries()
        {
            var sonnet = CostBudget.PricingFor("claude-sonnet-5", new DateTime(2026, 9, 1));
            Assert.Equal(3.00, sonnet.InputPerMTok);
            Assert.Equal(15.00, sonnet.OutputPerMTok);
            Assert.Equal(3.00 * CostBudget.CacheWriteMultiplier, sonnet.CacheWritePerMTok);
            Assert.Equal(3.00 * CostBudget.CacheReadMultiplier, sonnet.CacheReadPerMTok);

            var haiku = CostBudget.PricingFor("claude-haiku-4-5");
            Assert.Equal(1.00, haiku.InputPerMTok);
            Assert.Equal(5.00, haiku.OutputPerMTok);
        }

        [Fact]
        public void ARealTurnOnDeepSeekCostsAFractionOfTheSameTurnOnSonnet()
        {
            // The shape of a mid-size iteration on a real floor plan.
            var usage = new TokenUsage
            {
                InputTokens = 40_000,
                OutputTokens = 1_500,
                CacheReadInputTokens = 60_000
            };

            double sonnet = CostBudget.PricingFor("claude-sonnet-5", new DateTime(2026, 9, 1)).CostOf(usage);
            double deepSeek = CostBudget.PricingFor("deepseek-v4-flash").CostOf(usage);

            Assert.True(deepSeek < sonnet / 10, "DeepSeek came out at $" + deepSeek + " vs Sonnet $" + sonnet);
        }
    }
}
