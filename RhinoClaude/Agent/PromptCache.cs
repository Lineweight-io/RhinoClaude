using System.Collections.Generic;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Places <c>cache_control</c> breakpoints on an outgoing request.
    ///
    /// The API is stateless, so every iteration of the tool-use loop re-sends the system
    /// prompt, all 25 tool schemas, and the whole accumulated conversation at the full input
    /// rate. On a real floor plan that dominates the bill: the static prefix alone is tens of
    /// thousands of tokens, re-billed once per iteration. Caching is a prefix match — the
    /// bytes before a breakpoint are stored, and a later request whose prefix matches
    /// byte-for-byte reads them back at a tenth of the input rate.
    ///
    /// Render order is <c>tools</c> → <c>system</c> → <c>messages</c>, which is why the
    /// placement below works out to "everything static, then the conversation so far".
    /// </summary>
    public static class PromptCache
    {
        /// <summary>The API accepts at most four breakpoints per request.</summary>
        public const int MaxBreakpoints = 4;

        /// <summary>
        /// How many breakpoints the message list gets. The rest go to tools and system.
        /// Two is what makes the loop cheap: the newer one writes the entry covering this
        /// iteration, the older one is the read point the previous iteration wrote. One alone
        /// would write a fresh entry every time and never read one back.
        /// </summary>
        public const int MessageBreakpoints = 2;

        /// <summary>
        /// Mark the request's breakpoints, clearing any left over from a previous call.
        ///
        /// Clearing matters because <see cref="MessagesRequest.Messages"/> aliases the
        /// session's live message list — the same block objects come back next iteration, and
        /// stale markers would spend breakpoints on positions that are no longer useful.
        /// </summary>
        public static void Apply(MessagesRequest request)
        {
            if (request == null) return;

            Clear(request);

            // 1. Tools. The array renders first, so one breakpoint on the last schema caches
            //    all of them. Registration order is stable (see ToolRegistry.ToSpecs), which
            //    is what makes the bytes repeatable.
            if (request.Tools != null && request.Tools.Count > 0)
                request.Tools[request.Tools.Count - 1].CacheControl = CacheControl.Ephemeral;

            // 2. System prompt. Static for the life of a session by construction — anything
            //    document-specific is fetched through describe_document instead of being
            //    interpolated in. This breakpoint also covers the tools ahead of it, so the
            //    two together give a tools-only read point if the prompt ever changes.
            request.CacheSystemPrompt = !string.IsNullOrEmpty(request.System);

            // 3. Conversation. Everything before the newest turn is frozen, and each iteration
            //    only appends, so the prefix keeps matching as the loop runs.
            foreach (int index in MessageBreakpointIndices(request.Messages?.Count ?? 0))
            {
                var content = request.Messages[index].Content;
                if (content.Count > 0)
                    content[content.Count - 1].CacheControl = CacheControl.Ephemeral;
            }
        }

        /// <summary>Strip every breakpoint this class may have set on a previous call.</summary>
        public static void Clear(MessagesRequest request)
        {
            if (request == null) return;

            request.CacheSystemPrompt = false;

            if (request.Tools != null)
                foreach (var tool in request.Tools) tool.CacheControl = null;

            if (request.Messages != null)
                foreach (var message in request.Messages)
                    foreach (var block in message.Content) block.CacheControl = null;
        }

        /// <summary>
        /// Which messages carry a breakpoint, oldest first.
        ///
        /// One iteration of the loop appends exactly two messages — the assistant turn and the
        /// user turn carrying its tool results — so the message that was last on the previous
        /// request is at <c>Count - 3</c>. Marking that one as well as the current tail keeps a
        /// read point where the previous iteration wrote its entry, instead of writing a new
        /// entry every iteration and never reading one back.
        ///
        /// The pair also stays inside the API's 20-block lookback: the two breakpoints are one
        /// iteration apart, so a turn with a lot of parallel tool calls still finds the entry.
        /// </summary>
        public static IReadOnlyList<int> MessageBreakpointIndices(int messageCount)
        {
            var indices = new List<int>(MessageBreakpoints);
            if (messageCount <= 0) return indices;

            int previousTail = messageCount - 3;
            if (previousTail >= 0) indices.Add(previousTail);
            indices.Add(messageCount - 1);
            return indices;
        }

        /// <summary>Breakpoints the request actually carries — for tests and diagnostics.</summary>
        public static int CountBreakpoints(MessagesRequest request)
        {
            if (request == null) return 0;

            int count = request.CacheSystemPrompt && !string.IsNullOrEmpty(request.System) ? 1 : 0;

            if (request.Tools != null)
                foreach (var tool in request.Tools)
                    if (tool.CacheControl != null) count++;

            if (request.Messages != null)
                foreach (var message in request.Messages)
                    foreach (var block in message.Content)
                        if (block.CacheControl != null) count++;

            return count;
        }
    }
}
