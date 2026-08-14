using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rhino;
using RhinoClaude.Services.Agent;
using RhinoClaude.Tools;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Owns the per-document object graph: services, registry, snapshot log, and the
    /// current <see cref="AgentSession"/>. One host per open Rhino document, keyed on
    /// the document's runtime serial number.
    /// </summary>
    public sealed class AgentHost
    {
        private static readonly Dictionary<uint, AgentHost> Hosts = new Dictionary<uint, AgentHost>();
        private static readonly object Gate = new object();

        public static AgentHost For(RhinoDoc doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            lock (Gate)
            {
                if (!Hosts.TryGetValue(doc.RuntimeSerialNumber, out var host))
                {
                    host = new AgentHost(doc);
                    Hosts[doc.RuntimeSerialNumber] = host;
                }
                return host;
            }
        }

        public static void Forget(uint documentSerialNumber)
        {
            lock (Gate) Hosts.Remove(documentSerialNumber);
        }

        private AgentHost(RhinoDoc doc)
        {
            DocumentSerialNumber = doc.RuntimeSerialNumber;
            Settings = new AgentSettings();

            Query = new RhinoQueryService(doc);
            Snapshots = new SessionSnapshotService(doc);
            Mutation = new RhinoMutationService(Query, Snapshots);
            Interaction = new RhinoInteractionService(Query);

            ScriptLog = new JsonlLogger(Settings.ScriptLogPath);
            CaptureLog = new JsonlLogger(Settings.CaptureLogPath);

            StartSession();

            // Roslyn's first compile costs several hundred milliseconds. Pay it on a
            // background thread now rather than inside the agent's first script call.
            if (Settings.EnableScriptTool)
                Task.Run(() => ScriptExecutorService.Warm());
        }

        public uint DocumentSerialNumber { get; }
        public AgentSettings Settings { get; }

        public RhinoQueryService Query { get; }
        public RhinoMutationService Mutation { get; }
        public RhinoInteractionService Interaction { get; }
        public SessionSnapshotService Snapshots { get; }
        public JsonlLogger ScriptLog { get; }
        public JsonlLogger CaptureLog { get; }

        public ViewCaptureService Capture { get; private set; }
        public ScriptExecutorService Script { get; private set; }
        public ToolRegistry Registry { get; private set; }
        public AgentSession Session { get; private set; }

        /// <summary>Sessions this document has had, most recent last — backs the header dropdown.</summary>
        public List<AgentSession> History { get; } = new List<AgentSession>();

        /// <summary>Start a fresh session, keeping settings and services.</summary>
        public AgentSession StartSession()
        {
            var client = RhinoClaudePlugin.Instance?.AnthropicClient
                         ?? throw new InvalidOperationException("The plugin is not initialised.");

            var sessionId = Guid.NewGuid();

            Capture = new ViewCaptureService(Query, CaptureLog, sessionId);
            Script = new ScriptExecutorService(Query, Snapshots, ScriptLog, sessionId)
            {
                DefaultTimeoutSeconds = Settings.ScriptTimeoutSeconds
            };

            Registry = BuildRegistry();

            bool scriptEnabled = Settings.EnableScriptTool;
            Session = new AgentSession(
                client,
                Registry,
                Settings,
                () => SystemPrompt.Build(scriptEnabled));

            History.Add(Session);
            Snapshots.Forget();
            return Session;
        }

        /// <summary>Re-register tools after a settings change (model, budget, script toggle).</summary>
        public void ApplySettings()
        {
            Script.DefaultTimeoutSeconds = Settings.ScriptTimeoutSeconds;

            var current = Session;
            bool scriptEnabled = Settings.EnableScriptTool;

            Registry = BuildRegistry();

            // The registry the running session captured is immutable for its lifetime, so a
            // tool-set change takes effect on the next session. Model and budget changes are
            // read per turn and take effect immediately.
            if (current != null)
            {
                current.Settings.LoopModel = Settings.LoopModel;
                current.Settings.MaxCostUsd = Settings.MaxCostUsd;
                current.Settings.MaxIterations = Settings.MaxIterations;
                current.Settings.MaxTokens = Settings.MaxTokens;
            }
        }

        /// <summary>
        /// The full Tier 1 registry. Registration order is stable and matters: the tools array
        /// renders first in the prompt, so shuffling it would break prompt caching every turn.
        /// </summary>
        private ToolRegistry BuildRegistry()
        {
            var registry = new ToolRegistry();
            registry.RegisterAll(Phase1Tools.Build(Query, Mutation, Capture, Script, Settings));
            registry.RegisterAll(Tier1Tools.Build(Query, Mutation, Interaction));
            return registry;
        }

        /// <summary>True when the tool set changed and only a new session will pick it up.</summary>
        public bool ToolSetChangedSinceSessionStart(bool scriptEnabledAtStart) =>
            scriptEnabledAtStart != Settings.EnableScriptTool;
    }
}
