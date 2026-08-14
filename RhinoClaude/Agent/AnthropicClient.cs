using System;
using System.Collections.Generic;
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
    /// <summary>Thrown for non-retryable API failures so callers see the status and body.</summary>
    public sealed class AnthropicApiException : Exception
    {
        public HttpStatusCode StatusCode { get; }
        public string ResponseBody { get; }

        public AnthropicApiException(HttpStatusCode status, string body, string message)
            : base(message)
        {
            StatusCode = status;
            ResponseBody = body;
        }
    }

    /// <summary>
    /// HTTP + SSE layer for the Messages API. Replaces the old one-shot
    /// <c>ClaudeApiService</c>: streaming and tool use are core here, not bolt-ons.
    ///
    /// No RhinoCommon dependency — everything Rhino-facing is marshalled by the caller.
    /// </summary>
    public sealed class AnthropicClient
    {
        private const string ApiUrl = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2023-06-01";

        // Streamed turns can legitimately run for minutes. The per-read timeout is what
        // actually matters, and that is governed by the cancellation token.
        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        private string _apiKey;

        public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

        public void SetApiKey(string apiKey) => _apiKey = apiKey?.Trim();

        /// <summary>Retries on 429 and 5xx with exponential backoff.</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Stream one Messages API request. <paramref name="onEvent"/> is invoked on a
        /// background thread for every decoded SSE event — marshal to the UI yourself.
        /// Returns the accumulator holding the assembled message, usage, and stop reason.
        /// </summary>
        public async Task<StreamAccumulator> StreamAsync(
            MessagesRequest request,
            Action<StreamNotification> onEvent,
            CancellationToken cancellationToken)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("No Anthropic API key configured. Run 'ClaudeSetKey' first.");

            request.Stream = true;
            string body = request.ToJson();

            int attempt = 0;
            while (true)
            {
                attempt++;
                cancellationToken.ThrowIfCancellationRequested();

                using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrl))
                {
                    httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    httpRequest.Headers.Add("x-api-key", _apiKey);
                    httpRequest.Headers.Add("anthropic-version", ApiVersion);
                    httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

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

                            throw new AnthropicApiException(
                                response.StatusCode,
                                errorBody,
                                DescribeError(response.StatusCode, errorBody));
                        }

                        return await ReadStreamAsync(response, onEvent, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private static async Task<StreamAccumulator> ReadStreamAsync(
            HttpResponseMessage response,
            Action<StreamNotification> onEvent,
            CancellationToken cancellationToken)
        {
            var accumulator = new StreamAccumulator();
            var parser = new SseParser();

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

                    var notification = accumulator.Consume(sseEvent);
                    if (notification != null && notification.Kind != StreamEventKind.Ping)
                        onEvent?.Invoke(notification);

                    if (accumulator.Completed) break;
                }
            }

            if (!string.IsNullOrEmpty(accumulator.ErrorMessage))
                throw new AnthropicApiException(HttpStatusCode.OK, null, accumulator.ErrorMessage);

            return accumulator;
        }

        /// <summary>
        /// Non-streaming single call. Used by ClaudeTag, which stays one-shot because its
        /// output is small and structured.
        /// </summary>
        public async Task<AgentMessage> SendAsync(MessagesRequest request, CancellationToken cancellationToken)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("No Anthropic API key configured. Run 'ClaudeSetKey' first.");

            request.Stream = null;
            string body = request.ToJson();

            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrl))
            {
                httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
                httpRequest.Headers.Add("x-api-key", _apiKey);
                httpRequest.Headers.Add("anthropic-version", ApiVersion);

                using (var response = await Http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false))
                {
                    string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                        throw new AnthropicApiException(response.StatusCode, responseBody,
                            DescribeError(response.StatusCode, responseBody));

                    var message = new AgentMessage { Role = "assistant" };
                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        if (doc.RootElement.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.Array)
                        {
                            var blocks = JsonSerializer.Deserialize<List<ContentBlock>>(
                                content.GetRawText(), MessagesRequest.SerializerOptions);
                            if (blocks != null) message.Content.AddRange(blocks);
                        }
                    }
                    return message;
                }
            }
        }

        private static string DescribeError(HttpStatusCode status, string body)
        {
            string detail = null;
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(body))
                    {
                        if (doc.RootElement.TryGetProperty("error", out var err) &&
                            err.TryGetProperty("message", out var msg))
                        {
                            detail = msg.GetString();
                        }
                    }
                }
                catch (JsonException) { /* fall through to the raw body */ }
            }

            if (string.IsNullOrEmpty(detail))
                detail = string.IsNullOrEmpty(body) ? "(no response body)" : Truncate(body, 500);

            switch ((int)status)
            {
                case 401: return "Authentication failed — check the API key (ClaudeSetKey). " + detail;
                case 403: return "The API key lacks permission for this request. " + detail;
                case 404: return "Unknown model or endpoint. " + detail;
                case 429: return "Rate limited, and retries were exhausted. " + detail;
                default:
                    return string.Format("API error {0} ({1}): {2}", (int)status, status, detail);
            }
        }

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value.Substring(0, max) + "…";

        private static async Task BackoffAsync(int attempt, CancellationToken ct, HttpResponseMessage response = null)
        {
            // Honour Retry-After when the server sends one, otherwise 1s, 2s, 4s…
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
