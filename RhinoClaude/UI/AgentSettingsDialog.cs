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
        private readonly TextBox _maxCost = new TextBox { Width = 80 };
        private readonly TextBox _maxIterations = new TextBox { Width = 80 };
        private readonly TextBox _maxTokens = new TextBox { Width = 80 };
        private readonly CheckBox _enableScript = new CheckBox { Text = "Enable run_rhinocommon_script (Tier 2 escape hatch)" };
        private readonly TextBox _scriptTimeout = new TextBox { Width = 80 };

        public AgentSettingsDialog(AgentSettings settings)
        {
            _settings = settings ?? new AgentSettings();

            Title = "RhinoClaude settings";
            Padding = new Padding(12);
            Resizable = false;

            _model.Items.Add(new ListItem { Text = "Claude Sonnet 4.5 (default)", Key = AgentSettings.DefaultLoopModel });
            _model.Items.Add(new ListItem { Text = "Claude Sonnet 4.6", Key = "claude-sonnet-4-6" });
            _model.Items.Add(new ListItem { Text = "Claude Sonnet 5", Key = "claude-sonnet-5" });
            _model.Items.Add(new ListItem { Text = "Claude Opus 4.8", Key = "claude-opus-4-8" });
            _model.Items.Add(new ListItem { Text = "Claude Opus 5", Key = "claude-opus-5" });
            _model.SelectedKey = _settings.LoopModel;
            if (_model.SelectedIndex < 0)
            {
                _model.Items.Add(new ListItem { Text = _settings.LoopModel, Key = _settings.LoopModel });
                _model.SelectedKey = _settings.LoopModel;
            }

            _maxCost.Text = _settings.MaxCostUsd.ToString("0.00", CultureInfo.InvariantCulture);
            _maxIterations.Text = _settings.MaxIterations.ToString(CultureInfo.InvariantCulture);
            _maxTokens.Text = _settings.MaxTokens.ToString(CultureInfo.InvariantCulture);
            _enableScript.Checked = _settings.EnableScriptTool;
            _scriptTimeout.Text = _settings.ScriptTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

            var layout = new DynamicLayout { DefaultSpacing = new Size(8, 8) };

            layout.AddRow(new Label { Text = "Loop model", VerticalAlignment = VerticalAlignment.Center }, _model);
            layout.AddRow(new Label { Text = "Cost budget per turn (USD)", VerticalAlignment = VerticalAlignment.Center }, _maxCost);
            layout.AddRow(new Label { Text = "Max iterations per turn", VerticalAlignment = VerticalAlignment.Center }, _maxIterations);
            layout.AddRow(new Label { Text = "Max tokens per response", VerticalAlignment = VerticalAlignment.Center }, _maxTokens);
            layout.AddRow(_enableScript, null);
            layout.AddRow(new Label { Text = "Script timeout (seconds)", VerticalAlignment = VerticalAlignment.Center }, _scriptTimeout);

            layout.AddRow(Divider(), null);
            layout.AddRow(new Label
            {
                Text = "Script log:  " + _settings.ScriptLogPath +
                       "\nCapture log: " + _settings.CaptureLogPath,
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

            if (!int.TryParse(_maxTokens.Text, out int tokens) || tokens < 256 || tokens > 64000)
            {
                MessageBox.Show(this, "Max tokens must be between 256 and 64000.", "Invalid value");
                return;
            }

            if (!int.TryParse(_scriptTimeout.Text, out int timeout) || timeout < 1 || timeout > 60)
            {
                MessageBox.Show(this, "Script timeout must be between 1 and 60 seconds.", "Invalid value");
                return;
            }

            _settings.LoopModel = _model.SelectedKey;
            _settings.MaxCostUsd = cost;
            _settings.MaxIterations = iterations;
            _settings.MaxTokens = tokens;
            _settings.EnableScriptTool = _enableScript.Checked == true;
            _settings.ScriptTimeoutSeconds = timeout;

            Close(_settings);
        }
    }
}
