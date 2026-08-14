using System;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Tunables surfaced behind the sidebar's settings gear. Defaults are the plan's
    /// §8 decisions: Sonnet 4.5 for the loop, $0.50 per turn, 25 iterations.
    /// </summary>
    public sealed class AgentSettings
    {
        /// <summary>Loop model (plan §8.1). Sonnet 4.5 is a dated snapshot id, deliberately pinned.</summary>
        public const string DefaultLoopModel = "claude-sonnet-4-5-20250929";

        public string LoopModel { get; set; } = DefaultLoopModel;
        public double MaxCostUsd { get; set; } = 0.50;
        public int MaxIterations { get; set; } = 25;
        public int MaxTokens { get; set; } = 16384;

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
            UiFlushIntervalMs = UiFlushIntervalMs,
            EnableScriptTool = EnableScriptTool,
            ScriptTimeoutSeconds = ScriptTimeoutSeconds,
            ScriptLogPath = ScriptLogPath,
            CaptureLogPath = CaptureLogPath
        };
    }
}
