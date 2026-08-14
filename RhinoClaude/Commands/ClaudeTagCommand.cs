using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using RhinoClaude.Agent;
using RhinoClaude.Schema;
using RhinoClaude.Services;

namespace RhinoClaude.Commands
{
    /// <summary>
    /// Command: ClaudeTag
    /// Select objects, describe them in natural language, and Claude
    /// parses the description into structured RC: tags.
    /// Confirms before writing by default; use AutoConfirm=Yes to skip.
    /// </summary>
    public class ClaudeTagCommand : Command
    {
        public override string EnglishName => "ClaudeTag";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = RhinoClaudePlugin.Instance;
            if (!plugin.AnthropicClient.IsConfigured)
            {
                RhinoApp.WriteLine("RhinoClaude: No API key configured. Run 'ClaudeSetKey' first.");
                return Result.Failure;
            }

            // ── Step 1: Get selected objects ─────────────────────────
            var selectedObjects = new List<RhinoObject>();
            foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
            {
                selectedObjects.Add(obj);
            }

            if (selectedObjects.Count == 0)
            {
                RhinoApp.WriteLine("RhinoClaude: No objects selected. Select objects first, then run ClaudeTag.");
                return Result.Cancel;
            }

            // ── Step 2: Get the description from the user ────────────
            string description = string.Empty;
            if (!Dialogs.ShowEditBox(
                "ClaudeTag",
                string.Format("Describe these {0} object(s) (e.g., 'exterior wall, 2-hr fire rated, Level 2'):", selectedObjects.Count),
                string.Empty, true, out description))
                return Result.Cancel;

            description = description?.Trim();
            if (string.IsNullOrEmpty(description))
                return Result.Cancel;

            // ── Step 3: Check for AutoConfirm option ─────────────────
            bool autoConfirm = false;
            var getOption = new GetOption();
            getOption.SetCommandPrompt("Auto-confirm tags without review?");
            getOption.AddOption("Confirm");
            getOption.AddOption("AutoApply");
            getOption.SetDefaultString("Confirm");

            var optResult = getOption.Get();
            if (optResult == GetResult.Option)
            {
                autoConfirm = getOption.OptionIndex() == 2; // AutoApply
            }
            // If user presses Enter (GetResult.String with default), keep autoConfirm = false

            // ── Step 4: Build prompt for Claude ──────────────────────
            string tagPrompt = BuildTagPrompt(description, selectedObjects);

            RhinoApp.WriteLine("RhinoClaude: Analyzing description... (press Escape to cancel)");

            // ── Step 5: Send to Claude ───────────────────────────────
            string response;
            var cts = new CancellationTokenSource();
            EventHandler escapeHandler = (s, e) => cts.Cancel();
            RhinoApp.EscapeKeyPressed += escapeHandler;

            try
            {
                // Still one-shot: the output is small and structured, so the agent loop
                // would only add latency. No tools, no streaming — just the tag JSON.
                //
                // Thinking is explicitly disabled. On models that think by default, max_tokens
                // caps thinking plus the answer together, so a tight budget on a task this small
                // would burn the whole allowance before the JSON was written.
                var request = new MessagesRequest
                {
                    Model = AgentSettings.DefaultUtilityModel,
                    MaxTokens = 2048,
                    Messages = { AgentMessage.User(tagPrompt) }
                };

                if (ModelCapabilities.SupportsAdaptiveThinking(request.Model))
                    request.Thinking = new ThinkingConfig { Type = "disabled" };
                if (ModelCapabilities.SupportsEffort(request.Model))
                    request.OutputConfig = new OutputConfig { Effort = "low" };

                var task = plugin.AnthropicClient.SendAsync(request, cts.Token);
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

                response = task.Result.TextContent();
            }
            catch (AggregateException ex) when (ex.InnerException is AnthropicApiException apiEx)
            {
                RhinoApp.WriteLine("RhinoClaude Error: " + apiEx.Message);
                return Result.Failure;
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                RhinoApp.WriteLine("RhinoClaude: Cancelled.");
                return Result.Cancel;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine(string.Format("RhinoClaude Error: {0}", ex.Message));
                return Result.Failure;
            }
            finally
            {
                RhinoApp.EscapeKeyPressed -= escapeHandler;
                cts.Dispose();
            }

            // ── Step 6: Parse Claude's response into tags ────────────
            var parsedTags = ParseTagResponse(response);

            if (parsedTags.Count == 0)
            {
                RhinoApp.WriteLine("RhinoClaude: Could not extract tags from Claude's response.");
                RhinoApp.WriteLine("Response:");
                RhinoApp.WriteLine(response);
                return Result.Failure;
            }

            // ── Step 7: Display proposed tags ────────────────────────
            RhinoApp.WriteLine("─────────────────────────────────────────");
            RhinoApp.WriteLine(string.Format("Proposed tags for {0} object(s):", selectedObjects.Count));
            RhinoApp.WriteLine("─────────────────────────────────────────");
            foreach (var kvp in parsedTags)
            {
                string displayName = TagSchema.DisplayNames.GetValueOrDefault(kvp.Key, kvp.Key);
                RhinoApp.WriteLine(string.Format("  {0}: {1}", displayName, kvp.Value));
            }
            RhinoApp.WriteLine("─────────────────────────────────────────");

            // ── Step 8: Confirm or auto-apply ────────────────────────
            if (!autoConfirm)
            {
                var confirm = new GetOption();
                confirm.SetCommandPrompt("Apply these tags?");
                confirm.AddOption("Apply");
                int cancelIdx = confirm.AddOption("Cancel");

                if (confirm.Get() != GetResult.Option || confirm.OptionIndex() == cancelIdx)
                {
                    RhinoApp.WriteLine("RhinoClaude: Tagging cancelled.");
                    return Result.Cancel;
                }
            }

            // ── Step 9: Write tags to all selected objects ───────────
            var tagService = plugin.TagService;
            int successCount = 0;
            var allErrors = new List<string>();

            foreach (var obj in selectedObjects)
            {
                var errors = tagService.SetTags(doc, obj, parsedTags);
                if (errors.Count == 0)
                    successCount++;
                else
                    allErrors.AddRange(errors);
            }

            RhinoApp.WriteLine(string.Format(
                "RhinoClaude: Tagged {0} of {1} objects.", successCount, selectedObjects.Count));

            if (allErrors.Count > 0)
            {
                RhinoApp.WriteLine("Warnings:");
                foreach (var err in allErrors)
                    RhinoApp.WriteLine(string.Format("  - {0}", err));
            }

            return Result.Success;
        }

        /// <summary>
        /// Build the prompt that asks Claude to parse a description into structured tags.
        /// </summary>
        private string BuildTagPrompt(string description, List<RhinoObject> objects)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are a semantic tagging assistant for architectural 3D models.");
            sb.AppendLine("The user has selected geometry in Rhino and described it. Parse their description into structured tags.");
            sb.AppendLine();
            sb.AppendLine(TagSchema.GetSchemaDescription());
            sb.AppendLine();
            sb.AppendLine("RULES:");
            sb.AppendLine("- Return ONLY a JSON object with tag keys and values. No explanation, no markdown.");
            sb.AppendLine("- Use the exact RC: prefixed key names.");
            sb.AppendLine("- For constrained keys, use ONLY the allowed values listed above.");
            sb.AppendLine("- Only include tags that the description provides information for. Do NOT guess.");
            sb.AppendLine("- If the description mentions materials/layers, set RC:MaterialLayers as a comma-separated list.");
            sb.AppendLine();

            // Plan phase 9: the document summary comes from SceneContextCollector rather than
            // being re-serialized inline here. One line of context is enough for a tagging
            // call — the agent loop is where a full scene read belongs.
            var doc = objects.Count > 0 ? objects[0].Document : null;
            if (doc != null)
            {
                sb.AppendLine(SceneContextCollector.CollectBriefContext(doc));
                sb.AppendLine();
            }

            sb.AppendLine(string.Format("Selected: {0} object(s)", objects.Count));

            // Include brief info about the selected objects
            int limit = Math.Min(objects.Count, 5);
            for (int i = 0; i < limit; i++)
            {
                var obj = objects[i];
                string name = obj.Name ?? "(unnamed)";
                string layer = obj.Document?.Layers[obj.Attributes.LayerIndex]?.FullPath ?? "?";
                sb.AppendLine(string.Format("  [{0}] {1}, layer: {2}", obj.ObjectType, name, layer));
            }
            if (objects.Count > limit)
                sb.AppendLine(string.Format("  ... and {0} more.", objects.Count - limit));

            sb.AppendLine();
            sb.AppendLine(string.Format("USER DESCRIPTION: {0}", description));
            sb.AppendLine();
            sb.AppendLine("Respond with ONLY the JSON object. Example:");
            sb.AppendLine("{");
            sb.AppendLine("  \"RC:ElementType\": \"Wall\",");
            sb.AppendLine("  \"RC:FireRating\": \"2-hr\",");
            sb.AppendLine("  \"RC:IntExt\": \"Exterior\",");
            sb.AppendLine("  \"RC:Level\": \"Level 2\"");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Parse Claude's JSON response into a tag dictionary.
        /// Handles cases where Claude wraps JSON in markdown code blocks.
        /// </summary>
        private Dictionary<string, string> ParseTagResponse(string response)
        {
            var tags = new Dictionary<string, string>();

            if (string.IsNullOrWhiteSpace(response))
                return tags;

            // Strip markdown code fences if present
            string json = response.Trim();
            if (json.StartsWith("```"))
            {
                int firstNewline = json.IndexOf('\n');
                if (firstNewline > 0)
                    json = json.Substring(firstNewline + 1);
                int lastFence = json.LastIndexOf("```");
                if (lastFence > 0)
                    json = json.Substring(0, lastFence);
                json = json.Trim();
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (parsed == null)
                    return tags;

                // Validate and resolve each key
                foreach (var kvp in parsed)
                {
                    string resolvedKey = TagSchema.ResolveKey(kvp.Key);
                    if (resolvedKey != null && !string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        tags[resolvedKey] = TagSchema.NormalizeValue(resolvedKey, kvp.Value);
                    }
                }
            }
            catch (JsonException ex)
            {
                RhinoApp.WriteLine(string.Format("RhinoClaude: JSON parse error: {0}", ex.Message));
            }

            return tags;
        }
    }
}
