using Rhino;
using Rhino.Commands;
using Rhino.UI;
using RhinoClaude.UI;

namespace RhinoClaude.Commands
{
    /// <summary>
    /// Command: RCTagInspector
    /// Toggles the Tag Inspector docked panel.
    /// </summary>
    public class RCTagInspectorCommand : Command
    {
        public override string EnglishName => "RCTagInspector";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var panelId = TagInspectorPanel.PanelId;

            if (Panels.IsPanelVisible(panelId))
                Panels.ClosePanel(panelId);
            else
                Panels.OpenPanel(panelId);

            return Result.Success;
        }
    }
}
