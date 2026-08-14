using Rhino;
using Rhino.Commands;
using Rhino.UI;
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

            var plugin = RhinoClaudePlugin.Instance;
            if (plugin != null && !plugin.AnthropicClient.IsConfigured)
                RhinoApp.WriteLine("RhinoClaude: no API key configured — run 'ClaudeSetKey' first.");

            return Result.Success;
        }
    }
}
