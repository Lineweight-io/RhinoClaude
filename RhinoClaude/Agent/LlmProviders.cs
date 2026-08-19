using System;
using System.Collections.Generic;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Which service the loop talks to. Everything except <see cref="Anthropic"/> goes through
    /// <see cref="OpenAiCompatibleClient"/> — they all speak OpenAI Chat Completions, and the
    /// differences between them are endpoints, model ids, and the handful of flags in
    /// <see cref="OpenAiQuirks"/>.
    /// </summary>
    public enum LlmProvider
    {
        Anthropic = 0,
        DeepSeek = 1,
        Kimi = 2,
        Qwen = 3,
        OpenAI = 4,
        Ollama = 5,

        /// <summary>Any other OpenAI-compatible endpoint, with the URL typed in by the user.</summary>
        OpenAiCompatibleCustom = 6
    }

    /// <summary>One model offered in the settings dropdown.</summary>
    public sealed class LlmModelOption
    {
        public LlmModelOption(string id, string label)
        {
            Id = id;
            Label = label;
        }

        public string Id { get; }
        public string Label { get; }
    }

    /// <summary>
    /// Where OpenAI-compatible providers actually differ. Defaults describe the common case
    /// (DeepSeek, Moonshot, Model Studio); the outliers set what they need in the catalog.
    /// </summary>
    public sealed class OpenAiQuirks
    {
        /// <summary>Some self-hosted gateways only do one-shot completions.</summary>
        public bool SupportsStreaming { get; set; } = true;

        /// <summary><c>stream_options: {include_usage: true}</c>. Without it a streamed turn
        /// reports no usage at all and the cost meter reads zero.</summary>
        public bool SupportsStreamUsage { get; set; } = true;

        /// <summary><c>response_format: {type: "json_schema", …}</c>. Only OpenAI itself
        /// implements the strict-schema form across the board.</summary>
        public bool SupportsJsonSchema { get; set; }

        /// <summary><c>response_format: {type: "json_object"}</c> — the portable fallback for
        /// the self-review pass, which wants JSON back but can live without schema enforcement.</summary>
        public bool SupportsJsonObject { get; set; } = true;

        /// <summary>
        /// Whether image content parts survive. False means the review screenshots are dropped
        /// and replaced with a one-line note, rather than 400-ing the whole review call.
        /// </summary>
        public bool AcceptsImages { get; set; }

        /// <summary>OpenAI renamed <c>max_tokens</c> to <c>max_completion_tokens</c>; nobody
        /// else did.</summary>
        public bool UseMaxCompletionTokens { get; set; }

        /// <summary>Send <c>tool_choice: "auto"</c> alongside the tools array. Harmless
        /// everywhere it is understood, and it stops a couple of gateways defaulting to "none".</summary>
        public bool SendToolChoice { get; set; } = true;

        /// <summary>OpenAI's newer models want <c>developer</c> where everyone else wants
        /// <c>system</c>.</summary>
        public string SystemRoleName { get; set; } = "system";

        /// <summary>Local runtimes take any string as the key, or none at all.</summary>
        public bool RequiresApiKey { get; set; } = true;

        public OpenAiQuirks Clone() => (OpenAiQuirks)MemberwiseClone();
    }

    /// <summary>Everything the UI and the client factory need to know about one provider.</summary>
    public sealed class LlmProviderInfo
    {
        public LlmProvider Provider { get; set; }
        public string DisplayName { get; set; }

        /// <summary>Base URL including the version segment. Empty for the custom provider.</summary>
        public string BaseUrl { get; set; }

        /// <summary>Key under which the API key is stored in Rhino's plugin settings.</summary>
        public string ApiKeySettingsKey { get; set; }

        /// <summary>Checked at load when nothing is stored, matching the Anthropic path.</summary>
        public string ApiKeyEnvironmentVariable { get; set; }

        public string DefaultLoopModel { get; set; }
        public string DefaultReviewerModel { get; set; }

        /// <summary>Suggestions for the model dropdown. The field stays free-text — these lists
        /// go stale fast (see <see cref="PricingUrl"/>).</summary>
        public IReadOnlyList<LlmModelOption> KnownModels { get; set; } = new LlmModelOption[0];

        public OpenAiQuirks Quirks { get; set; } = new OpenAiQuirks();

        /// <summary>Where to check whether the hardcoded rates in <see cref="CostBudget"/> still hold.</summary>
        public string PricingUrl { get; set; }

        /// <summary>Anything worth telling the user before they switch, or null.</summary>
        public string Caveat { get; set; }

        public bool IsAnthropic => Provider == LlmProvider.Anthropic;
        public bool NeedsCustomEndpoint => Provider == LlmProvider.OpenAiCompatibleCustom;
    }

    /// <summary>
    /// The provider table. Endpoints, model ids, and quirks live here; the matching per-token
    /// rates live in <see cref="CostBudget"/>, keyed by model-id prefix.
    ///
    /// Verified 2026-08-14 against each provider's own docs. Model line-ups on the cheap
    /// providers turn over every few months — DeepSeek retired <c>deepseek-chat</c> and
    /// <c>deepseek-reasoner</c> in July 2026 — so the dropdowns are suggestions, not a
    /// whitelist, and the model field accepts anything typed into it.
    /// </summary>
    public static class LlmProviderCatalog
    {
        public const LlmProvider Default = LlmProvider.Anthropic;

        private static readonly List<LlmProviderInfo> All = new List<LlmProviderInfo>
        {
            new LlmProviderInfo
            {
                Provider = LlmProvider.Anthropic,
                DisplayName = "Anthropic (Claude)",
                BaseUrl = "https://api.anthropic.com/v1",
                ApiKeySettingsKey = "AnthropicApiKey",
                ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY",
                DefaultLoopModel = AgentSettings.DefaultLoopModel,
                DefaultReviewerModel = AgentSettings.DefaultReviewerModel,
                PricingUrl = "https://claude.com/pricing#api",
                // Cheapest first, which is also the order someone scanning for a cost decision
                // reads in. The default entry has to carry the same id as
                // AgentSettings.DefaultLoopModel or the dropdown opens with nothing selected.
                KnownModels = new[]
                {
                    new LlmModelOption("claude-haiku-4-5-20251001", "Claude Haiku 4.5"),
                    new LlmModelOption(AgentSettings.DefaultLoopModel, "Claude Sonnet 5 (default)"),
                    new LlmModelOption("claude-sonnet-4-6", "Claude Sonnet 4.6"),
                    new LlmModelOption("claude-sonnet-4-5-20250929", "Claude Sonnet 4.5 (legacy)"),
                    new LlmModelOption("claude-opus-5", "Claude Opus 5"),
                    new LlmModelOption("claude-opus-4-8", "Claude Opus 4.8")
                }
            },

            new LlmProviderInfo
            {
                Provider = LlmProvider.DeepSeek,
                DisplayName = "DeepSeek",
                // The OpenAI-compatible surface is served from both /v1 and the bare host;
                // /v1 is what the docs use for the OpenAI SDK's base_url.
                BaseUrl = "https://api.deepseek.com/v1",
                ApiKeySettingsKey = "DeepSeekApiKey",
                ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY",
                DefaultLoopModel = "deepseek-v4-flash",
                DefaultReviewerModel = "deepseek-v4-pro",
                PricingUrl = "https://api-docs.deepseek.com/quick_start/pricing",
                Caveat =
                    "deepseek-chat and deepseek-reasoner were retired after 2026-07-24; the two " +
                    "current ids are deepseek-v4-flash and deepseek-v4-pro. Text only — review " +
                    "screenshots are dropped. Off-peak rates (half price) apply outside peak UTC hours.",
                KnownModels = new[]
                {
                    new LlmModelOption("deepseek-v4-flash", "DeepSeek V4 Flash (cheapest)"),
                    new LlmModelOption("deepseek-v4-pro", "DeepSeek V4 Pro")
                }
            },

            new LlmProviderInfo
            {
                Provider = LlmProvider.Kimi,
                DisplayName = "Moonshot (Kimi)",
                BaseUrl = "https://api.moonshot.ai/v1",
                ApiKeySettingsKey = "KimiApiKey",
                ApiKeyEnvironmentVariable = "MOONSHOT_API_KEY",
                // K2.6 rather than K3: K3 lists at $3/$15, which is Sonnet 5's list rate, so it
                // is not a cost move. K2.6 is the one that actually undercuts Claude.
                DefaultLoopModel = "kimi-k2.6",
                DefaultReviewerModel = "kimi-k3",
                PricingUrl = "https://platform.kimi.ai/docs/pricing/chat",
                Caveat =
                    "kimi-k2-0711-preview is long superseded. K3 (released 2026-07-16) is $3/$15 " +
                    "per MTok — the same as Sonnet 5 list — so pick K2.6 or K2.5 if the point is cost.",
                KnownModels = new[]
                {
                    new LlmModelOption("kimi-k2.6", "Kimi K2.6 (256K, best value)"),
                    new LlmModelOption("kimi-k2.5", "Kimi K2.5 (cheapest)"),
                    new LlmModelOption("kimi-k2.7-code", "Kimi K2.7 Code"),
                    new LlmModelOption("kimi-k3", "Kimi K3 (1M context, flagship)")
                }
            },

            new LlmProviderInfo
            {
                Provider = LlmProvider.Qwen,
                DisplayName = "Alibaba Qwen (Model Studio)",
                // Model Studio now documents a per-workspace Singapore host; the old shared
                // dashscope-intl domain still answers but is no longer recommended. Users on a
                // workspace endpoint should switch the provider to "custom endpoint" and paste
                // https://<workspace>.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1 in.
                BaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1",
                ApiKeySettingsKey = "QwenApiKey",
                ApiKeyEnvironmentVariable = "DASHSCOPE_API_KEY",
                DefaultLoopModel = "qwen-plus",
                DefaultReviewerModel = "qwen3.7-max",
                PricingUrl = "https://www.alibabacloud.com/help/en/model-studio/models",
                Caveat =
                    "The shared dashscope-intl host is deprecated in favour of a per-workspace " +
                    "Singapore endpoint — if calls 404, switch to the custom-endpoint provider and " +
                    "paste your workspace URL. qwen2.5-72b-instruct is a legacy id.",
                KnownModels = new[]
                {
                    new LlmModelOption("qwen-plus", "Qwen Plus (mid-tier)"),
                    new LlmModelOption("qwen3.7-flash", "Qwen 3.7 Flash (cheapest)"),
                    new LlmModelOption("qwen3.7-max", "Qwen 3.7 Max"),
                    new LlmModelOption("qwen3.8-max", "Qwen 3.8 Max (flagship)")
                }
            },

            new LlmProviderInfo
            {
                Provider = LlmProvider.OpenAI,
                DisplayName = "OpenAI",
                BaseUrl = "https://api.openai.com/v1",
                ApiKeySettingsKey = "OpenAiApiKey",
                ApiKeyEnvironmentVariable = "OPENAI_API_KEY",
                DefaultLoopModel = "gpt-5.1",
                DefaultReviewerModel = "gpt-5.1",
                PricingUrl = "https://openai.com/api/pricing/",
                Caveat = "Not requested, but the same adapter serves it. Model ids and rates are unverified here.",
                Quirks = new OpenAiQuirks
                {
                    SupportsJsonSchema = true,
                    AcceptsImages = true,
                    UseMaxCompletionTokens = true,
                    SystemRoleName = "developer"
                },
                KnownModels = new[]
                {
                    new LlmModelOption("gpt-5.1", "GPT-5.1"),
                    new LlmModelOption("gpt-5.1-mini", "GPT-5.1 mini")
                }
            },

            new LlmProviderInfo
            {
                Provider = LlmProvider.Ollama,
                DisplayName = "Ollama (local)",
                BaseUrl = "http://localhost:11434/v1",
                ApiKeySettingsKey = "OllamaApiKey",
                ApiKeyEnvironmentVariable = null,
                DefaultLoopModel = "qwen3:32b",
                DefaultReviewerModel = "qwen3:32b",
                PricingUrl = null,
                Caveat = "Free, and priced at $0 by the cost meter. Tool use depends entirely on the local model.",
                Quirks = new OpenAiQuirks
                {
                    RequiresApiKey = false,
                    SupportsStreamUsage = true,
                    SupportsJsonObject = true
                },
                KnownModels = new[]
                {
                    new LlmModelOption("qwen3:32b", "qwen3:32b"),
                    new LlmModelOption("llama3.3:70b", "llama3.3:70b")
                }
            },

            new LlmProviderInfo
            {
                Provider = LlmProvider.OpenAiCompatibleCustom,
                DisplayName = "Custom OpenAI-compatible endpoint",
                BaseUrl = string.Empty,
                ApiKeySettingsKey = "CustomLlmApiKey",
                ApiKeyEnvironmentVariable = null,
                DefaultLoopModel = string.Empty,
                DefaultReviewerModel = string.Empty,
                PricingUrl = null,
                Caveat = "Priced at $0 unless the model id happens to match a known pricing prefix.",
                Quirks = new OpenAiQuirks { RequiresApiKey = false }
            }
        };

        public static IReadOnlyList<LlmProviderInfo> Providers => All;

        public static LlmProviderInfo Get(LlmProvider provider)
        {
            foreach (var info in All)
                if (info.Provider == provider) return info;

            return All[0];
        }

        /// <summary>Parse a persisted provider name, falling back to Anthropic.</summary>
        public static LlmProvider Parse(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                foreach (LlmProvider value in Enum.GetValues(typeof(LlmProvider)))
                {
                    if (string.Equals(value.ToString(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                        return value;
                }
            }
            return Default;
        }
    }
}
