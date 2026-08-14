using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;
using RhinoClaude.Agent;

namespace RhinoClaude.Commands
{
    /// <summary>
    /// Command: ClaudeRevertSession
    /// Pops the undo records the current agent session created, back to the session start.
    /// Same action as the sidebar's "Revert session" button, available from the command line.
    /// </summary>
    public class ClaudeRevertSessionCommand : Command
    {
        public override string EnglishName => "ClaudeRevertSession";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var host = AgentHost.For(doc);
            int count = host.Snapshots.PendingUndoCount;

            if (count == 0)
            {
                RhinoApp.WriteLine("RhinoClaude: this agent session has not changed the document — nothing to revert.");
                return Result.Nothing;
            }

            if (host.Session.IsRunning)
            {
                RhinoApp.WriteLine("RhinoClaude: a turn is still running. Stop it from the panel first.");
                return Result.Cancel;
            }

            var confirm = new GetOption();
            confirm.SetCommandPrompt(string.Format(
                "Undo {0} change(s) made by this agent session? Hand edits made since the session " +
                "started will be undone too.", count));
            confirm.AddOption("Revert");
            int cancelIndex = confirm.AddOption("Cancel");

            if (confirm.Get() != Rhino.Input.GetResult.Option || confirm.OptionIndex() == cancelIndex)
            {
                RhinoApp.WriteLine("RhinoClaude: revert cancelled.");
                return Result.Cancel;
            }

            int performed = host.Snapshots.RevertSession();
            RhinoApp.WriteLine("RhinoClaude: reverted {0} of {1} change(s).", performed, count);
            return Result.Success;
        }
    }
}
