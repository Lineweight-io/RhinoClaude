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

        public string LoopModel { get; set; } = DefaultLoopModel;
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
            MaxCostUsd = MaxCostUsd,
            MaxIterations = MaxIterations,
            MaxTokens = MaxTokens,
            Effort = Effort,
            ShowThinking = ShowThinking,
            UiFlushIntervalMs = UiFlushIntervalMs,
            EnableScriptTool = EnableScriptTool,
            ScriptTimeoutSeconds = ScriptTimeoutSeconds,
            ScriptLogPath = ScriptLogPath,
            CaptureLogPath = CaptureLogPath
        };
    }
}
