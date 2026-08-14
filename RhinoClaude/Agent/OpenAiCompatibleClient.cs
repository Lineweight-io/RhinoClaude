using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// One client for every OpenAI-compatible provider — DeepSeek, Moonshot (Kimi), Alibaba
    /// Model Studio, OpenAI itself, Ollama, or anything else that serves
    /// <c>POST {baseUrl}/chat/completions</c>.
    ///
    /// The provider-specific part is three fields (base URL, model, key) plus the flags in
    /// <see cref="OpenAiQuirks"/>; the shape translation lives in <see cref="OpenAiTranslator"/>
    /// and <see cref="OpenAiStreamTranslator"/>. Retry and backoff behaviour mirrors
    /// <see cref="AnthropicClient"/> so a provider swap does not change how failures feel.
    /// </summary>
    public sealed class OpenAiCompatibleClient : ILlmClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        private readonly string _baseUrl;
        private readonly OpenAiQuirks _quirks;
        private string _apiKey;

        public OpenAiCompatibleClient(
            string providerName,
            string baseUrl,
            string model,
            string apiKey,
            OpenAiQuirks quirks = null)
        {
            ProviderName = string.IsNullOrWhiteSpace(providerName) ? "OpenAI-compatible" : providerName;
            _baseUrl = NormalizeBaseUrl(baseUrl);
            ModelId = model;
            _apiKey = apiKey?.Trim();
            _quirks = quirks ?? new OpenAiQuirks();
        }

        public string ProviderName { get; }
        public string ModelId { get; }
        public string BaseUrl => _baseUrl;
        public OpenAiQuirks Quirks => _quirks;

        public bool AcceptsImages => _quirks.AcceptsImages;

        public bool IsConfigured =>
            !string.IsNullOrEmpty(_baseUrl) && (!_quirks.RequiresApiKey || !string.IsNullOrEmpty(_apiKey));

        public void SetApiKey(string apiKey) => _apiKey = apiKey?.Trim();

        /// <summary>Retries on 429 and 5xx with exponential backoff.</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Trailing slashes and a trailing <c>/chat/completions</c> are both common in
        /// hand-typed endpoints; accept either rather than 404-ing on a stray character.
        /// </summary>
        public static string NormalizeBaseUrl(string baseUrl)
        {
            string url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            const string suffix = "/chat/completions";
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                url = url.Substring(0, url.Length - suffix.Length);
            return url;
        }

        private string CompletionsUrl => _baseUrl + "/chat/completions";

        public async Task<StreamAccumulator> StreamAsync(
            MessagesRequest request,
            Action<StreamNotification> onEvent,
            CancellationToken cancellationToken)
        {
            RequireConfigured();

            // A gateway that cannot stream still has to produce a StreamAccumulator, because
            // that is what the loop consumes. Synthesise one from a one-shot call.
            if (!_quirks.SupportsStreaming)
                return await FakeStreamAsync(request, onEvent, cancellationToken).ConfigureAwait(false);

            request.Stream = true;
            string body = OpenAiTranslator.BuildRequestJson(request, _quirks);

            int attempt = 0;
            while (true)
            {
                attempt++;
                cancellationToken.ThrowIfCancellationRequested();

                using (var httpRequest = BuildHttpRequest(body, streaming: true))
                {
                    HttpResponseMessage response;
                    try
                    {
                        response = await Http.SendAsync(
                            httpRequest,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (HttpRequestException) when (attempt <= MaxRetries)
                    {
                        await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    using (response)
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            string errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                            bool retryable = response.StatusCode == (HttpStatusCode)429 ||
                                             (int)response.StatusCode >= 500;

                            if (retryable && attempt <= MaxRetries)
                            {
                                await BackoffAsync(attempt, cancellationToken, response).ConfigureAwait(false);
                                continue;
                            }

                            throw new LlmApiException(response.StatusCode, errorBody,
                                DescribeError(response.StatusCode, errorBody), ProviderName);
                        }

                        return await ReadStreamAsync(response, onEvent, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task<StreamAccumulator> ReadStreamAsync(
            HttpResponseMessage response,
            Action<StreamNotification> onEvent,
            CancellationToken cancellationToken)
        {
            var accumulator = new StreamAccumulator();
            var parser = new SseParser();
            var translator = new OpenAiStreamTranslator();

            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;

                    var sseEvent = parser.PushLine(line);
                    if (sseEvent == null) continue;

                    if (Feed(accumulator, onEvent, translator.Push(sseEvent))) break;
                }
            }

            // A provider that just closes the socket, with no [DONE] and no finish_reason,
            // still has to leave the accumulator in a completed state.
            if (!accumulator.Completed)
                Feed(accumulator, onEvent, translator.Finish());

            if (!string.IsNullOrEmpty(accumulator.ErrorMessage))
                throw new LlmApiException(HttpStatusCode.OK, null,
                    ProviderName + ": " + accumulator.ErrorMessage, ProviderName);

            return accumulator;
        }

        /// <summary>Push translated events through the accumulator. Returns true once complete.</summary>
        private static bool Feed(
            StreamAccumulator accumulator,
            Action<StreamNotification> onEvent,
            System.Collections.Generic.List<SseEvent> events)
        {
            foreach (var translated in events)
            {
                var notification = accumulator.Consume(translated);
                if (notification != null && notification.Kind != StreamEventKind.Ping)
                    onEvent?.Invoke(notification);

                if (accumulator.Completed) return true;
            }
            return false;
        }

        /// <summary>
        /// Non-streaming path dressed up as a stream, for gateways without SSE. The UI gets one
        /// delta per block instead of a live trickle; everything downstream is unchanged.
        /// </summary>
        private async Task<StreamAccumulator> FakeStreamAsync(
            MessagesRequest request,
            Action<StreamNotification> onEvent,
            CancellationToken cancellationToken)
        {
            var usage = new TokenUsage();
            var message = await SendAsync(request, cancellationToken, usage).ConfigureAwait(false);

            var accumulator = new StreamAccumulator();
            var translator = new OpenAiStreamTranslator();

            // Replay the finished message as a synthetic OpenAI stream so exactly one code path
            // builds the blocks.
            Feed(accumulator, onEvent, translator.Push(new SseEvent { Data = SyntheticChunk(message, usage) }));
            Feed(accumulator, onEvent, translator.Push(new SseEvent { Data = "[DONE]" }));
            return accumulator;
        }

        private static string SyntheticChunk(AgentMessage message, TokenUsage usage)
        {
            return OpenAiTranslator.Json(w =>
            {
                w.WriteStartObject();
                w.WritePropertyName("choices");
                w.WriteStartArray();
                w.WriteStartObject();
                w.WriteNumber("index", 0);
                w.WritePropertyName("delta");
                w.WriteStartObject();

                var text = new StringBuilder();
                var toolUses = new System.Collections.Generic.List<ToolUseBlock>();
                foreach (var block in message.Content)
                {
                    if (block is TextBlock t) text.Append(t.Text ?? string.Empty);
                    else if (block is ToolUseBlock tu) toolUses.Add(tu);
                }

                if (text.Length > 0) w.WriteString("content", text.ToString());

                if (toolUses.Count > 0)
                {
                    w.WritePropertyName("tool_calls");
                    w.WriteStartArray();
                    for (int i = 0; i < toolUses.Count; i++)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("index", i);
                        w.WriteString("id", toolUses[i].Id ?? ("call_" + i));
                        w.WriteString("type", "function");
                        w.WritePropertyName("function");
                        w.WriteStartObject();
                        w.WriteString("name", toolUses[i].Name ?? string.Empty);
                        w.WriteString("arguments", toolUses[i].InputJson ?? "{}");
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                w.WriteEndObject();
                w.WriteString("finish_reason", toolUses.Count > 0 ? "tool_calls" : "stop");
                w.WriteEndObject();
                w.WriteEndArray();

                // Already-normalised numbers, written back in the OpenAI shape the translator
                // expects to read.
                w.WritePropertyName("usage");
                w.WriteStartObject();
                w.WriteNumber("prompt_tokens", usage.InputTokens + usage.CacheReadInputTokens);
                w.WriteNumber("completion_tokens", usage.OutputTokens);
                w.WritePropertyName("prompt_tokens_details");
                w.WriteStartObject();
                w.WriteNumber("cached_tokens", usage.CacheReadInputTokens);
                w.WriteEndObject();
                w.WriteEndObject();

                w.WriteEndObject();
            });
        }

        public async Task<AgentMessage> SendAsync(
            MessagesRequest request,
            CancellationToken cancellationToken,
            TokenUsage usageSink = null)
        {
            RequireConfigured();

            request.Stream = null;
            string body = OpenAiTranslator.BuildRequestJson(request, _quirks);

            int attempt = 0;
            while (true)
            {
                attempt++;
                cancellationToken.ThrowIfCancellationRequested();

                using (var httpRequest = BuildHttpRequest(body, streaming: false))
                using (var response = await Http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false))
                {
                    string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        bool retryable = response.StatusCode == (HttpStatusCode)429 ||
                                         (int)response.StatusCode >= 500;

                        if (retryable && attempt <= MaxRetries)
                        {
                            await BackoffAsync(attempt, cancellationToken, response).ConfigureAwait(false);
                            continue;
                        }

                        throw new LlmApiException(response.StatusCode, responseBody,
                            DescribeError(response.StatusCode, responseBody), ProviderName);
                    }

                    return OpenAiTranslator.ParseResponse(responseBody, usageSink);
                }
            }
        }

        private HttpRequestMessage BuildHttpRequest(string body, bool streaming)
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, CompletionsUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrEmpty(_apiKey))
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            if (streaming)
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            return httpRequest;
        }

        private void RequireConfigured()
        {
            if (IsConfigured) return;

            throw new InvalidOperationException(
                "No API key configured for " + ProviderName +
                ". Set one in the sidebar's settings gear.");
        }

        private string DescribeError(HttpStatusCode status, string body)
        {
            string detail = null;
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("error", out var err))
                        {
                            if (err.ValueKind == JsonValueKind.String) detail = err.GetString();
                            else if (err.TryGetProperty("message", out var msg)) detail = msg.GetString();
                        }
                        else if (root.TryGetProperty("message", out var topLevel))
                        {
                            detail = topLevel.GetString();
                        }
                    }
                }
                catch (JsonException) { /* fall through to the raw body */ }
            }

            if (string.IsNullOrEmpty(detail))
                detail = string.IsNullOrEmpty(body) ? "(no response body)" : Truncate(body, 500);

            string prefix = ProviderName + " (" + ModelId + "): ";

            switch ((int)status)
            {
                case 400: return prefix + "the provider rejected the request. This usually means the " +
                                 "model id is wrong or it does not support tool use. " + detail;
                case 401: return prefix + "authentication failed — check the API key in the settings gear. " + detail;
                case 402: return prefix + "the account is out of credit. " + detail;
                case 403: return prefix + "the API key lacks permission for this request. " + detail;
                case 404: return prefix + "unknown model or endpoint — check the model id and the base URL. " + detail;
                case 429: return prefix + "rate limited, and retries were exhausted. " + detail;
                default:
                    return string.Format("{0}API error {1} ({2}): {3}", prefix, (int)status, status, detail);
            }
        }

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value.Substring(0, max) + "…";

        private static async Task BackoffAsync(int attempt, CancellationToken ct, HttpResponseMessage response = null)
        {
            TimeSpan delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            var retryAfter = response?.Headers?.RetryAfter;
            if (retryAfter != null)
            {
                if (retryAfter.Delta.HasValue) delay = retryAfter.Delta.Value;
                else if (retryAfter.Date.HasValue)
                {
                    var d = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                    if (d > TimeSpan.Zero) delay = d;
                }
            }
            if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
