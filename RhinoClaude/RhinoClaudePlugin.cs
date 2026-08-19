using Rhino;
using Rhino.PlugIns;
using Rhino.UI;
using RhinoClaude.Agent;
using RhinoClaude.Services;
using RhinoClaude.UI;

namespace RhinoClaude
{
    /// <summary>
    /// The main plugin class for RhinoClaude.
    /// Rhino discovers and loads this automatically via the assembly .rhp file.
    /// </summary>
    public class RhinoClaudePlugin : PlugIn
    {
        /// <summary>Singleton instance of the plugin.</summary>
        public static RhinoClaudePlugin Instance { get; private set; }

        /// <summary>Streaming, tool-use-capable Anthropic client shared by every session.</summary>
        public AnthropicClient AnthropicClient { get; private set; }

        /// <summary>Shared tag service for reading/writing semantic tags.</summary>
        public TagService TagService { get; private set; }

        public RhinoClaudePlugin()
        {
            Instance = this;
        }

        /// <summary>
        /// Called once when the plugin is first loaded. Initializes services and registers
        /// both docked panels.
        /// </summary>
        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            RhinoApp.WriteLine("RhinoClaude plugin loaded (agent refactor + semantic layer).");

            AnthropicClient = new AnthropicClient();
            TagService = new TagService();

            // Try plugin settings first, then the environment variable.
            string savedKey = RhinoClaude.Services.Agent.SecretStore.Unprotect(
                Settings.GetString("AnthropicApiKey", string.Empty));
            if (!string.IsNullOrEmpty(savedKey))
            {
                AnthropicClient.SetApiKey(savedKey);
                RhinoApp.WriteLine("RhinoClaude: API key loaded from settings.");
            }
            else
            {
                string envKey = System.Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                if (!string.IsNullOrEmpty(envKey))
                {
                    AnthropicClient.SetApiKey(envKey);
                    RhinoApp.WriteLine("RhinoClaude: API key loaded from the ANTHROPIC_API_KEY environment variable.");
                }
                else
                {
                    RhinoApp.WriteLine("RhinoClaude: no API key found. Run 'ClaudeSetKey' to set one.");
                }
            }

            Panels.RegisterPanel(
                this,
                typeof(AgentChatPanel),
                "RhinoClaude",
                (System.Drawing.Icon)null);

            Panels.RegisterPanel(
                this,
                typeof(TagInspectorPanel),
                "Tag Inspector",
                (System.Drawing.Icon)null);

            // Drop the per-document agent host when its document closes, so a reopened
            // document does not inherit a stale undo log.
            RhinoDoc.CloseDocument += (sender, e) => AgentHost.Forget(e.DocumentSerialNumber);

            RhinoApp.WriteLine(
                "RhinoClaude: run 'ClaudeChat' to open the agent panel. " +
                "Other commands — ClaudeSetKey, ClaudeTag, ClaudeRevertSession, ClaudeAddReviewView, " +
                "ClaudeSetElement, ClaudeClearElement, ClaudeLearnNamingConvention, " +
                "RCSetTag, RCQuery, RCInspectTags, RCValidateTags, RCTagInspector, RCBuildFromDiagram");

            return LoadReturnCode.Success;
        }
    }
}
