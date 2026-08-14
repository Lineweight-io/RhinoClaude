using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The wire format is hand-rolled (System.Text.Json polymorphism attributes are .NET 7+
    /// and this ships to net48 too), so round-tripping every block type is the guard that
    /// conversation history stays replayable.
    /// </summary>
    public class ContentBlockRoundTripTests
    {
        private static string Write(ContentBlock block) =>
            JsonSerializer.Serialize(block, MessagesRequest.SerializerOptions);

        private static ContentBlock Read(string json) =>
            JsonSerializer.Deserialize<ContentBlock>(json, MessagesRequest.SerializerOptions);

        [Fact]
        public void TextBlockRoundTrips()
        {
            var original = new TextBlock("hello \"world\"\nsecond line");
            var restored = Assert.IsType<TextBlock>(Read(Write(original)));
            Assert.Equal(original.Text, restored.Text);
        }

        [Fact]
        public void ToolUseBlockRoundTripsWithItsInputIntact()
        {
            var original = new ToolUseBlock
            {
                Id = "toolu_abc",
                Name = "create_box",
                InputJson = "{\"corner1\":[0,0,0],\"corner2\":[10,20,5],\"layer\":\"Walls::Interior\"}"
            };

            string json = Write(original);
            // The input must be a JSON object on the wire, not a quoted string.
            Assert.Contains("\"input\":{", json);

            var restored = Assert.IsType<ToolUseBlock>(Read(json));
            Assert.Equal("toolu_abc", restored.Id);
            Assert.Equal("create_box", restored.Name);
            Assert.Equal("Walls::Interior", restored.ParseInput().GetProperty("layer").GetString());
        }

        [Fact]
        public void ToolResultBlockRoundTripsWithMixedTextAndImageContent()
        {
            var original = new ToolResultBlock { ToolUseId = "toolu_1" };
            original.Content.Add(new TextBlock("{\"success\":true}"));
            original.Content.Add(new ImageBlock { MediaType = "image/png", Data = "iVBORw0KGgo=" });

            var restored = Assert.IsType<ToolResultBlock>(Read(Write(original)));

            Assert.Equal("toolu_1", restored.ToolUseId);
            Assert.False(restored.IsError);
            Assert.Equal(2, restored.Content.Count);
            Assert.IsType<TextBlock>(restored.Content[0]);
            var image = Assert.IsType<ImageBlock>(restored.Content[1]);
            Assert.Equal("image/png", image.MediaType);
            Assert.Equal("iVBORw0KGgo=", image.Data);
        }

        [Fact]
        public void ErrorToolResultCarriesIsError()
        {
            var original = new ToolResultBlock { ToolUseId = "t", IsError = true };
            original.Content.Add(new TextBlock("boom"));

            var restored = Assert.IsType<ToolResultBlock>(Read(Write(original)));
            Assert.True(restored.IsError);
        }

        [Fact]
        public void ImageBlockUsesTheBase64SourceShape()
        {
            string json = Write(new ImageBlock { MediaType = "image/jpeg", Data = "AAAA" });

            using var doc = JsonDocument.Parse(json);
            var source = doc.RootElement.GetProperty("source");
            Assert.Equal("base64", source.GetProperty("type").GetString());
            Assert.Equal("image/jpeg", source.GetProperty("media_type").GetString());
            Assert.Equal("AAAA", source.GetProperty("data").GetString());
        }

        [Fact]
        public void UnknownBlockSurvivesUnchanged()
        {
            const string raw = "{\"type\":\"thinking\",\"thinking\":\"…\",\"signature\":\"sig\"}";

            var restored = Assert.IsType<UnknownBlock>(Read(raw));
            var rewritten = Write(restored);

            using var doc = JsonDocument.Parse(rewritten);
            Assert.Equal("thinking", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("sig", doc.RootElement.GetProperty("signature").GetString());
        }

        [Fact]
        public void ToolResultWithAStringContentIsAccepted()
        {
            // The API also permits a bare string for tool_result content.
            const string raw = "{\"type\":\"tool_result\",\"tool_use_id\":\"t\",\"content\":\"plain text\"}";

            var restored = Assert.IsType<ToolResultBlock>(Read(raw));
            var text = Assert.IsType<TextBlock>(Assert.Single(restored.Content));
            Assert.Equal("plain text", text.Text);
        }

        [Fact]
        public void MalformedToolInputSerializesAsAnEmptyObjectRatherThanThrowing()
        {
            var original = new ToolUseBlock { Id = "t", Name = "x", InputJson = "{not json" };

            string json = Write(original);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("input").ValueKind);
            Assert.Equal(0, doc.RootElement.GetProperty("input").EnumerateObject().Count());
        }

        [Fact]
        public void AFullRequestSerializesWithToolsAndSchemasAsObjects()
        {
            var request = new MessagesRequest
            {
                Model = AgentSettingsModel,
                MaxTokens = 4096,
                System = "system prompt",
                Messages = { AgentMessage.User("draw a box") },
                Tools = new List<ToolSpec>
                {
                    new ToolSpec
                    {
                        Name = "create_box",
                        Description = "Create a box.",
                        InputSchemaJson = "{\"type\":\"object\",\"required\":[\"corner1\"],\"properties\":{\"corner1\":{\"type\":\"array\"}}}"
                    }
                },
                Stream = true
            };

            using var doc = JsonDocument.Parse(request.ToJson());
            var root = doc.RootElement;

            Assert.Equal(AgentSettingsModel, root.GetProperty("model").GetString());
            Assert.True(root.GetProperty("stream").GetBoolean());

            var tool = root.GetProperty("tools")[0];
            Assert.Equal("create_box", tool.GetProperty("name").GetString());

            // input_schema must be an object, not a JSON-encoded string.
            var schema = tool.GetProperty("input_schema");
            Assert.Equal(JsonValueKind.Object, schema.ValueKind);
            Assert.Equal("object", schema.GetProperty("type").GetString());
        }

        [Fact]
        public void NullSystemPromptIsOmittedFromTheRequest()
        {
            var request = new MessagesRequest
            {
                Model = AgentSettingsModel,
                Messages = { AgentMessage.User("hi") }
            };

            using var doc = JsonDocument.Parse(request.ToJson());
            Assert.False(doc.RootElement.TryGetProperty("system", out _));
            Assert.False(doc.RootElement.TryGetProperty("tools", out _));
        }

        private const string AgentSettingsModel = "claude-sonnet-4-5-20250929";
    }
}
