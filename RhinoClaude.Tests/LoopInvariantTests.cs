using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// Properties the whole conversation must hold for the API to accept it. These are the
    /// failures that only show up as a 400 several iterations into a live turn, which is
    /// exactly when they are most expensive to diagnose — so they are pinned here instead.
    /// </summary>
    public class ConversationValidityTests
    {
        /// <summary>
        /// Simulates what AgentSession builds across a multi-tool turn, then serializes it the
        /// way the client would.
        /// </summary>
        private static List<AgentMessage> BuildTurn(int toolsPerIteration, int iterations)
        {
            var messages = new List<AgentMessage> { AgentMessage.User("build something") };

            for (int i = 0; i < iterations; i++)
            {
                var assistant = new AgentMessage("assistant");
                assistant.Content.Add(new ThinkingBlock { Thinking = "step " + i, Signature = "sig" + i });
                assistant.Content.Add(new TextBlock("Working."));

                var ids = new List<string>();
                for (int t = 0; t < toolsPerIteration; t++)
                {
                    string id = "toolu_" + i + "_" + t;
                    ids.Add(id);
                    assistant.Content.Add(new ToolUseBlock
                    {
                        Id = id,
                        Name = "create_box",
                        InputJson = "{\"corner1\":[0,0,0],\"corner2\":[1,1,1]}"
                    });
                }
                messages.Add(assistant);

                var results = new AgentMessage("user");
                foreach (var id in ids)
                    results.Content.Add(ToolResult.Ok(new Dictionary<string, object> { { "id", id } }).ToBlock(id));
                messages.Add(results);
            }

            return messages;
        }

        private static void AssertPairingHolds(List<AgentMessage> messages)
        {
            var toolUseIds = messages.SelectMany(m => m.Content).OfType<ToolUseBlock>()
                                     .Select(b => b.Id).ToList();
            var resultIds = messages.SelectMany(m => m.Content).OfType<ToolResultBlock>()
                                    .Select(b => b.ToolUseId).ToList();

            Assert.Equal(toolUseIds.OrderBy(x => x), resultIds.OrderBy(x => x));
            Assert.Equal(toolUseIds.Count, toolUseIds.Distinct().Count());
        }

        [Fact]
        public void ParallelToolCallsAllGetResultsInOneUserMessage()
        {
            // Splitting results across messages silently trains the model out of parallel calls.
            var messages = BuildTurn(toolsPerIteration: 4, iterations: 1);

            var resultMessage = messages.Last();
            Assert.Equal("user", resultMessage.Role);
            Assert.Equal(4, resultMessage.Content.OfType<ToolResultBlock>().Count());
            AssertPairingHolds(messages);
        }

        [Fact]
        public void PairingSurvivesManyIterations()
        {
            AssertPairingHolds(BuildTurn(toolsPerIteration: 3, iterations: 8));
        }

        [Fact]
        public void PairingSurvivesCompaction()
        {
            var messages = BuildTurn(toolsPerIteration: 3, iterations: 8);
            HistoryCompactor.Compact(messages, keepRecentTurns: 1);
            AssertPairingHolds(messages);
        }

        [Fact]
        public void PairingSurvivesAPersistenceRoundTrip()
        {
            var snapshot = new ConversationSnapshot { SessionId = "s", Messages = BuildTurn(2, 4) };
            var restored = ConversationSnapshot.FromJson(snapshot.ToJson());

            Assert.NotNull(restored);
            AssertPairingHolds(restored.Messages);
        }

        [Fact]
        public void PairingSurvivesCompactionThenPersistence()
        {
            var messages = BuildTurn(3, 6);
            HistoryCompactor.Compact(messages, keepRecentTurns: 1);

            var restored = ConversationSnapshot.FromJson(
                new ConversationSnapshot { SessionId = "s", Messages = messages }.ToJson());

            Assert.NotNull(restored);
            AssertPairingHolds(restored.Messages);
        }

        [Fact]
        public void TheWholeConversationSerializesToValidJson()
        {
            var request = new MessagesRequest
            {
                Model = "claude-sonnet-5",
                MaxTokens = 32000,
                System = "system",
                Messages = BuildTurn(3, 5)
            };
            request.ApplyModelCapabilities("high", true);

            using var doc = JsonDocument.Parse(request.ToJson());

            var roles = doc.RootElement.GetProperty("messages").EnumerateArray()
                .Select(m => m.GetProperty("role").GetString()).ToList();

            // user, then (assistant, user) per iteration.
            Assert.Equal("user", roles.First());
            Assert.Equal(11, roles.Count);
        }

        [Fact]
        public void NoMessageIsEverEmpty()
        {
            // The API rejects a message with an empty content array.
            var messages = BuildTurn(2, 3);
            HistoryCompactor.Compact(messages, keepRecentTurns: 1);

            Assert.All(messages, m => Assert.NotEmpty(m.Content));
        }

        [Fact]
        public void EveryToolResultCarriesItsSuccessFlag()
        {
            var messages = BuildTurn(2, 2);

            foreach (var result in messages.SelectMany(m => m.Content).OfType<ToolResultBlock>())
            {
                var text = Assert.IsType<TextBlock>(result.Content[0]);
                using var doc = JsonDocument.Parse(text.Text);
                Assert.True(doc.RootElement.TryGetProperty("success", out _));
            }
        }

        [Fact]
        public void AFailedToolStillProducesAResultBlockMarkedAsError()
        {
            // A tool_use with no matching result 400s the next request, so a failure must
            // still return something rather than being dropped.
            var block = ToolResult.Fail("Rhino refused the operation.").ToBlock("toolu_1");

            Assert.Equal("toolu_1", block.ToolUseId);
            Assert.True(block.IsError);
            Assert.NotEmpty(block.Content);
        }

        [Fact]
        public void ThinkingSignaturesSurviveTheFullPipeline()
        {
            // Compaction, then persistence, then re-serialization for the wire.
            var messages = BuildTurn(2, 6);
            HistoryCompactor.Compact(messages, keepRecentTurns: 1);

            var restored = ConversationSnapshot.FromJson(
                new ConversationSnapshot { SessionId = "s", Messages = messages }.ToJson());

            var signatures = restored.Messages.SelectMany(m => m.Content).OfType<ThinkingBlock>()
                                     .Select(t => t.Signature).ToList();

            Assert.Equal(6, signatures.Count);
            Assert.All(signatures, s => Assert.False(string.IsNullOrEmpty(s)));
        }
    }

    /// <summary>
    /// The tool registry is what the model sees. A malformed schema or an unstable order is
    /// a per-request cost, so both are pinned.
    /// </summary>
    public class ToolSurfaceTests
    {
        private static ToolDefinition Tool(string name, string schema) => new ToolDefinition
        {
            Name = name,
            Description = name,
            InputSchemaJson = schema,
            Handler = (input, ct) => ToolResult.Ok()
        };

        [Fact]
        public void EverySpecSchemaSerializesAsAJsonObjectNotAString()
        {
            var registry = new ToolRegistry();
            registry.Register(Tool("a", "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"}}}"));

            var request = new MessagesRequest { Model = "claude-sonnet-5", Tools = registry.ToSpecs() };

            using var doc = JsonDocument.Parse(request.ToJson());
            var schema = doc.RootElement.GetProperty("tools")[0].GetProperty("input_schema");

            Assert.Equal(JsonValueKind.Object, schema.ValueKind);
            Assert.Equal("number", schema.GetProperty("properties").GetProperty("x").GetProperty("type").GetString());
        }

        [Fact]
        public void AMalformedSchemaDegradesToAnEmptyObjectRatherThanBreakingTheRequest()
        {
            var registry = new ToolRegistry();
            registry.Register(Tool("bad", "{not json"));

            var request = new MessagesRequest { Model = "claude-sonnet-5", Tools = registry.ToSpecs() };

            using var doc = JsonDocument.Parse(request.ToJson());
            Assert.Equal(JsonValueKind.Object,
                doc.RootElement.GetProperty("tools")[0].GetProperty("input_schema").ValueKind);
        }

        [Fact]
        public void ToolOrderIsIdenticalAcrossRebuilds()
        {
            // Tools render first in the prompt; a shuffled order invalidates the cache each turn.
            List<string> Build()
            {
                var registry = new ToolRegistry();
                foreach (var name in new[] { "describe_document", "list_layers", "create_box", "signal_done" })
                    registry.Register(Tool(name, "{\"type\":\"object\"}"));
                return registry.ToSpecs().Select(s => s.Name).ToList();
            }

            Assert.Equal(Build(), Build());
        }
    }
}
