using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

namespace RhinoClaude.Agent
{
    /// <summary>One image produced by a tool, ready to become an <see cref="ImageBlock"/>.</summary>
    public sealed class ToolImage
    {
        public string MediaType { get; set; } = "image/png";
        public string Base64 { get; set; }
        /// <summary>Raw bytes, kept for the sidebar thumbnail and the on-disk screenshot cache.</summary>
        public byte[] Bytes { get; set; }
    }

    /// <summary>
    /// A tool's return value. Per the plan's design contract every tool returns an object
    /// with <c>success</c> and <c>error</c>, never a bare boolean or GUID.
    /// </summary>
    public sealed class ToolResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        /// <summary>Serialized tool-specific payload (a JSON object).</summary>
        public string Json { get; set; } = "{}";
        public List<ToolImage> Images { get; } = new List<ToolImage>();

        public static ToolResult Ok(object payload = null)
        {
            return new ToolResult
            {
                Success = true,
                Json = payload == null ? "{}" : JsonSerializer.Serialize(payload, ToolJson.Options)
            };
        }

        public static ToolResult Fail(string error)
        {
            return new ToolResult { Success = false, Error = error ?? "Unknown error." };
        }

        /// <summary>
        /// Wrap the payload as the content array of a tool_result block. The JSON summary
        /// always leads; images follow so the model reads the metadata before the pixels.
        /// </summary>
        public ToolResultBlock ToBlock(string toolUseId)
        {
            var envelope = new Dictionary<string, object>
            {
                { "success", Success },
                { "error", Error }
            };

            string body;
            try
            {
                using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(Json) ? "{}" : Json))
                {
                    var merged = new Dictionary<string, object>(envelope);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            merged[prop.Name] = new RawJson(prop.Value.GetRawText());
                    }
                    else
                    {
                        merged["result"] = new RawJson(doc.RootElement.GetRawText());
                    }
                    body = ToolJson.Serialize(merged);
                }
            }
            catch (JsonException)
            {
                envelope["result"] = Json;
                body = ToolJson.Serialize(envelope);
            }

            var block = new ToolResultBlock { ToolUseId = toolUseId, IsError = !Success };
            block.Content.Add(new TextBlock(body));
            foreach (var image in Images)
                block.Content.Add(new ImageBlock { MediaType = image.MediaType, Data = image.Base64 });
            return block;
        }
    }

    /// <summary>Marker so already-serialized JSON survives a second serialization pass.</summary>
    public sealed class RawJson
    {
        public string Value { get; }
        public RawJson(string value) { Value = value ?? "null"; }
    }

    public static class ToolJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new RawJsonWriteConverter() }
        };

        public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

        private sealed class RawJsonWriteConverter : System.Text.Json.Serialization.JsonConverter<RawJson>
        {
            public override RawJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                using (var doc = JsonDocument.ParseValue(ref reader))
                    return new RawJson(doc.RootElement.GetRawText());
            }

            public override void Write(Utf8JsonWriter writer, RawJson value, JsonSerializerOptions options)
            {
                try
                {
                    using (var doc = JsonDocument.Parse(value.Value))
                        doc.RootElement.WriteTo(writer);
                }
                catch (JsonException)
                {
                    writer.WriteNullValue();
                }
            }
        }

        /// <summary>Strip control characters and wrap user-authored strings (layer/object names)
        /// so a malicious name can't read as instructions in the tool result. Risk #7 in the plan.</summary>
        public static string Safe(string userText)
        {
            if (string.IsNullOrEmpty(userText)) return userText;
            var sb = new System.Text.StringBuilder(userText.Length);
            foreach (char c in userText)
            {
                if (c == '\t' || c == '\n' || c == ' ' || !char.IsControl(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// A registered tool: what Claude sees (name / description / schema) plus the delegate
    /// that runs it. The handler is always invoked on Rhino's UI thread by the dispatcher.
    /// </summary>
    public sealed class ToolDefinition
    {
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>JSON Schema for the tool's input, as a JSON string.</summary>
        public string InputSchemaJson { get; set; } = "{\"type\":\"object\",\"properties\":{}}";
        /// <summary>True for read-only tools — the dispatcher skips undo bookkeeping for these.</summary>
        public bool IsReadOnly { get; set; }
        /// <summary>Set on <c>signal_done</c>; ends the turn once its result is returned.</summary>
        public bool TerminatesTurn { get; set; }

        public Func<JsonElement, CancellationToken, ToolResult> Handler { get; set; }

        public ToolSpec ToSpec() => new ToolSpec
        {
            Name = Name,
            Description = Description,
            InputSchemaJson = InputSchemaJson
        };
    }
}
