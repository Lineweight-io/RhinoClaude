using System.Linq;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    public class SseParserTests
    {
        [Fact]
        public void ParsesASingleEvent()
        {
            var events = SseParser.ParseAll("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");

            var evt = Assert.Single(events);
            Assert.Equal("message_stop", evt.EventName);
            Assert.Equal("{\"type\":\"message_stop\"}", evt.Data);
        }

        [Fact]
        public void StripsExactlyOneSpaceAfterTheColon()
        {
            // "data:  x" (two spaces) must keep the second one.
            var events = SseParser.ParseAll("data:  x\n\n");
            Assert.Equal(" x", Assert.Single(events).Data);
        }

        [Fact]
        public void HandlesAFieldWithNoColon()
        {
            var events = SseParser.ParseAll("event\ndata: payload\n\n");
            var evt = Assert.Single(events);
            Assert.Equal(string.Empty, evt.EventName);
            Assert.Equal("payload", evt.Data);
        }

        [Fact]
        public void JoinsMultipleDataLinesWithNewlines()
        {
            var events = SseParser.ParseAll("data: line one\ndata: line two\n\n");
            Assert.Equal("line one\nline two", Assert.Single(events).Data);
        }

        [Fact]
        public void IgnoresCommentsAndHeartbeats()
        {
            var events = SseParser.ParseAll(": keep-alive\n\ndata: real\n\n");
            Assert.Equal("real", Assert.Single(events).Data);
        }

        [Fact]
        public void SplitsConsecutiveEventsOnBlankLines()
        {
            const string payload =
                "event: a\ndata: 1\n\n" +
                "event: b\ndata: 2\n\n" +
                "event: c\ndata: 3\n\n";

            var events = SseParser.ParseAll(payload);

            Assert.Equal(3, events.Count);
            Assert.Equal(new[] { "a", "b", "c" }, events.Select(e => e.EventName));
            Assert.Equal(new[] { "1", "2", "3" }, events.Select(e => e.Data));
        }

        [Fact]
        public void HandlesCrlfLineEndings()
        {
            var events = SseParser.ParseAll("event: a\r\ndata: 1\r\n\r\n");
            Assert.Equal("a", Assert.Single(events).EventName);
        }

        [Fact]
        public void FlushesATrailingEventWithNoBlankLine()
        {
            // A stream cut short mid-frame should still surface what arrived.
            var events = SseParser.ParseAll("event: a\ndata: 1");
            Assert.Equal("1", Assert.Single(events).Data);
        }

        [Fact]
        public void IncrementalPushMatchesBatchParse()
        {
            var parser = new SseParser();

            Assert.Null(parser.PushLine("event: message_start"));
            Assert.Null(parser.PushLine("data: {\"type\":\"message_start\"}"));

            var evt = parser.PushLine(string.Empty);

            Assert.NotNull(evt);
            Assert.Equal("message_start", evt.EventName);
        }

        [Fact]
        public void ExtraBlankLinesProduceNoEvents()
        {
            var events = SseParser.ParseAll("\n\n\n");
            Assert.Empty(events);
        }
    }
}
