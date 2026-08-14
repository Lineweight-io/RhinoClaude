using System;
using System.Collections.Generic;
using System.Threading;
using Rhino;
using Rhino.Commands;
using Rhino.UI;
using RhinoClaude.Services;

namespace RhinoClaude.Commands
{
    public class ClaudeAskCommand : Command
    {
        public override string EnglishName => "ClaudeAsk";

        private readonly List<ConversationMessage> _history = new List<ConversationMessage>();

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = RhinoClaudePlugin.Instance;
            if (!plugin.ClaudeService.IsConfigured)
            {
                RhinoApp.WriteLine("RhinoClaude: No API key configured. Run 'ClaudeSetKey' first.");
                return Result.Failure;
            }

            // Get user input via dialog so spaces work normally
            string userMessage = string.Empty;
            if (!Dialogs.ShowEditBox("ClaudeAsk", "Ask Claude a question:", string.Empty, true, out userMessage))
                return Result.Cancel;

            userMessage = userMessage?.Trim();
            if (string.IsNullOrEmpty(userMessage))
                return Result.Cancel;

            // Collect scene context
            string sceneContext = SceneContextCollector.CollectContext(doc, false);

            // Send to Claude
            RhinoApp.WriteLine("RhinoClaude: Thinking... (press Escape to cancel)");

            string response;
            var cts = new CancellationTokenSource();
            EventHandler escapeHandler = (s, e) => cts.Cancel();
            RhinoApp.EscapeKeyPressed += escapeHandler;

            try
            {
                // --- Send the message to Claude ---
                try
                {
                    var task = plugin.ClaudeService.SendMessageAsync(userMessage, sceneContext, _history, cts.Token);
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

                // --- Store in conversation history ---
                string fullUserMsg = string.IsNullOrEmpty(sceneContext)
                    ? userMessage
                    : $"[Scene context was included]\n{userMessage}";
                _history.Add(new ConversationMessage("user", fullUserMsg));
                _history.Add(new ConversationMessage("assistant", response));

                while (_history.Count > 40)
                {
                    _history.RemoveAt(0);
                    _history.RemoveAt(0);
                }

                // --- Check if response contains a Python script ---
                string script = ScriptRunner.ExtractPythonCode(response);
                if (!string.IsNullOrEmpty(script))
                {
                    string explanation = ScriptRunner.ExtractExplanation(response);
                    if (!string.IsNullOrEmpty(explanation))
                    {
                        RhinoApp.WriteLine("─────────────────────────────────────────");
                        RhinoApp.WriteLine("Claude:");
                        RhinoApp.WriteLine(explanation);
                    }

                    RhinoApp.WriteLine("─────────────────────────────────────────");
                    RhinoApp.WriteLine("Generated Script:");
                    RhinoApp.WriteLine("─────────────────────────────────────────");
                    RhinoApp.WriteLine(script);
                    RhinoApp.WriteLine("─────────────────────────────────────────");

                    var getConfirm = new Rhino.Input.Custom.GetOption();
                    getConfirm.SetCommandPrompt("Run this script?");
                    getConfirm.AddOption("Run");
                    int cancelIndex = getConfirm.AddOption("Cancel");

                    if (getConfirm.Get() != Rhino.Input.GetResult.Option || getConfirm.OptionIndex() == cancelIndex)
                    {
                        RhinoApp.WriteLine("RhinoClaude: Script cancelled.");
                        return Result.Cancel;
                    }

                    // RunWithRetry may call Claude for error fixes — escape handler
                    // is still active so the user can cancel during retries too
                    bool success = ScriptRunner.RunWithRetry(doc, plugin.ClaudeService, script, userMessage, cts.Token);
                    return success ? Result.Success : Result.Failure;
                }
                else
                {
                    RhinoApp.WriteLine("─────────────────────────────────────────");
                    RhinoApp.WriteLine("Claude:");
                    RhinoApp.WriteLine(response);
                    RhinoApp.WriteLine("─────────────────────────────────────────");
                }

                return Result.Success;
            }
            finally
            {
                // Always clean up — whether we return from the API call,
                // script execution, or anywhere in between
                RhinoApp.EscapeKeyPressed -= escapeHandler;
                cts.Dispose();
            }
        }
    }
}
