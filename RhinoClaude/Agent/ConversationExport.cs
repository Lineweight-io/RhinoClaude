using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Everything the markdown export needs, gathered on the UI thread and handed to a
    /// formatter that knows nothing about Rhino. Splitting it this way is what makes the
    /// formatting unit-testable — the interesting logic is "what does a turn look like on
    /// the page", not "how do I read a document name".
    /// </summary>
    public sealed class ConversationExportRequest
    {
        public string DocumentName { get; set; }
        public string SessionDisplayName { get; set; }
        public string SessionId { get; set; }

        /// <summary>Local times: the file is read by a person, not by a machine.</summary>
        public DateTime StartedLocal { get; set; }
        public DateTime ExportedLocal { get; set; }

        public string Model { get; set; }
        public string ReviewerModel { get; set; }

        public List<AgentMessage> Messages { get; set; } = new List<AgentMessage>();

        /// <summary>
        /// Tool timings for this session. Empty for a conversation that was restored from a
        /// .3dm — timings are not persisted — in which case results are read back out of the
        /// tool_result blocks instead.
        /// </summary>
        public List<ToolInvocation> Invocations { get; set; } = new List<ToolInvocation>();

        public TokenUsage SessionUsage { get; set; }
        public IReadOnlyList<SessionMutation> Mutations { get; set; }
        public int PendingUndoCount { get; set; }

        /// <summary>Tool input and result JSON longer than this is truncated with a marker.</summary>
        public int MaxJsonChars { get; set; } = 1500;
    }

    /// <summary>
    /// Renders a session as a markdown document a reviewer can read without Rhino open
    /// (user turns, model turns, every tool call with its arguments and result, timing and
    /// cost). Deliberately free of RhinoCommon and of file I/O.
    /// </summary>
    public static class ConversationExport
    {
        private const string Nl = "\n";

        public static string ToMarkdown(ConversationExportRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var messages = request.Messages ?? new List<AgentMessage>();
            var invocations = request.Invocations ?? new List<ToolInvocation>();

            // A tool_use is matched to its invocation when we have one (timing, parsed result)
            // and otherwise to the tool_result block replayed from the conversation itself.
            var byId = new Dictionary<string, ToolInvocation>(StringComparer.Ordinal);
            foreach (var invocation in invocations)
                if (!string.IsNullOrEmpty(invocation.ToolUseId)) byId[invocation.ToolUseId] = invocation;

            var resultBlocks = new Dictionary<string, ToolResultBlock>(StringComparer.Ordinal);
            foreach (var message in messages)
                foreach (var block in message.Content.OfType<ToolResultBlock>())
                    if (!string.IsNullOrEmpty(block.ToolUseId)) resultBlocks[block.ToolUseId] = block;

            var sb = new StringBuilder();

            WriteHeader(sb, request, messages, invocations);
            WriteChanges(sb, request);
            WriteTranscript(sb, request, messages, byId, resultBlocks);

            return sb.ToString();
        }

        /// <summary>Convert to the platform's line endings for writing to disk.</summary>
        public static string ForFile(string markdown) =>
            markdown == null ? string.Empty : markdown.Replace(Nl, Environment.NewLine);

        // ── Header ────────────────────────────────────────────────────

        private static void WriteHeader(
            StringBuilder sb, ConversationExportRequest request,
            List<AgentMessage> messages, List<ToolInvocation> invocations)
        {
            string title = string.IsNullOrWhiteSpace(request.SessionDisplayName)
                ? "RhinoClaude conversation"
                : "RhinoClaude conversation — " + request.SessionDisplayName;

            sb.Append("# ").Append(Inline(title)).Append(Nl).Append(Nl);

            int userTurns = messages.Count(IsUserTurn);
            int modelTurns = messages.Count(m => m.Role == "assistant");
            int toolUses = messages.Sum(m => m.Content.OfType<ToolUseBlock>().Count());
            int failed = invocations.Count(i => i.Result != null && !i.Result.Success);
            long toolMs = invocations.Sum(i => i.ElapsedMs);

            var rows = new List<KeyValuePair<string, string>>
            {
                Row("Document", string.IsNullOrWhiteSpace(request.DocumentName)
                    ? ExportNaming.UntitledDocument + " (never saved)"
                    : request.DocumentName),
                Row("Session", string.IsNullOrWhiteSpace(request.SessionDisplayName)
                    ? "(unnamed)" : request.SessionDisplayName),
                Row("Session id", string.IsNullOrWhiteSpace(request.SessionId) ? "—" : "`" + request.SessionId + "`"),
                Row("Started", Stamp(request.StartedLocal)),
                Row("Exported", Stamp(request.ExportedLocal)),
                Row("Model", string.IsNullOrWhiteSpace(request.Model) ? "—" : "`" + request.Model + "`")
            };

            if (!string.IsNullOrWhiteSpace(request.ReviewerModel))
                rows.Add(Row("Reviewer model", "`" + request.ReviewerModel + "`"));

            rows.Add(Row("Turns", userTurns + " from you, " + modelTurns + " from Claude"));

            string toolSummary = toolUses + " call" + (toolUses == 1 ? "" : "s");
            if (invocations.Count > 0)
            {
                toolSummary += ", " + (invocations.Count - failed) + " ok / " + failed + " failed";
                toolSummary += ", " + Duration(toolMs) + " of tool time";
            }
            else if (toolUses > 0)
            {
                toolSummary += " (timings were not saved with this conversation)";
            }
            rows.Add(Row("Tool calls", toolSummary));

            var usage = request.SessionUsage;
            if (usage != null)
            {
                rows.Add(Row("Tokens", string.Format(CultureInfo.InvariantCulture,
                    "{0:n0} in · {1:n0} out · {2:n0} cache write · {3:n0} cache read",
                    usage.InputTokens, usage.OutputTokens,
                    usage.CacheCreationInputTokens, usage.CacheReadInputTokens)));

                double cost = CostBudget.PricingFor(request.Model).CostOf(usage);
                rows.Add(Row("Estimated cost", "$" + cost.ToString("0.0000", CultureInfo.InvariantCulture) +
                    " (loop model rates; any reviewer calls are extra)"));
            }

            sb.Append("| | |").Append(Nl).Append("|---|---|").Append(Nl);
            foreach (var row in rows)
                sb.Append("| ").Append(row.Key).Append(" | ").Append(Inline(row.Value)).Append(" |").Append(Nl);

            sb.Append(Nl);
        }

        private static KeyValuePair<string, string> Row(string key, string value) =>
            new KeyValuePair<string, string>(key, value);

        // ── What the agent changed ────────────────────────────────────

        private static void WriteChanges(StringBuilder sb, ConversationExportRequest request)
        {
            var mutations = request.Mutations;
            if (mutations == null || mutations.Count == 0) return;

            var log = new SessionMutationLog();
            foreach (var mutation in mutations) log.Add(mutation);

            var surviving = log.SurvivingTouchedIds();
            int created = mutations.Sum(m => m.CreatedIds.Count);
            int deleted = mutations.Sum(m => m.DeletedIds.Count);
            var layers = log.LayersTouched();

            sb.Append("## What the agent changed").Append(Nl).Append(Nl);
            sb.Append("- ").Append(created).Append(" object(s) created, ")
              .Append(deleted).Append(" deleted (net ")
              .Append(log.NetObjectDelta() >= 0 ? "+" : "").Append(log.NetObjectDelta()).Append(")").Append(Nl);
            sb.Append("- ").Append(surviving.Count)
              .Append(" object(s) still in the document because of the agent").Append(Nl);

            if (layers.Count > 0)
                sb.Append("- Layers touched: ").Append(Inline(string.Join(", ", layers))).Append(Nl);

            if (request.PendingUndoCount > 0)
                sb.Append("- ").Append(request.PendingUndoCount)
                  .Append(" undo record(s) are still revertable from the sidebar").Append(Nl);

            var box = log.AffectedBox();
            if (box != null)
            {
                sb.Append("- Affected bounding box: ")
                  .Append(FormatBox(box)).Append(Nl);
            }

            sb.Append(Nl);
        }

        private static string FormatBox(double[] box) => string.Format(CultureInfo.InvariantCulture,
            "({0:0.###}, {1:0.###}, {2:0.###}) → ({3:0.###}, {4:0.###}, {5:0.###})",
            box[0], box[1], box[2], box[3], box[4], box[5]);

        // ── Transcript ────────────────────────────────────────────────

        private static void WriteTranscript(
            StringBuilder sb, ConversationExportRequest request, List<AgentMessage> messages,
            Dictionary<string, ToolInvocation> byId, Dictionary<string, ToolResultBlock> resultBlocks)
        {
            sb.Append("## Transcript").Append(Nl).Append(Nl);

            if (messages.Count == 0)
            {
                sb.Append("_This session has no messages._").Append(Nl);
                return;
            }

            int turn = 0;

            foreach (var message in messages)
            {
                if (message.Role == "user")
                {
                    // Tool-result turns are the loop's own plumbing; their content is already
                    // reported under the tool call that produced it.
                    if (!IsUserTurn(message)) continue;

                    turn++;
                    sb.Append("### Turn ").Append(turn).Append(" — You").Append(Nl).Append(Nl);
                    sb.Append(Body(message.TextContent())).Append(Nl).Append(Nl);
                    continue;
                }

                string text = message.TextContent();
                var thinking = message.Content.OfType<ThinkingBlock>()
                    .Select(t => t.Thinking)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                if (thinking.Count > 0)
                {
                    sb.Append("#### Claude — thinking").Append(Nl).Append(Nl);
                    foreach (var t in thinking)
                        sb.Append("> ").Append(Body(t).Replace(Nl, Nl + "> ")).Append(Nl).Append(Nl);
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.Append("#### Claude").Append(Nl).Append(Nl);
                    sb.Append(Body(text)).Append(Nl).Append(Nl);
                }

                foreach (var toolUse in message.Content.OfType<ToolUseBlock>())
                    WriteToolCall(sb, request, toolUse, byId, resultBlocks);
            }
        }

        private static void WriteToolCall(
            StringBuilder sb, ConversationExportRequest request, ToolUseBlock toolUse,
            Dictionary<string, ToolInvocation> byId, Dictionary<string, ToolResultBlock> resultBlocks)
        {
            ToolInvocation invocation = null;
            if (!string.IsNullOrEmpty(toolUse.Id)) byId.TryGetValue(toolUse.Id, out invocation);

            string status;
            if (invocation?.Result == null) status = "·";
            else status = invocation.Result.Success ? "✓" : "✗";

            string timing = invocation != null && invocation.ElapsedMs > 0
                ? "  ·  " + Duration(invocation.ElapsedMs)
                : string.Empty;

            sb.Append("##### Tool  ").Append(status).Append("  `")
              .Append(toolUse.Name ?? "(unnamed)").Append("`").Append(timing).Append(Nl).Append(Nl);

            sb.Append("*input*").Append(Nl).Append(Nl);
            sb.Append(JsonFence(toolUse.InputJson, request.MaxJsonChars)).Append(Nl);

            string resultLabel;
            string resultText = ResultText(invocation, toolUse.Id, resultBlocks, out resultLabel);

            sb.Append("*").Append(resultLabel).Append("*").Append(Nl).Append(Nl);
            sb.Append(JsonFence(resultText, request.MaxJsonChars)).Append(Nl);

            int images = invocation?.Result?.Images?.Count ?? 0;
            if (images > 0)
                sb.Append("_").Append(images).Append(" image(s) were captured; screenshots are not embedded in this export._")
                  .Append(Nl).Append(Nl);
        }

        /// <summary>
        /// The result to print: the live invocation when we still have it, otherwise whatever
        /// the tool_result block in the conversation carries (which is what a resumed session
        /// has left).
        /// </summary>
        private static string ResultText(
            ToolInvocation invocation, string toolUseId,
            Dictionary<string, ToolResultBlock> resultBlocks, out string label)
        {
            if (invocation?.Result != null)
            {
                if (invocation.Result.Success)
                {
                    label = "result";
                    return invocation.Result.Json;
                }
                label = "error";
                return invocation.Result.Error ?? "(no error text)";
            }

            ToolResultBlock block = null;
            if (!string.IsNullOrEmpty(toolUseId)) resultBlocks.TryGetValue(toolUseId, out block);

            if (block == null)
            {
                label = "result";
                return "(no result was recorded for this call)";
            }

            label = block.IsError ? "error" : "result";

            var parts = new List<string>();
            int images = 0;
            foreach (var child in block.Content)
            {
                if (child is TextBlock t) parts.Add(t.Text);
                else if (child is ImageBlock) images++;
            }
            if (images > 0) parts.Add("(" + images + " image block(s) omitted)");

            return parts.Count == 0 ? "(empty result)" : string.Join(Nl, parts);
        }

        // ── Formatting helpers ────────────────────────────────────────

        private static bool IsUserTurn(AgentMessage message) =>
            message.Role == "user" &&
            !message.Content.Any(b => b is ToolResultBlock) &&
            !string.IsNullOrWhiteSpace(message.TextContent());

        private static string Stamp(DateTime value) =>
            value == default(DateTime)
                ? "—"
                : value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        public static string Duration(long milliseconds)
        {
            if (milliseconds < 1000) return milliseconds + " ms";
            double seconds = milliseconds / 1000.0;
            if (seconds < 90) return seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            return TimeSpan.FromMilliseconds(milliseconds).ToString(@"m\ms\s");
        }

        /// <summary>Normalize line endings and keep a block of prose out of the table syntax.</summary>
        private static string Body(string text)
        {
            if (string.IsNullOrEmpty(text)) return "_(no text)_";
            return text.Replace("\r\n", Nl).Replace("\r", Nl).TrimEnd();
        }

        /// <summary>Squash to one line so a stray newline cannot break a markdown table row.</summary>
        private static string Inline(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
        }

        /// <summary>
        /// A fenced JSON block, pretty-printed when it parses, truncated when it is enormous,
        /// and fenced with enough backticks that content containing a fence cannot escape.
        /// </summary>
        private static string JsonFence(string json, int maxChars)
        {
            string content = Pretty(json);

            if (maxChars > 0 && content.Length > maxChars)
            {
                content = content.Substring(0, maxChars).TrimEnd() +
                          Nl + "… truncated (" + content.Length.ToString("n0", CultureInfo.InvariantCulture) +
                          " characters in full)";
            }

            content = content.Replace("\r\n", Nl).Replace("\r", Nl);

            string fence = new string('`', Math.Max(3, LongestBacktickRun(content) + 1));
            return fence + "json" + Nl + content + Nl + fence + Nl;
        }

        private static int LongestBacktickRun(string text)
        {
            int longest = 0, run = 0;
            foreach (char c in text ?? string.Empty)
            {
                if (c == '`') { run++; if (run > longest) longest = run; }
                else run = 0;
            }
            return longest;
        }

        private static string Pretty(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "{}";
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        });
                }
            }
            catch (System.Text.Json.JsonException)
            {
                return json;   // an error string, or a payload that was never JSON
            }
        }
    }
}
