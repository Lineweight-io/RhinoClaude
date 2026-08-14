using System;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Which request parameters a given model actually accepts.
    ///
    /// This matters because the settings gear lets the model be changed at runtime, and the
    /// parameters are not interchangeable: sending <c>thinking</c> or <c>output_config.effort</c>
    /// to Sonnet 4.5 is a 400, while omitting <c>thinking</c> on Sonnet 5 silently changes
    /// behaviour (adaptive thinking is on by default there). Rather than pin one model's shape,
    /// the request is built from these flags.
    /// </summary>
    public static class ModelCapabilities
    {
        /// <summary>
        /// Adaptive thinking (<c>thinking: {type: "adaptive"}</c>). On models without it the
        /// parameter must be omitted entirely — the older <c>budget_tokens</c> form is not used
        /// here because this loop has no reason to cap thinking separately from max_tokens.
        /// </summary>
        public static bool SupportsAdaptiveThinking(string modelId)
        {
            return MatchesAny(modelId,
                "claude-sonnet-4-6",
                "claude-sonnet-5",
                "claude-opus-4-6",
                "claude-opus-4-7",
                "claude-opus-4-8",
                "claude-opus-5",
                "claude-fable-5",
                "claude-mythos-5");
        }

        /// <summary>
        /// <c>output_config.effort</c>. Errors on Sonnet 4.5 and Haiku 4.5.
        /// </summary>
        public static bool SupportsEffort(string modelId)
        {
            return SupportsAdaptiveThinking(modelId) || MatchesAny(modelId, "claude-opus-4-5");
        }

        /// <summary>
        /// Whether the model accepts the <c>xhigh</c> effort level. Opus 4.6 and Opus 4.5 top
        /// out below it, so an <c>xhigh</c> carried over from another model has to be clamped.
        /// </summary>
        public static bool SupportsXHighEffort(string modelId)
        {
            return MatchesAny(modelId,
                "claude-sonnet-5",
                "claude-opus-4-7",
                "claude-opus-4-8",
                "claude-opus-5",
                "claude-fable-5",
                "claude-mythos-5");
        }

        /// <summary>
        /// True when omitting <c>thinking</c> still produces thinking. On these models
        /// <c>max_tokens</c> caps thinking plus response text together, so a limit tuned for a
        /// non-thinking model can truncate the answer.
        /// </summary>
        public static bool ThinksByDefault(string modelId)
        {
            return MatchesAny(modelId,
                "claude-sonnet-5",
                "claude-opus-5",
                "claude-fable-5",
                "claude-mythos-5");
        }

        /// <summary>Clamp an effort level to something the model will accept.</summary>
        public static string ClampEffort(string modelId, string effort)
        {
            if (string.IsNullOrWhiteSpace(effort)) return null;
            effort = effort.Trim().ToLowerInvariant();

            switch (effort)
            {
                case "low":
                case "medium":
                case "high":
                    return effort;
                case "xhigh":
                    return SupportsXHighEffort(modelId) ? "xhigh" : "high";
                case "max":
                    return "max";
                default:
                    return "high";
            }
        }

        public static readonly string[] EffortLevels = { "low", "medium", "high", "xhigh", "max" };

        private static bool MatchesAny(string modelId, params string[] prefixes)
        {
            if (string.IsNullOrEmpty(modelId)) return false;
            foreach (var prefix in prefixes)
            {
                if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    // Guard against a longer id that merely shares a prefix — "claude-sonnet-5"
                    // must not match "claude-sonnet-5x" if such a thing ever ships. A dated
                    // snapshot suffix always begins with '-' followed by digits.
                    if (modelId.Length == prefix.Length) return true;
                    char next = modelId[prefix.Length];
                    if (next == '-' || next == '@' || next == '[') return true;
                }
            }
            return false;
        }
    }
}
