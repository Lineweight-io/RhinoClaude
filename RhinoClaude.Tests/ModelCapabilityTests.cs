using System.Text.Json;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The settings gear lets the model change at runtime, and the request parameters are not
    /// interchangeable across models — sending `thinking` or `effort` where it isn't supported
    /// is a 400. These tests pin which shape each model gets.
    /// </summary>
    public class ModelCapabilityTests
    {
        [Theory]
        [InlineData("claude-sonnet-5", true)]
        [InlineData("claude-sonnet-4-6", true)]
        [InlineData("claude-opus-5", true)]
        [InlineData("claude-opus-4-8", true)]
        [InlineData("claude-sonnet-4-5-20250929", false)]
        [InlineData("claude-haiku-4-5", false)]
        [InlineData("claude-opus-4-5", false)]
        public void AdaptiveThinkingSupport(string model, bool expected)
        {
            Assert.Equal(expected, ModelCapabilities.SupportsAdaptiveThinking(model));
        }

        [Theory]
        [InlineData("claude-sonnet-5", true)]
        [InlineData("claude-opus-4-5", true)]   // effort predates adaptive thinking
        [InlineData("claude-sonnet-4-5-20250929", false)]
        [InlineData("claude-haiku-4-5", false)]
        public void EffortSupport(string model, bool expected)
        {
            Assert.Equal(expected, ModelCapabilities.SupportsEffort(model));
        }

        [Theory]
        [InlineData("claude-sonnet-5", true)]
        [InlineData("claude-opus-5", true)]
        [InlineData("claude-sonnet-4-6", false)]
        [InlineData("claude-opus-4-6", false)]
        public void ThinksByDefault(string model, bool expected)
        {
            Assert.Equal(expected, ModelCapabilities.ThinksByDefault(model));
        }

        [Fact]
        public void XHighIsClampedDownOnModelsThatLackIt()
        {
            Assert.Equal("xhigh", ModelCapabilities.ClampEffort("claude-sonnet-5", "xhigh"));
            Assert.Equal("high", ModelCapabilities.ClampEffort("claude-sonnet-4-6", "xhigh"));
        }

        [Fact]
        public void AnUnrecognisedEffortFallsBackToHigh()
        {
            Assert.Equal("high", ModelCapabilities.ClampEffort("claude-sonnet-5", "turbo"));
            Assert.Null(ModelCapabilities.ClampEffort("claude-sonnet-5", null));
        }

        [Fact]
        public void ADatedSnapshotIdMatchesItsFamily()
        {
            Assert.True(ModelCapabilities.SupportsAdaptiveThinking("claude-sonnet-4-6-20251114"));
            Assert.True(ModelCapabilities.ThinksByDefault("claude-sonnet-5"));
        }

        [Fact]
        public void Sonnet45IsNotMistakenForSonnet5()
        {
            // The prefixes overlap textually; getting this wrong sends `thinking` to a model
            // that 400s on it.
            Assert.False(ModelCapabilities.SupportsAdaptiveThinking("claude-sonnet-4-5-20250929"));
            Assert.False(ModelCapabilities.ThinksByDefault("claude-sonnet-4-5-20250929"));
        }

        // ── Request shaping ───────────────────────────────────────────

        private static JsonDocument BuildRequest(string model, string effort, bool showThinking)
        {
            var request = new MessagesRequest
            {
                Model = model,
                MaxTokens = 32000,
                Messages = { AgentMessage.User("hi") }
            };
            request.ApplyModelCapabilities(effort, showThinking);
            return JsonDocument.Parse(request.ToJson());
        }

        [Fact]
        public void Sonnet5RequestCarriesAdaptiveThinkingAndEffort()
        {
            using var doc = BuildRequest("claude-sonnet-5", "high", true);
            var root = doc.RootElement;

            var thinking = root.GetProperty("thinking");
            Assert.Equal("adaptive", thinking.GetProperty("type").GetString());
            Assert.Equal("summarized", thinking.GetProperty("display").GetString());

            Assert.Equal("high", root.GetProperty("output_config").GetProperty("effort").GetString());
        }

        [Fact]
        public void DisplayIsOmittedWhenThinkingIsNotShown()
        {
            using var doc = BuildRequest("claude-sonnet-5", "high", false);
            var thinking = doc.RootElement.GetProperty("thinking");

            Assert.Equal("adaptive", thinking.GetProperty("type").GetString());
            Assert.False(thinking.TryGetProperty("display", out _));
        }

        [Fact]
        public void Sonnet45RequestOmitsBothParametersEntirely()
        {
            using var doc = BuildRequest("claude-sonnet-4-5-20250929", "high", true);
            var root = doc.RootElement;

            Assert.False(root.TryGetProperty("thinking", out _));
            Assert.False(root.TryGetProperty("output_config", out _));
        }

        [Fact]
        public void EffortIsClampedIntoTheRequest()
        {
            using var doc = BuildRequest("claude-sonnet-4-6", "xhigh", true);
            Assert.Equal("high", doc.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        }
    }

    /// <summary>
    /// Thinking blocks have to survive a round trip byte-for-byte: the API validates the
    /// signature when the turn is replayed, and every tool-use iteration replays the whole
    /// conversation.
    /// </summary>
    public class ThinkingBlockTests
    {
        private const string ThinkingTurn =
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"The document is in \"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"inches, so 10 ft is 120.\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"EqQBCgIY\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"AhgCIkA=\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Building the wall.\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":50}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        private static StreamAccumulator Feed(string payload)
        {
            var accumulator = new StreamAccumulator();
            foreach (var sse in SseParser.ParseAll(payload))
                accumulator.Consume(sse);
            return accumulator;
        }

        [Fact]
        public void ThinkingTextAndSignatureAreBothAccumulated()
        {
            var message = Feed(ThinkingTurn).BuildMessage();

            Assert.Equal(2, message.Content.Count);
            var thinking = Assert.IsType<ThinkingBlock>(message.Content[0]);

            Assert.Equal("The document is in inches, so 10 ft is 120.", thinking.Thinking);
            Assert.Equal("EqQBCgIYAhgCIkA=", thinking.Signature);
        }

        [Fact]
        public void ThinkingIsNotMixedIntoTheAssistantText()
        {
            // The panel shows reasoning separately; TextContent must stay the answer only.
            Assert.Equal("Building the wall.", Feed(ThinkingTurn).BuildMessage().TextContent());
        }

        [Fact]
        public void ThinkingDeltasSurfaceAsNotifications()
        {
            var accumulator = new StreamAccumulator();
            var chunks = new System.Collections.Generic.List<string>();
            bool started = false;

            foreach (var sse in SseParser.ParseAll(ThinkingTurn))
            {
                var n = accumulator.Consume(sse);
                if (n == null) continue;
                if (n.Kind == StreamEventKind.ThinkingBlockStart) started = true;
                if (n.Kind == StreamEventKind.ThinkingDelta) chunks.Add(n.Text);
            }

            Assert.True(started);
            Assert.Equal(2, chunks.Count);
            // The signature must never reach the UI.
            Assert.DoesNotContain(chunks, c => c.Contains("EqQB"));
        }

        [Fact]
        public void ThinkingBlockRoundTripsForReplay()
        {
            var original = new ThinkingBlock { Thinking = "reasoning", Signature = "sig-abc" };

            string json = JsonSerializer.Serialize<ContentBlock>(original, MessagesRequest.SerializerOptions);
            var restored = Assert.IsType<ThinkingBlock>(
                JsonSerializer.Deserialize<ContentBlock>(json, MessagesRequest.SerializerOptions));

            Assert.Equal("reasoning", restored.Thinking);
            Assert.Equal("sig-abc", restored.Signature);
        }

        [Fact]
        public void AnEmptyThinkingBlockIsKeptBecauseItsSignatureStillMatters()
        {
            // With display "omitted" the text is empty but the block must still be replayed.
            const string payload =
                "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}\n\n" +
                "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig\"}}\n\n" +
                "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n";

            var block = Assert.Single(Feed(payload).BuildMessage().Content);
            var thinking = Assert.IsType<ThinkingBlock>(block);

            Assert.Equal(string.Empty, thinking.Thinking);
            Assert.Equal("sig", thinking.Signature);
        }

        [Fact]
        public void RedactedThinkingIsPreserved()
        {
            const string payload =
                "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"redacted_thinking\",\"data\":\"encrypted-blob\"}}\n\n" +
                "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n";

            var block = Assert.Single(Feed(payload).BuildMessage().Content);
            var redacted = Assert.IsType<RedactedThinkingBlock>(block);
            Assert.Equal("encrypted-blob", redacted.Data);

            string json = JsonSerializer.Serialize<ContentBlock>(redacted, MessagesRequest.SerializerOptions);
            Assert.Contains("encrypted-blob", json);
        }

        [Fact]
        public void ASignatureOnlyBlockDoesNotEmitANullSignatureField()
        {
            // Writing "signature": null would be rejected; the field is omitted instead.
            var block = new ThinkingBlock { Thinking = "x", Signature = null };
            string json = JsonSerializer.Serialize<ContentBlock>(block, MessagesRequest.SerializerOptions);

            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.TryGetProperty("signature", out _));
        }
    }
}
