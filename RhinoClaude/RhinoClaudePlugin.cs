using Rhino;
using Rhino.PlugIns;
using Rhino.UI;
using RhinoClaude.Services;
using RhinoClaude.UI;

namespace RhinoClaude
{
    /// <summary>
    /// The main plugin class for RhinoClaude.
    /// Rhino discovers and loads this automatically via the assembly .rhp file.
    /// </summary>
    public class RhinoClaudePlugin : PlugIn
    {
        /// <summary>Singleton instance of the plugin.</summary>
        public static RhinoClaudePlugin Instance { get; private set; }

        /// <summary>Shared Claude API service used by all commands.</summary>
        public ClaudeApiService ClaudeService { get; private set; }

        /// <summary>Shared tag service for reading/writing semantic tags.</summary>
        public TagService TagService { get; private set; }

        public RhinoClaudePlugin()
        {
            Instance = this;
        }

        /// <summary>
        /// Called once when the plugin is first loaded.
        /// Initializes services.
        /// </summary>
        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            RhinoApp.WriteLine("RhinoClaude plugin loaded (Phase 3 — Auto-Modeling).");

            // Initialize services
            ClaudeService = new ClaudeApiService();
            TagService = new TagService();

            // Try to load the API key from plugin settings
            string savedKey = Settings.GetString("AnthropicApiKey", string.Empty);
            if (!string.IsNullOrEmpty(savedKey))
            {
                ClaudeService.SetApiKey(savedKey);
                RhinoApp.WriteLine("RhinoClaude: API key loaded from settings.");
            }
            else
            {
                // Fall back to environment variable
                string envKey = System.Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                if (!string.IsNullOrEmpty(envKey))
                {
                    ClaudeService.SetApiKey(envKey);
                    RhinoApp.WriteLine("RhinoClaude: API key loaded from environment variable.");
                }
                else
                {
                    RhinoApp.WriteLine("RhinoClaude: No API key found. Use 'ClaudeSetKey' command to set one.");
                }
            }

            // Register the Tag Inspector docked panel
            Panels.RegisterPanel(
                this,
                typeof(TagInspectorPanel),
                "Tag Inspector",
                (System.Drawing.Icon)null);

            RhinoApp.WriteLine("RhinoClaude: Commands available — ClaudeAsk, ClaudeRunScript, ClaudeTag, RCSetTag, RCQuery, RCInspectTags, RCValidateTags, RCTagInspector, RCBuildFromDiagram");
            return LoadReturnCode.Success;
        }
    }
}
