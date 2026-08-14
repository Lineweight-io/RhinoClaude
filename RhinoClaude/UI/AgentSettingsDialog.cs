using System;
using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using RhinoClaude.Agent;

namespace RhinoClaude.UI
{
    /// <summary>
    /// The settings gear from plan §7.2. Returns the edited settings, or null on cancel.
    ///
    /// The caller hands in a clone, so everything here — including the provider switch, which
    /// has to take effect while the dialog is open so the model list can follow it — is
    /// discarded on cancel.
    /// </summary>
    public sealed class AgentSettingsDialog : Dialog<AgentSettings>
    {
        private readonly AgentSettings _settings;

        private readonly DropDown _provider = new DropDown { Width = 230 };

        // Editable rather than a fixed list: the cheap providers rename their models every few
        // months, so the known ids are suggestions and anything can be typed in.
        private readonly ComboBox _model = new ComboBox { Width = 230 };
        private readonly ComboBox _reviewerModel = new ComboBox { Width = 230 };

        private readonly PasswordBox _apiKey = new PasswordBox { Width = 230 };
        private readonly TextBox _endpoint = new TextBox { Width = 230 };

        private readonly Label _providerNote = Note();
        private readonly Label _modelNote = Note();

        private readonly DropDown _effort = new DropDown { Width = 110 };
        private readonly CheckBox _showThinking = new CheckBox { Text = "Show summarized reasoning in the panel" };
        private readonly TextBox _maxCost = new TextBox { Width = 80 };
        private readonly TextBox _maxIterations = new TextBox { Width = 80 };
        private readonly TextBox _maxTokens = new TextBox { Width = 80 };
        private readonly CheckBox _enableScript = new CheckBox { Text = "Enable run_rhinocommon_script (Tier 2 escape hatch)" };
        private readonly TextBox _scriptTimeout = new TextBox { Width = 80 };
        private readonly CheckBox _enableRhinoCommand = new CheckBox { Text = "Enable run_rhino_command (Tier 3 — scripted Rhino commands)" };
        private readonly CheckBox _enableReview = new CheckBox { Text = "Self-review when the agent signals done" };

        private readonly CheckBox _enableSemantic = new CheckBox
        {
            Text = "Enable the semantic layer (massing, faces, openings — 24 extra tools)",
            ToolTip = "Off gives the agent exactly the phase 1 raw geometry tools. " +
                      "Takes effect on the next session."
        };

        private readonly TextBox _floorToFloor = new TextBox
        {
            Width = 90,
            ToolTip = "Used when Levels are inferred rather than drawn. " +
                      "ClaudeLearnNamingConvention sets this too."
        };
        private readonly TextBox _maxReviewCycles = new TextBox { Width = 80 };

        private bool _loading;

        public AgentSettingsDialog(AgentSettings settings)
        {
            _settings = settings ?? new AgentSettings();

            Title = "RhinoClaude settings";
            Padding = new Padding(12);
            Resizable = false;

            foreach (var info in LlmProviderCatalog.Providers)
                _provider.Items.Add(new ListItem { Text = info.DisplayName, Key = info.Provider.ToString() });
            _provider.SelectedKey = _settings.Provider.ToString();
            _provider.SelectedKeyChanged += (s, e) => OnProviderChanged();

            _model.TextChanged += (s, e) => { if (!_loading) RefreshModelDependentControls(); };

            foreach (var level in ModelCapabilities.EffortLevels)
                _effort.Items.Add(new ListItem { Text = level, Key = level });
            _effort.SelectedKey = _settings.Effort ?? "high";
            if (_effort.SelectedIndex < 0) _effort.SelectedKey = "high";

            _showThinking.Checked = _settings.ShowThinking;

            _maxCost.Text = _settings.MaxCostUsd.ToString("0.00", CultureInfo.InvariantCulture);
            _maxIterations.Text = _settings.MaxIterations.ToString(CultureInfo.InvariantCulture);
            _maxTokens.Text = _settings.MaxTokens.ToString(CultureInfo.InvariantCulture);
            _enableScript.Checked = _settings.EnableScriptTool;
            _scriptTimeout.Text = _settings.ScriptTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            _enableRhinoCommand.Checked = _settings.EnableRhinoCommandTool;
            _enableSemantic.Checked = _settings.EnableSemanticTools;
            _floorToFloor.Text = _settings.FloorToFloorDefault.ToString("0.###", CultureInfo.InvariantCulture);
            _enableRhinoCommand.ToolTip =
                "Off by default. Scripted commands are non-atomic and undo less cleanly than the " +
                "curated tools. The first use in a session raises a notice in the panel.";

            _enableReview.Checked = _settings.EnableSelfReview;
            _maxReviewCycles.Text = _settings.MaxReviewCycles.ToString(CultureInfo.InvariantCulture);

            var layout = new DynamicLayout { DefaultSpacing = new Size(8, 8) };

            layout.AddRow(new Label { Text = "Provider", VerticalAlignment = VerticalAlignment.Center }, _provider);
            layout.AddRow(new Label { Text = "API key", VerticalAlignment = VerticalAlignment.Center }, _apiKey);
            layout.AddRow(new Label { Text = "Endpoint", VerticalAlignment = VerticalAlignment.Center }, _endpoint);
            layout.AddRow(null, _providerNote);
            layout.AddRow(new Label { Text = "Loop model", VerticalAlignment = VerticalAlignment.Center }, _model);
            layout.AddRow(null, _modelNote);
            layout.AddRow(new Label { Text = "Effort", VerticalAlignment = VerticalAlignment.Center }, _effort);
            layout.AddRow(_showThinking, null);
            layout.AddRow(new Label { Text = "Cost budget per turn (USD)", VerticalAlignment = VerticalAlignment.Center }, _maxCost);
            layout.AddRow(new Label { Text = "Max iterations per turn", VerticalAlignment = VerticalAlignment.Center }, _maxIterations);
            layout.AddRow(new Label { Text = "Max tokens per response", VerticalAlignment = VerticalAlignment.Center }, _maxTokens);
            layout.AddRow(_enableScript, null);
            layout.AddRow(new Label { Text = "Script timeout (seconds)", VerticalAlignment = VerticalAlignment.Center }, _scriptTimeout);
            layout.AddRow(_enableRhinoCommand, null);

            layout.AddRow(Divider(), null);
            layout.AddRow(_enableSemantic, null);
            layout.AddRow(new Label { Text = "Firm floor-to-floor (model units, 0 = unset)", VerticalAlignment = VerticalAlignment.Center }, _floorToFloor);

            layout.AddRow(Divider(), null);
            layout.AddRow(_enableReview, null);
            layout.AddRow(new Label { Text = "Reviewer model", VerticalAlignment = VerticalAlignment.Center }, _reviewerModel);
            layout.AddRow(new Label { Text = "Max review cycles per turn", VerticalAlignment = VerticalAlignment.Center }, _maxReviewCycles);

            layout.AddRow(Divider(), null);
            layout.AddRow(new Label
            {
                Text = "Script log:     " + _settings.ScriptLogPath +
                       "\nCapture log:    " + _settings.CaptureLogPath +
                       "\nClassifier log: " + _settings.ClassifierLogPath,
                Font = SystemFonts.Default(SystemFonts.Default().Size - 1),
                TextColor = Color.FromArgb(130, 130, 130)
            }, null);

            var ok = new Button { Text = "OK" };
            ok.Click += (s, e) => Apply();

            var cancel = new Button { Text = "Cancel" };
            cancel.Click += (s, e) => Close(null);

            DefaultButton = ok;
            AbortButton = cancel;

            layout.AddRow(new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Items = { null, ok, cancel }
            }, null);

            Content = layout;
            LoadProviderControls();
        }

        /// <summary>
        /// Move to a different provider, keeping whatever was typed for the old one. The model
        /// boxes have to repopulate immediately — a Claude model id pointed at DeepSeek is a
        /// 404 the user would only find out about on their next turn.
        /// </summary>
        private void OnProviderChanged()
        {
            if (_loading) return;

            // Capture the outgoing provider's state first; SelectProvider files it away.
            _settings.LoopModel = _model.Text?.Trim();
            _settings.ReviewerModel = _reviewerModel.Text?.Trim();
            _settings.SetApiKey(_settings.Provider, _apiKey.Text);
            if (LlmProviderCatalog.Get(_settings.Provider).NeedsCustomEndpoint)
                _settings.CustomEndpoint = _endpoint.Text?.Trim();

            _settings.SelectProvider(LlmProviderCatalog.Parse(_provider.SelectedKey));
            LoadProviderControls();
        }

        private void LoadProviderControls()
        {
            _loading = true;
            try
            {
                var info = LlmProviderCatalog.Get(_settings.Provider);

                FillModels(_model, info, _settings.LoopModel ?? info.DefaultLoopModel);
                FillModels(_reviewerModel, info, _settings.ReviewerModel ?? info.DefaultReviewerModel);

                _apiKey.Text = _settings.ApiKeyFor(_settings.Provider) ?? string.Empty;
                _apiKey.ToolTip = info.IsAnthropic
                    ? "Same key as ClaudeSetKey — either place sets it."
                    : "Stored under " + info.ApiKeySettingsKey + " in Rhino's plugin settings." +
                      (string.IsNullOrEmpty(info.ApiKeyEnvironmentVariable)
                          ? string.Empty
                          : " " + info.ApiKeyEnvironmentVariable + " is used when this is blank.");

                // Editable only for the custom provider; shown for the rest so it is obvious
                // which host the turns are actually going to.
                _endpoint.ReadOnly = !info.NeedsCustomEndpoint;
                _endpoint.Text = info.NeedsCustomEndpoint
                    ? (_settings.CustomEndpoint ?? string.Empty)
                    : info.BaseUrl;
                _endpoint.ToolTip = info.NeedsCustomEndpoint
                    ? "Base URL of an OpenAI-compatible API, e.g. http://localhost:11434/v1. " +
                      "The adapter appends /chat/completions."
                    : "Fixed for this provider. Choose the custom provider to type your own.";

                string note = info.Caveat ?? string.Empty;
                if (!string.IsNullOrEmpty(info.PricingUrl))
                    note += (note.Length > 0 ? "\n" : string.Empty) + "Rates: " + info.PricingUrl;
                _providerNote.Text = note;

                RefreshModelDependentControls();
            }
            finally
            {
                _loading = false;
            }
        }

        private static void FillModels(ComboBox box, LlmProviderInfo info, string current)
        {
            box.Items.Clear();
            foreach (var model in info.KnownModels)
                box.Items.Add(new ListItem { Text = model.Id, Key = model.Id });

            box.Text = current ?? string.Empty;
        }

        private string CurrentModel() => (_model.Text ?? string.Empty).Trim();

        /// <summary>
        /// Effort and summarized thinking only exist on some models — grey them out rather than
        /// letting a setting look active while the request silently omits it. On every
        /// OpenAI-compatible provider both are dropped in translation, so both are disabled.
        /// </summary>
        private void RefreshModelDependentControls()
        {
            string model = CurrentModel();
            bool anthropic = _settings.Provider == LlmProvider.Anthropic;

            bool effortSupported = anthropic && ModelCapabilities.SupportsEffort(model);
            _effort.Enabled = effortSupported;
            _effort.ToolTip = effortSupported
                ? "Controls how much the model thinks and how hard it works. 'high' is the API default."
                : anthropic
                    ? "This model has no effort parameter; the setting is ignored."
                    : "Effort is Anthropic-only and is dropped for this provider.";

            bool thinkingSupported = anthropic && ModelCapabilities.SupportsAdaptiveThinking(model);
            _showThinking.Enabled = thinkingSupported;
            _showThinking.ToolTip = thinkingSupported
                ? "Thinking is billed the same whether or not it is displayed."
                : anthropic
                    ? "This model does not support adaptive thinking; the setting is ignored."
                    : "Reasoning text is shown when the provider streams it, whatever this says.";

            // The friendly name for whichever id is typed, or a warning that it is unpriced.
            var info = LlmProviderCatalog.Get(_settings.Provider);
            string label = null;
            foreach (var known in info.KnownModels)
                if (string.Equals(known.Id, model, StringComparison.OrdinalIgnoreCase)) label = known.Label;

            _modelNote.Text = label ?? (string.IsNullOrEmpty(model)
                ? "Type a model id."
                : "Not a known id — it will be sent as typed, and the cost meter may not have rates for it.");
        }

        private static Label Note() => new Label
        {
            Font = SystemFonts.Default(SystemFonts.Default().Size - 1),
            TextColor = Color.FromArgb(130, 130, 130),
            Wrap = WrapMode.Word,
            Width = 340
        };

        private static Control Divider() => new Eto.Forms.Panel
        {
            Height = 1,
            BackgroundColor = Color.FromArgb(120, 120, 120, 90)
        };

        private void Apply()
        {
            string loopModel = CurrentModel();
            if (loopModel.Length == 0)
            {
                MessageBox.Show(this, "Pick or type a loop model id.", "Invalid value");
                return;
            }

            string reviewerModel = (_reviewerModel.Text ?? string.Empty).Trim();
            if (_enableReview.Checked == true && reviewerModel.Length == 0)
            {
                MessageBox.Show(this, "Pick or type a reviewer model id, or turn self-review off.", "Invalid value");
                return;
            }

            var info = LlmProviderCatalog.Get(_settings.Provider);
            string endpoint = (_endpoint.Text ?? string.Empty).Trim();
            if (info.NeedsCustomEndpoint && endpoint.Length == 0)
            {
                MessageBox.Show(this, "A custom provider needs a base URL, e.g. http://localhost:11434/v1.", "Invalid value");
                return;
            }

            if (info.Quirks.RequiresApiKey && string.IsNullOrWhiteSpace(_apiKey.Text))
            {
                var proceed = MessageBox.Show(this,
                    info.DisplayName + " needs an API key, and none is set. Save anyway?",
                    "No API key", MessageBoxButtons.YesNo, MessageBoxType.Warning);
                if (proceed != DialogResult.Yes) return;
            }

            if (!double.TryParse(_maxCost.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double cost) ||
                cost <= 0)
            {
                MessageBox.Show(this, "Cost budget must be a positive number, e.g. 2.00.", "Invalid value");
                return;
            }

            if (!int.TryParse(_maxIterations.Text, out int iterations) || iterations < 1 || iterations > 200)
            {
                MessageBox.Show(this, "Max iterations must be between 1 and 200.", "Invalid value");
                return;
            }

            if (!int.TryParse(_maxTokens.Text, out int tokens) || tokens < 256 || tokens > 128000)
            {
                MessageBox.Show(this, "Max tokens must be between 256 and 128000.", "Invalid value");
                return;
            }

            // On a model that thinks by default, max_tokens caps thinking plus the answer, so a
            // small limit truncates mid-response rather than merely shortening it.
            if (ModelCapabilities.ThinksByDefault(loopModel) && tokens < 8000)
            {
                var proceed = MessageBox.Show(this,
                    "This model thinks by default, and max tokens caps thinking plus the response " +
                    "together. " + tokens + " is low enough that answers will likely be cut off " +
                    "mid-sentence.\n\nUse it anyway?",
                    "Low max tokens", MessageBoxButtons.YesNo, MessageBoxType.Warning);
                if (proceed != DialogResult.Yes) return;
            }

            if (!int.TryParse(_scriptTimeout.Text, out int timeout) || timeout < 1 || timeout > 60)
            {
                MessageBox.Show(this, "Script timeout must be between 1 and 60 seconds.", "Invalid value");
                return;
            }

            if (!int.TryParse(_maxReviewCycles.Text, out int reviewCycles) || reviewCycles < 1 || reviewCycles > 5)
            {
                MessageBox.Show(this, "Max review cycles must be between 1 and 5.", "Invalid value");
                return;
            }

            string floorToFloorText = (_floorToFloor.Text ?? string.Empty).Trim();
            double floorToFloor = 0;
            if (floorToFloorText.Length > 0
                && (!double.TryParse(floorToFloorText, NumberStyles.Float, CultureInfo.InvariantCulture, out floorToFloor)
                    || floorToFloor < 0))
            {
                MessageBox.Show(this, "Floor-to-floor must be a non-negative number, or 0 for unset.", "Invalid value");
                return;
            }

            _settings.LoopModel = loopModel;
            _settings.ReviewerModel = reviewerModel;
            _settings.SetApiKey(_settings.Provider, _apiKey.Text);
            _settings.RememberModels(_settings.Provider, loopModel, reviewerModel);
            if (info.NeedsCustomEndpoint) _settings.CustomEndpoint = endpoint;

            _settings.EnableSelfReview = _enableReview.Checked == true;
            _settings.MaxReviewCycles = reviewCycles;
            _settings.Effort = _effort.SelectedKey ?? "high";
            _settings.ShowThinking = _showThinking.Checked == true;
            _settings.MaxCostUsd = cost;
            _settings.MaxIterations = iterations;
            _settings.MaxTokens = tokens;
            _settings.EnableScriptTool = _enableScript.Checked == true;
            _settings.ScriptTimeoutSeconds = timeout;
            _settings.EnableRhinoCommandTool = _enableRhinoCommand.Checked == true;
            _settings.EnableSemanticTools = _enableSemantic.Checked == true;
            _settings.FloorToFloorDefault = floorToFloor;

            Close(_settings);
        }
    }
}
