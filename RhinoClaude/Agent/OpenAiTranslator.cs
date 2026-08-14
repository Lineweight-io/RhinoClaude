using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Translates between the Anthropic Messages shape the plugin speaks internally and the
    /// OpenAI Chat Completions shape every cheap provider speaks.
    ///
    /// Pure — no HTTP, no RhinoCommon — so both directions are unit-testable. The inbound
    /// direction deliberately produces Anthropic-shaped <see cref="SseEvent"/>s rather than a
    /// parallel event model: <see cref="StreamAccumulator"/> then assembles the message, the
    /// usage, and the tool calls with exactly the code path the Anthropic provider uses.
    ///
    /// What does not survive the round trip, by design:
    ///   • <c>cache_control</c> breakpoints — no OpenAI-compatible provider takes them. These
    ///     providers cache automatically instead, and report the hit in usage.
    ///   • <c>thinking</c> blocks replayed from history, and <c>output_config.effort</c>.
    ///   • Images, on providers whose <see cref="OpenAiQuirks.AcceptsImages"/> is false; they
    ///     become a one-line note so the call succeeds instead of 400-ing.
    /// </summary>
    public static class OpenAiTranslator
    {
        private static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // ── Outgoing: Anthropic request → OpenAI request ──────────────

        /// <summary>
        /// Render <paramref name="request"/> as an OpenAI Chat Completions body.
        /// <c>request.Stream</c> decides whether the streaming fields go out.
        /// </summary>
        public static string BuildRequestJson(MessagesRequest request, OpenAiQuirks quirks)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            quirks = quirks ?? new OpenAiQuirks();

            bool stream = request.Stream == true;

            using (var buffer = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(buffer, WriterOptions))
                {
                    w.WriteStartObject();
                    w.WriteString("model", request.Model ?? string.Empty);

                    // OpenAI renamed the field; everyone else kept max_tokens.
                    if (quirks.UseMaxCompletionTokens)
                        w.WriteNumber("max_completion_tokens", request.MaxTokens);
                    else
                        w.WriteNumber("max_tokens", request.MaxTokens);

                    if (stream)
                    {
                        w.WriteBoolean("stream", true);
                        if (quirks.SupportsStreamUsage)
                        {
                            // Without this a streamed turn reports no usage at all and the cost
                            // meter silently reads zero for the whole session.
                            w.WritePropertyName("stream_options");
                            w.WriteStartObject();
                            w.WriteBoolean("include_usage", true);
                            w.WriteEndObject();
                        }
                    }

                    w.WritePropertyName("messages");
                    w.WriteStartArray();
                    WriteSystemMessage(w, request, quirks);
                    foreach (var message in request.Messages ?? new List<AgentMessage>())
                        WriteMessage(w, message, quirks);
                    w.WriteEndArray();

                    if (request.Tools != null && request.Tools.Count > 0)
                    {
                        w.WritePropertyName("tools");
                        w.WriteStartArray();
                        foreach (var tool in request.Tools)
                        {
                            w.WriteStartObject();
                            w.WriteString("type", "function");
                            w.WritePropertyName("function");
                            w.WriteStartObject();
                            w.WriteString("name", tool.Name ?? string.Empty);
                            if (!string.IsNullOrEmpty(tool.Description))
                                w.WriteString("description", tool.Description);
                            w.WritePropertyName("parameters");
                            WriteRawJson(w, tool.InputSchemaJson);
                            w.WriteEndObject();
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();

                        if (quirks.SendToolChoice)
                            w.WriteString("tool_choice", "auto");
                    }

                    WriteResponseFormat(w, request, quirks);

                    w.WriteEndObject();
                }

                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        /// <summary>
        /// The system prompt, plus — on providers without strict JSON-schema support — the
        /// schema spelled out in prose. DeepSeek's <c>json_object</c> mode additionally
        /// requires the word "json" somewhere in the prompt, which this satisfies.
        /// </summary>
        private static void WriteSystemMessage(Utf8JsonWriter w, MessagesRequest request, OpenAiQuirks quirks)
        {
            string system = request.System ?? string.Empty;

            var schema = request.OutputConfig?.Format;
            if (schema != null && !quirks.SupportsJsonSchema)
            {
                system = (system.Length > 0 ? system + "\n\n" : string.Empty) +
                         "Respond with a single JSON object and nothing else — no prose, no code " +
                         "fence. It must conform to this JSON schema:\n" + schema.SchemaJson;
            }

            if (system.Length == 0) return;

            w.WriteStartObject();
            w.WriteString("role", quirks.SystemRoleName ?? "system");
            w.WriteString("content", system);
            w.WriteEndObject();
        }

        private static void WriteResponseFormat(Utf8JsonWriter w, MessagesRequest request, OpenAiQuirks quirks)
        {
            var format = request.OutputConfig?.Format;
            if (format == null) return;

            if (quirks.SupportsJsonSchema)
            {
                w.WritePropertyName("response_format");
                w.WriteStartObject();
                w.WriteString("type", "json_schema");
                w.WritePropertyName("json_schema");
                w.WriteStartObject();
                w.WriteString("name", "response");
                w.WriteBoolean("strict", false);
                w.WritePropertyName("schema");
                WriteRawJson(w, format.SchemaJson);
                w.WriteEndObject();
                w.WriteEndObject();
                return;
            }

            if (quirks.SupportsJsonObject)
            {
                w.WritePropertyName("response_format");
                w.WriteStartObject();
                w.WriteString("type", "json_object");
                w.WriteEndObject();
            }
        }

        /// <summary>
        /// One Anthropic message can become several OpenAI ones: a user turn carrying tool
        /// results becomes one <c>tool</c> message per result, and anything else in that turn
        /// follows as a <c>user</c> message. Tool messages are emitted first, because OpenAI
        /// requires them to directly follow the assistant message that requested them.
        /// </summary>
        private static void WriteMessage(Utf8JsonWriter w, AgentMessage message, OpenAiQuirks quirks)
        {
            if (message?.Content == null) return;

            if (string.Equals(message.Role, "assistant", StringComparison.Ordinal))
            {
                WriteAssistantMessage(w, message, quirks);
                return;
            }

            var text = new StringBuilder();
            var images = new List<ImageBlock>();
            bool wroteToolMessage = false;

            foreach (var block in message.Content)
            {
                if (block is ToolResultBlock result)
                {
                    WriteToolResultMessage(w, result, quirks, images);
                    wroteToolMessage = true;
                }
                else if (block is TextBlock t)
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(t.Text ?? string.Empty);
                }
                else if (block is ImageBlock image)
                {
                    images.Add(image);
                }
                // thinking / redacted_thinking / unknown blocks never belong on a user turn.
            }

            if (text.Length == 0 && images.Count == 0)
            {
                // A pure tool-result turn is already fully written.
                if (!wroteToolMessage) WriteSimpleMessage(w, "user", "(empty)");
                return;
            }

            WriteUserMessage(w, text.ToString(), images, quirks);
        }

        private static void WriteAssistantMessage(Utf8JsonWriter w, AgentMessage message, OpenAiQuirks quirks)
        {
            var text = new StringBuilder();
            var toolUses = new List<ToolUseBlock>();

            foreach (var block in message.Content)
            {
                if (block is TextBlock t)
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(t.Text ?? string.Empty);
                }
                else if (block is ToolUseBlock tu)
                {
                    toolUses.Add(tu);
                }
                // Thinking blocks are Anthropic-only and carry a signature that means nothing
                // here; replaying them would be noise at best and a 400 at worst.
            }

            if (text.Length == 0 && toolUses.Count == 0) return;

            w.WriteStartObject();
            w.WriteString("role", "assistant");

            if (text.Length > 0) w.WriteString("content", text.ToString());
            else w.WriteNull("content");

            if (toolUses.Count > 0)
            {
                w.WritePropertyName("tool_calls");
                w.WriteStartArray();
                foreach (var toolUse in toolUses)
                {
                    w.WriteStartObject();
                    w.WriteString("id", toolUse.Id ?? string.Empty);
                    w.WriteString("type", "function");
                    w.WritePropertyName("function");
                    w.WriteStartObject();
                    w.WriteString("name", toolUse.Name ?? string.Empty);
                    // arguments is a *string* of JSON on this wire, so the model's own bytes
                    // go across verbatim — no reparse, no reformat, no lost precision.
                    w.WriteString("arguments", string.IsNullOrWhiteSpace(toolUse.InputJson) ? "{}" : toolUse.InputJson);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        /// <summary>
        /// A tool result. The <c>tool</c> role takes a plain string, so images inside a result
        /// are pulled out into <paramref name="images"/> and re-attached to the user message
        /// that follows.
        /// </summary>
        private static void WriteToolResultMessage(
            Utf8JsonWriter w, ToolResultBlock result, OpenAiQuirks quirks, List<ImageBlock> images)
        {
            var text = new StringBuilder();
            int imageCount = 0;

            foreach (var block in result.Content ?? new List<ContentBlock>())
            {
                if (block is TextBlock t)
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(t.Text ?? string.Empty);
                }
                else if (block is ImageBlock image)
                {
                    imageCount++;
                    if (quirks.AcceptsImages) images.Add(image);
                }
            }

            if (imageCount > 0)
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(quirks.AcceptsImages
                    ? "(" + imageCount + " image(s) attached in the following message.)"
                    : "(" + imageCount + " image(s) omitted — this provider does not accept images.)");
            }

            if (text.Length == 0) text.Append("(no output)");

            // There is no is_error flag on this wire; say so in the text instead, which is what
            // the model reads either way.
            string content = result.IsError ? "ERROR: " + text : text.ToString();

            w.WriteStartObject();
            w.WriteString("role", "tool");
            w.WriteString("tool_call_id", result.ToolUseId ?? string.Empty);
            w.WriteString("content", content);
            w.WriteEndObject();
        }

        private static void WriteUserMessage(
            Utf8JsonWriter w, string text, List<ImageBlock> images, OpenAiQuirks quirks)
        {
            bool sendImages = quirks.AcceptsImages && images.Count > 0;

            if (!sendImages)
            {
                if (images.Count > 0)
                {
                    text = (text.Length > 0 ? text + "\n" : string.Empty) +
                           "(" + images.Count + " image(s) omitted — this provider does not accept images.)";
                }
                WriteSimpleMessage(w, "user", text.Length > 0 ? text : "(empty)");
                return;
            }

            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WritePropertyName("content");
            w.WriteStartArray();

            if (text.Length > 0)
            {
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", text);
                w.WriteEndObject();
            }

            foreach (var image in images)
            {
                w.WriteStartObject();
                w.WriteString("type", "image_url");
                w.WritePropertyName("image_url");
                w.WriteStartObject();
                w.WriteString("url", "data:" + (image.MediaType ?? "image/png") + ";base64," + (image.Data ?? string.Empty));
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        private static void WriteSimpleMessage(Utf8JsonWriter w, string role, string content)
        {
            w.WriteStartObject();
            w.WriteString("role", role);
            w.WriteString("content", content ?? string.Empty);
            w.WriteEndObject();
        }

        private static void WriteRawJson(Utf8JsonWriter w, string rawJson)
        {
            string raw = string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson;
            try
            {
                using (var doc = JsonDocument.Parse(raw))
                    doc.RootElement.WriteTo(w);
            }
            catch (JsonException)
            {
                w.WriteStartObject();
                w.WriteEndObject();
            }
        }

        // ── Incoming: OpenAI response → Anthropic message ─────────────

        /// <summary>
        /// Parse a non-streaming Chat Completions response into an assistant message, filling
        /// <paramref name="usageSink"/> if one was supplied.
        /// </summary>
        public static AgentMessage ParseResponse(string responseBody, TokenUsage usageSink = null)
        {
            var message = new AgentMessage { Role = "assistant" };
            if (string.IsNullOrWhiteSpace(responseBody)) return message;

            using (var doc = JsonDocument.Parse(responseBody))
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("message", out var m))
                    {
                        if (m.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.String)
                        {
                            string text = content.GetString();
                            if (!string.IsNullOrEmpty(text)) message.Content.Add(new TextBlock(text));
                        }

                        if (m.TryGetProperty("tool_calls", out var toolCalls) &&
                            toolCalls.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var call in toolCalls.EnumerateArray())
                            {
                                string id = call.TryGetProperty("id", out var cid) ? cid.GetString() : null;
                                string name = null, arguments = null;
                                if (call.TryGetProperty("function", out var fn))
                                {
                                    if (fn.TryGetProperty("name", out var fname)) name = fname.GetString();
                                    if (fn.TryGetProperty("arguments", out var fargs)) arguments = fargs.GetString();
                                }
                                message.Content.Add(new ToolUseBlock
                                {
                                    Id = id,
                                    Name = name,
                                    InputJson = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments
                                });
                            }
                        }
                    }
                }

                if (usageSink != null && root.TryGetProperty("usage", out var usage))
                {
                    var parsed = ReadUsage(usage);
                    usageSink.InputTokens += parsed.InputTokens;
                    usageSink.OutputTokens += parsed.OutputTokens;
                    usageSink.CacheReadInputTokens += parsed.CacheReadInputTokens;
                    usageSink.CacheCreationInputTokens += parsed.CacheCreationInputTokens;
                }
            }

            return message;
        }

        /// <summary>
        /// Normalise an OpenAI-shaped usage object.
        ///
        /// The cached-prompt count is reported three different ways in the wild:
        /// <c>prompt_tokens_details.cached_tokens</c> (OpenAI, Moonshot, Model Studio) and
        /// <c>prompt_cache_hit_tokens</c> / <c>prompt_cache_miss_tokens</c> (DeepSeek). Whichever
        /// arrives, cached tokens land in <c>cache_read_input_tokens</c> and the remainder in
        /// <c>input_tokens</c>, so <see cref="CostBudget"/> prices the two pools separately.
        /// Nothing maps to <c>cache_creation_input_tokens</c>: these providers cache implicitly
        /// and do not bill a write.
        /// </summary>
        public static TokenUsage ReadUsage(JsonElement usage)
        {
            var result = new TokenUsage();
            if (usage.ValueKind != JsonValueKind.Object) return result;

            int prompt = ReadInt(usage, "prompt_tokens");
            result.OutputTokens = ReadInt(usage, "completion_tokens");

            int cached = 0;
            if (usage.TryGetProperty("prompt_tokens_details", out var details) &&
                details.ValueKind == JsonValueKind.Object)
            {
                cached = ReadInt(details, "cached_tokens");
            }

            int hit = ReadInt(usage, "prompt_cache_hit_tokens");
            if (hit > 0) cached = hit;

            if (cached > prompt) cached = prompt;

            result.CacheReadInputTokens = cached;
            result.InputTokens = prompt - cached;
            return result;
        }

        private static int ReadInt(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.Number &&
                   value.TryGetInt32(out int parsed)
                ? parsed
                : 0;
        }

        internal static string Json(Action<Utf8JsonWriter> body)
        {
            using (var buffer = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(buffer, WriterOptions))
                {
                    body(w);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }
    }
}
