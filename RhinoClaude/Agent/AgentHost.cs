using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rhino;
using RhinoClaude.Services.Agent;
using RhinoClaude.Services.Semantic;
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
            lock (Gate)
            {
                if (Hosts.TryGetValue(documentSerialNumber, out var host)) host.Release();
                Hosts.Remove(documentSerialNumber);
            }
        }

        private AgentHost(RhinoDoc doc)
        {
            DocumentSerialNumber = doc.RuntimeSerialNumber;
            Settings = new AgentSettings();

            Query = new RhinoQueryService(doc);
            Snapshots = new SessionSnapshotService(doc);
            Mutation = new RhinoMutationService(Query, Snapshots);
            Interaction = new RhinoInteractionService(Query);
            Conversations = new AgentConversationStore(doc);

            ScriptLog = new JsonlLogger(Settings.ScriptLogPath);
            CaptureLog = new JsonlLogger(Settings.CaptureLogPath);
            ClassifierLog = new JsonlLogger(Settings.ClassifierLogPath);

            // The semantic layer sits on top of the phase 1 services, never beside them: its
            // mutations go through RhinoMutationService so undo and session-revert stay
            // consistent, and its reads go through the registry's two-tier cache.
            LayerConventions = new LayerConventionStore(doc, Settings);
            LayerConventionStore.Restore(Settings);

            History_Reader = new BooleanHistoryReader(doc);
            Classifier = new SemanticClassifier(doc, LayerConventions);
            GeometryAnalyzer = new MassGeometryAnalyzer(doc, History_Reader);
            Elements = new ElementRegistry(doc, Classifier, GeometryAnalyzer, ClassifierLog);
            SemanticQuery = new SemanticQueryService(doc, Elements);

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
        public AgentConversationStore Conversations { get; }
        public RhinoCommandService Command { get; private set; }
        public SessionSnapshotService Snapshots { get; }
        public JsonlLogger ScriptLog { get; }
        public JsonlLogger CaptureLog { get; }
        public JsonlLogger ClassifierLog { get; }

        // ── Semantic layer ────────────────────────────────────────────

        public LayerConventionStore LayerConventions { get; }
        public SemanticClassifier Classifier { get; }
        public MassGeometryAnalyzer GeometryAnalyzer { get; }
        public BooleanHistoryReader History_Reader { get; }
        public ElementRegistry Elements { get; }
        public SemanticQueryService SemanticQuery { get; }
        public SemanticMutationService SemanticMutation { get; private set; }

        public ViewCaptureService Capture { get; private set; }
        public ScriptExecutorService Script { get; private set; }
        public SelfReviewService Review { get; private set; }
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

            Command = new RhinoCommandService(Query, Snapshots, ScriptLog, sessionId);
            Command.FirstUseInSession += line => FirstScriptedCommand?.Invoke(line);

            Review = new SelfReviewService(Query, Capture, client, Mutation.MutationLog)
            {
                ReviewerModel = Settings.ReviewerModel
            };

            Registry = BuildRegistry();

            bool scriptEnabled = Settings.EnableScriptTool;
            bool semanticEnabled = Settings.EnableSemanticTools;
            Session = new AgentSession(
                client,
                Registry,
                Settings,
                () => SystemPrompt.Build(scriptEnabled, semanticEnabled));

            WireReview(Session);

            History.Add(Session);
            Snapshots.Forget();
            Mutation.MutationLog.Clear();
            return Session;
        }

        /// <summary>Re-register tools after a settings change (model, budget, script toggle).</summary>
        public void ApplySettings()
        {
            Script.DefaultTimeoutSeconds = Settings.ScriptTimeoutSeconds;
            Review.ReviewerModel = Settings.ReviewerModel;
            if (Session != null) WireReview(Session);

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
        /// Connect signal_done to self-review. The facts and captures must be gathered on
        /// Rhino's UI thread; only the reviewer HTTP call runs in the background.
        /// </summary>
        private void WireReview(AgentSession session)
        {
            if (!Settings.EnableSelfReview)
            {
                session.ReviewHook = null;
                return;
            }

            session.ReviewHook = async (summary, expectedOutcome, token) =>
            {
                Review.ReviewerModel = Settings.ReviewerModel;

                // The mark is 0 because review covers the whole session's work, not just the
                // last turn — the user judges the model in front of them, not a diff.
                var facts = await ToolDispatcher.RunOnUiThreadAsync(
                    () => Review.CollectFacts(session.LastUserMessage, summary, expectedOutcome, 0))
                    .ConfigureAwait(false);

                var captures = await ToolDispatcher.RunOnUiThreadAsync(
                    () => Review.CaptureForReview(0)).ConfigureAwait(false);

                return await Review.ReviewAsync(facts, captures, token).ConfigureAwait(false);
            };
        }

        /// <summary>
        /// The full Tier 1 registry. Registration order is stable and matters: the tools array
        /// renders first in the prompt, so shuffling it would break prompt caching every turn.
        /// </summary>
        private ToolRegistry BuildRegistry()
        {
            var registry = new ToolRegistry();
            registry.RegisterAll(Phase1Tools.Build(Query, Mutation, Capture, Script, Settings));
            registry.RegisterAll(Tier1Tools.Build(
                Query, Mutation, Interaction,
                Settings.EnableRhinoCommandTool ? Command : null));

            // Semantic tools register after the raw ones, so the raw block stays byte-identical
            // in the prompt whether or not the semantic layer is on — the cached prefix survives
            // a settings change.
            if (Settings.EnableSemanticTools)
            {
                SemanticMutation = new SemanticMutationService(Query, Mutation, Elements);
                registry.RegisterAll(SemanticReadTools.Build(SemanticQuery));
                registry.RegisterAll(SemanticWriteTools.Build(SemanticMutation));
            }

            return registry;
        }

        /// <summary>
        /// Raised the first time a session runs a scripted Rhino command, so the sidebar can
        /// show the banner from plan §4.7. Non-blocking: the user is told, not asked.
        /// </summary>
        public event Action<string> FirstScriptedCommand;

        /// <summary>True when the tool set changed and only a new session will pick it up.</summary>
        public bool ToolSetChangedSinceSessionStart(bool scriptEnabledAtStart) =>
            scriptEnabledAtStart != Settings.EnableScriptTool;

        /// <summary>Release the registry's document-event subscriptions when the doc closes.</summary>
        public void Release() => Elements?.Dispose();
    }
}
