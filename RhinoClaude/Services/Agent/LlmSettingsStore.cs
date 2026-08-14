using System;
using Rhino;
using RhinoClaude.Agent;

namespace RhinoClaude.Services.Agent
{
    /// <summary>
    /// Reads and writes the provider settings — which service, which models, which API keys —
    /// through Rhino's per-plugin persistent settings. Same pattern as the layer-convention
    /// store: firm-scope, not per-document, because a key belongs to the user, not the file.
    ///
    /// The Anthropic key keeps its existing <c>AnthropicApiKey</c> settings key so nothing an
    /// existing user has already saved with <c>ClaudeSetKey</c> is lost.
    /// </summary>
    public static class LlmSettingsStore
    {
        private const string ProviderKey = "LlmProvider";
        private const string CustomEndpointKey = "LlmCustomEndpoint";

        /// <summary>Write the provider block through to the plugin's settings.</summary>
        public static void Persist(AgentSettings settings)
        {
            var plugin = RhinoClaudePlugin.Instance;
            if (plugin == null || settings == null) return;

            try
            {
                plugin.Settings.SetString(ProviderKey, settings.Provider.ToString());
                plugin.Settings.SetString(CustomEndpointKey, settings.CustomEndpoint ?? string.Empty);

                foreach (var info in LlmProviderCatalog.Providers)
                {
                    string key = settings.ApiKeyFor(info.Provider);
                    if (key != null) plugin.Settings.SetString(info.ApiKeySettingsKey, key);

                    plugin.Settings.SetString(LoopModelKey(info.Provider), ModelOf(settings, info.Provider, loop: true));
                    plugin.Settings.SetString(ReviewerModelKey(info.Provider), ModelOf(settings, info.Provider, loop: false));
                }

                // The plugin-wide Anthropic client is what ClaudeTag and the naming-convention
                // command use, so a key typed into the settings gear has to reach it too.
                string anthropicKey = settings.ApiKeyFor(LlmProvider.Anthropic);
                if (!string.IsNullOrWhiteSpace(anthropicKey))
                    plugin.AnthropicClient?.SetApiKey(anthropicKey);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("RhinoClaude: could not persist the provider settings — " + ex.Message);
            }
        }

        /// <summary>Read the provider block back into a freshly constructed AgentSettings.</summary>
        public static void Restore(AgentSettings settings)
        {
            var plugin = RhinoClaudePlugin.Instance;
            if (plugin == null || settings == null) return;

            try
            {
                foreach (var info in LlmProviderCatalog.Providers)
                {
                    string key = plugin.Settings.GetString(info.ApiKeySettingsKey, string.Empty);
                    if (string.IsNullOrWhiteSpace(key) && !string.IsNullOrEmpty(info.ApiKeyEnvironmentVariable))
                        key = Environment.GetEnvironmentVariable(info.ApiKeyEnvironmentVariable);

                    if (!string.IsNullOrWhiteSpace(key)) settings.SetApiKey(info.Provider, key);

                    settings.RememberModels(
                        info.Provider,
                        plugin.Settings.GetString(LoopModelKey(info.Provider), string.Empty),
                        plugin.Settings.GetString(ReviewerModelKey(info.Provider), string.Empty));
                }

                settings.CustomEndpoint = plugin.Settings.GetString(CustomEndpointKey, string.Empty);

                // Applied through SelectProvider so the models land on the ones remembered for
                // whichever provider was saved, instead of the Claude defaults.
                var saved = LlmProviderCatalog.Parse(plugin.Settings.GetString(ProviderKey, string.Empty));
                settings.SelectProvider(saved);
            }
            catch (Exception)
            {
                // Nothing saved yet: the Anthropic defaults stand.
            }
        }

        private static string ModelOf(AgentSettings settings, LlmProvider provider, bool loop)
        {
            if (provider == settings.Provider)
                return (loop ? settings.LoopModel : settings.ReviewerModel) ?? string.Empty;

            var remembered = loop ? settings.RememberedLoopModels : settings.RememberedReviewerModels;
            return remembered.TryGetValue(provider, out var model) ? model ?? string.Empty : string.Empty;
        }

        private static string LoopModelKey(LlmProvider provider) => "LlmLoopModel_" + provider;
        private static string ReviewerModelKey(LlmProvider provider) => "LlmReviewerModel_" + provider;
    }
}
