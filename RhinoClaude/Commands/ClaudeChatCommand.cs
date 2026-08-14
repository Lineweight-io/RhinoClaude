using Rhino;
using Rhino.Commands;
using Rhino.UI;
using RhinoClaude.Agent;
using RhinoClaude.UI;

namespace RhinoClaude.Commands
{
    /// <summary>
    /// Command: ClaudeChat
    /// Opens and focuses the agent sidebar. The panel is the front door — this command
    /// exists so users who reach for the command line still land in the right place.
    /// </summary>
    public class ClaudeChatCommand : Command
    {
        public override string EnglishName => "ClaudeChat";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var panelId = AgentChatPanel.PanelId;

            if (Panels.IsPanelVisible(panelId))
            {
                // Already open — bring it forward rather than toggling it shut.
                Panels.OpenPanel(panelId);
                RhinoApp.WriteLine("RhinoClaude: chat panel focused.");
            }
            else
            {
                Panels.OpenPanel(panelId);
                RhinoApp.WriteLine("RhinoClaude: chat panel opened.");
            }

            // Reported for whichever provider is selected; only the Anthropic one has a
            // command-line way to set its key.
            var client = doc != null ? AgentHost.For(doc).Client : null;
            if (client != null && !client.IsConfigured)
            {
                RhinoApp.WriteLine(client is AnthropicClient
                    ? "RhinoClaude: no API key configured — run 'ClaudeSetKey' first."
                    : "RhinoClaude: no API key configured for " + client.ProviderName +
                      " — add one in the panel's settings gear.");
            }

            return Result.Success;
        }
    }
}
