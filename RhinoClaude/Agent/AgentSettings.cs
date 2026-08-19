using System;
using System.Collections.Generic;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Tunables surfaced behind the sidebar's settings gear. Defaults follow the plan's
    /// §8 decisions — Sonnet for the loop, 25 iterations — with the per-turn ceiling
    /// raised from the plan's $0.50 after live testing (see <see cref="MaxCostUsd"/>).
    /// </summary>
    public sealed class AgentSettings
    {
        /// <summary>
        /// Loop model. The plan named Sonnet 4.5, then Sonnet 5; Sonnet 5 is the current
        /// default, at $3/$15 per MTok list against Haiku 4.5's $1/$5.
        ///
        /// The trade runs the other way from Haiku. Sonnet 5 takes both <c>thinking</c> and
        /// <c>output_config.effort</c> — <see cref="ModelCapabilities"/> sends both, and the
        /// settings gear no longer greys them out — so the <see cref="Effort"/> and <see
        /// cref="ShowThinking"/> values below are live rather than inert. Its context window
        /// is 1M rather than Haiku's 200K, which lifts the one wall a busy document could
        /// previously hit.
        /// </summary>
        public const string DefaultLoopModel = "claude-sonnet-5";

        /// <summary>
        /// Self-review model (plan §8.1 chose Opus 5). A second opinion is worth more from a
        /// stronger model than the one that did the work, and the review is one short call.
        /// </summary>
        public const string DefaultReviewerModel = "claude-opus-5";

        /// <summary>
        /// One-shot calls that are not the loop — currently ClaudeTag's description-to-tag
        /// classification. Named separately rather than borrowing
        /// <see cref="DefaultLoopModel"/> so that changing the loop's model does not silently
        /// move every other call in the plugin with it.
        /// </summary>
        public const string DefaultUtilityModel = "claude-sonnet-5";

        public string LoopModel { get; set; } = DefaultLoopModel;
        public string ReviewerModel { get; set; } = DefaultReviewerModel;

        // ── Provider selection ────────────────────────────────────────

        /// <summary>
        /// Which service the loop talks to. Anthropic is the default and nothing about that
        /// path changes; the rest go through <see cref="OpenAiCompatibleClient"/>.
        /// </summary>
        public LlmProvider Provider { get; set; } = LlmProviderCatalog.Default;

        /// <summary>Base URL for <see cref="LlmProvider.OpenAiCompatibleCustom"/>. Ignored otherwise.</summary>
        public string CustomEndpoint { get; set; }

        /// <summary>
        /// API keys, one per provider. Kept separate so switching back and forth does not make
        /// the user re-paste a key, and so a leaked key belongs to exactly one service.
        /// </summary>
        private readonly Dictionary<LlmProvider, string> _apiKeys = new Dictionary<LlmProvider, string>();

        /// <summary>Loop and reviewer model last chosen for each provider.</summary>
        private readonly Dictionary<LlmProvider, string> _loopModels = new Dictionary<LlmProvider, string>();
        private readonly Dictionary<LlmProvider, string> _reviewerModels = new Dictionary<LlmProvider, string>();

        public string ApiKeyFor(LlmProvider provider) =>
            _apiKeys.TryGetValue(provider, out var key) ? key : null;

        public void SetApiKey(LlmProvider provider, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) _apiKeys.Remove(provider);
            else _apiKeys[provider] = apiKey.Trim();
        }

        /// <summary>The key for the provider currently selected.</summary>
        public string ActiveApiKey => ApiKeyFor(Provider);

        /// <summary>
        /// The endpoint actually used: the typed-in URL for the custom provider, the catalog's
        /// otherwise.
        /// </summary>
        public string ActiveEndpoint
        {
            get
            {
                var info = LlmProviderCatalog.Get(Provider);
                return info.NeedsCustomEndpoint
                    ? (CustomEndpoint ?? string.Empty).Trim()
                    : info.BaseUrl;
            }
        }

        /// <summary>
        /// Switch provider, remembering the models chosen for the old one and restoring (or
        /// defaulting) the models for the new one. Called by the settings dialog rather than
        /// setting <see cref="Provider"/> directly, so a switch never leaves a Claude model id
        /// pointed at DeepSeek.
        /// </summary>
        public void SelectProvider(LlmProvider provider)
        {
            if (provider == Provider) return;

            _loopModels[Provider] = LoopModel;
            _reviewerModels[Provider] = ReviewerModel;

            Provider = provider;
            var info = LlmProviderCatalog.Get(provider);

            LoopModel = _loopModels.TryGetValue(provider, out var loop) && !string.IsNullOrWhiteSpace(loop)
                ? loop
                : info.DefaultLoopModel;

            ReviewerModel = _reviewerModels.TryGetValue(provider, out var reviewer) && !string.IsNullOrWhiteSpace(reviewer)
                ? reviewer
                : info.DefaultReviewerModel;
        }

        /// <summary>
        /// Copy the whole provider block across. The settings dialog edits a clone, so this is
        /// how an accepted edit reaches the host's live settings object.
        /// </summary>
        public void AdoptProviderSettings(AgentSettings other)
        {
            if (other == null) return;

            Provider = other.Provider;
            CustomEndpoint = other.CustomEndpoint;

            _apiKeys.Clear();
            foreach (var pair in other._apiKeys) _apiKeys[pair.Key] = pair.Value;

            _loopModels.Clear();
            foreach (var pair in other._loopModels) _loopModels[pair.Key] = pair.Value;

            _reviewerModels.Clear();
            foreach (var pair in other._reviewerModels) _reviewerModels[pair.Key] = pair.Value;
        }

        /// <summary>Models remembered per provider — persisted so a switch back is a no-op.</summary>
        public IReadOnlyDictionary<LlmProvider, string> RememberedLoopModels => _loopModels;
        public IReadOnlyDictionary<LlmProvider, string> RememberedReviewerModels => _reviewerModels;

        public void RememberModels(LlmProvider provider, string loopModel, string reviewerModel)
        {
            if (!string.IsNullOrWhiteSpace(loopModel)) _loopModels[provider] = loopModel;
            if (!string.IsNullOrWhiteSpace(reviewerModel)) _reviewerModels[provider] = reviewerModel;
        }

        /// <summary>Run self-review when the agent calls signal_done.</summary>
        public bool EnableSelfReview { get; set; } = true;

        /// <summary>Plan §5.5: beyond this many iterate cycles per turn, force ask_user.</summary>
        public int MaxReviewCycles { get; set; } = 2;

        /// <summary>
        /// Plan §5.1 trigger 2: review defensively after this many iterations without one, even
        /// if the agent has not called signal_done. Catches a loop that is churning without
        /// noticing it has gone wrong. 0 disables the defensive trigger.
        /// </summary>
        public int DefensiveReviewAfterIterations { get; set; } = 10;
        /// <summary>
        /// Per-turn USD ceiling. The plan's $0.50 tripped after three iterations of read-only
        /// tools on a real (1,918-object) floor plan — the whole scene is re-sent uncached on
        /// every iteration, so input dominates. $2.00 covers a typical SD-scale request; the
        /// sidebar's settings gear overrides it per user.
        /// </summary>
        public const double DefaultMaxCostUsd = 2.00;

        public double MaxCostUsd { get; set; } = DefaultMaxCostUsd;
        public int MaxIterations { get; set; } = 25;

        /// <summary>
        /// Caps thinking plus response text together on models that think by default, so this
        /// needs more headroom than a non-thinking model would want. Requests always stream,
        /// so there is no HTTP-timeout reason to keep it small.
        /// </summary>
        public int MaxTokens { get; set; } = 32000;

        /// <summary>
        /// <c>output_config.effort</c>. 'high' is the API default; 'xhigh' suits the hardest
        /// agentic work, 'medium' is the cost-saving step down. Ignored on models without it.
        /// </summary>
        public string Effort { get; set; } = "high";

        /// <summary>
        /// Request summarized thinking and render it in the sidebar. Thinking is billed
        /// identically either way — this only controls whether the reasoning is visible
        /// instead of appearing as a silent pause before output.
        /// </summary>
        public bool ShowThinking { get; set; } = true;

        /// <summary>Coalescing interval for streamed text, per the plan's risk #2.</summary>
        public int UiFlushIntervalMs { get; set; } = 33;

        /// <summary>Tier 2 escape hatch. Off disables run_rhinocommon_script entirely.</summary>
        public bool EnableScriptTool { get; set; } = true;

        public int ScriptTimeoutSeconds { get; set; } = 15;

        /// <summary>
        /// Tier 3 escape hatch (plan §4.7). Off by default: scripted commands are non-atomic,
        /// undo less cleanly than everything else, and the curated tools plus the C# hatch
        /// cover almost everything. Turn it on when a specific command needs it.
        /// </summary>
        public bool EnableRhinoCommandTool { get; set; } = false;

        /// <summary>
        /// Register the semantic layer's tools alongside the raw geometry ones. Off means the
        /// agent sees exactly the phase 1 tool set — a clean A/B and a safety valve if the
        /// classifier misbehaves on a particular file.
        /// </summary>
        public bool EnableSemanticTools { get; set; } = true;

        /// <summary>
        /// Firm-standard floor-to-floor, in model units, used when Levels are inferred rather
        /// than drawn (semantic plan §5.6). 0 means "not configured" — get_level_info then
        /// reports only the levels it can actually see.
        /// </summary>
        public double FloorToFloorDefault { get; set; }

        /// <summary>
        /// The firm-level learned layer convention as JSON, applied to every document this user
        /// opens. The per-document map lives in the .3dm and takes precedence over this.
        /// </summary>
        public string FirmLayerConventionJson { get; set; }

        public string ScriptLogPath { get; set; }
        public string CaptureLogPath { get; set; }

        /// <summary>Classifier timings, per semantic plan §6.2.</summary>
        public string ClassifierLogPath { get; set; }

        public AgentSettings()
        {
            ScriptLogPath = System.IO.Path.Combine(JsonlLogger.DefaultDirectory, "script_log.jsonl");
            CaptureLogPath = System.IO.Path.Combine(JsonlLogger.DefaultDirectory, "capture_log.jsonl");
            ClassifierLogPath = System.IO.Path.Combine(JsonlLogger.DefaultDirectory, "classifier_timing.jsonl");
        }

        public AgentSettings Clone()
        {
            var copy = CloneScalars();
            foreach (var pair in _apiKeys) copy._apiKeys[pair.Key] = pair.Value;
            foreach (var pair in _loopModels) copy._loopModels[pair.Key] = pair.Value;
            foreach (var pair in _reviewerModels) copy._reviewerModels[pair.Key] = pair.Value;
            return copy;
        }

        private AgentSettings CloneScalars() => new AgentSettings
        {
            LoopModel = LoopModel,
            ReviewerModel = ReviewerModel,
            Provider = Provider,
            CustomEndpoint = CustomEndpoint,
            EnableSelfReview = EnableSelfReview,
            MaxReviewCycles = MaxReviewCycles,
            DefensiveReviewAfterIterations = DefensiveReviewAfterIterations,
            MaxCostUsd = MaxCostUsd,
            MaxIterations = MaxIterations,
            MaxTokens = MaxTokens,
            Effort = Effort,
            ShowThinking = ShowThinking,
            UiFlushIntervalMs = UiFlushIntervalMs,
            EnableScriptTool = EnableScriptTool,
            ScriptTimeoutSeconds = ScriptTimeoutSeconds,
            EnableRhinoCommandTool = EnableRhinoCommandTool,
            EnableSemanticTools = EnableSemanticTools,
            FloorToFloorDefault = FloorToFloorDefault,
            FirmLayerConventionJson = FirmLayerConventionJson,
            ScriptLogPath = ScriptLogPath,
            CaptureLogPath = CaptureLogPath,
            ClassifierLogPath = ClassifierLogPath
        };
    }
}
