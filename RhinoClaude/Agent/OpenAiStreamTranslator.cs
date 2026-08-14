using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Rewrites an OpenAI Chat Completions SSE stream into the Anthropic event shape, so
    /// <see cref="StreamAccumulator"/> assembles the turn with the same code that handles a
    /// real Anthropic stream.
    ///
    /// Stateful and single-use: one instance per streamed response. Feed it every decoded
    /// <see cref="SseEvent"/> in order, then call <see cref="Finish"/> when the HTTP stream
    /// ends — a provider that closes the connection without sending <c>[DONE]</c> still gets a
    /// well-formed <c>message_stop</c> out of it.
    /// </summary>
    public sealed class OpenAiStreamTranslator
    {
        private sealed class ToolState
        {
            public int BlockIndex = -1;
            public string Id;
            public string Name;
            public bool Started;
            public string PendingArguments = string.Empty;
        }

        private readonly Dictionary<int, ToolState> _tools = new Dictionary<int, ToolState>();
        private readonly List<int> _openBlocks = new List<int>();

        private int _nextIndex;
        private int _textIndex = -1;
        private int _thinkingIndex = -1;

        private bool _startedMessage;
        private bool _emittedStopReason;
        private bool _completed;
        private bool _sawToolCall;

        /// <summary>Set when the stream carried a provider error object.</summary>
        public bool SawError { get; private set; }

        /// <summary>Consume one OpenAI SSE event; returns the Anthropic events it becomes.</summary>
        public List<SseEvent> Push(SseEvent input)
        {
            var output = new List<SseEvent>();
            if (input == null || string.IsNullOrWhiteSpace(input.Data)) return output;

            string data = input.Data.Trim();
            if (data == "[DONE]")
            {
                CloseEverything(output, null, null);
                return output;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(data);
            }
            catch (JsonException ex)
            {
                SawError = true;
                output.Add(Event(OpenAiTranslator.Json(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("type", "error");
                    w.WritePropertyName("error");
                    w.WriteStartObject();
                    w.WriteString("type", "malformed_chunk");
                    w.WriteString("message", "The provider sent a chunk that is not JSON: " + ex.Message);
                    w.WriteEndObject();
                    w.WriteEndObject();
                })));
                return output;
            }

            using (doc)
            {
                var root = doc.RootElement;

                // Some gateways deliver failures mid-stream as a data frame rather than a
                // non-200, so this is the only place they would otherwise vanish.
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    SawError = true;
                    string type = error.TryGetProperty("type", out var et) ? et.GetString() : "api_error";
                    string message = error.TryGetProperty("message", out var em) ? em.GetString() : "Unknown provider error.";
                    output.Add(Event(OpenAiTranslator.Json(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "error");
                        w.WritePropertyName("error");
                        w.WriteStartObject();
                        w.WriteString("type", type ?? "api_error");
                        w.WriteString("message", message ?? string.Empty);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    })));
                    return output;
                }

                StartMessage(output);

                string finishReason = null;
                if (root.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];

                    if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                        ReadDelta(output, delta);

                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                        finishReason = fr.GetString();
                }

                string usageJson = null;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    usageJson = UsageJson(usage);

                if (!string.IsNullOrEmpty(finishReason))
                {
                    CloseBlocks(output);
                    EmitMessageDelta(output, MapStopReason(finishReason), usageJson);
                }
                else if (usageJson != null)
                {
                    // include_usage delivers the totals in a final choice-less chunk, after the
                    // one that carried finish_reason.
                    EmitMessageDelta(output, null, usageJson);
                }
            }

            return output;
        }

        /// <summary>Flush anything still open when the HTTP stream ends.</summary>
        public List<SseEvent> Finish()
        {
            var output = new List<SseEvent>();
            CloseEverything(output, null, null);
            return output;
        }

        private void CloseEverything(List<SseEvent> output, string stopReason, string usageJson)
        {
            if (_completed) return;

            StartMessage(output);
            CloseBlocks(output);

            if (!_emittedStopReason)
                EmitMessageDelta(output, stopReason ?? DefaultStopReason(), usageJson);

            output.Add(Event("{\"type\":\"message_stop\"}"));
            _completed = true;
        }

        private string DefaultStopReason() => _sawToolCall ? "tool_use" : "end_turn";

        private void StartMessage(List<SseEvent> output)
        {
            if (_startedMessage) return;
            _startedMessage = true;
            output.Add(Event("{\"type\":\"message_start\",\"message\":{\"role\":\"assistant\",\"content\":[]}}"));
        }

        private void ReadDelta(List<SseEvent> output, JsonElement delta)
        {
            // DeepSeek's reasoning models and several gateways stream chain-of-thought in a
            // sibling field. Surfaced as a thinking block so the sidebar shows it live; it is
            // dropped again on the way back out, since it means nothing to any other provider.
            string reasoning = ReadString(delta, "reasoning_content") ?? ReadString(delta, "reasoning");
            if (!string.IsNullOrEmpty(reasoning))
            {
                if (_thinkingIndex < 0)
                {
                    _thinkingIndex = _nextIndex++;
                    _openBlocks.Add(_thinkingIndex);
                    output.Add(BlockStart(_thinkingIndex, w =>
                    {
                        w.WriteString("type", "thinking");
                        w.WriteString("thinking", string.Empty);
                    }));
                }
                output.Add(BlockDelta(_thinkingIndex, w =>
                {
                    w.WriteString("type", "thinking_delta");
                    w.WriteString("thinking", reasoning);
                }));
            }

            string content = ReadString(delta, "content");
            if (!string.IsNullOrEmpty(content))
            {
                if (_textIndex < 0)
                {
                    _textIndex = _nextIndex++;
                    _openBlocks.Add(_textIndex);
                    output.Add(BlockStart(_textIndex, w =>
                    {
                        w.WriteString("type", "text");
                        w.WriteString("text", string.Empty);
                    }));
                }
                output.Add(BlockDelta(_textIndex, w =>
                {
                    w.WriteString("type", "text_delta");
                    w.WriteString("text", content);
                }));
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                int ordinal = 0;
                foreach (var call in toolCalls.EnumerateArray())
                    ReadToolCall(output, call, ordinal++);
            }
        }

        /// <summary>
        /// One <c>tool_calls[]</c> entry. The name usually arrives in the first fragment for a
        /// slot and the arguments dribble in afterwards, but a provider is free to send the
        /// whole call at once or to withhold the name — so the block start is deferred until
        /// there is a name to put in it, and any arguments that arrive first are buffered.
        /// </summary>
        private void ReadToolCall(List<SseEvent> output, JsonElement call, int ordinal)
        {
            _sawToolCall = true;

            int slot = call.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number
                ? ix.GetInt32()
                : ordinal;

            if (!_tools.TryGetValue(slot, out var state))
            {
                state = new ToolState();
                _tools[slot] = state;
            }

            string id = ReadString(call, "id");
            if (!string.IsNullOrEmpty(id) && string.IsNullOrEmpty(state.Id)) state.Id = id;

            string arguments = null;
            if (call.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                string name = ReadString(fn, "name");
                if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(state.Name)) state.Name = name;
                arguments = ReadString(fn, "arguments");
            }

            if (!state.Started && !string.IsNullOrEmpty(state.Name))
            {
                state.BlockIndex = _nextIndex++;
                state.Started = true;
                _openBlocks.Add(state.BlockIndex);

                var captured = state;
                output.Add(BlockStart(state.BlockIndex, w =>
                {
                    w.WriteString("type", "tool_use");
                    w.WriteString("id", string.IsNullOrEmpty(captured.Id) ? "call_" + captured.BlockIndex : captured.Id);
                    w.WriteString("name", captured.Name);
                }));

                if (captured.PendingArguments.Length > 0)
                {
                    string buffered = captured.PendingArguments;
                    captured.PendingArguments = string.Empty;
                    output.Add(BlockDelta(captured.BlockIndex, w =>
                    {
                        w.WriteString("type", "input_json_delta");
                        w.WriteString("partial_json", buffered);
                    }));
                }
            }

            if (string.IsNullOrEmpty(arguments)) return;

            if (!state.Started)
            {
                state.PendingArguments += arguments;
                return;
            }

            output.Add(BlockDelta(state.BlockIndex, w =>
            {
                w.WriteString("type", "input_json_delta");
                w.WriteString("partial_json", arguments);
            }));
        }

        private void CloseBlocks(List<SseEvent> output)
        {
            // A tool call whose name never arrived has no block; emit it now under a synthetic
            // name rather than losing the call entirely.
            foreach (var pair in _tools)
            {
                var state = pair.Value;
                if (state.Started) continue;

                state.Name = string.IsNullOrEmpty(state.Name) ? "unknown_tool" : state.Name;
                state.BlockIndex = _nextIndex++;
                state.Started = true;
                _openBlocks.Add(state.BlockIndex);

                var captured = state;
                output.Add(BlockStart(captured.BlockIndex, w =>
                {
                    w.WriteString("type", "tool_use");
                    w.WriteString("id", string.IsNullOrEmpty(captured.Id) ? "call_" + captured.BlockIndex : captured.Id);
                    w.WriteString("name", captured.Name);
                }));

                if (captured.PendingArguments.Length > 0)
                {
                    string buffered = captured.PendingArguments;
                    captured.PendingArguments = string.Empty;
                    output.Add(BlockDelta(captured.BlockIndex, w =>
                    {
                        w.WriteString("type", "input_json_delta");
                        w.WriteString("partial_json", buffered);
                    }));
                }
            }

            _openBlocks.Sort();
            foreach (int index in _openBlocks)
            {
                output.Add(Event(OpenAiTranslator.Json(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("type", "content_block_stop");
                    w.WriteNumber("index", index);
                    w.WriteEndObject();
                })));
            }
            _openBlocks.Clear();
        }

        private void EmitMessageDelta(List<SseEvent> output, string stopReason, string usageJson)
        {
            if (!string.IsNullOrEmpty(stopReason)) _emittedStopReason = true;

            output.Add(Event(OpenAiTranslator.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "message_delta");
                w.WritePropertyName("delta");
                w.WriteStartObject();
                if (!string.IsNullOrEmpty(stopReason)) w.WriteString("stop_reason", stopReason);
                w.WriteEndObject();
                if (usageJson != null)
                {
                    w.WritePropertyName("usage");
                    using (var usage = JsonDocument.Parse(usageJson))
                        usage.RootElement.WriteTo(w);
                }
                w.WriteEndObject();
            })));
        }

        private string UsageJson(JsonElement usage)
        {
            var normalized = OpenAiTranslator.ReadUsage(usage);
            return OpenAiTranslator.Json(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("input_tokens", normalized.InputTokens);
                w.WriteNumber("output_tokens", normalized.OutputTokens);
                w.WriteNumber("cache_creation_input_tokens", normalized.CacheCreationInputTokens);
                w.WriteNumber("cache_read_input_tokens", normalized.CacheReadInputTokens);
                w.WriteEndObject();
            });
        }

        /// <summary>
        /// <c>finish_reason</c> → Anthropic's <c>stop_reason</c>. The loop only branches on
        /// <c>tool_use</c> and <c>max_tokens</c>, so everything else lands on <c>end_turn</c>.
        ///
        /// The <c>stop</c> case is deliberately overridden when tool calls went out: DeepSeek
        /// and Model Studio have both been observed closing a tool-calling turn with
        /// <c>finish_reason: "stop"</c>, and taking that literally would end the turn with the
        /// tool calls unanswered.
        /// </summary>
        private string MapStopReason(string finishReason)
        {
            switch ((finishReason ?? string.Empty).ToLowerInvariant())
            {
                case "tool_calls":
                case "function_call":
                    return "tool_use";
                case "length":
                    return "max_tokens";
                case "stop":
                case "":
                    return _sawToolCall ? "tool_use" : "end_turn";
                default:
                    return _sawToolCall ? "tool_use" : "end_turn";
            }
        }

        private static string ReadString(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static SseEvent BlockStart(int index, Action<System.Text.Json.Utf8JsonWriter> block)
        {
            return Event(OpenAiTranslator.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "content_block_start");
                w.WriteNumber("index", index);
                w.WritePropertyName("content_block");
                w.WriteStartObject();
                block(w);
                w.WriteEndObject();
                w.WriteEndObject();
            }));
        }

        private static SseEvent BlockDelta(int index, Action<System.Text.Json.Utf8JsonWriter> delta)
        {
            return Event(OpenAiTranslator.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "content_block_delta");
                w.WriteNumber("index", index);
                w.WritePropertyName("delta");
                w.WriteStartObject();
                delta(w);
                w.WriteEndObject();
                w.WriteEndObject();
            }));
        }

        private static SseEvent Event(string data) => new SseEvent { Data = data };
    }
}
