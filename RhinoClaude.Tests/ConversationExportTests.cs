using System;
using System.Collections.Generic;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The markdown export. What matters is that a reviewer opening the file can see every
    /// turn, every tool call with its arguments and result, and the cost — and that nothing
    /// in the conversation can break the document's own structure.
    /// </summary>
    public class ConversationExportTests
    {
        private static ConversationExportRequest Request(params AgentMessage[] messages)
        {
            return new ConversationExportRequest
            {
                DocumentName = "Restroom Test.3dm",
                SessionDisplayName = "Add a north wing",
                SessionId = "11111111-2222-3333-4444-555555555555",
                StartedLocal = new DateTime(2026, 8, 14, 9, 5, 0),
                ExportedLocal = new DateTime(2026, 8, 14, 9, 41, 0),
                Model = "claude-sonnet-4-5-20250929",
                Messages = new List<AgentMessage>(messages)
            };
        }

        private static AgentMessage Assistant(params ContentBlock[] blocks) =>
            new AgentMessage("assistant", blocks);

        // ── Header ────────────────────────────────────────────────────

        [Fact]
        public void HeaderCarriesTheDocumentSessionAndModel()
        {
            var markdown = ConversationExport.ToMarkdown(Request(AgentMessage.User("Make a box")));

            Assert.Contains("# RhinoClaude conversation — Add a north wing", markdown);
            Assert.Contains("| Document | Restroom Test.3dm |", markdown);
            Assert.Contains("2026-08-14 09:05", markdown);
            Assert.Contains("claude-sonnet-4-5-20250929", markdown);
        }

        [Fact]
        public void CostIsPricedFromTheLoopModelsRates()
        {
            var request = Request(AgentMessage.User("hi"));
            request.SessionUsage = new TokenUsage { InputTokens = 1_000_000, OutputTokens = 1_000_000 };

            var markdown = ConversationExport.ToMarkdown(request);

            // Sonnet: $3 in + $15 out per MTok.
            Assert.Contains("$18.0000", markdown);
            Assert.Contains("1,000,000 in", markdown);
        }

        [Fact]
        public void AnUnsavedDocumentIsLabelledRatherThanLeftBlank()
        {
            var request = Request(AgentMessage.User("hi"));
            request.DocumentName = null;

            Assert.Contains("Untitled (never saved)", ConversationExport.ToMarkdown(request));
        }

        // ── Transcript ────────────────────────────────────────────────

        [Fact]
        public void UserAndAssistantTurnsAppearInOrderAndAreNumbered()
        {
            var markdown = ConversationExport.ToMarkdown(Request(
                AgentMessage.User("Make a box"),
                Assistant(new TextBlock("Making a box.")),
                AgentMessage.User("Now move it"),
                Assistant(new TextBlock("Moved."))));

            Assert.Contains("### Turn 1 — You", markdown);
            Assert.Contains("Make a box", markdown);
            Assert.Contains("### Turn 2 — You", markdown);
            Assert.True(markdown.IndexOf("Making a box.", StringComparison.Ordinal) <
                        markdown.IndexOf("### Turn 2 — You", StringComparison.Ordinal));
        }

        [Fact]
        public void ToolResultTurnsAreNotRenderedAsUserMessages()
        {
            var resultTurn = new AgentMessage("user",
                new ToolResultBlock { ToolUseId = "tu_1", Content = { new TextBlock("{\"ok\":true}") } });

            var markdown = ConversationExport.ToMarkdown(Request(
                AgentMessage.User("Make a box"), resultTurn));

            Assert.Single(Occurrences(markdown, "— You"));
        }

        [Fact]
        public void ToolCallsCarryNameArgumentsResultAndTiming()
        {
            var request = Request(
                AgentMessage.User("Make a box"),
                Assistant(new ToolUseBlock
                {
                    Id = "tu_1",
                    Name = "create_box",
                    InputJson = "{\"layer\":\"MASS_Building\"}"
                }));

            request.Invocations.Add(new ToolInvocation
            {
                ToolUseId = "tu_1",
                ToolName = "create_box",
                InputJson = "{\"layer\":\"MASS_Building\"}",
                ElapsedMs = 42,
                Result = ToolResult.Ok(new Dictionary<string, object> { { "id", "abc" } })
            });

            var markdown = ConversationExport.ToMarkdown(request);

            Assert.Contains("`create_box`", markdown);
            Assert.Contains("42 ms", markdown);
            Assert.Contains("MASS_Building", markdown);
            Assert.Contains("\"id\": \"abc\"", markdown);
            Assert.Contains("✓", markdown);
        }

        [Fact]
        public void AFailedToolCallShowsItsErrorRatherThanAnEmptyResult()
        {
            var request = Request(Assistant(new ToolUseBlock { Id = "tu_1", Name = "create_box" }));
            request.Invocations.Add(new ToolInvocation
            {
                ToolUseId = "tu_1",
                ToolName = "create_box",
                Result = ToolResult.Fail("No layer named 'MASS_Nope'.")
            });

            var markdown = ConversationExport.ToMarkdown(request);

            Assert.Contains("*error*", markdown);
            Assert.Contains("No layer named 'MASS_Nope'.", markdown);
            Assert.Contains("✗", markdown);
        }

        [Fact]
        public void AResumedConversationFallsBackToTheToolResultBlock()
        {
            // Restore() clears Invocations, so the only record of what a tool returned is the
            // tool_result block replayed from the .3dm.
            var request = Request(
                Assistant(new ToolUseBlock { Id = "tu_1", Name = "create_box" }),
                new AgentMessage("user", new ToolResultBlock
                {
                    ToolUseId = "tu_1",
                    Content = { new TextBlock("{\"success\":true,\"id\":\"restored\"}") }
                }));

            var markdown = ConversationExport.ToMarkdown(request);

            Assert.Contains("restored", markdown);
            Assert.Contains("timings were not saved", markdown);
        }

        [Fact]
        public void CapturedImagesAreNotedButNotEmbedded()
        {
            var request = Request(Assistant(new ToolUseBlock { Id = "tu_1", Name = "capture_views" }));
            var result = ToolResult.Ok(new Dictionary<string, object> { { "views", 2 } });
            result.Images.Add(new ToolImage { Base64 = "AAAA" });
            result.Images.Add(new ToolImage { Base64 = "BBBB" });
            request.Invocations.Add(new ToolInvocation
            {
                ToolUseId = "tu_1", ToolName = "capture_views", Result = result
            });

            var markdown = ConversationExport.ToMarkdown(request);

            Assert.Contains("2 image(s) were captured", markdown);
            Assert.DoesNotContain("AAAA", markdown);
        }

        [Fact]
        public void ThinkingIsQuotedSeparatelyFromTheAnswer()
        {
            var markdown = ConversationExport.ToMarkdown(Request(Assistant(
                new ThinkingBlock { Thinking = "The wall runs north." },
                new TextBlock("Done."))));

            Assert.Contains("#### Claude — thinking", markdown);
            Assert.Contains("> The wall runs north.", markdown);
        }

        // ── Robustness ────────────────────────────────────────────────

        [Fact]
        public void ANewlineInATitleCannotBreakTheMetadataTable()
        {
            var request = Request(AgentMessage.User("hi"));
            request.SessionDisplayName = "Make a\nbox | now";

            var markdown = ConversationExport.ToMarkdown(request);

            Assert.Contains("| Session | Make a box \\| now |", markdown);
        }

        [Fact]
        public void ContentContainingAFenceCannotEscapeItsCodeBlock()
        {
            var request = Request(Assistant(new ToolUseBlock
            {
                Id = "tu_1",
                Name = "run_rhinocommon_script",
                InputJson = "{\"code\":\"// ``` not a fence\"}"
            }));

            var markdown = ConversationExport.ToMarkdown(request);

            Assert.Contains("````json", markdown);
        }

        [Fact]
        public void OversizedPayloadsAreTruncatedWithAMarker()
        {
            var request = Request(Assistant(new ToolUseBlock
            {
                Id = "tu_1",
                Name = "big",
                InputJson = "{\"blob\":\"" + new string('x', 5000) + "\"}"
            }));
            request.MaxJsonChars = 200;

            var markdown = ConversationExport.ToMarkdown(request);

            Assert.Contains("… truncated", markdown);
            Assert.DoesNotContain(new string('x', 1000), markdown);
        }

        [Fact]
        public void AnEmptySessionSaysSoRatherThanProducingAnEmptyFile()
        {
            var markdown = ConversationExport.ToMarkdown(Request());

            Assert.Contains("## Transcript", markdown);
            Assert.Contains("_This session has no messages._", markdown);
        }

        // ── What changed ──────────────────────────────────────────────

        [Fact]
        public void TheChangeSummaryCountsCreatedDeletedAndSurviving()
        {
            var request = Request(AgentMessage.User("hi"));

            var created = new SessionMutation { ToolName = "create_box" };
            created.CreatedIds.Add("a");
            created.CreatedIds.Add("b");
            created.LayersTouched.Add("MASS_Building");

            var deleted = new SessionMutation { ToolName = "delete_objects" };
            deleted.DeletedIds.Add("b");

            request.Mutations = new[] { created, deleted };
            request.PendingUndoCount = 2;

            var markdown = ConversationExport.ToMarkdown(request);

            Assert.Contains("2 object(s) created, 1 deleted (net +1)", markdown);
            Assert.Contains("1 object(s) still in the document", markdown);
            Assert.Contains("MASS_Building", markdown);
            Assert.Contains("2 undo record(s)", markdown);
        }

        [Fact]
        public void TheChangeSectionIsOmittedWhenNothingWasChanged()
        {
            var markdown = ConversationExport.ToMarkdown(Request(AgentMessage.User("hi")));

            Assert.DoesNotContain("## What the agent changed", markdown);
        }

        // ── Line endings ──────────────────────────────────────────────

        [Fact]
        public void ForFileUsesThePlatformsLineEndings()
        {
            string onDisk = ConversationExport.ForFile("a\nb");

            Assert.Equal("a" + Environment.NewLine + "b", onDisk);
        }

        private static List<int> Occurrences(string haystack, string needle)
        {
            var found = new List<int>();
            int index = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (index >= 0)
            {
                found.Add(index);
                index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
            }
            return found;
        }
    }
}
