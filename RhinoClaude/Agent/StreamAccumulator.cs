using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace RhinoClaude.Agent
{
    /// <summary>What the panel is told about, as it happens.</summary>
    public enum StreamEventKind
    {
        MessageStart,
        TextBlockStart,
        TextDelta,
        ThinkingBlockStart,
        ThinkingDelta,
        ToolUseBlockStart,
        ToolInputDelta,
        BlockStop,
        MessageDelta,
        MessageStop,
        Error,
        Ping
    }

    public sealed class StreamNotification
    {
        public StreamEventKind Kind { get; set; }
        public int BlockIndex { get; set; }
        /// <summary>Text delta, tool-input JSON fragment, or error message depending on <see cref="Kind"/>.</summary>
        public string Text { get; set; }
        public string ToolName { get; set; }
        public string ToolUseId { get; set; }
    }

    /// <summary>
    /// Turns the raw SSE event stream into (a) live notifications for the UI and
    /// (b) the assembled assistant message once the turn completes.
    ///
    /// Pure — no HTTP, no RhinoCommon — so the delta-assembly rules are unit-testable.
    /// </summary>
    public sealed class StreamAccumulator
    {
        private readonly Dictionary<int, ContentBlock> _blocks = new Dictionary<int, ContentBlock>();
        private readonly Dictionary<int, StringBuilder> _partialJson = new Dictionary<int, StringBuilder>();
        private readonly Dictionary<int, StringBuilder> _partialText = new Dictionary<int, StringBuilder>();
        private readonly Dictionary<int, StringBuilder> _partialThinking = new Dictionary<int, StringBuilder>();
        private readonly Dictionary<int, StringBuilder> _partialSignature = new Dictionary<int, StringBuilder>();

        /// <summary>Populated as <c>message_delta</c> events arrive.</summary>
        public TokenUsage Usage { get; } = new TokenUsage();

        /// <summary><c>end_turn</c>, <c>tool_use</c>, <c>max_tokens</c>, …</summary>
        public string StopReason { get; private set; }

        /// <summary>Set when the stream carried an <c>error</c> event.</summary>
        public string ErrorMessage { get; private set; }

        public bool Completed { get; private set; }

        /// <summary>
        /// Consume one decoded SSE event. Returns a notification for the UI, or null
        /// when the event carries no user-visible change.
        /// </summary>
        public StreamNotification Consume(SseEvent sse)
        {
            if (sse == null || string.IsNullOrEmpty(sse.Data))
                return null;

            // The terminal sentinel some proxies append.
            if (sse.Data == "[DONE]")
            {
                Completed = true;
                return new StreamNotification { Kind = StreamEventKind.MessageStop };
            }

            JsonElement root;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(sse.Data);
                root = doc.RootElement;
            }
            catch (JsonException ex)
            {
                ErrorMessage = "Malformed stream payload: " + ex.Message;
                return new StreamNotification { Kind = StreamEventKind.Error, Text = ErrorMessage };
            }

            using (doc)
            {
                string type = root.TryGetProperty("type", out var t) ? t.GetString() : sse.EventName;
                int index = root.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number
                    ? ix.GetInt32()
                    : 0;

                switch (type)
                {
                    case "ping":
                        return new StreamNotification { Kind = StreamEventKind.Ping };

                    case "error":
                        {
                            string msg = "Unknown API error.";
                            if (root.TryGetProperty("error", out var err))
                            {
                                string errType = err.TryGetProperty("type", out var et) ? et.GetString() : null;
                                string errMsg = err.TryGetProperty("message", out var em) ? em.GetString() : null;
                                msg = string.IsNullOrEmpty(errType) ? errMsg : errType + ": " + errMsg;
                            }
                            ErrorMessage = msg;
                            return new StreamNotification { Kind = StreamEventKind.Error, Text = msg };
                        }

                    case "message_start":
                        {
                            if (root.TryGetProperty("message", out var m) &&
                                m.TryGetProperty("usage", out var u))
                            {
                                Usage.Add(ReadUsage(u));
                            }
                            return new StreamNotification { Kind = StreamEventKind.MessageStart };
                        }

                    case "content_block_start":
                        {
                            if (!root.TryGetProperty("content_block", out var cb))
                                return null;

                            string blockType = cb.TryGetProperty("type", out var bt) ? bt.GetString() : null;

                            if (blockType == "text")
                            {
                                _blocks[index] = new TextBlock(cb.TryGetProperty("text", out var tx) ? tx.GetString() : string.Empty);
                                _partialText[index] = new StringBuilder(((TextBlock)_blocks[index]).Text ?? string.Empty);
                                return new StreamNotification { Kind = StreamEventKind.TextBlockStart, BlockIndex = index };
                            }

                            if (blockType == "thinking")
                            {
                                var block = new ThinkingBlock
                                {
                                    Thinking = cb.TryGetProperty("thinking", out var th) ? th.GetString() : string.Empty,
                                    Signature = cb.TryGetProperty("signature", out var sg) ? sg.GetString() : null
                                };
                                _blocks[index] = block;
                                _partialThinking[index] = new StringBuilder(block.Thinking ?? string.Empty);
                                _partialSignature[index] = new StringBuilder(block.Signature ?? string.Empty);
                                return new StreamNotification { Kind = StreamEventKind.ThinkingBlockStart, BlockIndex = index };
                            }

                            if (blockType == "redacted_thinking")
                            {
                                _blocks[index] = new RedactedThinkingBlock
                                {
                                    Data = cb.TryGetProperty("data", out var rd) ? rd.GetString() : null
                                };
                                return null;
                            }

                            if (blockType == "tool_use")
                            {
                                var block = new ToolUseBlock
                                {
                                    Id = cb.TryGetProperty("id", out var id) ? id.GetString() : null,
                                    Name = cb.TryGetProperty("name", out var nm) ? nm.GetString() : null,
                                    InputJson = "{}"
                                };
                                _blocks[index] = block;
                                _partialJson[index] = new StringBuilder();
                                return new StreamNotification
                                {
                                    Kind = StreamEventKind.ToolUseBlockStart,
                                    BlockIndex = index,
                                    ToolName = block.Name,
                                    ToolUseId = block.Id
                                };
                            }

                            // Anything else (thinking, server tool use, …) is preserved verbatim.
                            _blocks[index] = new UnknownBlock(blockType, cb.GetRawText());
                            return null;
                        }

                    case "content_block_delta":
                        {
                            if (!root.TryGetProperty("delta", out var delta))
                                return null;

                            string deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;

                            if (deltaType == "text_delta")
                            {
                                string chunk = delta.TryGetProperty("text", out var dtx) ? dtx.GetString() : string.Empty;
                                if (!_partialText.TryGetValue(index, out var sb))
                                {
                                    sb = new StringBuilder();
                                    _partialText[index] = sb;
                                    if (!_blocks.ContainsKey(index)) _blocks[index] = new TextBlock();
                                }
                                sb.Append(chunk);
                                return new StreamNotification
                                {
                                    Kind = StreamEventKind.TextDelta,
                                    BlockIndex = index,
                                    Text = chunk
                                };
                            }

                            if (deltaType == "thinking_delta")
                            {
                                string chunk = delta.TryGetProperty("thinking", out var dth) ? dth.GetString() : string.Empty;
                                if (!_partialThinking.TryGetValue(index, out var sb))
                                {
                                    sb = new StringBuilder();
                                    _partialThinking[index] = sb;
                                    if (!_blocks.ContainsKey(index)) _blocks[index] = new ThinkingBlock();
                                }
                                sb.Append(chunk);
                                return new StreamNotification
                                {
                                    Kind = StreamEventKind.ThinkingDelta,
                                    BlockIndex = index,
                                    Text = chunk
                                };
                            }

                            if (deltaType == "signature_delta")
                            {
                                // Never surfaced to the UI — it exists only so the block can be
                                // replayed unchanged on the next request.
                                string chunk = delta.TryGetProperty("signature", out var dsg) ? dsg.GetString() : string.Empty;
                                if (!_partialSignature.TryGetValue(index, out var sb))
                                {
                                    sb = new StringBuilder();
                                    _partialSignature[index] = sb;
                                    if (!_blocks.ContainsKey(index)) _blocks[index] = new ThinkingBlock();
                                }
                                sb.Append(chunk);
                                return null;
                            }

                            if (deltaType == "input_json_delta")
                            {
                                string chunk = delta.TryGetProperty("partial_json", out var pj) ? pj.GetString() : string.Empty;
                                if (!_partialJson.TryGetValue(index, out var sb))
                                {
                                    sb = new StringBuilder();
                                    _partialJson[index] = sb;
                                }
                                sb.Append(chunk);
                                return new StreamNotification
                                {
                                    Kind = StreamEventKind.ToolInputDelta,
                                    BlockIndex = index,
                                    Text = chunk
                                };
                            }

                            return null;
                        }

                    case "content_block_stop":
                        {
                            FinalizeBlock(index);
                            return new StreamNotification { Kind = StreamEventKind.BlockStop, BlockIndex = index };
                        }

                    case "message_delta":
                        {
                            if (root.TryGetProperty("delta", out var d) &&
                                d.TryGetProperty("stop_reason", out var sr) &&
                                sr.ValueKind == JsonValueKind.String)
                            {
                                StopReason = sr.GetString();
                            }
                            if (root.TryGetProperty("usage", out var u2))
                                Usage.Add(ReadUsage(u2));

                            return new StreamNotification { Kind = StreamEventKind.MessageDelta };
                        }

                    case "message_stop":
                        Completed = true;
                        return new StreamNotification { Kind = StreamEventKind.MessageStop };

                    default:
                        return null;
                }
            }
        }

        private void FinalizeBlock(int index)
        {
            if (!_blocks.TryGetValue(index, out var block))
                return;

            if (block is TextBlock text && _partialText.TryGetValue(index, out var textSb))
                text.Text = textSb.ToString();

            if (block is ThinkingBlock thinking)
            {
                if (_partialThinking.TryGetValue(index, out var thinkingSb))
                    thinking.Thinking = thinkingSb.ToString();
                if (_partialSignature.TryGetValue(index, out var signatureSb))
                {
                    string signature = signatureSb.ToString();
                    thinking.Signature = signature.Length == 0 ? null : signature;
                }
            }

            if (block is ToolUseBlock toolUse && _partialJson.TryGetValue(index, out var jsonSb))
            {
                string raw = jsonSb.ToString();
                // An empty-object tool input arrives as zero deltas, not as "{}".
                toolUse.InputJson = string.IsNullOrWhiteSpace(raw) ? "{}" : raw;
            }
        }

        private static TokenUsage ReadUsage(JsonElement u)
        {
            return new TokenUsage
            {
                InputTokens = ReadInt(u, "input_tokens"),
                OutputTokens = ReadInt(u, "output_tokens"),
                CacheCreationInputTokens = ReadInt(u, "cache_creation_input_tokens"),
                CacheReadInputTokens = ReadInt(u, "cache_read_input_tokens")
            };
        }

        private static int ReadInt(JsonElement e, string name)
        {
            return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        }

        /// <summary>The assembled assistant turn, blocks in wire order.</summary>
        public AgentMessage BuildMessage()
        {
            // content_block_stop is normally guaranteed, but a cancelled or truncated
            // stream can leave a block open — flush every block before assembling.
            foreach (var index in new List<int>(_blocks.Keys))
                FinalizeBlock(index);

            var message = new AgentMessage { Role = "assistant" };
            var indices = new List<int>(_blocks.Keys);
            indices.Sort();
            foreach (var i in indices)
            {
                var block = _blocks[i];
                // Drop empty trailing text blocks; the API rejects a message whose
                // only content is an empty string.
                if (block is TextBlock t && string.IsNullOrEmpty(t.Text))
                    continue;
                message.Content.Add(block);
            }
            return message;
        }

        /// <summary>Tool calls the model requested this turn, in wire order.</summary>
        public List<ToolUseBlock> ToolUses()
        {
            var result = new List<ToolUseBlock>();
            var indices = new List<int>(_blocks.Keys);
            indices.Sort();
            foreach (var i in indices)
                if (_blocks[i] is ToolUseBlock tu) result.Add(tu);
            return result;
        }
    }
}
