using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Rhino;
using Rhino.Runtime;
using RhinoClaude.Services;

namespace RhinoClaude.Services
{
    public static class ScriptRunner
    {
        private const int MaxRetries = 3;

        public static bool RunWithRetry(
            RhinoDoc doc,
            ClaudeApiService claudeService,
            string script,
            string originalPrompt,
            CancellationToken cancellationToken)
        {
            string currentScript = script;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                RhinoApp.WriteLine("RhinoClaude: Running script (attempt {0}/{1})...", attempt, MaxRetries);

                string error = ExecuteScript(doc, currentScript);
                if (error == null)
                {
                    RhinoApp.WriteLine("RhinoClaude: Script completed successfully.");
                    doc.Views.Redraw();
                    return true;
                }

                RhinoApp.WriteLine("RhinoClaude: Script error: {0}", error);

                if (attempt >= MaxRetries)
                {
                    RhinoApp.WriteLine("RhinoClaude: Max retries reached. Script failed.");
                    return false;
                }

                // Ask Claude to fix the error
                RhinoApp.WriteLine("RhinoClaude: Asking Claude to fix the error... (press Escape to cancel)");

                string fixPrompt = string.Format(
                    "The following RhinoPython script failed with an error. Fix the script and return ONLY the corrected script in a ```python code block.\n\n" +
                    "ORIGINAL TASK: {0}\n\n" +
                    "FAILED SCRIPT:\n```python\n{1}\n```\n\n" +
                    "ERROR: {2}\n\n" +
                    "Fix the error and return the complete corrected script.",
                    originalPrompt, currentScript, error);

                string fixResponse;
                try
                {
                    var task = claudeService.SendMessageAsync(fixPrompt, cancellationToken: cancellationToken);
                    while (!task.IsCompleted)
                    {
                        RhinoApp.Wait();
                        if (cancellationToken.IsCancellationRequested)
                        {
                            RhinoApp.WriteLine("RhinoClaude: Cancelled.");
                            return false;
                        }
                    }
                    fixResponse = task.Result;
                }
                catch (Exception)
                {
                    RhinoApp.WriteLine("RhinoClaude: Failed to get fix from Claude.");
                    return false;
                }

                string fixedScript = ExtractPythonCode(fixResponse);
                if (string.IsNullOrEmpty(fixedScript))
                {
                    RhinoApp.WriteLine("RhinoClaude: Could not extract fixed script from Claude's response.");
                    return false;
                }

                RhinoApp.WriteLine("─────────────────────────────────────────");
                RhinoApp.WriteLine("Fixed Script (attempt {0}):", attempt + 1);
                RhinoApp.WriteLine("─────────────────────────────────────────");
                RhinoApp.WriteLine(fixedScript);
                RhinoApp.WriteLine("─────────────────────────────────────────");

                currentScript = fixedScript;
            }

            return false;
        }

        /// <summary>
        /// Execute a Python script. Returns null on success, or the error message on failure.
        /// </summary>
        private static string ExecuteScript(RhinoDoc doc, string script)
        {
            try
            {
                PythonScript python = PythonScript.Create();
                if (python == null)
                    return "Python scripting engine not available.";

                python.ScriptContextDoc = doc;

                // Capture ALL output (normal + errors) so we can extract error details
                // when the script fails. Normal output is also echoed to the Rhino console.
                var outputCapture = new StringBuilder();

                python.Output = (text) =>
                {
                    outputCapture.Append(text);
                    RhinoApp.Write(text);
                };

                python.SetVariable("__name__", "__main__");

                bool success = python.ExecuteScript(script);
                if (!success)
                {
                    string captured = outputCapture.ToString().Trim();
                    return string.IsNullOrEmpty(captured)
                        ? "Script execution failed (no details available)."
                        : captured;
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public static string ExtractPythonCode(string response)
        {
            var match = Regex.Match(response, @"```python\s*\n(.*?)```", RegexOptions.Singleline);
            if (match.Success)
                return match.Groups[1].Value.Trim();

            match = Regex.Match(response, @"```\s*\n(.*?)```", RegexOptions.Singleline);
            if (match.Success)
                return match.Groups[1].Value.Trim();

            return null;
        }

        public static string ExtractExplanation(string response)
        {
            string cleaned = Regex.Replace(response, @"```[\s\S]*?```", "").Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
        }
    }
}
