using System.Collections.Generic;
using System.Linq;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// Compaction rewrites live conversation history that gets replayed to the API on every
    /// iteration, so the invariants matter more than the saving: every tool_use keeps its
    /// matching tool_result, and thinking blocks are never touched.
    /// </summary>
    public class HistoryCompactorTests
    {
        private static AgentMessage UserTurn(string text) => AgentMessage.User(text);

        private static AgentMessage AssistantWithTool(string toolUseId, string name = "create_box")
        {
            return new AgentMessage("assistant",
                new TextBlock("Working on it."),
                new ToolUseBlock { Id = toolUseId, Name = name, InputJson = "{\"corner1\":[0,0,0]}" });
        }

        private static AgentMessage ToolResult(string toolUseId, string json, int images = 0)
        {
            var block = new ToolResultBlock { ToolUseId = toolUseId };
            block.Content.Add(new TextBlock(json));
            for (int i = 0; i < images; i++)
                block.Content.Add(new ImageBlock { MediaType = "image/png", Data = new string('A', 5000) });
            return new AgentMessage("user", block);
        }

        /// <summary>Six user turns, each with one tool round-trip.</summary>
        private static List<AgentMessage> Conversation(int turns, int imagesPerResult = 0)
        {
            var messages = new List<AgentMessage>();
            for (int i = 0; i < turns; i++)
            {
                messages.Add(UserTurn("turn " + i));
                messages.Add(AssistantWithTool("toolu_" + i));
                messages.Add(ToolResult("toolu_" + i,
                    "{\"success\":true,\"id\":\"abc" + i + "\",\"padding\":\"" + new string('x', 500) + "\"}",
                    imagesPerResult));
                messages.Add(new AgentMessage("assistant", new TextBlock("Done with turn " + i)));
            }
            return messages;
        }

        [Fact]
        public void LeavesTheMostRecentTurnsAlone()
        {
            var messages = Conversation(6);
            HistoryCompactor.Compact(messages, keepRecentTurns: 3);

            // The last three turns' results must still hold their full payload.
            var recent = messages.Skip(messages.Count - 12)
                                 .SelectMany(m => m.Content)
                                 .OfType<ToolResultBlock>()
                                 .ToList();

            Assert.Equal(3, recent.Count);
            Assert.All(recent, r =>
                Assert.DoesNotContain(HistoryCompactor.CompactedMarker,
                    ((TextBlock)r.Content[0]).Text));
        }

        [Fact]
        public void CondensesOlderResults()
        {
            var messages = Conversation(6);
            var report = HistoryCompactor.Compact(messages, keepRecentTurns: 3);

            Assert.Equal(3, report.ResultsCompacted);
            Assert.True(report.CharactersSaved > 0);
        }

        [Fact]
        public void EveryToolUseStillHasAMatchingToolResult()
        {
            // This is the invariant the API enforces; breaking it 400s the next request.
            var messages = Conversation(6);
            HistoryCompactor.Compact(messages, keepRecentTurns: 1);

            var toolUseIds = messages.SelectMany(m => m.Content).OfType<ToolUseBlock>()
                                     .Select(b => b.Id).OrderBy(x => x).ToList();
            var resultIds = messages.SelectMany(m => m.Content).OfType<ToolResultBlock>()
                                    .Select(b => b.ToolUseId).OrderBy(x => x).ToList();

            Assert.Equal(toolUseIds, resultIds);
        }

        [Fact]
        public void CompactedResultsAreNeverEmpty()
        {
            var messages = Conversation(6);
            HistoryCompactor.Compact(messages, keepRecentTurns: 1);

            var results = messages.SelectMany(m => m.Content).OfType<ToolResultBlock>();
            Assert.All(results, r => Assert.NotEmpty(r.Content));
        }

        [Fact]
        public void ThinkingBlocksAreNeverTouched()
        {
            // Removing or editing a thinking block breaks signature validation on replay.
            var messages = Conversation(4);
            messages.Insert(1, new AgentMessage("assistant",
                new ThinkingBlock { Thinking = "reasoning", Signature = "sig-1" },
                new TextBlock("hello")));

            HistoryCompactor.Compact(messages, keepRecentTurns: 1);

            var thinking = messages.SelectMany(m => m.Content).OfType<ThinkingBlock>().Single();
            Assert.Equal("reasoning", thinking.Thinking);
            Assert.Equal("sig-1", thinking.Signature);
        }

        [Fact]
        public void ToolUseInputsAreNotRewritten()
        {
            var messages = Conversation(6);
            HistoryCompactor.Compact(messages, keepRecentTurns: 1);

            Assert.All(messages.SelectMany(m => m.Content).OfType<ToolUseBlock>(),
                b => Assert.Contains("corner1", b.InputJson));
        }

        [Fact]
        public void DropsImagesFromOldResultsAndSaysSo()
        {
            var messages = Conversation(6, imagesPerResult: 2);
            var report = HistoryCompactor.Compact(messages, keepRecentTurns: 3);

            Assert.Equal(6, report.ImagesDropped);

            var oldest = messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().First();
            Assert.Empty(oldest.Content.OfType<ImageBlock>());
            Assert.Contains("image(s) from this call were dropped",
                ((TextBlock)oldest.Content[0]).Text);
        }

        [Fact]
        public void PreservesWhetherTheOldCallSucceeded()
        {
            var messages = new List<AgentMessage>
            {
                UserTurn("one"),
                AssistantWithTool("t1"),
                ToolResult("t1", "{\"success\":false,\"error\":\"No layer named Walls.\"}"),
                UserTurn("two"),
                UserTurn("three"),
                UserTurn("four")
            };

            HistoryCompactor.Compact(messages, keepRecentTurns: 2);

            string text = ((TextBlock)messages.SelectMany(m => m.Content)
                .OfType<ToolResultBlock>().First().Content[0]).Text;

            Assert.Contains("failed", text);
            Assert.Contains("No layer named Walls", text);
        }

        [Fact]
        public void IsIdempotent()
        {
            var messages = Conversation(6);

            var first = HistoryCompactor.Compact(messages, keepRecentTurns: 3);
            var second = HistoryCompactor.Compact(messages, keepRecentTurns: 3);

            Assert.Equal(3, first.ResultsCompacted);
            Assert.Equal(0, second.ResultsCompacted);
        }

        [Fact]
        public void ShortConversationsAreUntouched()
        {
            var messages = Conversation(2);
            Assert.False(HistoryCompactor.Compact(messages, keepRecentTurns: 3).ChangedAnything);
        }

        [Fact]
        public void ToolResultTurnsDoNotCountAsUserTurns()
        {
            // If they did, the retained window would be filled with the loop's own traffic
            // instead of the last few things the user actually said.
            var messages = Conversation(6);
            HistoryCompactor.Compact(messages, keepRecentTurns: 3);

            // 6 real turns, keeping 3 => exactly 3 compacted.
            Assert.Equal(3, messages.SelectMany(m => m.Content).OfType<ToolResultBlock>()
                .Count(r => ((TextBlock)r.Content[0]).Text.StartsWith(HistoryCompactor.CompactedMarker)));
        }

        [Fact]
        public void MeasuresTheConversationIncludingImages()
        {
            var withImages = HistoryCompactor.MeasureConversation(Conversation(2, imagesPerResult: 1));
            var without = HistoryCompactor.MeasureConversation(Conversation(2));

            Assert.True(withImages > without + 9000);
        }

        [Fact]
        public void NullAndEmptyAreSafe()
        {
            Assert.False(HistoryCompactor.Compact(null).ChangedAnything);
            Assert.False(HistoryCompactor.Compact(new List<AgentMessage>()).ChangedAnything);
            Assert.Equal(0, HistoryCompactor.MeasureConversation(null));
        }
    }

    /// <summary>
    /// Conversations are stored inside the .3dm, so a bad round trip corrupts a user's file
    /// rather than just a cache.
    /// </summary>
    public class ConversationSnapshotTests
    {
        private static ConversationSnapshot Sample()
        {
            var snapshot = new ConversationSnapshot
            {
                SessionId = "11111111-2222-3333-4444-555555555555",
                DisplayName = "Build a 10x12 office",
                CreatedUtc = "2026-08-14T09:00:00.0000000Z",
                SavedUtc = "2026-08-14T09:30:00.0000000Z",
                Model = "claude-sonnet-5",
                UndoRecordCount = 7
            };

            snapshot.Messages.Add(AgentMessage.User("Build a 10x12 office"));
            snapshot.Messages.Add(new AgentMessage("assistant",
                new ThinkingBlock { Thinking = "inches", Signature = "sig" },
                new TextBlock("On it."),
                new ToolUseBlock { Id = "t1", Name = "create_box", InputJson = "{\"corner1\":[0,0,0]}" }));

            var result = new ToolResultBlock { ToolUseId = "t1" };
            result.Content.Add(new TextBlock("{\"success\":true}"));
            result.Content.Add(new ImageBlock { MediaType = "image/png", Data = "AAAABBBB" });
            snapshot.Messages.Add(new AgentMessage("user", result));

            return snapshot;
        }

        [Fact]
        public void RoundTripsMetadata()
        {
            var restored = ConversationSnapshot.FromJson(Sample().ToJson());

            Assert.NotNull(restored);
            Assert.Equal("Build a 10x12 office", restored.DisplayName);
            Assert.Equal("claude-sonnet-5", restored.Model);
            Assert.Equal(7, restored.UndoRecordCount);
            Assert.Equal(ConversationSnapshot.CurrentVersion, restored.Version);
        }

        [Fact]
        public void RoundTripsThinkingSignatures()
        {
            var restored = ConversationSnapshot.FromJson(Sample().ToJson());
            var thinking = restored.Messages.SelectMany(m => m.Content).OfType<ThinkingBlock>().Single();

            Assert.Equal("sig", thinking.Signature);
        }

        [Fact]
        public void RoundTripsToolUseInput()
        {
            var restored = ConversationSnapshot.FromJson(Sample().ToJson());
            var toolUse = restored.Messages.SelectMany(m => m.Content).OfType<ToolUseBlock>().Single();

            Assert.Equal("create_box", toolUse.Name);
            Assert.Equal(0, toolUse.ParseInput().GetProperty("corner1")[0].GetDouble());
        }

        [Fact]
        public void StripsImagesButKeepsTheResultValid()
        {
            // Base64 screenshots would bloat the .3dm by megabytes for little benefit.
            string json = Sample().ToJson();
            Assert.DoesNotContain("AAAABBBB", json);

            var restored = ConversationSnapshot.FromJson(json);
            var result = restored.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();

            Assert.Empty(result.Content.OfType<ImageBlock>());
            Assert.NotEmpty(result.Content);
            Assert.Contains(result.Content.OfType<TextBlock>(),
                t => t.Text.Contains("image(s) were not saved"));
        }

        [Fact]
        public void CountsOnlyRealUserTurns()
        {
            // Three messages, but only one is something the user typed.
            Assert.Equal(1, Sample().UserTurnCount);
        }

        [Fact]
        public void CorruptJsonReturnsNullRatherThanThrowing()
        {
            // A damaged blob must not stop the document opening.
            Assert.Null(ConversationSnapshot.FromJson("{not json"));
            Assert.Null(ConversationSnapshot.FromJson(""));
            Assert.Null(ConversationSnapshot.FromJson(null));
        }

        [Fact]
        public void ASnapshotFromANewerBuildIsRejected()
        {
            string json = "{\"version\":999,\"sessionId\":\"x\",\"messages\":[]}";
            Assert.Null(ConversationSnapshot.FromJson(json));
        }

        [Fact]
        public void ChunkingRoundTripsExactly()
        {
            string payload = string.Join("", Enumerable.Range(0, 5000).Select(i => (i % 10).ToString()));
            var chunks = ConversationSnapshot.Chunk(payload, 1024);

            Assert.True(chunks.Count > 1);
            Assert.All(chunks, c => Assert.True(c.Length <= 1024));
            Assert.Equal(payload, ConversationSnapshot.Join(chunks));
        }

        [Fact]
        public void ChunkingHandlesAnExactMultipleAndAnEmptyPayload()
        {
            var exact = ConversationSnapshot.Chunk(new string('x', 2048), 1024);
            Assert.Equal(2, exact.Count);
            Assert.Equal(new string('x', 2048), ConversationSnapshot.Join(exact));

            Assert.Empty(ConversationSnapshot.Chunk(""));
        }

        [Fact]
        public void ALargeConversationSurvivesChunkedStorage()
        {
            var snapshot = Sample();
            for (int i = 0; i < 200; i++)
                snapshot.Messages.Add(AgentMessage.User("turn " + i + " " + new string('y', 400)));

            string json = snapshot.ToJson();
            var rebuilt = ConversationSnapshot.Join(ConversationSnapshot.Chunk(json));
            var restored = ConversationSnapshot.FromJson(rebuilt);

            Assert.NotNull(restored);
            Assert.Equal(snapshot.Messages.Count, restored.Messages.Count);
        }
    }
}
