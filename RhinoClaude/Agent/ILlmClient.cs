using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Non-retryable API failure, whichever provider raised it. <see cref="AnthropicApiException"/>
    /// derives from this so the loop's one catch clause covers every provider.
    /// </summary>
    public class LlmApiException : Exception
    {
        public HttpStatusCode StatusCode { get; }
        public string ResponseBody { get; }

        /// <summary>Display name of the provider that failed — the message already names it.</summary>
        public string Provider { get; }

        public LlmApiException(HttpStatusCode status, string body, string message, string provider = null)
            : base(message)
        {
            StatusCode = status;
            ResponseBody = body;
            Provider = provider;
        }
    }

    /// <summary>
    /// The whole surface the agent loop needs from a model provider.
    ///
    /// Everything above this interface — <see cref="AgentSession"/>, the tool loop, the
    /// self-review pass — speaks the Anthropic Messages shape and nothing else. A provider
    /// that speaks something different (see <see cref="OpenAiCompatibleClient"/>) translates
    /// on the way in and out rather than leaking its own wire format upward.
    /// </summary>
    public interface ILlmClient
    {
        /// <summary>For error messages and the panel's status line, e.g. "DeepSeek".</summary>
        string ProviderName { get; }

        /// <summary>The model the client was configured with. Informational only — the
        /// per-request <see cref="MessagesRequest.Model"/> is what actually goes on the wire.</summary>
        string ModelId { get; }

        /// <summary>False when no API key is set; callers surface a "set your key" message.</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Whether image blocks survive translation. False on the text-only providers, where
        /// the self-review pass skips its screenshots entirely and the reviewer judges on the
        /// deterministic checks alone.
        /// </summary>
        bool AcceptsImages { get; }

        /// <summary>
        /// Stream one turn. <paramref name="onEvent"/> is invoked on a background thread for
        /// every decoded event — marshal to the UI yourself. Returns the accumulator holding
        /// the assembled message, usage, and stop reason.
        /// </summary>
        Task<StreamAccumulator> StreamAsync(
            MessagesRequest request,
            Action<StreamNotification> onEvent,
            CancellationToken cancellationToken);

        /// <summary>
        /// Non-streaming single call, used by ClaudeTag and the self-review pass.
        /// </summary>
        /// <param name="usageSink">
        /// Optional. Filled with the response's token usage so callers that bill the call can
        /// price it. Left null by callers that do not care.
        /// </param>
        Task<AgentMessage> SendAsync(
            MessagesRequest request,
            CancellationToken cancellationToken,
            TokenUsage usageSink = null);
    }
}
