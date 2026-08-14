using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RhinoClaude.Agent
{
    public enum AgentState
    {
        Idle,
        Streaming,
        DispatchingTools,
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
        void OnToolInvocationStarted(string toolUseId, string toolName);
        void OnToolInvocationFinished(ToolInvocation invocation);
        void OnBudgetChanged(CostBudget budget);
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
        private readonly AnthropicClient _client;
        private readonly ToolRegistry _registry;
        private readonly ToolDispatcher _dispatcher;
        private readonly Func<string> _systemPromptFactory;

        private CancellationTokenSource _cts;

        public AgentSession(
            AnthropicClient client,
            ToolRegistry registry,
            AgentSettings settings,
            Func<string> systemPromptFactory)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _dispatcher = new ToolDispatcher(registry);
            _systemPromptFactory = systemPromptFactory ?? (() => string.Empty);
            Settings = settings ?? new AgentSettings();

            Id = Guid.NewGuid();
            CreatedUtc = DateTime.UtcNow;
        }

        public Guid Id { get; }
        public DateTime CreatedUtc { get; }
        public AgentSettings Settings { get; }
        public IAgentSessionObserver Observer { get; set; }

        /// <summary>First user turn, truncated — the session dropdown's label.</summary>
        public string DisplayName { get; private set; } = "New session";

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

            using (_cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken))
            {
                var token = _cts.Token;

                Messages.Add(AgentMessage.User(userMessage));
                Observer?.OnUserTurn(userMessage);

                CurrentBudget = new CostBudget(Settings.LoopModel, Settings.MaxCostUsd, Settings.MaxIterations);
                Observer?.OnBudgetChanged(CurrentBudget);

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

                        foreach (var toolUse in toolUses)
                        {
                            Observer?.OnToolInvocationStarted(toolUse.Id, toolUse.Name);

                            var invocation = await _dispatcher.InvokeAsync(toolUse, token).ConfigureAwait(false);

                            Invocations.Add(invocation);
                            Observer?.OnToolInvocationFinished(invocation);

                            resultBlocks.Add(invocation.Result.ToBlock(toolUse.Id));

                            if (invocation.TerminatesTurn && invocation.Result.Success)
                                turnTerminated = true;

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

                        Messages.Add(new AgentMessage("user", resultBlocks.ToArray()));

                        if (token.IsCancellationRequested)
                        {
                            SetState(AgentState.Cancelled, null);
                            finalMessage = "Cancelled. Tools already dispatched completed; nothing new was started.";
                            break;
                        }

                        if (turnTerminated)
                        {
                            SetState(AgentState.Done, null);
                            finalMessage = "Agent signalled the task is complete.";
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    SetState(AgentState.Cancelled, null);
                    finalMessage = "Cancelled.";
                }
                catch (AnthropicApiException ex)
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
                    Observer?.OnTurnFinished(State, finalMessage);
                }

                return State;
            }
        }

        private void OnStreamNotification(StreamNotification notification)
        {
            switch (notification.Kind)
            {
                case StreamEventKind.TextDelta:
                    Observer?.OnAssistantTextDelta(notification.Text);
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
