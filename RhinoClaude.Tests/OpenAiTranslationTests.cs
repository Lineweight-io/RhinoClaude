using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The Anthropic ↔ OpenAI translation layer. Everything here is pure: no HTTP, no provider,
    /// just the two wire shapes and the rules for getting between them.
    /// </summary>
    public class OpenAiTranslationTests
    {
        private static OpenAiQuirks TextOnly() => new OpenAiQuirks();
        private static OpenAiQuirks Vision() => new OpenAiQuirks { AcceptsImages = true };

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static MessagesRequest BasicRequest() => new MessagesRequest
        {
            Model = "deepseek-v4-flash",
            MaxTokens = 4096,
            System = "You are a Rhino agent.",
            Messages = { AgentMessage.User("Draw a wall.") }
        };

        // ── Outgoing: request shape ───────────────────────────────────

        [Fact]
        public void SystemPromptBecomesASystemRoleMessage()
        {
            var json = Parse(OpenAiTranslator.BuildRequestJson(BasicRequest(), TextOnly()));

            var messages = json.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Equal("You are a Rhino agent.", messages[0].GetProperty("content").GetString());
            Assert.Equal("user", messages[1].GetProperty("role").GetString());
            Assert.Equal("Draw a wall.", messages[1].GetProperty("content").GetString());
        }

        [Fact]
        public void SystemRoleNameFollowsTheProviderQuirk()
        {
            var quirks = new OpenAiQuirks { SystemRoleName = "developer" };
            var json = Parse(OpenAiTranslator.BuildRequestJson(BasicRequest(), quirks));

            Assert.Equal("developer", json.GetProperty("messages")[0].GetProperty("role").GetString());
        }

        [Fact]
        public void MaxTokensFieldNameFollowsTheProviderQuirk()
        {
            var standard = Parse(OpenAiTranslator.BuildRequestJson(BasicRequest(), TextOnly()));
            Assert.Equal(4096, standard.GetProperty("max_tokens").GetInt32());
            Assert.False(standard.TryGetProperty("max_completion_tokens", out _));

            var openAi = Parse(OpenAiTranslator.BuildRequestJson(
                BasicRequest(), new OpenAiQuirks { UseMaxCompletionTokens = true }));
            Assert.Equal(4096, openAi.GetProperty("max_completion_tokens").GetInt32());
            Assert.False(openAi.TryGetProperty("max_tokens", out _));
        }

        [Fact]
        public void StreamOptionsOnlyGoOutOnAStreamedRequest()
        {
            var oneShot = Parse(OpenAiTranslator.BuildRequestJson(BasicRequest(), TextOnly()));
            Assert.False(oneShot.TryGetProperty("stream", out _));
            Assert.False(oneShot.TryGetProperty("stream_options", out _));

            var request = BasicRequest();
            request.Stream = true;
            var streamed = Parse(OpenAiTranslator.BuildRequestJson(request, TextOnly()));
            Assert.True(streamed.GetProperty("stream").GetBoolean());
            Assert.True(streamed.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        }

        [Fact]
        public void StreamUsageIsOmittedForProvidersThatRejectIt()
        {
            var request = BasicRequest();
            request.Stream = true;
            var json = Parse(OpenAiTranslator.BuildRequestJson(
                request, new OpenAiQuirks { SupportsStreamUsage = false }));

            Assert.True(json.GetProperty("stream").GetBoolean());
            Assert.False(json.TryGetProperty("stream_options", out _));
        }

        [Fact]
        public void ToolSchemasBecomeFunctionToolsWithTheirSchemaIntact()
        {
            var request = BasicRequest();
            request.Tools = new List<ToolSpec>
            {
                new ToolSpec
                {
                    Name = "create_box",
                    Description = "Creates a box.",
                    InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"width\":{\"type\":\"number\"}},\"required\":[\"width\"]}"
                }
            };

            var json = Parse(OpenAiTranslator.BuildRequestJson(request, TextOnly()));

            var tool = json.GetProperty("tools")[0];
            Assert.Equal("function", tool.GetProperty("type").GetString());
            Assert.Equal("create_box", tool.GetProperty("function").GetProperty("name").GetString());
            Assert.Equal("Creates a box.", tool.GetProperty("function").GetProperty("description").GetString());

            var parameters = tool.GetProperty("function").GetProperty("parameters");
            Assert.Equal("number", parameters.GetProperty("properties").GetProperty("width").GetProperty("type").GetString());
            Assert.Equal("width", parameters.GetProperty("required")[0].GetString());

            Assert.Equal("auto", json.GetProperty("tool_choice").GetString());
        }

        [Fact]
        public void NoToolsMeansNoToolsArrayAndNoToolChoice()
        {
            var json = Parse(OpenAiTranslator.BuildRequestJson(BasicRequest(), TextOnly()));
            Assert.False(json.TryGetProperty("tools", out _));
            Assert.False(json.TryGetProperty("tool_choice", out _));
        }

        // ── Outgoing: tool-call fidelity ──────────────────────────────

        [Fact]
        public void ToolUseBecomesAToolCallWithItsArgumentsVerbatim()
        {
            const string arguments =
                "{\"name\":\"Wall \\\"A\\\"\",\"height\":3.6499999999,\"nested\":{\"ids\":[1,2,3]},\"unicode\":\"é—\"}";

            var request = BasicRequest();
            request.Messages.Add(new AgentMessage("assistant",
                new TextBlock("Placing it."),
                new ToolUseBlock { Id = "call_7", Name = "create_wall", InputJson = arguments }));

            var json = Parse(OpenAiTranslator.BuildRequestJson(request, TextOnly()));
            var assistant = json.GetProperty("messages").EnumerateArray()
                .Single(m => m.GetProperty("role").GetString() == "assistant");

            Assert.Equal("Placing it.", assistant.GetProperty("content").GetString());

            var call = assistant.GetProperty("tool_calls")[0];
            Assert.Equal("call_7", call.GetProperty("id").GetString());
            Assert.Equal("function", call.GetProperty("type").GetString());
            Assert.Equal("create_wall", call.GetProperty("function").GetProperty("name").GetString());

            // arguments is a JSON *string* on this wire, so the model's own bytes cross
            // untouched — no reserialisation, no lost precision on that height.
            string carried = call.GetProperty("function").GetProperty("arguments").GetString();
            Assert.Equal(arguments, carried);

            var reparsed = Parse(carried);
            Assert.Equal("Wall \"A\"", reparsed.GetProperty("name").GetString());
            Assert.Equal(3.6499999999, reparsed.GetProperty("height").GetDouble());
            Assert.Equal(3, reparsed.GetProperty("nested").GetProperty("ids").GetArrayLength());
            Assert.Equal("é—", reparsed.GetProperty("unicode").GetString());
        }

        [Fact]
        public void AToolCallSurvivesAFullRoundTrip()
        {
            const string arguments = "{\"layer\":\"MASS_Core\",\"points\":[[0,0,0],[10.5,0,0]]}";

            var request = BasicRequest();
            request.Messages.Add(new AgentMessage("assistant",
                new ToolUseBlock { Id = "call_1", Name = "draw_polyline", InputJson = arguments }));

            // Out to OpenAI shape…
            var outgoing = Parse(OpenAiTranslator.BuildRequestJson(request, TextOnly()));
            var call = outgoing.GetProperty("messages").EnumerateArray()
                .Single(m => m.GetProperty("role").GetString() == "assistant")
                .GetProperty("tool_calls")[0];

            // …and back in, as the provider would echo it.
            string response = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[" +
                              call.GetRawText() + "]}}]}";

            var message = OpenAiTranslator.ParseResponse(response);
            var toolUse = Assert.IsType<ToolUseBlock>(message.Content.Single());

            Assert.Equal("call_1", toolUse.Id);
            Assert.Equal("draw_polyline", toolUse.Name);
            Assert.Equal(arguments, toolUse.InputJson);
            Assert.Equal(10.5, toolUse.ParseInput().GetProperty("points")[1][0].GetDouble());
        }

        [Fact]
        public void ToolResultsBecomeToolRoleMessagesInOrder()
        {
            var request = BasicRequest();
            request.Messages.Add(new AgentMessage("assistant",
                new ToolUseBlock { Id = "call_a", Name = "a", InputJson = "{}" },
                new ToolUseBlock { Id = "call_b", Name = "b", InputJson = "{}" }));
            request.Messages.Add(new AgentMessage("user",
                Result("call_a", "created 3 objects", isError: false),
                Result("call_b", "layer not found", isError: true)));

            var json = Parse(OpenAiTranslator.BuildRequestJson(request, TextOnly()));
            var toolMessages = json.GetProperty("messages").EnumerateArray()
                .Where(m => m.GetProperty("role").GetString() == "tool")
                .ToList();

            Assert.Equal(2, toolMessages.Count);
            Assert.Equal("call_a", toolMessages[0].GetProperty("tool_call_id").GetString());
            Assert.Equal("created 3 objects", toolMessages[0].GetProperty("content").GetString());

            // There is no is_error flag on this wire, so it is said in the text instead.
            Assert.Equal("call_b", toolMessages[1].GetProperty("tool_call_id").GetString());
            Assert.StartsWith("ERROR: ", toolMessages[1].GetProperty("content").GetString());
        }

        [Fact]
        public void ToolMessagesComeBeforeAnyLooseTextInTheSameTurn()
        {
            // The defensive-review note is appended to the results turn as a bare text block;
            // it must not be allowed to separate the tool messages from their assistant turn.
            var request = BasicRequest();
            request.Messages.Add(new AgentMessage("assistant",
                new ToolUseBlock { Id = "call_a", Name = "a", InputJson = "{}" }));
            request.Messages.Add(new AgentMessage("user",
                Result("call_a", "done", isError: false),
                new TextBlock("[automatic review after 10 iterations] check the layers.")));

            var roles = Parse(OpenAiTranslator.BuildRequestJson(request, TextOnly()))
                .GetProperty("messages").EnumerateArray()
                .Select(m => m.GetProperty("role").GetString())
                .ToList();

            Assert.Equal(new[] { "system", "user", "assistant", "tool", "user" }, roles);
        }

        [Fact]
        public void ThinkingBlocksAreNeverReplayed()
        {
            var request = BasicRequest();
            request.Messages.Add(new AgentMessage("assistant",
                new ThinkingBlock { Thinking = "the user wants a wall", Signature = "sig" },
                new TextBlock("Done.")));

            string json = OpenAiTranslator.BuildRequestJson(request, TextOnly());

            Assert.DoesNotContain("thinking", json);
            Assert.DoesNotContain("the user wants a wall", json);
            Assert.Contains("Done.", json);
        }

        [Fact]
        public void NoCacheBreakpointEverReachesAnOpenAiEndpoint()
        {
            // The translator writes the wire body field by field and never copies a breakpoint
            // across, so this holds whatever the request carries. It is asserted because the
            // prompt-cache work lands on the Anthropic path in parallel with this: no
            // OpenAI-compatible provider accepts cache_control, and sending one is a 400.
            var request = BasicRequest();
            request.Tools = new List<ToolSpec> { new ToolSpec { Name = "t" } };
            request.Messages.Add(new AgentMessage("assistant",
                new ToolUseBlock { Id = "call_a", Name = "t", InputJson = "{}" }));

            string json = OpenAiTranslator.BuildRequestJson(request, TextOnly());

            Assert.DoesNotContain("cache_control", json);
            Assert.DoesNotContain("ephemeral", json);
        }

        // ── Outgoing: images ──────────────────────────────────────────

        [Fact]
        public void ImagesBecomeDataUrlPartsWhenTheProviderTakesThem()
        {
            var request = BasicRequest();
            request.Messages.Add(new AgentMessage("user",
                new TextBlock("Review this."),
                new ImageBlock { MediaType = "image/png", Data = "QUJD" }));

            var json = Parse(OpenAiTranslator.BuildRequestJson(request, Vision()));
            var content = json.GetProperty("messages").EnumerateArray().Last().GetProperty("content");

            Assert.Equal(JsonValueKind.Array, content.ValueKind);
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal("image_url", content[1].GetProperty("type").GetString());
            Assert.Equal("data:image/png;base64,QUJD",
                content[1].GetProperty("image_url").GetProperty("url").GetString());
        }

        [Fact]
        public void ImagesBecomeANoteOnATextOnlyProvider()
        {
            var request = BasicRequest();
            request.Messages.Add(new AgentMessage("user",
                new TextBlock("Review this."),
                new ImageBlock { MediaType = "image/png", Data = "QUJD" }));

            var json = Parse(OpenAiTranslator.BuildRequestJson(request, TextOnly()));
            string content = json.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString();

            Assert.Contains("Review this.", content);
            Assert.Contains("1 image(s) omitted", content);
            Assert.DoesNotContain("QUJD", json.GetRawText());
        }

        [Fact]
        public void ImagesInsideAToolResultMoveToTheFollowingUserMessage()
        {
            var result = new ToolResultBlock { ToolUseId = "call_a" };
            result.Content.Add(new TextBlock("captured 2 views"));
            result.Content.Add(new ImageBlock { MediaType = "image/png", Data = "QUJD" });

            var request = BasicRequest();
            request.Messages.Add(new AgentMessage("assistant",
                new ToolUseBlock { Id = "call_a", Name = "capture", InputJson = "{}" }));
            request.Messages.Add(new AgentMessage("user", result));

            var messages = Parse(OpenAiTranslator.BuildRequestJson(request, Vision()))
                .GetProperty("messages").EnumerateArray().ToList();

            var toolMessage = messages.Single(m => m.GetProperty("role").GetString() == "tool");
            Assert.Contains("captured 2 views", toolMessage.GetProperty("content").GetString());

            // The tool role only takes a string, so the image rides on the next user message.
            var trailing = messages.Last();
            Assert.Equal("user", trailing.GetProperty("role").GetString());
            Assert.Equal("image_url", trailing.GetProperty("content")[0].GetProperty("type").GetString());
        }

        // ── Outgoing: structured output ───────────────────────────────

        [Fact]
        public void JsonSchemaGoesOutNativelyWhereItIsSupported()
        {
            var request = BasicRequest();
            request.OutputConfig = new OutputConfig
            {
                Format = new OutputFormat { SchemaJson = "{\"type\":\"object\",\"properties\":{\"verdict\":{\"type\":\"string\"}}}" }
            };

            var json = Parse(OpenAiTranslator.BuildRequestJson(
                request, new OpenAiQuirks { SupportsJsonSchema = true }));

            var format = json.GetProperty("response_format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.Equal("string", format.GetProperty("json_schema").GetProperty("schema")
                .GetProperty("properties").GetProperty("verdict").GetProperty("type").GetString());

            // The prompt is left alone when the schema is enforced natively.
            Assert.Equal("You are a Rhino agent.", json.GetProperty("messages")[0].GetProperty("content").GetString());
        }

        [Fact]
        public void JsonObjectFallbackSpellsTheSchemaOutInThePrompt()
        {
            var request = BasicRequest();
            request.OutputConfig = new OutputConfig
            {
                Format = new OutputFormat { SchemaJson = "{\"type\":\"object\",\"properties\":{\"verdict\":{\"type\":\"string\"}}}" }
            };

            var json = Parse(OpenAiTranslator.BuildRequestJson(request, TextOnly()));

            Assert.Equal("json_object", json.GetProperty("response_format").GetProperty("type").GetString());

            // DeepSeek's json_object mode also requires the word "json" in the prompt.
            string system = json.GetProperty("messages")[0].GetProperty("content").GetString();
            Assert.Contains("JSON schema", system);
            Assert.Contains("\"verdict\"", system);
        }

        // ── Incoming: non-streaming ───────────────────────────────────

        [Fact]
        public void ANonStreamingResponseBecomesAnAssistantMessage()
        {
            const string body =
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Two walls placed.\"}," +
                "\"finish_reason\":\"stop\"}]," +
                "\"usage\":{\"prompt_tokens\":1200,\"completion_tokens\":40}}";

            var usage = new TokenUsage();
            var message = OpenAiTranslator.ParseResponse(body, usage);

            Assert.Equal("assistant", message.Role);
            Assert.Equal("Two walls placed.", message.TextContent());
            Assert.Equal(1200, usage.InputTokens);
            Assert.Equal(40, usage.OutputTokens);
        }

        [Fact]
        public void CachedPromptTokensArePricedSeparately()
        {
            // OpenAI / Moonshot / Model Studio shape.
            var openAi = OpenAiTranslator.ReadUsage(Parse(
                "{\"prompt_tokens\":1000,\"completion_tokens\":50,\"prompt_tokens_details\":{\"cached_tokens\":800}}"));
            Assert.Equal(200, openAi.InputTokens);
            Assert.Equal(800, openAi.CacheReadInputTokens);
            Assert.Equal(50, openAi.OutputTokens);
            Assert.Equal(0, openAi.CacheCreationInputTokens);

            // DeepSeek's own shape.
            var deepSeek = OpenAiTranslator.ReadUsage(Parse(
                "{\"prompt_tokens\":1000,\"completion_tokens\":50," +
                "\"prompt_cache_hit_tokens\":700,\"prompt_cache_miss_tokens\":300}"));
            Assert.Equal(300, deepSeek.InputTokens);
            Assert.Equal(700, deepSeek.CacheReadInputTokens);
        }

        // ── Incoming: streaming ───────────────────────────────────────

        [Fact]
        public void AStreamedTurnWithTextAndAToolCallParsesIntoInternalEvents()
        {
            const string sse =
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Placing \"}}]}\n\n" +
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"the wall.\"}}]}\n\n" +
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_9\"," +
                "\"type\":\"function\",\"function\":{\"name\":\"create_wall\",\"arguments\":\"\"}}]}}]}\n\n" +
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0," +
                "\"function\":{\"arguments\":\"{\\\"length\\\":\"}}]}}]}\n\n" +
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0," +
                "\"function\":{\"arguments\":\"12.5}\"}}]}}]}\n\n" +
                "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]," +
                "\"usage\":{\"prompt_tokens\":900,\"completion_tokens\":30,\"prompt_cache_hit_tokens\":600}}\n\n" +
                "data: [DONE]\n\n";

            var run = Replay(sse);

            Assert.True(run.Accumulator.Completed);
            Assert.Equal("tool_use", run.Accumulator.StopReason);

            var message = run.Accumulator.BuildMessage();
            Assert.Equal("Placing the wall.", message.TextContent());

            var toolUse = Assert.Single(run.Accumulator.ToolUses());
            Assert.Equal("call_9", toolUse.Id);
            Assert.Equal("create_wall", toolUse.Name);
            Assert.Equal("{\"length\":12.5}", toolUse.InputJson);
            Assert.Equal(12.5, toolUse.ParseInput().GetProperty("length").GetDouble());

            Assert.Equal(300, run.Accumulator.Usage.InputTokens);
            Assert.Equal(600, run.Accumulator.Usage.CacheReadInputTokens);
            Assert.Equal(30, run.Accumulator.Usage.OutputTokens);

            // The panel still sees live text as it arrives.
            var text = run.Notifications.Where(n => n.Kind == StreamEventKind.TextDelta).Select(n => n.Text);
            Assert.Equal(new[] { "Placing ", "the wall." }, text);
            Assert.Contains(run.Notifications,
                n => n.Kind == StreamEventKind.ToolUseBlockStart && n.ToolName == "create_wall");
        }

        [Fact]
        public void TwoParallelToolCallsKeepTheirOwnArguments()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"call_a\",\"function\":{\"name\":\"a\",\"arguments\":\"{\\\"x\\\":1}\"}}," +
                "{\"index\":1,\"id\":\"call_b\",\"function\":{\"name\":\"b\",\"arguments\":\"{\\\"y\\\":\"}}]}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"function\":{\"arguments\":\"2}\"}}]}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
                "data: [DONE]\n\n";

            var toolUses = Replay(sse).Accumulator.ToolUses();

            Assert.Equal(2, toolUses.Count);
            Assert.Equal("a", toolUses[0].Name);
            Assert.Equal("{\"x\":1}", toolUses[0].InputJson);
            Assert.Equal("b", toolUses[1].Name);
            Assert.Equal("{\"y\":2}", toolUses[1].InputJson);
        }

        [Fact]
        public void AToolCallClosedWithFinishReasonStopIsStillATurnThatNeedsTools()
        {
            // DeepSeek and Model Studio have both been seen doing this. Taking it literally
            // would end the turn with the tool calls unanswered.
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\"," +
                "\"function\":{\"name\":\"a\",\"arguments\":\"{}\"}}]}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n";

            Assert.Equal("tool_use", Replay(sse).Accumulator.StopReason);
        }

        [Fact]
        public void PlainTextTurnsEndWithEndTurn()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"All done.\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n";

            var run = Replay(sse);
            Assert.Equal("end_turn", run.Accumulator.StopReason);
            Assert.Equal("All done.", run.Accumulator.BuildMessage().TextContent());
        }

        [Fact]
        public void ATruncatedTurnReportsMaxTokens()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"half a sen\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n" +
                "data: [DONE]\n\n";

            Assert.Equal("max_tokens", Replay(sse).Accumulator.StopReason);
        }

        [Fact]
        public void ReasoningContentBecomesThinkingDeltas()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"the wall is 12m\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"Placed.\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n";

            var run = Replay(sse);

            Assert.Contains(run.Notifications,
                n => n.Kind == StreamEventKind.ThinkingDelta && n.Text == "the wall is 12m");
            Assert.Equal("Placed.", run.Accumulator.BuildMessage().TextContent());
        }

        [Fact]
        public void AStreamThatEndsWithoutDoneStillCompletes()
        {
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"cut short\"}}]}\n\n";

            var run = Replay(sse, sendFinish: true);

            Assert.True(run.Accumulator.Completed);
            Assert.Equal("end_turn", run.Accumulator.StopReason);
            Assert.Equal("cut short", run.Accumulator.BuildMessage().TextContent());
        }

        [Fact]
        public void AMidStreamErrorFrameSurfacesAsAnError()
        {
            const string sse =
                "data: {\"error\":{\"type\":\"rate_limit\",\"message\":\"too many requests\"}}\n\n";

            var run = Replay(sse);

            Assert.Contains(run.Notifications, n => n.Kind == StreamEventKind.Error);
            Assert.Contains("too many requests", run.Accumulator.ErrorMessage);
        }

        [Fact]
        public void UsageArrivingInItsOwnTrailingChunkIsStillCounted()
        {
            // include_usage delivers the totals in a choice-less chunk after finish_reason.
            const string sse =
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":500,\"completion_tokens\":12}}\n\n" +
                "data: [DONE]\n\n";

            var run = Replay(sse);

            Assert.Equal(500, run.Accumulator.Usage.InputTokens);
            Assert.Equal(12, run.Accumulator.Usage.OutputTokens);
            Assert.Equal("end_turn", run.Accumulator.StopReason);
        }

        // ── Endpoint handling ─────────────────────────────────────────

        [Theory]
        [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1")]
        [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1")]
        [InlineData("https://api.moonshot.ai/v1/chat/completions", "https://api.moonshot.ai/v1")]
        [InlineData("  http://localhost:11434/v1  ", "http://localhost:11434/v1")]
        public void HandTypedEndpointsAreNormalised(string typed, string expected)
        {
            Assert.Equal(expected, OpenAiCompatibleClient.NormalizeBaseUrl(typed));
        }

        [Fact]
        public void AProviderWithoutAKeyIsNotConfigured()
        {
            var deepSeek = new OpenAiCompatibleClient("DeepSeek", "https://api.deepseek.com/v1", "deepseek-v4-flash", null);
            Assert.False(deepSeek.IsConfigured);

            var withKey = new OpenAiCompatibleClient("DeepSeek", "https://api.deepseek.com/v1", "deepseek-v4-flash", "sk-x");
            Assert.True(withKey.IsConfigured);

            // Local runtimes take no key at all.
            var ollama = new OpenAiCompatibleClient("Ollama", "http://localhost:11434/v1", "qwen3:32b", null,
                new OpenAiQuirks { RequiresApiKey = false });
            Assert.True(ollama.IsConfigured);
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static ToolResultBlock Result(string toolUseId, string text, bool isError)
        {
            var block = new ToolResultBlock { ToolUseId = toolUseId, IsError = isError };
            block.Content.Add(new TextBlock(text));
            return block;
        }

        private sealed class ReplayResult
        {
            public StreamAccumulator Accumulator = new StreamAccumulator();
            public List<StreamNotification> Notifications = new List<StreamNotification>();
        }

        /// <summary>
        /// Push a raw OpenAI SSE payload through the same three stages the client uses:
        /// SSE framing → translation → accumulation.
        /// </summary>
        private static ReplayResult Replay(string payload, bool sendFinish = false)
        {
            var run = new ReplayResult();
            var translator = new OpenAiStreamTranslator();

            foreach (var sseEvent in SseParser.ParseAll(payload))
                Consume(run, translator.Push(sseEvent));

            if (sendFinish && !run.Accumulator.Completed)
                Consume(run, translator.Finish());

            return run;
        }

        private static void Consume(ReplayResult run, List<SseEvent> events)
        {
            foreach (var translated in events)
            {
                var notification = run.Accumulator.Consume(translated);
                if (notification != null && notification.Kind != StreamEventKind.Ping)
                    run.Notifications.Add(notification);
            }
        }
    }
}
