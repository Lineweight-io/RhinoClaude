using System;
using System.Collections.Generic;

namespace RhinoClaude.Agent
{
    /// <summary>Per-million-token rates in USD.</summary>
    public sealed class ModelPricing
    {
        public double InputPerMTok { get; set; }
        public double OutputPerMTok { get; set; }
        public double CacheWritePerMTok { get; set; }
        public double CacheReadPerMTok { get; set; }

        public double CostOf(TokenUsage usage)
        {
            if (usage == null) return 0.0;
            return (usage.InputTokens / 1_000_000.0) * InputPerMTok
                 + (usage.OutputTokens / 1_000_000.0) * OutputPerMTok
                 + (usage.CacheCreationInputTokens / 1_000_000.0) * CacheWritePerMTok
                 + (usage.CacheReadInputTokens / 1_000_000.0) * CacheReadPerMTok;
        }
    }

    /// <summary>A model call made outside the loop's own model — currently self-review.</summary>
    public sealed class SideCall
    {
        public string Label { get; set; }
        public string ModelId { get; set; }
        public TokenUsage Usage { get; set; } = new TokenUsage();
        public double CostUsd { get; set; }
    }

    /// <summary>
    /// Token accounting for one agent turn, plus the two guardrails from the plan:
    /// a per-turn USD ceiling and an iteration cap.
    /// </summary>
    public sealed class CostBudget
    {
        /// <summary>Cache writes bill at 1.25x the input rate (the 5-minute TTL).</summary>
        /// <remarks>
        /// The 1-hour TTL is 2x instead. Nothing in the plugin sets <c>cache_control</c>, so
        /// both cache pools are always zero today; revisit this constant if caching is added
        /// with a 1h TTL.
        /// </remarks>
        public const double CacheWriteMultiplier = 1.25;

        /// <summary>Cache reads bill at 0.1x the input rate.</summary>
        public const double CacheReadMultiplier = 0.10;

        /// <summary>One model's rates, plus any promotional rate and the date it lapses.</summary>
        private sealed class RateEntry
        {
            public string Prefix;
            public ModelPricing List;
            public ModelPricing Intro;          // null when the model has no promotional rate
            public DateTime IntroThroughUtc;    // inclusive; the last day Intro applies

            public ModelPricing For(DateTime asOfUtc) =>
                Intro != null && asOfUtc.Date <= IntroThroughUtc.Date ? Intro : List;
        }

        /// <summary>
        /// The one place per-token prices live. Keyed by model-id prefix, longest match wins.
        /// Cache-write and cache-read are fixed multiples of the input rate on every current
        /// model, so only the two base rates are maintained per entry.
        /// </summary>
        private static readonly List<RateEntry> Table = new List<RateEntry>
        {
            Rate("claude-sonnet-4-5", 3.00, 15.00),
            Rate("claude-sonnet-4-6", 3.00, 15.00),
            // Sonnet 5 is on introductory pricing through 2026-08-31, after which it reverts
            // to the $3/$15 list rate. Encoded with its end date rather than assumed either
            // way: charging list today over-reports every Sonnet 5 turn by 50%.
            Rate("claude-sonnet-5",   3.00, 15.00, introInput: 2.00, introOutput: 10.00,
                                                   introThroughUtc: new DateTime(2026, 8, 31)),
            Rate("claude-opus-4",     5.00, 25.00),
            Rate("claude-opus-5",     5.00, 25.00),
            Rate("claude-haiku-4-5",  1.00,  5.00),
            Rate("claude-fable-5",   10.00, 50.00),

            // ── OpenAI-compatible providers ───────────────────────────
            //
            // These do not bill a cache write — they cache implicitly — so the write rate is
            // the plain input rate and the read rate is quoted directly rather than derived.
            // Every figure below was taken from the provider's own pricing page on 2026-08-14
            // and will go stale; the URL on each block is where to re-check it.
            //
            // https://api-docs.deepseek.com/quick_start/pricing
            // DeepSeek moved to peak / off-peak billing on 2026-08-16, with off-peak at half
            // these rates. Only the peak rate is encoded, so an off-peak turn over-reports by 2x
            // — the safe direction for a budget ceiling.
            Rate("deepseek-v4-flash", 0.14, 0.28,  cacheRead: 0.0028),
            Rate("deepseek-v4-pro",   0.435, 0.87, cacheRead: 0.003625),
            Rate("deepseek",          0.435, 0.87, cacheRead: 0.003625),   // unknown DeepSeek id

            // https://platform.kimi.ai/docs/pricing/chat
            Rate("kimi-k2.5",         0.60, 3.00,  cacheRead: 0.10),
            Rate("kimi-k2.6",         0.95, 4.00,  cacheRead: 0.16),
            Rate("kimi-k2.7",         0.95, 4.00,  cacheRead: 0.19),
            Rate("kimi-k3",           3.00, 15.00, cacheRead: 0.30),
            Rate("kimi",              3.00, 15.00, cacheRead: 0.30),       // unknown Kimi id
            Rate("moonshot",          3.00, 15.00, cacheRead: 0.30),

            // https://www.alibabacloud.com/help/en/model-studio/models  (International/Singapore)
            Rate("qwen-plus",         0.40, 1.20,  cacheRead: 0.04),
            Rate("qwen3.7-flash",     0.03, 0.13,  cacheRead: 0.003),
            Rate("qwen3.7-max",       2.00, 6.00,  cacheRead: 0.20,
                                      introInput: 1.25, introOutput: 3.75,
                                      introThroughUtc: new DateTime(2026, 12, 31)),
            Rate("qwen3.8-max",       2.00, 6.00,  cacheRead: 0.20),
            Rate("qwen",              2.00, 6.00,  cacheRead: 0.20),       // unknown Qwen id

            // Local models cost nothing to call.
            Rate("ollama",            0.00, 0.00,  cacheRead: 0.0),
        };

        private static RateEntry Rate(
            string prefix, double input, double output,
            double introInput = 0, double introOutput = 0, DateTime introThroughUtc = default(DateTime),
            double cacheRead = -1)
        {
            return new RateEntry
            {
                Prefix = prefix,
                List = Pricing(input, output, cacheRead),
                Intro = introInput > 0 ? Pricing(introInput, introOutput, cacheRead) : null,
                IntroThroughUtc = introThroughUtc
            };
        }

        /// <summary>
        /// Rates for one model. <paramref name="cacheRead"/> below zero means "derive it the
        /// Anthropic way" — 0.1x input for a read, 1.25x for a write. A provider that quotes its
        /// own cache-hit price passes it explicitly, and its write rate is the plain input rate,
        /// because none of the OpenAI-compatible providers bill for populating a cache.
        /// </summary>
        private static ModelPricing Pricing(double input, double output, double cacheRead = -1) => new ModelPricing
        {
            InputPerMTok = input,
            OutputPerMTok = output,
            CacheWritePerMTok = cacheRead < 0 ? input * CacheWriteMultiplier : input,
            CacheReadPerMTok = cacheRead < 0 ? input * CacheReadMultiplier : cacheRead
        };

        /// <summary>Falls back to Sonnet-tier rates for an unrecognised model id.</summary>
        public static ModelPricing PricingFor(string modelId) => PricingFor(modelId, DateTime.UtcNow);

        /// <summary>
        /// Rates in force on <paramref name="asOfUtc"/> — the date matters only while a model
        /// is on introductory pricing. Exposed so the boundary is testable.
        /// </summary>
        public static ModelPricing PricingFor(string modelId, DateTime asOfUtc)
        {
            RateEntry best = null;
            int bestLength = -1;
            if (!string.IsNullOrEmpty(modelId))
            {
                foreach (var entry in Table)
                {
                    if (modelId.StartsWith(entry.Prefix, StringComparison.OrdinalIgnoreCase) &&
                        entry.Prefix.Length > bestLength)
                    {
                        best = entry;
                        bestLength = entry.Prefix.Length;
                    }
                }
            }
            return (best ?? Table[0]).For(asOfUtc);
        }

        private readonly ModelPricing _pricing;

        public CostBudget(string modelId, double maxCostUsd, int maxIterations)
        {
            ModelId = modelId;
            _pricing = PricingFor(modelId);
            MaxCostUsd = maxCostUsd;
            MaxIterations = maxIterations;
        }

        public string ModelId { get; }
        public double MaxCostUsd { get; }
        public int MaxIterations { get; }

        public TokenUsage TotalUsage { get; } = new TokenUsage();
        public int Iterations { get; private set; }

        /// <summary>Per-iteration usage, for the cost-breakdown popover.</summary>
        public List<TokenUsage> PerIteration { get; } = new List<TokenUsage>();

        /// <summary>
        /// Calls made on a different model during the turn — currently the self-review pass,
        /// which runs on Opus while the loop runs on Sonnet. Priced on their own model so the
        /// meter stays honest; they count toward the ceiling but not the iteration cap.
        /// </summary>
        public List<SideCall> SideCalls { get; } = new List<SideCall>();

        public double SideSpendUsd { get; private set; }

        public double SpentUsd => _pricing.CostOf(TotalUsage) + SideSpendUsd;
        public double RemainingUsd => Math.Max(0.0, MaxCostUsd - SpentUsd);
        public double FractionSpent => MaxCostUsd <= 0 ? 1.0 : Math.Min(1.0, SpentUsd / MaxCostUsd);

        public bool CostExceeded => SpentUsd >= MaxCostUsd;
        public bool IterationsExceeded => Iterations >= MaxIterations;
        public bool Exceeded => CostExceeded || IterationsExceeded;

        /// <summary>Record one completed model call.</summary>
        public void RecordIteration(TokenUsage usage)
        {
            Iterations++;
            var snapshot = usage != null ? usage.Clone() : new TokenUsage();
            PerIteration.Add(snapshot);
            TotalUsage.Add(snapshot);
        }

        /// <summary>
        /// Record a call made on another model (the self-review pass). It does not advance the
        /// iteration counter — it is not a loop turn — but its cost does count against the
        /// per-turn ceiling, priced at its own model's rates.
        /// </summary>
        public void RecordSideCall(string label, string modelId, TokenUsage usage)
        {
            var snapshot = usage != null ? usage.Clone() : new TokenUsage();
            double cost = PricingFor(modelId).CostOf(snapshot);
            SideCalls.Add(new SideCall
            {
                Label = label,
                ModelId = modelId,
                Usage = snapshot,
                CostUsd = cost
            });
            SideSpendUsd += cost;
        }

        public string Describe()
        {
            return string.Format(
                "${0:0.00} / ${1:0.00}  |  iter {2}/{3}",
                SpentUsd, MaxCostUsd, Iterations, MaxIterations);
        }

        public string Breakdown()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Model: " + ModelId);
            sb.AppendLine(string.Format("Rates: ${0:0.00} in / ${1:0.00} out per MTok",
                _pricing.InputPerMTok, _pricing.OutputPerMTok));
            sb.AppendLine();
            for (int i = 0; i < PerIteration.Count; i++)
            {
                var u = PerIteration[i];
                sb.AppendLine(string.Format(
                    "  iter {0,2}:  in {1,7:n0}  out {2,6:n0}  cache w/r {3,6:n0}/{4,7:n0}  ${5:0.0000}",
                    i + 1, u.InputTokens, u.OutputTokens,
                    u.CacheCreationInputTokens, u.CacheReadInputTokens, _pricing.CostOf(u)));
            }
            if (SideCalls.Count > 0)
            {
                sb.AppendLine();
                foreach (var call in SideCalls)
                {
                    sb.AppendLine(string.Format(
                        "  {0} ({1}):  in {2,7:n0}  out {3,6:n0}  ${4:0.0000}",
                        call.Label, call.ModelId,
                        call.Usage.InputTokens, call.Usage.OutputTokens, call.CostUsd));
                }
            }

            sb.AppendLine();
            sb.AppendLine(string.Format("  TOTAL:   in {0,7:n0}  out {1,6:n0}                          ${2:0.0000}",
                TotalUsage.InputTokens, TotalUsage.OutputTokens, SpentUsd));
            return sb.ToString();
        }
    }
}
