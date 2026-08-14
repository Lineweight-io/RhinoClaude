using System;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Resolves the <see cref="AgentSettings.Provider"/> setting to a live client.
    ///
    /// The Anthropic client is passed in rather than constructed here because the plugin owns
    /// it: <c>ClaudeSetKey</c> and the <c>ANTHROPIC_API_KEY</c> environment variable both write
    /// to that one instance, and every other caller (ClaudeTag, the naming-convention command)
    /// already holds it.
    /// </summary>
    public static class LlmClientFactory
    {
        public static ILlmClient Create(AgentSettings settings, AnthropicClient anthropic)
        {
            if (settings == null || settings.Provider == LlmProvider.Anthropic)
            {
                if (anthropic == null) throw new InvalidOperationException("The Anthropic client is not initialised.");
                anthropic.ModelId = settings?.LoopModel;
                return anthropic;
            }

            var info = LlmProviderCatalog.Get(settings.Provider);

            return new OpenAiCompatibleClient(
                info.DisplayName,
                settings.ActiveEndpoint,
                settings.LoopModel,
                settings.ActiveApiKey,
                info.Quirks);
        }
    }
}
