using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// A session serialized for storage in the .3dm. Deliberately a separate shape from
    /// <see cref="AgentSession"/> — persistence needs a stable format that survives the
    /// session class changing, and it must not carry live Rhino state.
    /// </summary>
    public sealed class ConversationSnapshot
    {
        /// <summary>Bumped when the stored shape changes so old documents can be rejected cleanly.</summary>
        public const int CurrentVersion = 1;

        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("createdUtc")]
        public string CreatedUtc { get; set; }

        [JsonPropertyName("savedUtc")]
        public string SavedUtc { get; set; }

        [JsonPropertyName("model")]
        public string Model { get; set; }

        [JsonPropertyName("undoRecordCount")]
        public int UndoRecordCount { get; set; }

        [JsonPropertyName("messages")]
        public List<AgentMessage> Messages { get; set; } = new List<AgentMessage>();

        [JsonIgnore]
        public DateTime SavedAt =>
            DateTime.TryParse(SavedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTime.MinValue;

        /// <summary>Turns the user actually typed, ignoring the loop's tool-result traffic.</summary>
        [JsonIgnore]
        public int UserTurnCount =>
            Messages.Count(m => m.Role == "user" &&
                                m.Content.Any(b => b is TextBlock t && !string.IsNullOrWhiteSpace(t.Text)) &&
                                !m.Content.Any(b => b is ToolResultBlock));

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// Serialize for storage. Images are replaced with a placeholder: a handful of
        /// screenshots is megabytes of base64, which would bloat the .3dm far past anything a
        /// resumed conversation gains from them. Everything else round-trips exactly.
        /// </summary>
        public string ToJson()
        {
            var stripped = new ConversationSnapshot
            {
                Version = Version,
                SessionId = SessionId,
                DisplayName = DisplayName,
                CreatedUtc = CreatedUtc,
                SavedUtc = SavedUtc,
                Model = Model,
                UndoRecordCount = UndoRecordCount,
                Messages = Messages.Select(StripImages).ToList()
            };

            return JsonSerializer.Serialize(stripped, Options);
        }

        private static AgentMessage StripImages(AgentMessage message)
        {
            var copy = new AgentMessage { Role = message.Role };

            foreach (var block in message.Content)
            {
                if (block is ToolResultBlock result)
                {
                    var replacement = new ToolResultBlock
                    {
                        ToolUseId = result.ToolUseId,
                        IsError = result.IsError
                    };

                    int images = 0;
                    foreach (var child in result.Content)
                    {
                        if (child is ImageBlock) { images++; continue; }
                        replacement.Content.Add(child);
                    }

                    if (images > 0)
                    {
                        replacement.Content.Add(new TextBlock(
                            "[" + images + " image(s) were not saved with this conversation. " +
                            "Call capture_views again if you need to look at the model.]"));
                    }

                    // A tool_result must never be empty.
                    if (replacement.Content.Count == 0)
                        replacement.Content.Add(new TextBlock("[result not preserved]"));

                    copy.Content.Add(replacement);
                }
                else if (block is ImageBlock)
                {
                    copy.Content.Add(new TextBlock("[image not saved with this conversation]"));
                }
                else
                {
                    copy.Content.Add(block);
                }
            }

            return copy;
        }

        /// <summary>Returns null rather than throwing — a corrupt blob must not stop the document opening.</summary>
        public static ConversationSnapshot FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                var snapshot = JsonSerializer.Deserialize<ConversationSnapshot>(json, Options);
                if (snapshot == null) return null;
                if (snapshot.Version > CurrentVersion) return null;   // written by a newer build
                snapshot.Messages = snapshot.Messages ?? new List<AgentMessage>();
                return snapshot;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Split into chunks small enough for the document string table, which is not designed
        /// for megabyte values. Reassembled by <see cref="Join"/>.
        /// </summary>
        public static List<string> Chunk(string payload, int chunkSize = 30000)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(payload)) return chunks;
            if (chunkSize < 1024) chunkSize = 1024;

            for (int offset = 0; offset < payload.Length; offset += chunkSize)
            {
                int length = Math.Min(chunkSize, payload.Length - offset);
                chunks.Add(payload.Substring(offset, length));
            }

            return chunks;
        }

        public static string Join(IEnumerable<string> chunks)
        {
            if (chunks == null) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var chunk in chunks) sb.Append(chunk);
            return sb.ToString();
        }
    }
}
