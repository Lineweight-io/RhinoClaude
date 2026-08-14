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
        /// <summary>
        /// Rates keyed by model-id prefix, longest match wins. Cache-write is 1.25x input
        /// and cache-read is 0.1x input for every current model, so only the two base
        /// rates need maintaining per entry.
        ///
        /// Sonnet 5 carries introductory pricing ($2/$10) through 2026-08-31. The list rate is
        /// used instead, so the meter over-estimates slightly until then rather than encoding
        /// a cutoff date that silently becomes wrong.
        /// </summary>
        private static readonly List<KeyValuePair<string, ModelPricing>> Table =
            new List<KeyValuePair<string, ModelPricing>>
            {
                Rate("claude-sonnet-4-5", 3.00, 15.00),
                Rate("claude-sonnet-4-6", 3.00, 15.00),
                Rate("claude-sonnet-5",   3.00, 15.00),
                Rate("claude-opus-4",     5.00, 25.00),
                Rate("claude-opus-5",     5.00, 25.00),
                Rate("claude-haiku-4-5",  1.00,  5.00),
                Rate("claude-fable-5",   10.00, 50.00),
            };

        private static KeyValuePair<string, ModelPricing> Rate(string prefix, double input, double output)
        {
            return new KeyValuePair<string, ModelPricing>(prefix, new ModelPricing
            {
                InputPerMTok = input,
                OutputPerMTok = output,
                CacheWritePerMTok = input * 1.25,
                CacheReadPerMTok = input * 0.10
            });
        }

        /// <summary>Falls back to Sonnet-tier rates for an unrecognised model id.</summary>
        public static ModelPricing PricingFor(string modelId)
        {
            ModelPricing best = null;
            int bestLength = -1;
            if (!string.IsNullOrEmpty(modelId))
            {
                foreach (var entry in Table)
                {
                    if (modelId.StartsWith(entry.Key, StringComparison.OrdinalIgnoreCase) &&
                        entry.Key.Length > bestLength)
                    {
                        best = entry.Value;
                        bestLength = entry.Key.Length;
                    }
                }
            }
            return best ?? Table[0].Value;
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
