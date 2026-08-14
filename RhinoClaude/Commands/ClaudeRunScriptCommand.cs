using System;
using System.Threading;
using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;
using Rhino.UI;
using RhinoClaude.Services;

namespace RhinoClaude.Commands
{
    public class ClaudeRunScriptCommand : Command
    {
        public override string EnglishName => "ClaudeRunScript";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = RhinoClaudePlugin.Instance;
            if (!plugin.ClaudeService.IsConfigured)
            {
                RhinoApp.WriteLine("RhinoClaude: No API key configured. Run 'ClaudeSetKey' first.");
                return Result.Failure;
            }

            // Get task description via dialog so spaces work normally
            string taskDescription = string.Empty;
            if (!Dialogs.ShowEditBox("ClaudeRunScript", "Describe what you want the script to do:", string.Empty, true, out taskDescription))
                return Result.Cancel;

            taskDescription = taskDescription?.Trim();
            if (string.IsNullOrEmpty(taskDescription))
                return Result.Cancel;

            // Collect scene context
            string sceneContext = SceneContextCollector.CollectContext(doc, false);

            // Build a prompt that specifically asks for executable Python code
            string scriptPrompt = $@"The user wants a RhinoPython script to accomplish this task:

{taskDescription}

IMPORTANT RULES:
- Generate ONLY a Python script that runs inside Rhino's built-in Python editor.
- Follow all the rules from the system prompt for RhinoPython scripts.
- The script should be complete and ready to run — no placeholders.
- Put the complete script inside a ```python code block.
- After the code block, briefly explain what the script does.";

            RhinoApp.WriteLine("RhinoClaude: Generating script... (press Escape to cancel)");

            string response;
            using (var cts = new CancellationTokenSource())
            {
                EventHandler escapeHandler = (s, e) => cts.Cancel();
                RhinoApp.EscapeKeyPressed += escapeHandler;
                try
                {
                    var task = plugin.ClaudeService.SendMessageAsync(scriptPrompt, sceneContext, cancellationToken: cts.Token);
                    while (!task.IsCompleted)
                    {
                        RhinoApp.Wait();
                        if (cts.IsCancellationRequested)
                            break;
                    }

                    if (cts.IsCancellationRequested)
                    {
                        RhinoApp.WriteLine("RhinoClaude: Cancelled.");
                        return Result.Cancel;
                    }

                    response = task.Result;
                }
                catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
                {
                    RhinoApp.WriteLine("RhinoClaude: Cancelled.");
                    return Result.Cancel;
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"RhinoClaude Error: {ex.Message}");
                    return Result.Failure;
                }
                finally
                {
                    RhinoApp.EscapeKeyPressed -= escapeHandler;
                }

                // Extract Python code from the response
                string script = ScriptRunner.ExtractPythonCode(response);

                if (string.IsNullOrEmpty(script))
                {
                    RhinoApp.WriteLine("RhinoClaude: Could not extract a Python script from Claude's response.");
                    RhinoApp.WriteLine("Full response:");
                    RhinoApp.WriteLine(response);
                    return Result.Failure;
                }

                // Display the script and explanation
                string explanation = ScriptRunner.ExtractExplanation(response);
                if (!string.IsNullOrEmpty(explanation))
                {
                    RhinoApp.WriteLine("─────────────────────────────────────────");
                    RhinoApp.WriteLine("Explanation:");
                    RhinoApp.WriteLine(explanation);
                }

                RhinoApp.WriteLine("─────────────────────────────────────────");
                RhinoApp.WriteLine("Generated Script:");
                RhinoApp.WriteLine("─────────────────────────────────────────");
                RhinoApp.WriteLine(script);
                RhinoApp.WriteLine("─────────────────────────────────────────");

                // Ask user for confirmation before running
                var getConfirm = new GetOption();
                getConfirm.SetCommandPrompt("Run this script?");
                getConfirm.AddOption("Run");
                int cancelIndex = getConfirm.AddOption("Cancel");

                if (getConfirm.Get() != Rhino.Input.GetResult.Option || getConfirm.OptionIndex() == cancelIndex)
                {
                    RhinoApp.WriteLine("RhinoClaude: Script cancelled.");
                    return Result.Cancel;
                }

                bool success = ScriptRunner.RunWithRetry(doc, plugin.ClaudeService, script, taskDescription, cts.Token);
                return success ? Result.Success : Result.Failure;
            }
        }
    }
}
