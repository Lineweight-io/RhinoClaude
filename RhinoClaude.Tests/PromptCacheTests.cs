using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// Where the <c>cache_control</c> breakpoints land in the request body.
    ///
    /// These assert the wire shape rather than the saving, because the saving is entirely a
    /// consequence of the shape: caching is a prefix match, so a breakpoint in the wrong place
    /// (or one too many) is the difference between paying a tenth of the input rate and paying
    /// all of it. Nothing here calls the API.
    /// </summary>
    public class PromptCacheTests
    {
        private const string Model = "claude-sonnet-5";

        // ── Placement ─────────────────────────────────────────────────

        [Fact]
        public void TheSystemPromptIsSentAsATextBlockArrayCarryingABreakpoint()
        {
            var request = Request(systemPrompt: "you are an agent", toolCount: 2, messages: Turn(1));
            PromptCache.Apply(request);

            var system = Root(request).GetProperty("system");

            Assert.Equal(JsonValueKind.Array, system.ValueKind);
            Assert.Equal(1, system.GetArrayLength());
            Assert.Equal("you are an agent", system[0].GetProperty("text").GetString());
            Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());
        }

        [Fact]
        public void WithoutCachingTheSystemPromptStaysAPlainString()
        {
            // Every non-loop caller — ClaudeTag, the reviewer — never calls Apply.
            var request = Request(systemPrompt: "you are an agent", toolCount: 2, messages: Turn(1));

            var system = Root(request).GetProperty("system");

            Assert.Equal(JsonValueKind.String, system.ValueKind);
            Assert.Equal("you are an agent", system.GetString());
        }

        [Fact]
        public void OnlyTheLastToolSchemaCarriesABreakpoint()
        {
            // The tools array renders first, so one breakpoint at its end caches all of them.
            var request = Request(systemPrompt: "s", toolCount: 4, messages: Turn(1));
            PromptCache.Apply(request);

            var tools = Root(request).GetProperty("tools").EnumerateArray().ToList();

            Assert.Equal(4, tools.Count);
            Assert.All(tools.Take(3), t => Assert.False(t.TryGetProperty("cache_control", out _)));
            Assert.Equal("ephemeral", tools[3].GetProperty("cache_control").GetProperty("type").GetString());
        }

        [Fact]
        public void TheBreakpointGoesOnTheLastContentBlockOfTheMarkedMessage()
        {
            var message = new AgentMessage("assistant",
                new TextBlock("thinking out loud"),
                new ToolUseBlock { Id = "t1", Name = "list_objects", InputJson = "{}" });

            var request = Request("s", 1, new List<AgentMessage> { message });
            PromptCache.Apply(request);

            var blocks = Root(request).GetProperty("messages")[0].GetProperty("content");

            Assert.False(blocks[0].TryGetProperty("cache_control", out _));
            Assert.Equal("ephemeral", blocks[1].GetProperty("cache_control").GetProperty("type").GetString());
        }

        [Fact]
        public void ABreakpointOnAnUnknownBlockIsMergedIntoItsPreservedJson()
        {
            // Unknown blocks round-trip as raw JSON; the marker has to go inside the object
            // rather than after it, or the request is malformed.
            var message = new AgentMessage("assistant",
                new UnknownBlock("server_tool_use", "{\"type\":\"server_tool_use\",\"id\":\"srv_1\"}"));

            var request = Request("s", 1, new List<AgentMessage> { message });
            PromptCache.Apply(request);

            var block = Root(request).GetProperty("messages")[0].GetProperty("content")[0];

            Assert.Equal("server_tool_use", block.GetProperty("type").GetString());
            Assert.Equal("srv_1", block.GetProperty("id").GetString());
            Assert.Equal("ephemeral", block.GetProperty("cache_control").GetProperty("type").GetString());
        }

        // ── Budget ────────────────────────────────────────────────────

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(11)]
        [InlineData(51)]
        public void TheFourBreakpointCeilingIsNeverExceeded(int iterations)
        {
            var request = Request("s", 25, Turn(iterations));
            PromptCache.Apply(request);

            Assert.True(PromptCache.CountBreakpoints(request) <= PromptCache.MaxBreakpoints);
            Assert.Equal(PromptCache.CountBreakpoints(request), CountInJson(request));
        }

        [Fact]
        public void AFullRequestUsesAllFourBreakpoints()
        {
            var request = Request("s", 25, Turn(3));
            PromptCache.Apply(request);

            Assert.Equal(PromptCache.MaxBreakpoints, PromptCache.CountBreakpoints(request));
        }

        // ── Rolling placement across iterations ───────────────────────

        [Fact]
        public void TheOlderBreakpointSitsWhereThePreviousIterationWroteItsEntry()
        {
            // One iteration appends two messages (the assistant turn and the tool results),
            // so the previous request's tail is at Count - 3. Marking it is what turns a
            // write-only cache into one the next iteration can read back.
            Assert.Equal(new[] { 0, 2 }, PromptCache.MessageBreakpointIndices(3));
            Assert.Equal(new[] { 2, 4 }, PromptCache.MessageBreakpointIndices(5));
            Assert.Equal(new[] { 4, 6 }, PromptCache.MessageBreakpointIndices(7));
        }

        [Fact]
        public void TheFirstIterationHasOnlyOnePlaceToPutABreakpoint()
        {
            Assert.Equal(new[] { 0 }, PromptCache.MessageBreakpointIndices(1));
            Assert.Equal(new[] { 1 }, PromptCache.MessageBreakpointIndices(2));
            Assert.Empty(PromptCache.MessageBreakpointIndices(0));
        }

        [Fact]
        public void ReapplyingMovesTheBreakpointsInsteadOfAccumulatingThem()
        {
            // Messages is the session's live list, so the same block objects come back next
            // iteration. Without the clear, stale markers would spend the budget on positions
            // that are no longer the tail.
            var messages = Turn(1);
            var request = Request("s", 3, messages);
            PromptCache.Apply(request);

            for (int iteration = 0; iteration < 4; iteration++)
            {
                messages.Add(new AgentMessage("assistant", new TextBlock("working")));
                messages.Add(new AgentMessage("user", new TextBlock("result")));
                PromptCache.Apply(request);

                Assert.Equal(PromptCache.MaxBreakpoints, PromptCache.CountBreakpoints(request));

                var marked = messages
                    .Select((m, i) => new { Index = i, Marked = m.Content[m.Content.Count - 1].CacheControl != null })
                    .Where(x => x.Marked)
                    .Select(x => x.Index)
                    .ToList();

                Assert.Equal(new[] { messages.Count - 3, messages.Count - 1 }, marked);
            }
        }

        [Fact]
        public void ApplyingTwiceProducesTheIdenticalRequestBody()
        {
            // Byte-for-byte stability is the whole mechanism — an unstable prefix caches nothing.
            var request = Request("s", 3, Turn(2));

            PromptCache.Apply(request);
            string first = request.ToJson();
            PromptCache.Apply(request);

            Assert.Equal(first, request.ToJson());
        }

        [Fact]
        public void ClearRemovesEveryMarkerItSet()
        {
            var request = Request("s", 3, Turn(2));
            PromptCache.Apply(request);
            PromptCache.Clear(request);

            Assert.Equal(0, PromptCache.CountBreakpoints(request));
            Assert.Equal(0, CountInJson(request));
            Assert.Equal(JsonValueKind.String, Root(request).GetProperty("system").ValueKind);
        }

        // ── Degenerate requests ───────────────────────────────────────

        [Fact]
        public void ARequestWithNoToolsAndNoSystemPromptStillCachesTheConversation()
        {
            var request = new MessagesRequest { Model = Model, Messages = Turn(2) };
            PromptCache.Apply(request);

            Assert.Equal(PromptCache.MessageBreakpoints, PromptCache.CountBreakpoints(request));
            Assert.False(Root(request).TryGetProperty("system", out _));
        }

        [Fact]
        public void AnEmptyRequestIsLeftAlone()
        {
            var request = new MessagesRequest { Model = Model };
            PromptCache.Apply(request);

            Assert.Equal(0, PromptCache.CountBreakpoints(request));
        }

        [Fact]
        public void CachingSurvivesTheOtherRequestShapingSteps()
        {
            var request = Request("s", 25, Turn(3));
            request.ApplyModelCapabilities("high", showThinking: true);
            PromptCache.Apply(request);

            var root = Root(request);

            Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("high", root.GetProperty("output_config").GetProperty("effort").GetString());
            Assert.Equal(PromptCache.MaxBreakpoints, CountInJson(request));
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static MessagesRequest Request(string systemPrompt, int toolCount, List<AgentMessage> messages) =>
            new MessagesRequest
            {
                Model = Model,
                MaxTokens = 32000,
                System = systemPrompt,
                Messages = messages,
                Tools = toolCount == 0 ? null : Enumerable.Range(0, toolCount).Select(i => new ToolSpec
                {
                    Name = "tool_" + i,
                    Description = "Tool number " + i + "."
                }).ToList()
            };

        /// <summary>A user turn plus <paramref name="iterations"/> assistant/tool-result pairs.</summary>
        private static List<AgentMessage> Turn(int iterations)
        {
            var messages = new List<AgentMessage> { AgentMessage.User("draw an L-shaped mass") };

            for (int i = 0; i < iterations; i++)
            {
                messages.Add(new AgentMessage("assistant",
                    new TextBlock("step " + i),
                    new ToolUseBlock { Id = "t" + i, Name = "list_objects", InputJson = "{}" }));

                messages.Add(new AgentMessage("user", new ToolResultBlock
                {
                    ToolUseId = "t" + i,
                    Content = { new TextBlock("{\"success\":true}") }
                }));
            }

            return messages;
        }

        private static JsonElement Root(MessagesRequest request) =>
            JsonDocument.Parse(request.ToJson()).RootElement.Clone();

        /// <summary>Every <c>cache_control</c> the serialized body actually carries.</summary>
        private static int CountInJson(MessagesRequest request)
        {
            int count = 0;
            var root = Root(request);

            if (root.TryGetProperty("system", out var system) && system.ValueKind == JsonValueKind.Array)
                count += system.EnumerateArray().Count(b => b.TryGetProperty("cache_control", out _));

            if (root.TryGetProperty("tools", out var tools))
                count += tools.EnumerateArray().Count(t => t.TryGetProperty("cache_control", out _));

            if (root.TryGetProperty("messages", out var messages))
                foreach (var message in messages.EnumerateArray())
                    count += message.GetProperty("content").EnumerateArray()
                                    .Count(b => b.TryGetProperty("cache_control", out _));

            return count;
        }
    }
}
