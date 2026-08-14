using System.Linq;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    public class StreamAccumulatorTests
    {
        private static StreamAccumulator Feed(string payload)
        {
            var accumulator = new StreamAccumulator();
            foreach (var sse in SseParser.ParseAll(payload))
                accumulator.Consume(sse);
            return accumulator;
        }

        private const string TextTurn =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":120,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Let me \"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"look at the doc.\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":42}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        [Fact]
        public void AssemblesTextDeltasIntoOneBlock()
        {
            var accumulator = Feed(TextTurn);
            var message = accumulator.BuildMessage();

            Assert.Equal("assistant", message.Role);
            var text = Assert.IsType<TextBlock>(Assert.Single(message.Content));
            Assert.Equal("Let me look at the doc.", text.Text);
        }

        [Fact]
        public void CapturesStopReasonAndAccumulatesUsage()
        {
            var accumulator = Feed(TextTurn);

            Assert.Equal("end_turn", accumulator.StopReason);
            Assert.True(accumulator.Completed);
            Assert.Equal(120, accumulator.Usage.InputTokens);
            // message_start reports 1, message_delta reports 42 — both are summed.
            Assert.Equal(43, accumulator.Usage.OutputTokens);
        }

        private const string ToolTurn =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":900}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Building it.\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_01\",\"name\":\"create_box\",\"input\":{}}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"corner1\\\":\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"[0,0,0],\"}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"corner2\\\":[10,10,10]}\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":88}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        [Fact]
        public void ReassemblesToolInputFromPartialJsonDeltas()
        {
            var accumulator = Feed(ToolTurn);

            var toolUse = Assert.Single(accumulator.ToolUses());
            Assert.Equal("create_box", toolUse.Name);
            Assert.Equal("toolu_01", toolUse.Id);

            var input = toolUse.ParseInput();
            Assert.Equal(0, input.GetProperty("corner1")[0].GetDouble());
            Assert.Equal(10, input.GetProperty("corner2")[2].GetDouble());
        }

        [Fact]
        public void KeepsTextAndToolBlocksInWireOrder()
        {
            var message = Feed(ToolTurn).BuildMessage();

            Assert.Equal(2, message.Content.Count);
            Assert.IsType<TextBlock>(message.Content[0]);
            Assert.IsType<ToolUseBlock>(message.Content[1]);
        }

        [Fact]
        public void ToolUseWithNoDeltasBecomesAnEmptyObject()
        {
            // A no-argument tool such as describe_document sends zero input_json_delta events.
            const string payload =
                "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"describe_document\",\"input\":{}}}\n\n" +
                "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n";

            var toolUse = Assert.Single(Feed(payload).ToolUses());

            Assert.Equal("{}", toolUse.InputJson);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, toolUse.ParseInput().ValueKind);
        }

        [Fact]
        public void FinalizesBlocksLeftOpenByATruncatedStream()
        {
            // No content_block_stop, no message_stop — a cancelled read mid-flight.
            const string payload =
                "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
                "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n";

            var message = Feed(payload).BuildMessage();

            var text = Assert.IsType<TextBlock>(Assert.Single(message.Content));
            Assert.Equal("partial", text.Text);
        }

        [Fact]
        public void DropsEmptyTextBlocks()
        {
            // The API rejects an assistant turn whose only content is an empty string.
            const string payload =
                "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
                "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n";

            Assert.Empty(Feed(payload).BuildMessage().Content);
        }

        [Fact]
        public void SurfacesStreamErrorEvents()
        {
            const string payload =
                "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";

            var accumulator = Feed(payload);

            Assert.Contains("overloaded_error", accumulator.ErrorMessage);
            Assert.Contains("Overloaded", accumulator.ErrorMessage);
        }

        [Fact]
        public void PreservesUnknownBlockTypesVerbatim()
        {
            const string payload =
                "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"…\"}}\n\n" +
                "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n";

            var block = Assert.Single(Feed(payload).BuildMessage().Content);
            Assert.IsType<UnknownBlock>(block);
            Assert.Equal("thinking", block.Type);
        }

        [Fact]
        public void EmitsNotificationsForTextAndToolStarts()
        {
            var accumulator = new StreamAccumulator();
            var kinds = SseParser.ParseAll(ToolTurn)
                                 .Select(accumulator.Consume)
                                 .Where(n => n != null)
                                 .Select(n => n.Kind)
                                 .ToList();

            Assert.Contains(StreamEventKind.MessageStart, kinds);
            Assert.Contains(StreamEventKind.TextDelta, kinds);
            Assert.Contains(StreamEventKind.ToolUseBlockStart, kinds);
            Assert.Contains(StreamEventKind.ToolInputDelta, kinds);
            Assert.Contains(StreamEventKind.MessageStop, kinds);
        }
    }
}
