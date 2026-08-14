using System;
using System.Collections.Generic;
using System.Text;

namespace RhinoClaude.Agent
{
    /// <summary>One decoded Server-Sent Event.</summary>
    public sealed class SseEvent
    {
        public string EventName { get; set; }
        public string Data { get; set; }

        public override string ToString() => EventName + ": " + Data;
    }

    /// <summary>
    /// Incremental Server-Sent Events decoder.
    ///
    /// Feed it raw lines as they arrive off the wire; it emits an <see cref="SseEvent"/>
    /// each time a blank line terminates an event. Kept free of any HTTP or RhinoCommon
    /// dependency so the framing rules can be tested directly.
    /// </summary>
    public sealed class SseParser
    {
        private string _eventName;
        private readonly StringBuilder _data = new StringBuilder();

        /// <summary>
        /// Push one line (without its trailing newline). Returns a completed event, or null
        /// if this line only added to the event currently being assembled.
        /// </summary>
        public SseEvent PushLine(string line)
        {
            if (line == null) return null;

            // Strip a UTF-8 BOM if it leads the very first line.
            if (line.Length > 0 && line[0] == '﻿')
                line = line.Substring(1);

            // A blank line dispatches whatever has been accumulated.
            if (line.Length == 0)
            {
                if (_eventName == null && _data.Length == 0)
                    return null;

                var evt = new SseEvent
                {
                    EventName = _eventName,
                    Data = _data.ToString()
                };
                _eventName = null;
                _data.Clear();
                return evt;
            }

            // Comment / heartbeat.
            if (line[0] == ':')
                return null;

            int colon = line.IndexOf(':');
            string field, value;
            if (colon < 0)
            {
                field = line;
                value = string.Empty;
            }
            else
            {
                field = line.Substring(0, colon);
                value = line.Substring(colon + 1);
                // Per the SSE spec a single leading space after the colon is stripped.
                if (value.Length > 0 && value[0] == ' ')
                    value = value.Substring(1);
            }

            if (field == "event")
            {
                _eventName = value;
            }
            else if (field == "data")
            {
                if (_data.Length > 0) _data.Append('\n');
                _data.Append(value);
            }
            // "id" and "retry" carry no meaning for the Messages API stream.

            return null;
        }

        /// <summary>
        /// Convenience for tests and for buffered input: split a whole payload into events.
        /// </summary>
        public static List<SseEvent> ParseAll(string payload)
        {
            var results = new List<SseEvent>();
            if (payload == null) return results;

            var parser = new SseParser();
            foreach (var line in payload.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var evt = parser.PushLine(line);
                if (evt != null) results.Add(evt);
            }
            // Flush a trailing event that was not terminated by a blank line.
            var tail = parser.PushLine(string.Empty);
            if (tail != null) results.Add(tail);
            return results;
        }
    }
}
