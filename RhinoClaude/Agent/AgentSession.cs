using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RhinoClaude.Agent
{
    public enum AgentState
    {
        Idle,
        Streaming,
        DispatchingTools,
        Reviewing,
        WaitingForUser,
        Done,
        Cancelled,
        BudgetExceeded,
        Errored
    }

    /// <summary>
    /// Everything the sidebar needs to render a turn as it happens. Implementations are
    /// called from a background thread — marshal to Eto yourself.
    /// </summary>
    public interface IAgentSessionObserver
    {
        void OnStateChanged(AgentState state, string detail);
        void OnUserTurn(string text);
        void OnAssistantTextDelta(string chunk);
        void OnAssistantTextBlockClosed();
        /// <summary>Summarized reasoning, when the model and settings surface it.</summary>
        void OnThinkingDelta(string chunk);
        void OnThinkingBlockStarted();
        void OnToolInvocationStarted(string toolUseId, string toolName);
        void OnToolInvocationFinished(ToolInvocation invocation);
        void OnBudgetChanged(CostBudget budget);
        /// <summary>Self-review finished. <paramref name="cycle"/> is 1-based.</summary>
        void OnReviewCompleted(ReviewOutcome outcome, int cycle);
        /// <summary>Old tool results were condensed to keep the conversation affordable.</summary>
        void OnHistoryCompacted(HistoryCompactor.CompactionReport report);
        void OnTurnFinished(AgentState finalState, string message);
    }

    /// <summary>
    /// The tool-use loop. One session per Rhino document; survives across user turns so a
    /// follow-up ("now do the same on the north wall") reuses the message list.
    ///
    /// Runs on a background task. Every RhinoCommon call is marshalled to the UI thread by
    /// <see cref="ToolDispatcher"/>; every observer callback is marshalled to Eto by the panel.
    /// </summary>
    public sealed class AgentSession
    {
        private readonly ToolRegistry _registry;
        private readonly ToolDispatcher _dispatcher;
        private readonly Func<string> _systemPromptFactory;

        private CancellationTokenSource _cts;

        public AgentSession(
            ILlmClient client,
            ToolRegistry registry,
            AgentSettings settings,
            Func<string> systemPromptFactory)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _dispatcher = new ToolDispatcher(registry);
            _systemPromptFactory = systemPromptFactory ?? (() => string.Empty);
            Settings = settings ?? new AgentSettings();

            Id = Guid.NewGuid();
            CreatedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// The provider the loop talks to. Swapped by <c>AgentHost.ApplySettings</c> when the
        /// user changes provider, which is why it is not readonly — but only between turns:
        /// changing it mid-turn would leave the conversation half-translated.
        /// </summary>
        public ILlmClient Client
        {
            get => _client;
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                if (IsRunning) throw new InvalidOperationException("Cannot change provider while a turn is running.");
                _client = value;
            }
        }

        private ILlmClient _client;

        public Guid Id { get; }
        public DateTime CreatedUtc { get; }
        public AgentSettings Settings { get; }
        public IAgentSessionObserver Observer { get; set; }

        /// <summary>
        /// Runs self-review for a signal_done call. Null disables review entirely, in which
        /// case signal_done simply ends the turn. Arguments are the agent's own summary and
        /// expected outcome.
        /// </summary>
        public Func<string, string, CancellationToken, Task<ReviewOutcome>> ReviewHook { get; set; }

        /// <summary>Set when review returned ask_user; the panel surfaces this inline.</summary>
        public string PendingQuestion { get; private set; }

        /// <summary>Review cycles used in the current turn (plan §5.5 caps this).</summary>
        public int ReviewCycles { get; private set; }

        /// <summary>First user turn, truncated — the session dropdown's label.</summary>
        public string DisplayName { get; private set; } = "New session";

        /// <summary>The most recent user turn, verbatim. The reviewer judges against this.</summary>
        public string LastUserMessage { get; private set; }

        /// <summary>Full conversation, replayed on every request (the API is stateless).</summary>
        public List<AgentMessage> Messages { get; } = new List<AgentMessage>();

        /// <summary>Budget for the turn currently running, or the last one that ran.</summary>
        public CostBudget CurrentBudget { get; private set; }

        /// <summary>Running total across every turn in this session.</summary>
        public TokenUsage SessionUsage { get; } = new TokenUsage();

        public AgentState State { get; private set; } = AgentState.Idle;

        public bool IsRunning => State == AgentState.Streaming || State == AgentState.DispatchingTools;

        /// <summary>Tool invocations from the whole session, for the transcript and logs.</summary>
        public List<ToolInvocation> Invocations { get; } = new List<ToolInvocation>();

        public void Cancel()
        {
            try { _cts?.Cancel(); }
            catch (ObjectDisposedException) { /* turn already finished */ }
        }

        /// <summary>
        /// Restore a persisted conversation into this session (plan §2.4 "resume").
        /// </summary>
        public void Restore(ConversationSnapshot snapshot)
        {
            if (snapshot == null) return;
            if (IsRunning) throw new InvalidOperationException("Cannot restore while a turn is running.");

            Messages.Clear();
            Messages.AddRange(snapshot.Messages);
            Invocations.Clear();   // tool timings are not persisted; the transcript rebuilds from messages

            if (!string.IsNullOrWhiteSpace(snapshot.DisplayName))
                DisplayName = snapshot.DisplayName;

            LastUserMessage = Messages
                .Where(m => m.Role == "user" && !m.Content.Any(b => b is ToolResultBlock))
                .Select(m => m.TextContent())
                .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));

            State = AgentState.Idle;
        }

        /// <summary>
        /// Shrink old tool results (plan §2.4). Called after a turn ships rather than mid-turn,
        /// so the model never sees its own context change underneath it.
        ///
        /// Rewriting old results does invalidate the cached prefix, so the first request of the
        /// next turn pays a cold write. That is a fair trade — the rewrite is what stops the
        /// conversation growing without bound, it is idempotent, and it only ever fires on
        /// results outside the recent window.
        /// </summary>
        public HistoryCompactor.CompactionReport CompactHistory()
        {
            return HistoryCompactor.Compact(Messages, HistoryCompactor.DefaultKeepRecentTurns);
        }

        /// <summary>Rough character weight of the conversation, for the panel's status line.</summary>
        public int ConversationSize() => HistoryCompactor.MeasureConversation(Messages);

        /// <summary>Wipe the conversation but keep the session object (and its undo log) alive.</summary>
        public void Reset()
        {
            if (IsRunning) Cancel();
            Messages.Clear();
            Invocations.Clear();
            DisplayName = "New session";
            State = AgentState.Idle;
        }

        /// <summary>
        /// Run one user turn to completion: plan → call tools → feed results → iterate →
        /// stop on end_turn, signal_done, a guardrail, cancellation, or an error.
        /// </summary>
        public async Task<AgentState> RunTurnAsync(string userMessage, CancellationToken externalToken)
        {
            if (IsRunning)
                throw new InvalidOperationException("A turn is already running.");

            if (string.IsNullOrWhiteSpace(userMessage))
                throw new ArgumentException("Message is empty.", nameof(userMessage));

            if (Messages.Count == 0)
                DisplayName = Truncate(userMessage.Trim(), 40);

            LastUserMessage = userMessage;

            using (_cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken))
            {
                var token = _cts.Token;

                Messages.Add(AgentMessage.User(userMessage));
                Observer?.OnUserTurn(userMessage);

                CurrentBudget = new CostBudget(Settings.LoopModel, Settings.MaxCostUsd, Settings.MaxIterations);
                Observer?.OnBudgetChanged(CurrentBudget);

                ReviewCycles = 0;
                PendingQuestion = null;
                _iterationAtLastReview = 0;

                string finalMessage = null;

                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();

                        if (CurrentBudget.Exceeded)
                        {
                            finalMessage = CurrentBudget.CostExceeded
                                ? string.Format("Cost budget reached (${0:0.00} of ${1:0.00}). Stopped before the next model call.",
                                    CurrentBudget.SpentUsd, CurrentBudget.MaxCostUsd)
                                : string.Format("Iteration cap reached ({0}). Stopped before the next model call.",
                                    CurrentBudget.MaxIterations);
                            SetState(AgentState.BudgetExceeded, finalMessage);
                            break;
                        }

                        SetState(AgentState.Streaming, null);

                        var request = new MessagesRequest
                        {
                            Model = Settings.LoopModel,
                            MaxTokens = Settings.MaxTokens,
                            System = _systemPromptFactory(),
                            Messages = Messages,
                            Tools = _registry.ToSpecs()
                        };
                        request.ApplyModelCapabilities(Settings.Effort, Settings.ShowThinking);

                        // The whole prefix — system prompt, every tool schema, and every tool
                        // result so far — is otherwise re-billed at the full input rate on each
                        // iteration. Placed after ApplyModelCapabilities so the request is in
                        // its final shape.
                        PromptCache.Apply(request);

                        var accumulator = await _client.StreamAsync(request, OnStreamNotification, token)
                                                       .ConfigureAwait(false);

                        CurrentBudget.RecordIteration(accumulator.Usage);
                        SessionUsage.Add(accumulator.Usage);
                        Observer?.OnBudgetChanged(CurrentBudget);

                        var assistantMessage = accumulator.BuildMessage();
                        // An assistant turn with zero content blocks is not a valid history
                        // entry; skip it rather than poisoning every later request.
                        if (assistantMessage.Content.Count > 0)
                            Messages.Add(assistantMessage);

                        var toolUses = accumulator.ToolUses();

                        if (accumulator.StopReason != "tool_use" || toolUses.Count == 0)
                        {
                            finalMessage = assistantMessage.TextContent();
                            if (accumulator.StopReason == "max_tokens")
                            {
                                finalMessage += "\n\n(Response hit the max_tokens limit and was truncated.)";
                            }
                            SetState(AgentState.Done, null);
                            break;
                        }

                        SetState(AgentState.DispatchingTools, null);

                        var resultBlocks = new List<ContentBlock>();
                        bool turnTerminated = false;
                        string terminationMessage = null;

                        foreach (var toolUse in toolUses)
                        {
                            Observer?.OnToolInvocationStarted(toolUse.Id, toolUse.Name);

                            var invocation = await _dispatcher.InvokeAsync(toolUse, token).ConfigureAwait(false);

                            // signal_done is where self-review runs. The reviewer's verdict
                            // becomes the tool's return value, so the agent reads it exactly
                            // like any other result and can act on an "iterate".
                            if (invocation.TerminatesTurn && invocation.Result.Success && ReviewHook != null)
                            {
                                var decision = await RunReviewAsync(toolUse, invocation, token).ConfigureAwait(false);
                                turnTerminated = decision.EndTurn;
                                if (decision.EndTurn) terminationMessage = decision.Message;
                            }
                            else if (invocation.TerminatesTurn && invocation.Result.Success)
                            {
                                turnTerminated = true;
                                terminationMessage = "Agent signalled the task is complete.";
                            }

                            Invocations.Add(invocation);
                            Observer?.OnToolInvocationFinished(invocation);

                            resultBlocks.Add(invocation.Result.ToBlock(toolUse.Id));

                            // Cancellation between tools: finish the ones already dispatched,
                            // fire nothing new. Every tool_use still needs a matching result
                            // or the API rejects the next request, so stub the rest.
                            if (token.IsCancellationRequested)
                            {
                                foreach (var remaining in toolUses.SkipWhile(t => t.Id != toolUse.Id).Skip(1))
                                {
                                    resultBlocks.Add(ToolResult
                                        .Fail("Cancelled by the user before this tool ran.")
                                        .ToBlock(remaining.Id));
                                }
                                break;
                            }
                        }

                        // Plan §5.1 trigger 2 — a loop that keeps working without ever calling
                        // signal_done gets reviewed anyway, so churn is caught rather than
                        // silently burning the budget.
                        if (!turnTerminated && ReviewHook != null &&
                            Settings.DefensiveReviewAfterIterations > 0 &&
                            CurrentBudget.Iterations - _iterationAtLastReview >= Settings.DefensiveReviewAfterIterations)
                        {
                            _iterationAtLastReview = CurrentBudget.Iterations;

                            var defensive = await RunDefensiveReviewAsync(token).ConfigureAwait(false);
                            if (defensive != null)
                            {
                                // Delivered as an extra user-turn note rather than a tool result:
                                // there is no tool_use to answer, and every tool_use already has
                                // exactly one matching result.
                                resultBlocks.Add(new TextBlock(defensive));
                            }
                        }

                        Messages.Add(new AgentMessage("user", resultBlocks.ToArray()));

                        if (token.IsCancellationRequested)
                        {
                            SetState(AgentState.Cancelled, null);
                            finalMessage = "Cancelled. Tools already dispatched completed; nothing new was started.";
                            break;
                        }

                        if (turnTerminated)
                        {
                            SetState(PendingQuestion == null ? AgentState.Done : AgentState.WaitingForUser, null);
                            finalMessage = terminationMessage;
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    SetState(AgentState.Cancelled, null);
                    finalMessage = "Cancelled.";
                }
                catch (LlmApiException ex)
                {
                    SetState(AgentState.Errored, ex.Message);
                    finalMessage = ex.Message;
                }
                catch (Exception ex)
                {
                    SetState(AgentState.Errored, ex.Message);
                    finalMessage = ex.GetType().Name + ": " + ex.Message;
                }
                finally
                {
                    // Compact once the turn has settled — never mid-turn, or the model's own
                    // context would change underneath it between iterations.
                    if (State == AgentState.Done || State == AgentState.WaitingForUser)
                    {
                        var report = CompactHistory();
                        if (report.ChangedAnything)
                            Observer?.OnHistoryCompacted(report);
                    }

                    Observer?.OnTurnFinished(State, finalMessage);
                }

                return State;
            }
        }

        private int _iterationAtLastReview;

        private struct ReviewDecision
        {
            public bool EndTurn;
            public string Message;
        }

        /// <summary>
        /// A review the agent did not ask for. It never ends the turn — the agent is still
        /// working — it just puts the reviewer's observations in front of it.
        /// Returns the note to append, or null when there is nothing worth saying.
        /// </summary>
        private async Task<string> RunDefensiveReviewAsync(CancellationToken token)
        {
            var previousState = State;
            SetState(AgentState.Reviewing, null);

            try
            {
                var outcome = await ReviewHook(
                    "(no summary — this is an automatic check partway through the turn)",
                    null, token).ConfigureAwait(false);

                if (outcome.Usage != null && !string.IsNullOrEmpty(outcome.ModelId))
                {
                    CurrentBudget.RecordSideCall("review (auto)", outcome.ModelId, outcome.Usage);
                    Observer?.OnBudgetChanged(CurrentBudget);
                }

                Observer?.OnReviewCompleted(outcome, 0);

                if (outcome.Verdict == ReviewVerdict.Ship || outcome.Verdict == ReviewVerdict.Unavailable)
                    return null;

                return "[automatic review after " + CurrentBudget.Iterations + " iterations] " +
                       (outcome.Notes ?? "The reviewer flagged something.") +
                       " Take this into account, or say why it does not apply, and carry on.";
            }
            catch (Exception)
            {
                return null;   // never let a defensive check break a working turn
            }
            finally
            {
                SetState(previousState, null);
            }
        }

        /// <summary>
        /// Plan §5.5's decision tree. Mutates <paramref name="invocation"/>'s result so the
        /// verdict is what the agent sees come back from signal_done.
        /// </summary>
        private async Task<ReviewDecision> RunReviewAsync(
            ToolUseBlock toolUse, ToolInvocation invocation, CancellationToken token)
        {
            SetState(AgentState.Reviewing, null);

            string summary = null;
            string expected = null;
            try
            {
                var input = toolUse.ParseInput();
                if (input.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String)
                    summary = s.GetString();
                if (input.TryGetProperty("expectedOutcome", out var e) && e.ValueKind == JsonValueKind.String)
                    expected = e.GetString();
            }
            catch (Exception) { /* the reviewer copes with a missing summary */ }

            ReviewCycles++;
            var outcome = await ReviewHook(summary, expected, token).ConfigureAwait(false);

            // The reviewer runs on a different model, so it is priced separately but still
            // counts against this turn's ceiling.
            if (outcome.Usage != null && !string.IsNullOrEmpty(outcome.ModelId))
            {
                CurrentBudget.RecordSideCall("review", outcome.ModelId, outcome.Usage);
                Observer?.OnBudgetChanged(CurrentBudget);
            }

            Observer?.OnReviewCompleted(outcome, ReviewCycles);

            // Past the cycle cap, an iterate becomes a question for the user — otherwise a
            // reviewer that is never satisfied would spend the whole budget.
            if (outcome.Verdict == ReviewVerdict.Iterate && ReviewCycles >= Settings.MaxReviewCycles)
            {
                outcome.Verdict = ReviewVerdict.AskUser;
                outcome.QuestionForUser = string.IsNullOrWhiteSpace(outcome.QuestionForUser)
                    ? "I have revised this " + ReviewCycles + " times and the review still flags: " +
                      (outcome.Notes ?? "an unresolved issue") + ". How would you like me to proceed?"
                    : outcome.QuestionForUser;
                outcome.Notes = (outcome.Notes ?? string.Empty) +
                    " (Review cycle cap of " + Settings.MaxReviewCycles + " reached, so this became a question.)";
            }

            invocation.Result = ToolResult.Ok(outcome.ToToolPayload());

            switch (outcome.Verdict)
            {
                case ReviewVerdict.Iterate:
                    // Do not end the turn: the notes are now signal_done's return value, and
                    // the agent gets another pass to act on them.
                    return new ReviewDecision { EndTurn = false };

                case ReviewVerdict.AskUser:
                    PendingQuestion = string.IsNullOrWhiteSpace(outcome.QuestionForUser)
                        ? outcome.Notes
                        : outcome.QuestionForUser;
                    return new ReviewDecision
                    {
                        EndTurn = true,
                        Message = "Review needs a decision from you: " + PendingQuestion
                    };

                case ReviewVerdict.Ship:
                    return new ReviewDecision
                    {
                        EndTurn = true,
                        Message = "Reviewer: SHIP — " + (outcome.Notes ?? "the work matches the request.")
                    };

                default:
                    return new ReviewDecision
                    {
                        EndTurn = true,
                        Message = "Review was unavailable (" + (outcome.Notes ?? "no detail") +
                                  "). The work is in the document; check it yourself."
                    };
            }
        }

        private void OnStreamNotification(StreamNotification notification)
        {
            switch (notification.Kind)
            {
                case StreamEventKind.TextDelta:
                    Observer?.OnAssistantTextDelta(notification.Text);
                    break;
                case StreamEventKind.ThinkingBlockStart:
                    Observer?.OnThinkingBlockStarted();
                    break;
                case StreamEventKind.ThinkingDelta:
                    Observer?.OnThinkingDelta(notification.Text);
                    break;
                case StreamEventKind.BlockStop:
                    Observer?.OnAssistantTextBlockClosed();
                    break;
                case StreamEventKind.Error:
                    Observer?.OnStateChanged(AgentState.Errored, notification.Text);
                    break;
            }
        }

        private void SetState(AgentState state, string detail)
        {
            State = state;
            Observer?.OnStateChanged(state, detail);
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            value = value.Replace('\r', ' ').Replace('\n', ' ');
            return value.Length <= max ? value : value.Substring(0, max) + "…";
        }
    }
}
