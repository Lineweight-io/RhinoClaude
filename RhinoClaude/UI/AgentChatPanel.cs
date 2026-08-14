using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.UI;
using RhinoClaude.Agent;
using RhinoClaude.Services.Agent;

namespace RhinoClaude.UI
{
    /// <summary>
    /// The dockable chat sidebar — the primary interface to the plugin (plan §7).
    ///
    /// Everything the agent does arrives here from a background thread, so every UI touch
    /// goes through <see cref="Application.Instance.AsyncInvoke"/>. Streamed text is
    /// coalesced on a timer rather than repainted per token, which is both faster and
    /// far more readable than a jittering label.
    /// </summary>
    [System.Runtime.InteropServices.Guid("6C2E9B44-1F73-4A5D-9E28-0B7D5A31C4F6")]
    public class AgentChatPanel : Eto.Forms.Panel, IPanel, IAgentSessionObserver
    {
        public static Guid PanelId => typeof(AgentChatPanel).GUID;

        private readonly uint _docSN;

        // Header
        private DropDown _sessionDropDown;
        private Label _statusLabel;
        private Label _costLabel;
        private ProgressBar _costBar;
        private Button _settingsButton;

        // Stream
        private Scrollable _scroll;
        private StackLayout _messages;

        // Composer
        private Label _selectionLabel;
        private CheckBox _includeSelection;
        private TextArea _input;
        private Button _sendButton;
        private Button _stopButton;
        private Button _revertButton;
        private Button _newSessionButton;

        // Streaming state
        private Label _activeAssistantLabel;
        private readonly StringBuilder _activeAssistantText = new StringBuilder();
        private Label _activeThinkingLabel;
        private readonly StringBuilder _activeThinkingText = new StringBuilder();
        private readonly object _textGate = new object();
        private bool _textDirty;
        private bool _thinkingDirty;
        private UITimer _flushTimer;

        private readonly Dictionary<string, ToolCard> _openCards = new Dictionary<string, ToolCard>();
        private CancellationTokenSource _turnCts;
        private bool _suppressSessionEvent;

        public AgentChatPanel(uint documentSerialNumber)
        {
            _docSN = documentSerialNumber;
            BuildUI();
            RefreshSessionList();
            RefreshSelectionHint();
            SetStatus(AgentState.Idle, null);
        }

        private RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(_docSN);

        private AgentHost Host
        {
            get
            {
                var doc = Doc;
                return doc == null ? null : AgentHost.For(doc);
            }
        }

        // ── UI construction ───────────────────────────────────────────

        private void BuildUI()
        {
            var header = BuildHeader();
            var stream = BuildStream();
            var composer = BuildComposer();

            Content = new StackLayout
            {
                Orientation = Orientation.Vertical,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Spacing = 0,
                Items =
                {
                    new StackLayoutItem(header, false),
                    new StackLayoutItem(Divider(), false),
                    new StackLayoutItem(stream, true),
                    new StackLayoutItem(Divider(), false),
                    new StackLayoutItem(composer, false)
                }
            };

            _flushTimer = new UITimer { Interval = 0.033 };
            _flushTimer.Elapsed += (s, e) => FlushStreamedText();
        }

        private Control BuildHeader()
        {
            _sessionDropDown = new DropDown { Width = 190 };
            _sessionDropDown.SelectedIndexChanged += OnSessionSelected;

            _settingsButton = new Button { Text = "⚙", Width = 32, ToolTip = "Agent settings" };
            _settingsButton.Click += (s, e) => OpenSettings();

            _statusLabel = new Label { Text = "● Ready", VerticalAlignment = VerticalAlignment.Center };
            _costLabel = new Label
            {
                Text = string.Format("$0.00 / ${0:0.00}", AgentSettings.DefaultMaxCostUsd),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Click for the per-iteration cost breakdown."
            };

            _costBar = new ProgressBar { MinValue = 0, MaxValue = 1000, Value = 0, Height = 4 };

            var costClick = new Drawable { Content = _costLabel };
            costClick.MouseDown += (s, e) => ShowCostBreakdown();

            var layout = new DynamicLayout { Padding = new Padding(8, 6), DefaultSpacing = new Size(6, 4) };
            layout.AddRow(new Label
            {
                Text = "RhinoClaude",
                Font = SystemFonts.Bold(),
                VerticalAlignment = VerticalAlignment.Center
            }, _sessionDropDown, _settingsButton);
            layout.AddRow(_statusLabel, costClick, null);
            layout.Add(_costBar);

            return new Eto.Forms.Panel { Content = layout };
        }

        private Control BuildStream()
        {
            _messages = new StackLayout
            {
                Orientation = Orientation.Vertical,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Spacing = 8,
                Padding = new Padding(8)
            };

            _scroll = new Scrollable
            {
                Content = _messages,
                ExpandContentWidth = true,
                Border = BorderType.None
            };

            return _scroll;
        }

        private Control BuildComposer()
        {
            _selectionLabel = new Label { Text = "No selection", VerticalAlignment = VerticalAlignment.Center };
            _includeSelection = new CheckBox { Text = "send with message", Checked = false, Enabled = false };

            // TextArea has no PlaceholderText across Eto versions, so the hint is a label.
            var inputHint = new Label
            {
                Text = "Ask Claude to modify this scene — Enter to send, Shift+Enter for a newline.",
                Font = SystemFonts.Default(SystemFonts.Default().Size - 2),
                TextColor = Color.FromArgb(130, 130, 130)
            };

            _input = new TextArea { Height = 64, Wrap = true };
            _input.KeyDown += OnInputKeyDown;

            _sendButton = new Button { Text = "Send" };
            _sendButton.Click += (s, e) => SendTurn();

            _stopButton = new Button { Text = "⏸ Stop", Enabled = false };
            _stopButton.Click += (s, e) => StopTurn();

            _revertButton = new Button { Text = "↶ Revert session" };
            _revertButton.Click += (s, e) => RevertSession();

            _newSessionButton = new Button { Text = "⟲ New" };
            _newSessionButton.Click += (s, e) => NewSession();

            var layout = new DynamicLayout { Padding = new Padding(8), DefaultSpacing = new Size(6, 6) };
            layout.AddRow(_selectionLabel, _includeSelection, null);
            layout.Add(inputHint);
            layout.AddRow(new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items = { new StackLayoutItem(_input, true), new StackLayoutItem(_sendButton, false) }
            });
            layout.AddRow(new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Items = { _stopButton, _revertButton, _newSessionButton }
            });

            return new Eto.Forms.Panel { Content = layout };
        }

        private static Control Divider() => new Eto.Forms.Panel
        {
            Height = 1,
            BackgroundColor = Color.FromArgb(120, 120, 120, 90)
        };

        // ── Sending a turn ────────────────────────────────────────────

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            // Enter sends; Shift+Enter inserts a newline.
            if (e.Key == Keys.Enter && !e.Shift && !e.Control)
            {
                e.Handled = true;
                SendTurn();
            }
        }

        private bool _conventionHintShown;

        /// <summary>
        /// The onboarding surface from semantic plan §8. Shown once per panel, and only when
        /// the classifier actually found nothing — a user whose masses already classify does
        /// not need to be told about a layer convention they are evidently following.
        /// </summary>
        private void ShowConventionHintOnce(AgentHost host)
        {
            if (_conventionHintShown || !host.Settings.EnableSemanticTools) return;
            _conventionHintShown = true;

            try
            {
                if (host.Elements.View.Masses.Count > 0) return;
            }
            catch (Exception)
            {
                return;   // a classifier failure must not block the turn
            }

            AppendSystemNote(
                "New to semantic queries? Claude found no building masses in this document. " +
                "At minimum, put your masses on MASS_* layers — see LAYER_CONVENTIONS.md. " +
                "Or run ClaudeLearnNamingConvention to teach Claude your firm's convention, " +
                "or ClaudeSetElement to tag individual solids.");
        }

        private async void SendTurn()
        {
            var host = Host;
            if (host == null) return;

            // Checked against the provider actually selected, not against Anthropic — a
            // DeepSeek-only user has no sk-ant- key and must not be stopped here.
            var client = host.Session?.Client;
            if (client == null || !client.IsConfigured)
            {
                AppendSystemNote(client == null || client is AnthropicClient
                    ? "No API key configured. Run the ClaudeSetKey command, then try again."
                    : "No API key configured for " + client.ProviderName +
                      ". Add one in the settings gear, then try again.");
                return;
            }

            string text = _input.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (host.Session.IsRunning)
            {
                AppendSystemNote("A turn is already running. Stop it first, or wait for it to finish.");
                return;
            }

            if (_includeSelection.Checked == true)
            {
                var ids = SelectedIds();
                if (ids.Count > 0)
                    text += "\n\n[SELECTION: " + string.Join(", ", ids) + "]";
            }

            ShowConventionHintOnce(host);

            _input.Text = string.Empty;
            host.Session.Observer = this;

            _turnCts = new CancellationTokenSource();
            SetRunningUi(true);

            try
            {
                await host.Session.RunTurnAsync(text, _turnCts.Token);
            }
            catch (Exception ex)
            {
                AppendSystemNote("Turn failed: " + ex.Message);
            }
            finally
            {
                SetRunningUi(false);
                _turnCts?.Dispose();
                _turnCts = null;
                RefreshSessionList();
            }
        }

        private void StopTurn()
        {
            Host?.Session?.Cancel();
            _turnCts?.Cancel();
            _stopButton.Enabled = false;
        }

        private void SetRunningUi(bool running)
        {
            _sendButton.Enabled = !running;
            _stopButton.Enabled = running;
            _newSessionButton.Enabled = !running;
            _revertButton.Enabled = !running;
            _sessionDropDown.Enabled = !running;

            if (running) _flushTimer.Start();
            else { _flushTimer.Stop(); FlushStreamedText(); }
        }

        // ── Session management ────────────────────────────────────────

        private void RefreshSessionList()
        {
            var host = Host;
            if (host == null) return;

            _suppressSessionEvent = true;
            try
            {
                _sessionDropDown.Items.Clear();
                for (int i = 0; i < host.History.Count; i++)
                {
                    var session = host.History[i];
                    _sessionDropDown.Items.Add(new ListItem
                    {
                        Text = session.DisplayName,
                        Key = i.ToString()
                    });
                }
                _sessionDropDown.SelectedIndex = host.History.IndexOf(host.Session);
            }
            finally
            {
                _suppressSessionEvent = false;
            }
        }

        private void OnSessionSelected(object sender, EventArgs e)
        {
            if (_suppressSessionEvent) return;
            // Switching to a prior session is a read-only view in this build: the transcript
            // is rebuilt from its message list, but new turns always go to the live session.
            var host = Host;
            if (host == null || _sessionDropDown.SelectedIndex < 0) return;
            if (_sessionDropDown.SelectedIndex >= host.History.Count) return;

            var selected = host.History[_sessionDropDown.SelectedIndex];
            if (selected == host.Session) return;

            RebuildTranscript(selected);
            AppendSystemNote("Viewing an earlier session (read-only). Press ⟲ New or pick the current " +
                             "session to send a message.");
        }

        private void NewSession()
        {
            var host = Host;
            if (host == null) return;

            if (host.Snapshots.PendingUndoCount > 0)
            {
                var answer = MessageBox.Show(this,
                    "This session has made " + host.Snapshots.PendingUndoCount +
                    " change(s) to the document.\n\nStarting a new session forgets them, so " +
                    "\"Revert session\" will no longer be able to undo them. Continue?",
                    "New session", MessageBoxButtons.YesNo, MessageBoxType.Question);
                if (answer != DialogResult.Yes) return;
            }

            host.StartSession();
            host.Session.Observer = this;
            _messages.Items.Clear();
            _activeAssistantLabel = null;
            _openCards.Clear();
            RefreshSessionList();
            UpdateCost(null);
            SetStatus(AgentState.Idle, null);
            AppendSystemNote("New session started.");
        }

        private void RevertSession()
        {
            var host = Host;
            if (host == null) return;

            int count = host.Snapshots.PendingUndoCount;
            if (count == 0)
            {
                AppendSystemNote("Nothing to revert — this session has not changed the document.");
                return;
            }

            var answer = MessageBox.Show(this,
                "Undo the " + count + " change(s) this agent session made to the document?\n\n" +
                "This issues " + count + " undo steps. Anything you changed by hand since the " +
                "session started will be undone too.",
                "Revert session", MessageBoxButtons.YesNo, MessageBoxType.Warning);
            if (answer != DialogResult.Yes) return;

            int performed = host.Snapshots.RevertSession();
            AppendSystemNote("Reverted " + performed + " of " + count + " change(s).");
        }

        private void OpenSettings()
        {
            var host = Host;
            if (host == null) return;

            var dialog = new AgentSettingsDialog(host.Settings.Clone());
            var updated = dialog.ShowModal(this);
            if (updated == null) return;

            bool scriptWas = host.Settings.EnableScriptTool;
            var providerWas = host.Settings.Provider;

            host.Settings.AdoptProviderSettings(updated);
            host.Settings.LoopModel = updated.LoopModel;
            host.Settings.MaxCostUsd = updated.MaxCostUsd;
            host.Settings.MaxIterations = updated.MaxIterations;
            host.Settings.MaxTokens = updated.MaxTokens;
            host.Settings.Effort = updated.Effort;
            host.Settings.ShowThinking = updated.ShowThinking;
            host.Settings.EnableScriptTool = updated.EnableScriptTool;
            host.Settings.ScriptTimeoutSeconds = updated.ScriptTimeoutSeconds;
            host.Settings.EnableRhinoCommandTool = updated.EnableRhinoCommandTool;
            host.Settings.ReviewerModel = updated.ReviewerModel;
            host.Settings.EnableSelfReview = updated.EnableSelfReview;
            host.Settings.MaxReviewCycles = updated.MaxReviewCycles;
            LlmSettingsStore.Persist(host.Settings);
            host.ApplySettings();

            bool anthropic = host.Settings.Provider == LlmProvider.Anthropic;
            string effortNote = anthropic && ModelCapabilities.SupportsEffort(host.Settings.LoopModel)
                ? ", effort " + ModelCapabilities.ClampEffort(host.Settings.LoopModel, host.Settings.Effort)
                : anthropic ? " (this model has no effort setting)" : string.Empty;

            AppendSystemNote("Settings updated. " +
                             LlmProviderCatalog.Get(host.Settings.Provider).DisplayName + " / " +
                             host.Settings.LoopModel +
                             ", budget $" + host.Settings.MaxCostUsd.ToString("0.00") +
                             ", max " + host.Settings.MaxIterations + " iterations" + effortNote + ".");

            if (providerWas != host.Settings.Provider)
            {
                AppendSystemNote(
                    "Provider changed to " + LlmProviderCatalog.Get(host.Settings.Provider).DisplayName +
                    ". Start a new session (⟲ New) before continuing — this conversation was written " +
                    "for the old provider and its tool-call ids will not line up.");
            }

            if (scriptWas != host.Settings.EnableScriptTool)
                AppendSystemNote("The script tool was toggled — that takes effect in the next session (⟲ New).");
        }

        // ── Selection integration (plan §7.3) ─────────────────────────

        private List<string> SelectedIds()
        {
            var doc = Doc;
            if (doc == null) return new List<string>();
            return doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id.ToString()).ToList();
        }

        private void RefreshSelectionHint()
        {
            int count = SelectedIds().Count;
            _selectionLabel.Text = count == 0
                ? "No selection"
                : count + (count == 1 ? " object selected —" : " objects selected —");
            _includeSelection.Enabled = count > 0;
            if (count == 0) _includeSelection.Checked = false;
        }

        // ── IAgentSessionObserver — all called off the UI thread ──────

        public void OnStateChanged(AgentState state, string detail) =>
            Application.Instance.AsyncInvoke(() => SetStatus(state, detail));

        public void OnUserTurn(string text) =>
            Application.Instance.AsyncInvoke(() => AppendUserMessage(text));

        public void OnAssistantTextDelta(string chunk)
        {
            lock (_textGate)
            {
                _activeAssistantText.Append(chunk);
                _textDirty = true;
            }
        }

        public void OnAssistantTextBlockClosed() =>
            Application.Instance.AsyncInvoke(FlushStreamedText);

        public void OnThinkingBlockStarted() =>
            Application.Instance.AsyncInvoke(() =>
            {
                lock (_textGate) { _activeThinkingText.Clear(); _thinkingDirty = false; }
                _activeThinkingLabel = null;
            });

        public void OnThinkingDelta(string chunk)
        {
            lock (_textGate)
            {
                _activeThinkingText.Append(chunk);
                _thinkingDirty = true;
            }
        }

        public void OnToolInvocationStarted(string toolUseId, string toolName) =>
            Application.Instance.AsyncInvoke(() =>
            {
                FlushStreamedText();
                var card = new ToolCard(toolName);
                _openCards[toolUseId ?? Guid.NewGuid().ToString()] = card;
                _messages.Items.Add(new StackLayoutItem(card.Control));
                ScrollToBottom();
            });

        public void OnToolInvocationFinished(ToolInvocation invocation) =>
            Application.Instance.AsyncInvoke(() =>
            {
                if (invocation.ToolUseId != null && _openCards.TryGetValue(invocation.ToolUseId, out var card))
                {
                    card.Complete(invocation);
                    _openCards.Remove(invocation.ToolUseId);
                }
                ScrollToBottom();
            });

        public void OnBudgetChanged(CostBudget budget) =>
            Application.Instance.AsyncInvoke(() => UpdateCost(budget));

        public void OnReviewCompleted(ReviewOutcome outcome, int cycle) =>
            Application.Instance.AsyncInvoke(() => AppendReview(outcome, cycle));

        public void OnHistoryCompacted(HistoryCompactor.CompactionReport report) =>
            Application.Instance.AsyncInvoke(() =>
                AppendSystemNote("Conversation trimmed to stay affordable — " + report + "."));

        public void OnTurnFinished(AgentState finalState, string message) =>
            Application.Instance.AsyncInvoke(() =>
            {
                FlushStreamedText();
                _activeAssistantLabel = null;
                _activeThinkingLabel = null;

                if (finalState != AgentState.Done && !string.IsNullOrWhiteSpace(message))
                    AppendSystemNote(message);

                RefreshSelectionHint();
                ScrollToBottom();

                // Non-modal nudge for a turn that ended while the panel was not being watched.
                if (finalState == AgentState.Errored || finalState == AgentState.BudgetExceeded)
                    RhinoApp.WriteLine("RhinoClaude: turn ended — " + finalState + ".");
            });

        // ── Transcript rendering ──────────────────────────────────────

        private void FlushStreamedText()
        {
            FlushThinking();

            string text;
            lock (_textGate)
            {
                if (!_textDirty) return;
                _textDirty = false;
                text = _activeAssistantText.ToString();
            }

            if (string.IsNullOrEmpty(text)) return;

            if (_activeAssistantLabel == null)
            {
                _activeAssistantLabel = new Label { Wrap = WrapMode.Word };
                _messages.Items.Add(new StackLayoutItem(Bubble("Claude", _activeAssistantLabel, Color.FromArgb(70, 110, 160, 40))));
            }

            _activeAssistantLabel.Text = text;
            ScrollToBottom();
        }

        /// <summary>
        /// Summarized reasoning renders above the answer in a muted, collapsed card — visible
        /// if you want it, out of the way if you don't.
        /// </summary>
        private void FlushThinking()
        {
            string text;
            lock (_textGate)
            {
                if (!_thinkingDirty) return;
                _thinkingDirty = false;
                text = _activeThinkingText.ToString();
            }

            if (string.IsNullOrWhiteSpace(text)) return;

            if (_activeThinkingLabel == null)
            {
                _activeThinkingLabel = new Label
                {
                    Wrap = WrapMode.Word,
                    TextColor = Color.FromArgb(130, 130, 130),
                    Font = SystemFonts.Default(SystemFonts.Default().Size - 1)
                };

                _messages.Items.Add(new StackLayoutItem(new Expander
                {
                    Header = new Label
                    {
                        Text = "▸ thinking",
                        TextColor = Color.FromArgb(130, 130, 130),
                        Font = SystemFonts.Default(SystemFonts.Default().Size - 1)
                    },
                    Content = new Eto.Forms.Panel
                    {
                        Padding = new Padding(12, 2, 4, 4),
                        Content = _activeThinkingLabel
                    },
                    Expanded = false
                }));
            }

            _activeThinkingLabel.Text = text;
            ScrollToBottom();
        }

        private void AppendUserMessage(string text)
        {
            // The previous assistant bubble is finished; start a new one next time.
            lock (_textGate)
            {
                _activeAssistantText.Clear(); _textDirty = false;
                _activeThinkingText.Clear(); _thinkingDirty = false;
            }
            _activeAssistantLabel = null;
            _activeThinkingLabel = null;

            _messages.Items.Add(new StackLayoutItem(Bubble("You",
                new Label { Text = text, Wrap = WrapMode.Word },
                Color.FromArgb(150, 150, 150, 40))));
            ScrollToBottom();
        }

        /// <summary>
        /// The reviewer's verdict, colour-coded. An ask_user verdict gets an answer box right
        /// underneath — answering it is just the next user turn, so the agent keeps its context.
        /// </summary>
        private void AppendReview(ReviewOutcome outcome, int cycle)
        {
            FlushStreamedText();

            Color accent;
            string headline;

            switch (outcome.Verdict)
            {
                case ReviewVerdict.Ship:
                    accent = Color.FromArgb(60, 160, 110); headline = "Reviewer: SHIP"; break;
                case ReviewVerdict.Iterate:
                    accent = Color.FromArgb(200, 140, 40); headline = "Reviewer: ITERATE"; break;
                case ReviewVerdict.AskUser:
                    accent = Color.FromArgb(60, 140, 220); headline = "Reviewer: NEEDS YOUR CALL"; break;
                default:
                    accent = Color.FromArgb(130, 130, 130); headline = "Review unavailable"; break;
            }

            if (cycle > 1) headline += "  (cycle " + cycle + ")";

            var layout = new DynamicLayout { DefaultSpacing = new Size(4, 3), Padding = new Padding(6, 4) };
            layout.Add(new Label
            {
                Text = headline,
                Font = SystemFonts.Bold(SystemFonts.Default().Size - 1),
                TextColor = accent
            });

            if (!string.IsNullOrWhiteSpace(outcome.Notes))
                layout.Add(new Label { Text = outcome.Notes, Wrap = WrapMode.Word });

            if (outcome.Verdict == ReviewVerdict.AskUser && !string.IsNullOrWhiteSpace(outcome.QuestionForUser))
            {
                layout.Add(new Label
                {
                    Text = outcome.QuestionForUser,
                    Wrap = WrapMode.Word,
                    Font = SystemFonts.Bold()
                });

                var answer = new TextBox();
                var send = new Button { Text = "Answer" };

                EventHandler<EventArgs> submit = (s, e) =>
                {
                    string text = answer.Text?.Trim();
                    if (string.IsNullOrEmpty(text)) return;
                    answer.Enabled = false;
                    send.Enabled = false;
                    _input.Text = text;
                    SendTurn();
                };

                send.Click += submit;
                answer.KeyDown += (s, e) =>
                {
                    if (e.Key == Keys.Enter) { e.Handled = true; submit(s, EventArgs.Empty); }
                };

                layout.Add(new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Items = { new StackLayoutItem(answer, true), new StackLayoutItem(send, false) }
                });
            }

            _messages.Items.Add(new StackLayoutItem(new Eto.Forms.Panel
            {
                Content = layout,
                BackgroundColor = Color.FromArgb(accent.Rb, accent.Gb, accent.Bb, 35)
            }));

            ScrollToBottom();
        }

        private void AppendSystemNote(string text)
        {
            var label = new Label
            {
                Text = text,
                Wrap = WrapMode.Word,
                TextColor = Color.FromArgb(150, 120, 60)
            };
            _messages.Items.Add(new StackLayoutItem(new Eto.Forms.Panel
            {
                Padding = new Padding(6, 4),
                Content = label
            }));
            ScrollToBottom();
        }

        private static Control Bubble(string who, Control body, Color accent)
        {
            var layout = new DynamicLayout { DefaultSpacing = new Size(4, 2), Padding = new Padding(6, 4) };
            layout.Add(new Label { Text = who, Font = SystemFonts.Bold(SystemFonts.Default().Size - 1) });
            layout.Add(body);

            return new Eto.Forms.Panel
            {
                Content = layout,
                BackgroundColor = accent
            };
        }

        private void RebuildTranscript(AgentSession session)
        {
            _messages.Items.Clear();
            _activeAssistantLabel = null;
            _openCards.Clear();

            foreach (var message in session.Messages)
            {
                if (message.Role == "user")
                {
                    // Tool-result turns are machine traffic, not something to replay as chat.
                    if (message.Content.Any(c => c is ToolResultBlock)) continue;
                    _messages.Items.Add(new StackLayoutItem(Bubble("You",
                        new Label { Text = message.TextContent(), Wrap = WrapMode.Word },
                        Color.FromArgb(150, 150, 150, 40))));
                }
                else
                {
                    string text = message.TextContent();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _messages.Items.Add(new StackLayoutItem(Bubble("Claude",
                            new Label { Text = text, Wrap = WrapMode.Word },
                            Color.FromArgb(70, 110, 160, 40))));
                    }
                    foreach (var toolUse in message.Content.OfType<ToolUseBlock>())
                    {
                        var card = new ToolCard(toolUse.Name);
                        var invocation = session.Invocations.FirstOrDefault(i => i.ToolUseId == toolUse.Id);
                        if (invocation != null) card.Complete(invocation);
                        _messages.Items.Add(new StackLayoutItem(card.Control));
                    }
                }
            }
            ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            // Deferred: the layout has not measured the newly-added control yet.
            Application.Instance.AsyncInvoke(() =>
            {
                try
                {
                    var content = _scroll.Content;
                    if (content != null)
                        _scroll.ScrollPosition = new Point(0, Math.Max(0, content.Height));
                }
                catch (Exception) { /* platform-dependent; scrolling is cosmetic */ }
            });
        }

        // ── Status + cost ─────────────────────────────────────────────

        private void SetStatus(AgentState state, string detail)
        {
            string text;
            Color color;

            switch (state)
            {
                case AgentState.Streaming:
                    text = "● Streaming"; color = Color.FromArgb(60, 140, 220); break;
                case AgentState.DispatchingTools:
                    text = "● Working"; color = Color.FromArgb(60, 160, 110); break;
                case AgentState.Cancelled:
                    text = "● Cancelled"; color = Color.FromArgb(160, 130, 60); break;
                case AgentState.BudgetExceeded:
                    text = "● Budget reached"; color = Color.FromArgb(200, 120, 40); break;
                case AgentState.Errored:
                    text = "● Error"; color = Color.FromArgb(200, 70, 70); break;
                case AgentState.Done:
                    text = "● Done"; color = Color.FromArgb(60, 160, 110); break;
                default:
                    text = "● Ready"; color = Color.FromArgb(130, 130, 130); break;
            }

            _statusLabel.Text = text;
            _statusLabel.TextColor = color;
            _statusLabel.ToolTip = detail;
        }

        private void UpdateCost(CostBudget budget)
        {
            if (budget == null)
            {
                var host = Host;
                double max = host?.Settings.MaxCostUsd ?? AgentSettings.DefaultMaxCostUsd;
                _costLabel.Text = string.Format("$0.00 / ${0:0.00}  |  iter 0/{1}",
                    max, host?.Settings.MaxIterations ?? 25);
                _costBar.Value = 0;
                _costLabel.TextColor = Color.FromArgb(130, 130, 130);
                return;
            }

            _costLabel.Text = budget.Describe();
            _costBar.Value = (int)Math.Round(budget.FractionSpent * 1000);

            // Amber past 60%, red past 90% (plan §7.2).
            double fraction = budget.FractionSpent;
            _costLabel.TextColor = fraction >= 0.90 ? Color.FromArgb(200, 70, 70)
                                 : fraction >= 0.60 ? Color.FromArgb(200, 140, 40)
                                 : Color.FromArgb(130, 130, 130);
        }

        private void ShowCostBreakdown()
        {
            var budget = Host?.Session?.CurrentBudget;
            if (budget == null || budget.Iterations == 0)
            {
                MessageBox.Show(this, "No cost recorded yet in this session.", "Cost breakdown");
                return;
            }
            MessageBox.Show(this, budget.Breakdown(), "Cost breakdown");
        }

        // ── Rhino events ──────────────────────────────────────────────

        private bool _eventsHooked;

        private void HookEvents()
        {
            if (_eventsHooked) return;
            _eventsHooked = true;
            RhinoDoc.SelectObjects += OnSelectionChanged;
            RhinoDoc.DeselectObjects += OnSelectionChanged;
            RhinoDoc.DeselectAllObjects += OnDeselectAll;
            RhinoDoc.BeginSaveDocument += OnBeginSaveDocument;

            var host = Host;
            if (host != null) host.FirstScriptedCommand += OnFirstScriptedCommand;
        }

        private void UnhookEvents()
        {
            if (!_eventsHooked) return;
            _eventsHooked = false;
            RhinoDoc.SelectObjects -= OnSelectionChanged;
            RhinoDoc.DeselectObjects -= OnSelectionChanged;
            RhinoDoc.DeselectAllObjects -= OnDeselectAll;
            RhinoDoc.BeginSaveDocument -= OnBeginSaveDocument;

            var host = Host;
            if (host != null) host.FirstScriptedCommand -= OnFirstScriptedCommand;
        }

        /// <summary>
        /// Persist the conversation into the .3dm as it is saved, so it travels with the file
        /// (plan §2.4). Runs on the UI thread during the save, so it stays cheap.
        /// </summary>
        private void OnBeginSaveDocument(object sender, DocumentSaveEventArgs e)
        {
            if (e.Document == null || e.Document.RuntimeSerialNumber != _docSN) return;

            var host = Host;
            if (host?.Session == null) return;
            if (host.Session.Messages.Count == 0) return;

            host.Conversations.Save(host.Session, host.Snapshots.PendingUndoCount);
        }

        /// <summary>Plan §4.7: visible, non-blocking notice the first time Tier 3 is used.</summary>
        private void OnFirstScriptedCommand(string commandLine) =>
            Application.Instance.AsyncInvoke(() =>
            {
                AppendSystemNote(
                    "Claude ran a scripted Rhino command for the first time this session: " +
                    commandLine + ". Scripted commands are non-atomic and undo less cleanly than " +
                    "the other tools — check the result. You can switch this tool off in settings.");
                RhinoApp.WriteLine("RhinoClaude: agent ran a scripted command — " + commandLine);
            });

        private void OnSelectionChanged(object sender, RhinoObjectSelectionEventArgs e)
        {
            if (e.Document.RuntimeSerialNumber != _docSN) return;
            Application.Instance.AsyncInvoke(RefreshSelectionHint);
        }

        private void OnDeselectAll(object sender, RhinoDeselectAllObjectsEventArgs e)
        {
            if (e.Document.RuntimeSerialNumber != _docSN) return;
            Application.Instance.AsyncInvoke(RefreshSelectionHint);
        }

        // ── IPanel ────────────────────────────────────────────────────

        public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
        {
            HookEvents();
            RefreshSelectionHint();

            var host = Host;
            if (host != null) host.Session.Observer = this;

            OfferStoredConversation();
        }

        private bool _resumeOffered;

        /// <summary>
        /// Plan §2.4: if the document carries a saved conversation, offer New / Resume /
        /// Discard once per panel session, with New the default. Offered rather than restored
        /// automatically — reopening a file should not silently reinstate an agent's context.
        /// </summary>
        private void OfferStoredConversation()
        {
            if (_resumeOffered) return;
            _resumeOffered = true;

            var host = Host;
            if (host == null) return;
            if (host.Session.Messages.Count > 0) return;   // a live session already has context

            ConversationSnapshot snapshot;
            try { snapshot = host.Conversations.LoadMostRecent(); }
            catch (Exception) { return; }

            if (snapshot == null || snapshot.Messages.Count == 0) return;

            string when = snapshot.SavedAt == DateTime.MinValue
                ? "previously"
                : snapshot.SavedAt.ToLocalTime().ToString("g");

            var dialog = new Dialog<string>
            {
                Title = "Earlier RhinoClaude conversation",
                Padding = new Padding(12),
                Resizable = false
            };

            var layout = new DynamicLayout { DefaultSpacing = new Size(8, 8) };
            layout.Add(new Label
            {
                Text = "This document has a saved conversation:\n\n" +
                       "    \"" + (snapshot.DisplayName ?? "(untitled)") + "\"\n" +
                       "    " + snapshot.UserTurnCount + " turn(s), saved " + when + "\n\n" +
                       "Resuming restores the conversation so Claude remembers the context.\n" +
                       "Screenshots are not saved — it will re-capture if it needs to look.",
                Wrap = WrapMode.Word,
                Width = 380
            });

            var newSession = new Button { Text = "New session" };
            var resume = new Button { Text = "Resume" };
            var discard = new Button { Text = "Discard saved" };

            newSession.Click += (s, e) => dialog.Close("new");
            resume.Click += (s, e) => dialog.Close("resume");
            discard.Click += (s, e) => dialog.Close("discard");

            dialog.DefaultButton = newSession;   // §2.4: New is pre-selected
            dialog.AbortButton = newSession;

            layout.Add(new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Items = { null, resume, discard, newSession }
            });

            dialog.Content = layout;

            string choice;
            try { choice = dialog.ShowModal(this); }
            catch (Exception) { return; }

            switch (choice)
            {
                case "resume":
                    try
                    {
                        host.Session.Restore(snapshot);
                        RebuildTranscript(host.Session);
                        RefreshSessionList();
                        AppendSystemNote("Resumed \"" + snapshot.DisplayName + "\" — " +
                                         snapshot.UserTurnCount + " turn(s) of context restored. " +
                                         "Note that \"Revert session\" cannot undo work from before " +
                                         "the document was reopened.");
                    }
                    catch (Exception ex)
                    {
                        AppendSystemNote("Could not resume that conversation: " + ex.Message);
                    }
                    break;

                case "discard":
                    host.Conversations.DeleteAll();
                    AppendSystemNote("Saved conversation discarded. The document must be saved again " +
                                     "for that to persist.");
                    break;
            }
        }

        public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) => UnhookEvents();

        public void PanelClosing(uint documentSerialNumber, bool onCloseDocument)
        {
            UnhookEvents();
            if (onCloseDocument) AgentHost.Forget(documentSerialNumber);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UnhookEvents();
                _flushTimer?.Stop();
            }
            base.Dispose(disposing);
        }

        // ── Tool call card ────────────────────────────────────────────

        /// <summary>
        /// One collapsible tool invocation: header shows name + status + duration,
        /// expanded shows input and result JSON side by side, plus any images inline.
        /// </summary>
        private sealed class ToolCard
        {
            private readonly Label _header;
            private readonly StackLayout _body;
            private readonly Expander _expander;

            public Control Control => _expander;

            public ToolCard(string toolName)
            {
                _header = new Label
                {
                    Text = "▸ " + toolName + "   …",
                    Font = SystemFonts.Default(SystemFonts.Default().Size - 1)
                };

                _body = new StackLayout
                {
                    Orientation = Orientation.Vertical,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Spacing = 4,
                    Padding = new Padding(12, 4, 4, 6)
                };

                _expander = new Expander
                {
                    Header = _header,
                    Content = _body,
                    Expanded = false
                };
            }

            public void Complete(ToolInvocation invocation)
            {
                bool ok = invocation.Result != null && invocation.Result.Success;
                string mark = ok ? "✓" : "✗";
                _header.Text = string.Format("▸ {0}   {1} {2}ms", invocation.ToolName, mark, invocation.ElapsedMs);
                _header.TextColor = ok ? Color.FromArgb(60, 140, 90) : Color.FromArgb(200, 70, 70);

                _body.Items.Add(new StackLayoutItem(SectionLabel("input")));
                _body.Items.Add(new StackLayoutItem(Json(invocation.InputJson)));

                _body.Items.Add(new StackLayoutItem(SectionLabel(ok ? "result" : "error")));
                _body.Items.Add(new StackLayoutItem(Json(ok
                    ? invocation.Result.Json
                    : invocation.Result?.Error ?? "(no result)")));

                var images = invocation.Result?.Images;
                if (images != null && images.Count > 0)
                {
                    var strip = new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 4
                    };
                    foreach (var image in images)
                    {
                        var view = BuildThumbnail(image);
                        if (view != null) strip.Items.Add(new StackLayoutItem(view));
                    }
                    _body.Items.Add(new StackLayoutItem(SectionLabel(images.Count + " image(s)")));
                    _body.Items.Add(new StackLayoutItem(strip));
                    // Screenshots are the one result worth showing without a click.
                    _expander.Expanded = true;
                }

                if (!ok) _expander.Expanded = true;
            }

            private static Control BuildThumbnail(ToolImage image)
            {
                try
                {
                    var bitmap = new Bitmap(image.Bytes);
                    var view = new ImageView { Image = bitmap, Size = new Size(112, 72) };
                    var clickable = new Drawable { Content = view, ToolTip = "Click to open full size" };

                    // Write once to temp so the OS viewer has a file to open.
                    string path = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        "RhinoClaude-" + Guid.NewGuid().ToString("N") +
                        (image.MediaType == "image/jpeg" ? ".jpg" : ".png"));

                    clickable.MouseDown += (s, e) =>
                    {
                        try
                        {
                            if (!System.IO.File.Exists(path))
                                System.IO.File.WriteAllBytes(path, image.Bytes);
                            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            RhinoApp.WriteLine("RhinoClaude: could not open the image — " + ex.Message);
                        }
                    };

                    return clickable;
                }
                catch (Exception)
                {
                    return new Label { Text = "(image could not be decoded)" };
                }
            }

            private static Control SectionLabel(string text) => new Label
            {
                Text = text,
                Font = SystemFonts.Bold(SystemFonts.Default().Size - 2),
                TextColor = Color.FromArgb(130, 130, 130)
            };

            private static Control Json(string text) => new TextArea
            {
                Text = Pretty(text),
                ReadOnly = true,
                Wrap = true,
                Height = 90,
                Font = SystemFonts.Default(SystemFonts.Default().Size - 1)
            };

            private static string Pretty(string json)
            {
                if (string.IsNullOrWhiteSpace(json)) return string.Empty;
                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(json))
                    {
                        return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    }
                }
                catch (Exception)
                {
                    return json;
                }
            }
        }
    }
}
