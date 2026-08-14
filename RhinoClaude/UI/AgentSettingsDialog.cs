using System;
using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using RhinoClaude.Agent;

namespace RhinoClaude.UI
{
    /// <summary>
    /// The settings gear from plan §7.2. Returns the edited settings, or null on cancel.
    /// </summary>
    public sealed class AgentSettingsDialog : Dialog<AgentSettings>
    {
        private readonly AgentSettings _settings;

        private readonly DropDown _model = new DropDown();
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
        private readonly DropDown _reviewerModel = new DropDown { Width = 190 };
        private readonly TextBox _maxReviewCycles = new TextBox { Width = 80 };

        public AgentSettingsDialog(AgentSettings settings)
        {
            _settings = settings ?? new AgentSettings();

            Title = "RhinoClaude settings";
            Padding = new Padding(12);
            Resizable = false;

            _model.Items.Add(new ListItem { Text = "Claude Sonnet 5 (default)", Key = AgentSettings.DefaultLoopModel });
            _model.Items.Add(new ListItem { Text = "Claude Sonnet 4.6", Key = "claude-sonnet-4-6" });
            _model.Items.Add(new ListItem { Text = "Claude Sonnet 4.5 (legacy)", Key = "claude-sonnet-4-5-20250929" });
            _model.Items.Add(new ListItem { Text = "Claude Opus 5", Key = "claude-opus-5" });
            _model.Items.Add(new ListItem { Text = "Claude Opus 4.8", Key = "claude-opus-4-8" });
            _model.SelectedKey = _settings.LoopModel;
            if (_model.SelectedIndex < 0)
            {
                _model.Items.Add(new ListItem { Text = _settings.LoopModel, Key = _settings.LoopModel });
                _model.SelectedKey = _settings.LoopModel;
            }
            _model.SelectedKeyChanged += (s, e) => RefreshModelDependentControls();

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

            _reviewerModel.Items.Add(new ListItem { Text = "Claude Opus 5 (default)", Key = AgentSettings.DefaultReviewerModel });
            _reviewerModel.Items.Add(new ListItem { Text = "Claude Opus 4.8", Key = "claude-opus-4-8" });
            _reviewerModel.Items.Add(new ListItem { Text = "Claude Sonnet 5", Key = "claude-sonnet-5" });
            _reviewerModel.SelectedKey = _settings.ReviewerModel;
            if (_reviewerModel.SelectedIndex < 0)
            {
                _reviewerModel.Items.Add(new ListItem { Text = _settings.ReviewerModel, Key = _settings.ReviewerModel });
                _reviewerModel.SelectedKey = _settings.ReviewerModel;
            }

            _enableReview.Checked = _settings.EnableSelfReview;
            _maxReviewCycles.Text = _settings.MaxReviewCycles.ToString(CultureInfo.InvariantCulture);

            var layout = new DynamicLayout { DefaultSpacing = new Size(8, 8) };

            layout.AddRow(new Label { Text = "Loop model", VerticalAlignment = VerticalAlignment.Center }, _model);
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
            RefreshModelDependentControls();
        }

        /// <summary>
        /// Effort and summarized thinking only exist on some models — grey them out rather than
        /// letting a setting look active while the request silently omits it.
        /// </summary>
        private void RefreshModelDependentControls()
        {
            string model = _model.SelectedKey;

            bool effortSupported = ModelCapabilities.SupportsEffort(model);
            _effort.Enabled = effortSupported;
            _effort.ToolTip = effortSupported
                ? "Controls how much the model thinks and how hard it works. 'high' is the API default."
                : "This model has no effort parameter; the setting is ignored.";

            bool thinkingSupported = ModelCapabilities.SupportsAdaptiveThinking(model);
            _showThinking.Enabled = thinkingSupported;
            _showThinking.ToolTip = thinkingSupported
                ? "Thinking is billed the same whether or not it is displayed."
                : "This model does not support adaptive thinking; the setting is ignored.";
        }

        private static Control Divider() => new Eto.Forms.Panel
        {
            Height = 1,
            BackgroundColor = Color.FromArgb(120, 120, 120, 90)
        };

        private void Apply()
        {
            if (!double.TryParse(_maxCost.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double cost) ||
                cost <= 0)
            {
                MessageBox.Show(this, "Cost budget must be a positive number, e.g. 0.50.", "Invalid value");
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
            if (ModelCapabilities.ThinksByDefault(_model.SelectedKey) && tokens < 8000)
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

            _settings.LoopModel = _model.SelectedKey;
            _settings.ReviewerModel = _reviewerModel.SelectedKey;
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
