using System;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Tunables surfaced behind the sidebar's settings gear. Defaults are the plan's
    /// §8 decisions: Sonnet 4.5 for the loop, $0.50 per turn, 25 iterations.
    /// </summary>
    public sealed class AgentSettings
    {
        /// <summary>
        /// Loop model. The plan named Sonnet 4.5; Sonnet 5 supersedes it at the same
        /// $3/$15 per MTok with markedly better agentic and coding behaviour.
        /// </summary>
        public const string DefaultLoopModel = "claude-sonnet-5";

        /// <summary>
        /// Self-review model (plan §8.1 chose Opus 5). A second opinion is worth more from a
        /// stronger model than the one that did the work, and the review is one short call.
        /// </summary>
        public const string DefaultReviewerModel = "claude-opus-5";

        public string LoopModel { get; set; } = DefaultLoopModel;
        public string ReviewerModel { get; set; } = DefaultReviewerModel;

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
        public double MaxCostUsd { get; set; } = 0.50;
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

        public string ScriptLogPath { get; set; }
        public string CaptureLogPath { get; set; }

        public AgentSettings()
        {
            ScriptLogPath = System.IO.Path.Combine(JsonlLogger.DefaultDirectory, "script_log.jsonl");
            CaptureLogPath = System.IO.Path.Combine(JsonlLogger.DefaultDirectory, "capture_log.jsonl");
        }

        public AgentSettings Clone() => new AgentSettings
        {
            LoopModel = LoopModel,
            ReviewerModel = ReviewerModel,
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
            ScriptLogPath = ScriptLogPath,
            CaptureLogPath = CaptureLogPath
        };
    }
}
