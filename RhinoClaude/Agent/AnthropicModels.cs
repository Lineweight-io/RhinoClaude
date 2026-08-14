using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoClaude.Agent
{
    // ── Content blocks ────────────────────────────────────────────────
    //
    // The Messages API content array is polymorphic. System.Text.Json's
    // built-in polymorphism attributes are .NET 7+ only, and this assembly
    // also targets net48, so the discriminator is handled by an explicit
    // converter. Keeping it hand-rolled also means the round-trip is
    // unit-testable without RhinoCommon on the test host.

    /// <summary>
    /// A prompt-cache breakpoint. Everything rendered before the block that carries one is
    /// cached; later requests that share those exact bytes read it back at a tenth of the
    /// input rate. Only the 5-minute (default) TTL is used — see
    /// <see cref="CostBudget.CacheWriteMultiplier"/>, which prices the write at 1.25x.
    /// </summary>
    public sealed class CacheControl
    {
        /// <summary>The only value the plugin sets. Shared because the object is immutable.</summary>
        public static readonly CacheControl Ephemeral = new CacheControl();

        [JsonPropertyName("type")]
        public string Type { get; set; } = "ephemeral";
    }

    [JsonConverter(typeof(ContentBlockConverter))]
    public abstract class ContentBlock
    {
        public abstract string Type { get; }

        /// <summary>
        /// Set on the last block of a message to place a cache breakpoint there. Written by
        /// <see cref="ContentBlockConverter"/>; never read back, because
        /// <see cref="PromptCache"/> recomputes the placement on every request.
        /// </summary>
        [JsonIgnore]
        public CacheControl CacheControl { get; set; }
    }

    /// <summary>Plain text from the assistant, or text sent to it.</summary>
    public sealed class TextBlock : ContentBlock
    {
        public override string Type => "text";
        public string Text { get; set; } = string.Empty;

        public TextBlock() { }
        public TextBlock(string text) { Text = text ?? string.Empty; }
    }

    /// <summary>
    /// A tool invocation requested by the model. <see cref="InputJson"/> holds the
    /// raw JSON object so partial <c>input_json_delta</c> chunks can be accumulated
    /// as a string during streaming and parsed once at the end.
    /// </summary>
    public sealed class ToolUseBlock : ContentBlock
    {
        public override string Type => "tool_use";
        public string Id { get; set; }
        public string Name { get; set; }
        public string InputJson { get; set; } = "{}";

        /// <summary>Parsed copy of <see cref="InputJson"/>. Returns an empty object on malformed input.</summary>
        public JsonElement ParseInput()
        {
            string raw = string.IsNullOrWhiteSpace(InputJson) ? "{}" : InputJson;
            try
            {
                using (var doc = JsonDocument.Parse(raw))
                    return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                using (var doc = JsonDocument.Parse("{}"))
                    return doc.RootElement.Clone();
            }
        }
    }

    /// <summary>
    /// The result we hand back for a <see cref="ToolUseBlock"/>. Content is an array so a
    /// single result can mix a JSON text summary with image blocks (see ViewCaptureService).
    /// </summary>
    public sealed class ToolResultBlock : ContentBlock
    {
        public override string Type => "tool_result";
        public string ToolUseId { get; set; }
        public List<ContentBlock> Content { get; set; } = new List<ContentBlock>();
        public bool IsError { get; set; }
    }

    /// <summary>
    /// A thinking block from a model with adaptive thinking on.
    ///
    /// The signature matters more than the text: with the default
    /// <c>display: "omitted"</c> the text is empty, but the block still has to be replayed
    /// byte-identical on the next request or the API rejects the turn. Both fields are
    /// accumulated from <c>thinking_delta</c> / <c>signature_delta</c> events.
    /// </summary>
    public sealed class ThinkingBlock : ContentBlock
    {
        public override string Type => "thinking";
        public string Thinking { get; set; } = string.Empty;
        public string Signature { get; set; }
    }

    /// <summary>Encrypted thinking. Opaque — replayed verbatim, never inspected.</summary>
    public sealed class RedactedThinkingBlock : ContentBlock
    {
        public override string Type => "redacted_thinking";
        public string Data { get; set; }
    }

    /// <summary>Base64 image, used inside tool results for view captures.</summary>
    public sealed class ImageBlock : ContentBlock
    {
        public override string Type => "image";
        public string MediaType { get; set; } = "image/png";
        public string Data { get; set; } = string.Empty;
    }

    /// <summary>Any block type we do not model; preserved verbatim so history stays valid.</summary>
    public sealed class UnknownBlock : ContentBlock
    {
        public override string Type => _type;
        private readonly string _type;
        public string RawJson { get; set; } = "{}";

        public UnknownBlock(string type, string rawJson)
        {
            _type = type ?? "unknown";
            RawJson = rawJson ?? "{}";
        }
    }

    public sealed class ContentBlockConverter : JsonConverter<ContentBlock>
    {
        public override ContentBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using (var doc = JsonDocument.ParseValue(ref reader))
            {
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                switch (type)
                {
                    case "text":
                        return new TextBlock(root.TryGetProperty("text", out var tx) ? tx.GetString() : string.Empty);

                    case "tool_use":
                        return new ToolUseBlock
                        {
                            Id = root.TryGetProperty("id", out var id) ? id.GetString() : null,
                            Name = root.TryGetProperty("name", out var nm) ? nm.GetString() : null,
                            InputJson = root.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}"
                        };

                    case "tool_result":
                        {
                            var block = new ToolResultBlock
                            {
                                ToolUseId = root.TryGetProperty("tool_use_id", out var tid) ? tid.GetString() : null,
                                IsError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True
                            };
                            if (root.TryGetProperty("content", out var c))
                            {
                                if (c.ValueKind == JsonValueKind.String)
                                {
                                    block.Content.Add(new TextBlock(c.GetString()));
                                }
                                else if (c.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in c.EnumerateArray())
                                        block.Content.Add(ReadElement(item));
                                }
                            }
                            return block;
                        }

                    case "thinking":
                        return new ThinkingBlock
                        {
                            Thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() : string.Empty,
                            Signature = root.TryGetProperty("signature", out var sg) ? sg.GetString() : null
                        };

                    case "redacted_thinking":
                        return new RedactedThinkingBlock
                        {
                            Data = root.TryGetProperty("data", out var rd) ? rd.GetString() : null
                        };

                    case "image":
                        {
                            string media = "image/png";
                            string data = string.Empty;
                            if (root.TryGetProperty("source", out var src))
                            {
                                if (src.TryGetProperty("media_type", out var mt)) media = mt.GetString();
                                if (src.TryGetProperty("data", out var d)) data = d.GetString();
                            }
                            return new ImageBlock { MediaType = media, Data = data };
                        }

                    default:
                        return new UnknownBlock(type, root.GetRawText());
                }
            }
        }

        private static ContentBlock ReadElement(JsonElement element)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(element.GetRawText());
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            return new ContentBlockConverter().Read(ref reader, typeof(ContentBlock), null);
        }

        public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
        {
            // Unknown blocks round-trip as their original JSON rather than a
            // reconstructed object, so nothing the API sent is lost.
            if (value is UnknownBlock unknown)
            {
                WriteRawObject(writer, unknown.RawJson, unknown.CacheControl);
                return;
            }

            writer.WriteStartObject();

            if (value is TextBlock text)
            {
                writer.WriteString("type", "text");
                writer.WriteString("text", text.Text ?? string.Empty);
            }
            else if (value is ToolUseBlock toolUse)
            {
                writer.WriteString("type", "tool_use");
                writer.WriteString("id", toolUse.Id);
                writer.WriteString("name", toolUse.Name);
                writer.WritePropertyName("input");
                WriteRawObject(writer, toolUse.InputJson);
            }
            else if (value is ToolResultBlock toolResult)
            {
                writer.WriteString("type", "tool_result");
                writer.WriteString("tool_use_id", toolResult.ToolUseId);
                if (toolResult.IsError)
                    writer.WriteBoolean("is_error", true);
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                foreach (var child in toolResult.Content)
                    Write(writer, child, options);
                writer.WriteEndArray();
            }
            else if (value is ThinkingBlock thinking)
            {
                writer.WriteString("type", "thinking");
                writer.WriteString("thinking", thinking.Thinking ?? string.Empty);
                // The signature is what the API validates; omit rather than emit null.
                if (!string.IsNullOrEmpty(thinking.Signature))
                    writer.WriteString("signature", thinking.Signature);
            }
            else if (value is RedactedThinkingBlock redacted)
            {
                writer.WriteString("type", "redacted_thinking");
                writer.WriteString("data", redacted.Data ?? string.Empty);
            }
            else if (value is ImageBlock image)
            {
                writer.WriteString("type", "image");
                writer.WritePropertyName("source");
                writer.WriteStartObject();
                writer.WriteString("type", "base64");
                writer.WriteString("media_type", image.MediaType ?? "image/png");
                writer.WriteString("data", image.Data ?? string.Empty);
                writer.WriteEndObject();
            }

            WriteCacheControl(writer, value.CacheControl);
            writer.WriteEndObject();
        }

        internal static void WriteCacheControl(Utf8JsonWriter writer, CacheControl cacheControl)
        {
            if (cacheControl == null) return;
            writer.WritePropertyName("cache_control");
            writer.WriteStartObject();
            writer.WriteString("type", cacheControl.Type ?? "ephemeral");
            writer.WriteEndObject();
        }

        private static void WriteRawObject(Utf8JsonWriter writer, string rawJson, CacheControl cacheControl = null)
        {
            string raw = string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson;
            try
            {
                using (var doc = JsonDocument.Parse(raw))
                {
                    // A breakpoint on an unknown block has to be merged into the preserved
                    // JSON rather than appended after it, so the object stays well-formed.
                    if (cacheControl == null || doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        doc.RootElement.WriteTo(writer);
                        return;
                    }

                    writer.WriteStartObject();
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(property.Name, "cache_control", StringComparison.Ordinal)) continue;
                        property.WriteTo(writer);
                    }
                    WriteCacheControl(writer, cacheControl);
                    writer.WriteEndObject();
                }
            }
            catch (JsonException)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
        }
    }

    // ── Messages ──────────────────────────────────────────────────────

    public sealed class AgentMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; }

        [JsonPropertyName("content")]
        public List<ContentBlock> Content { get; set; } = new List<ContentBlock>();

        public AgentMessage() { }

        public AgentMessage(string role, params ContentBlock[] blocks)
        {
            Role = role;
            if (blocks != null) Content.AddRange(blocks);
        }

        public static AgentMessage User(string text) =>
            new AgentMessage("user", new TextBlock(text));

        /// <summary>Concatenated text of all text blocks — for panel display and logging.</summary>
        public string TextContent()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var block in Content)
                if (block is TextBlock t) sb.Append(t.Text);
            return sb.ToString();
        }
    }

    // ── Usage / request ───────────────────────────────────────────────

    public sealed class TokenUsage
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }

        [JsonPropertyName("cache_creation_input_tokens")]
        public int CacheCreationInputTokens { get; set; }

        [JsonPropertyName("cache_read_input_tokens")]
        public int CacheReadInputTokens { get; set; }

        public void Add(TokenUsage other)
        {
            if (other == null) return;
            InputTokens += other.InputTokens;
            OutputTokens += other.OutputTokens;
            CacheCreationInputTokens += other.CacheCreationInputTokens;
            CacheReadInputTokens += other.CacheReadInputTokens;
        }

        public TokenUsage Clone() => new TokenUsage
        {
            InputTokens = InputTokens,
            OutputTokens = OutputTokens,
            CacheCreationInputTokens = CacheCreationInputTokens,
            CacheReadInputTokens = CacheReadInputTokens
        };
    }

    /// <summary>A tool as sent in the request's <c>tools</c> array.</summary>
    public sealed class ToolSpec
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("input_schema")]
        [JsonConverter(typeof(RawJsonConverter))]
        public string InputSchemaJson { get; set; } = "{\"type\":\"object\",\"properties\":{}}";

        /// <summary>
        /// Set on the last tool only. The tools array renders first in the prompt, so a
        /// breakpoint here caches every schema in one entry — see <see cref="PromptCache"/>.
        /// </summary>
        [JsonPropertyName("cache_control")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CacheControl CacheControl { get; set; }
    }

    /// <summary>
    /// The <c>system</c> field's two legal shapes. A plain string cannot carry
    /// <c>cache_control</c>, so caching the system prompt means emitting the one-element
    /// text-block array instead. Same rendered prompt either way — only the wire shape differs.
    /// </summary>
    [JsonConverter(typeof(SystemFieldConverter))]
    public sealed class SystemField
    {
        public SystemField(string text, bool cached)
        {
            Text = text ?? string.Empty;
            Cached = cached;
        }

        public string Text { get; }
        public bool Cached { get; }
    }

    public sealed class SystemFieldConverter : JsonConverter<SystemField>
    {
        public override SystemField Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using (var doc = JsonDocument.ParseValue(ref reader))
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                    return new SystemField(root.GetString(), false);

                if (root.ValueKind == JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    bool cached = false;
                    foreach (var block in root.EnumerateArray())
                    {
                        if (block.TryGetProperty("text", out var text)) sb.Append(text.GetString());
                        if (block.TryGetProperty("cache_control", out _)) cached = true;
                    }
                    return new SystemField(sb.ToString(), cached);
                }

                return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, SystemField value, JsonSerializerOptions options)
        {
            if (value == null) { writer.WriteNullValue(); return; }

            if (!value.Cached)
            {
                writer.WriteStringValue(value.Text);
                return;
            }

            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", value.Text);
            ContentBlockConverter.WriteCacheControl(writer, CacheControl.Ephemeral);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }
    }

    /// <summary>Writes a pre-serialized JSON string through verbatim instead of quoting it.</summary>
    public sealed class RawJsonConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using (var doc = JsonDocument.ParseValue(ref reader))
                return doc.RootElement.GetRawText();
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            string raw = string.IsNullOrWhiteSpace(value) ? "{}" : value;
            try
            {
                using (var doc = JsonDocument.Parse(raw))
                    doc.RootElement.WriteTo(writer);
            }
            catch (JsonException)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
        }
    }

    /// <summary>
    /// Adaptive thinking. <c>display: "summarized"</c> costs nothing extra — thinking is billed
    /// the same under every setting — and it lets the sidebar show why Claude chose the
    /// dimensions it did instead of a silent pause.
    /// </summary>
    public sealed class ThinkingConfig
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "adaptive";

        [JsonPropertyName("display")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Display { get; set; }
    }

    /// <summary>Structured output — constrains the response to a JSON schema.</summary>
    public sealed class OutputFormat
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "json_schema";

        [JsonPropertyName("schema")]
        [JsonConverter(typeof(RawJsonConverter))]
        public string SchemaJson { get; set; } = "{\"type\":\"object\"}";
    }

    /// <summary>Carries <c>effort</c> and <c>format</c>, both nested here rather than top-level.</summary>
    public sealed class OutputConfig
    {
        [JsonPropertyName("effort")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Effort { get; set; }

        [JsonPropertyName("format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OutputFormat Format { get; set; }
    }

    public sealed class MessagesRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; }

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 16384;

        /// <summary>
        /// The system prompt. Kept as a plain string here; <see cref="SystemPayload"/> decides
        /// which of the field's two wire shapes goes out.
        /// </summary>
        [JsonIgnore]
        public string System { get; set; }

        /// <summary>
        /// Emit <c>system</c> as a text-block array with a cache breakpoint on it. Set by
        /// <see cref="PromptCache.Apply"/>; the plain-string shape is the default because it
        /// is what every non-loop caller (ClaudeTag, the reviewer) wants.
        /// </summary>
        [JsonIgnore]
        public bool CacheSystemPrompt { get; set; }

        [JsonPropertyName("system")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SystemField SystemPayload =>
            string.IsNullOrEmpty(System) ? null : new SystemField(System, CacheSystemPrompt);

        [JsonPropertyName("messages")]
        public List<AgentMessage> Messages { get; set; } = new List<AgentMessage>();

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ToolSpec> Tools { get; set; }

        [JsonPropertyName("stream")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Stream { get; set; }

        /// <summary>Omitted entirely on models that do not accept it — see <see cref="ModelCapabilities"/>.</summary>
        [JsonPropertyName("thinking")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ThinkingConfig Thinking { get; set; }

        [JsonPropertyName("output_config")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OutputConfig OutputConfig { get; set; }

        /// <summary>
        /// Populate <see cref="Thinking"/> and <see cref="OutputConfig"/> for the target model,
        /// leaving them null where the model would reject them.
        /// </summary>
        public void ApplyModelCapabilities(string effort, bool showThinking)
        {
            // A max_tokens carried over from another model is a 400 on every turn, not a
            // shorter answer. Clamped here as well as in the settings dialog so the wire stays
            // valid however the value arrived.
            int ceiling = ModelCapabilities.MaxOutputTokens(Model);
            if (MaxTokens > ceiling) MaxTokens = ceiling;

            if (ModelCapabilities.SupportsAdaptiveThinking(Model))
            {
                Thinking = new ThinkingConfig
                {
                    Type = "adaptive",
                    Display = showThinking ? "summarized" : null
                };
            }

            string clamped = ModelCapabilities.SupportsEffort(Model)
                ? ModelCapabilities.ClampEffort(Model, effort)
                : null;

            OutputConfig = string.IsNullOrEmpty(clamped) ? null : new OutputConfig { Effort = clamped };
        }

        // global:: is required — the System property on this class otherwise shadows the
        // System namespace inside the initializer.
        public static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
    }
}
