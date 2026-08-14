using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Keeps a long-lived session's message list from becoming a token bomb (plan risk #6).
    ///
    /// The compaction deliberately rewrites only the <em>content</em> of old tool_result
    /// blocks — never the block structure. Dropping or reordering blocks would break two
    /// invariants the API enforces: every tool_use needs a matching tool_result, and thinking
    /// blocks must be replayed unchanged with their signature. Shrinking payloads in place
    /// gets almost all of the saving with none of that risk, because the bulk is screenshots
    /// and verbose JSON rather than the envelopes.
    /// </summary>
    public static class HistoryCompactor
    {
        /// <summary>Tool results in the most recent N user turns are left untouched.</summary>
        public const int DefaultKeepRecentTurns = 3;

        /// <summary>How much of an old tool result's JSON to keep as a reminder of what it said.</summary>
        private const int SummaryLength = 200;

        public sealed class CompactionReport
        {
            public int ResultsCompacted { get; set; }
            public int ImagesDropped { get; set; }
            public int CharactersSaved { get; set; }

            public bool ChangedAnything => ResultsCompacted > 0 || ImagesDropped > 0;

            public override string ToString()
            {
                return string.Format(
                    "compacted {0} tool result(s), dropped {1} image(s), saved ~{2:n0} characters",
                    ResultsCompacted, ImagesDropped, CharactersSaved);
            }
        }

        /// <summary>
        /// Compact tool results older than the last <paramref name="keepRecentTurns"/> user turns.
        /// Mutates <paramref name="messages"/> in place.
        /// </summary>
        public static CompactionReport Compact(
            List<AgentMessage> messages,
            int keepRecentTurns = DefaultKeepRecentTurns)
        {
            var report = new CompactionReport();
            if (messages == null || messages.Count == 0) return report;
            if (keepRecentTurns < 0) keepRecentTurns = 0;

            int cutoff = CutoffIndex(messages, keepRecentTurns);
            if (cutoff <= 0) return report;

            for (int i = 0; i < cutoff; i++)
            {
                foreach (var block in messages[i].Content)
                {
                    if (!(block is ToolResultBlock result)) continue;
                    CompactResult(result, report);
                }
            }

            return report;
        }

        /// <summary>
        /// Index of the first message belonging to the retained window. Turns are delimited by
        /// user messages that carry real text — a user message made only of tool_result blocks
        /// is the loop feeding itself, not a new turn.
        /// </summary>
        private static int CutoffIndex(List<AgentMessage> messages, int keepRecentTurns)
        {
            var turnStarts = new List<int>();
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role == "user" && IsRealUserTurn(messages[i]))
                    turnStarts.Add(i);
            }

            if (turnStarts.Count <= keepRecentTurns) return 0;
            return turnStarts[turnStarts.Count - keepRecentTurns];
        }

        private static bool IsRealUserTurn(AgentMessage message)
        {
            return message.Content.Any(b => b is TextBlock t && !string.IsNullOrWhiteSpace(t.Text))
                && !message.Content.Any(b => b is ToolResultBlock);
        }

        private static void CompactResult(ToolResultBlock result, CompactionReport report)
        {
            if (result.Content.Count == 0) return;

            // Already compacted — leave it alone so repeated calls are idempotent.
            if (result.Content.Count == 1 &&
                result.Content[0] is TextBlock existing &&
                existing.Text != null &&
                existing.Text.StartsWith(CompactedMarker, StringComparison.Ordinal))
            {
                return;
            }

            int before = MeasureContent(result.Content);
            int images = result.Content.Count(b => b is ImageBlock);

            string summary = SummarizeText(result.Content);

            result.Content.Clear();
            result.Content.Add(new TextBlock(summary));

            int after = MeasureContent(result.Content);

            report.ResultsCompacted++;
            report.ImagesDropped += images;
            report.CharactersSaved += Math.Max(0, before - after);
        }

        public const string CompactedMarker = "[earlier result, condensed]";

        /// <summary>
        /// Keep the shape of what the result said — success and a short prefix of the payload —
        /// so the model still knows what happened without carrying the whole thing.
        /// </summary>
        private static string SummarizeText(List<ContentBlock> content)
        {
            var text = content.OfType<TextBlock>().FirstOrDefault()?.Text;
            int images = content.Count(b => b is ImageBlock);

            string detail;
            if (string.IsNullOrWhiteSpace(text))
            {
                detail = "(no text payload)";
            }
            else
            {
                bool? success = TryReadSuccess(text);
                string body = text.Length <= SummaryLength ? text : text.Substring(0, SummaryLength) + "…";
                detail = success.HasValue
                    ? (success.Value ? "succeeded. " : "failed. ") + body
                    : body;
            }

            string imageNote = images == 0
                ? string.Empty
                : " " + images + " image(s) from this call were dropped; capture again if you need to look.";

            return CompactedMarker + " " + detail + imageNote;
        }

        private static bool? TryReadSuccess(string json)
        {
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("success", out var success))
                    {
                        if (success.ValueKind == JsonValueKind.True) return true;
                        if (success.ValueKind == JsonValueKind.False) return false;
                    }
                }
            }
            catch (JsonException) { /* not JSON; the prefix is still useful */ }
            return null;
        }

        private static int MeasureContent(List<ContentBlock> content)
        {
            int total = 0;
            foreach (var block in content)
            {
                if (block is TextBlock text) total += text.Text?.Length ?? 0;
                else if (block is ImageBlock image) total += image.Data?.Length ?? 0;
            }
            return total;
        }

        /// <summary>Rough character weight of the whole conversation, for deciding when to compact.</summary>
        public static int MeasureConversation(List<AgentMessage> messages)
        {
            if (messages == null) return 0;

            int total = 0;
            foreach (var message in messages)
            {
                foreach (var block in message.Content)
                {
                    switch (block)
                    {
                        case TextBlock text: total += text.Text?.Length ?? 0; break;
                        case ImageBlock image: total += image.Data?.Length ?? 0; break;
                        case ToolUseBlock toolUse: total += toolUse.InputJson?.Length ?? 0; break;
                        case ToolResultBlock result: total += MeasureContent(result.Content); break;
                        case ThinkingBlock thinking: total += thinking.Thinking?.Length ?? 0; break;
                    }
                }
            }
            return total;
        }
    }
}
