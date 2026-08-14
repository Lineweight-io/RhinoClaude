using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;

namespace RhinoClaude.Commands
{
    /// <summary>
    /// Command: ClaudeSetKey
    /// Prompts the user to enter their Anthropic API key and saves it
    /// in the plugin's persistent settings.
    /// </summary>
    public class ClaudeSetKeyCommand : Command
    {
        public override string EnglishName => "ClaudeSetKey";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = RhinoClaudePlugin.Instance;

            var getKey = new GetString();
            getKey.SetCommandPrompt("Enter your Anthropic API key (starts with 'sk-ant-')");

            if (getKey.Get() != Rhino.Input.GetResult.String)
                return Result.Cancel;

            string apiKey = getKey.StringResult().Trim();

            if (string.IsNullOrEmpty(apiKey))
            {
                RhinoApp.WriteLine("RhinoClaude: No key entered.");
                return Result.Cancel;
            }

            if (!apiKey.StartsWith("sk-ant-"))
            {
                RhinoApp.WriteLine("RhinoClaude: Warning — key doesn't start with 'sk-ant-'. Saving anyway.");
            }

            // Save to plugin settings (persists across sessions)
            plugin.Settings.SetString("AnthropicApiKey", apiKey);
            plugin.AnthropicClient.SetApiKey(apiKey);

            RhinoApp.WriteLine("RhinoClaude: API key saved successfully.");
            return Result.Success;
        }
    }
}
