using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino;
using Rhino.Geometry;
using RhinoClaude.Agent;

namespace RhinoClaude.Services.Agent
{
    /// <summary>
    /// Tier 3 (plan §4.7): run a Rhino command as if typed at the command line.
    ///
    /// This is the last resort, below the C# escape hatch. Scripted commands are non-atomic,
    /// can prompt for input that nobody is there to give, and undo unpredictably — so the
    /// tool description says so, the first use per session raises a banner in the sidebar,
    /// and every call is logged next to the script log.
    ///
    /// UI-thread only.
    /// </summary>
    public sealed class RhinoCommandService
    {
        private readonly RhinoQueryService _query;
        private readonly SessionSnapshotService _snapshots;
        private readonly JsonlLogger _log;
        private readonly Guid _sessionId;

        public RhinoCommandService(
            RhinoQueryService query,
            SessionSnapshotService snapshots,
            JsonlLogger log,
            Guid sessionId)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
            _log = log;
            _sessionId = sessionId;
        }

        /// <summary>Raised the first time this session runs a scripted command.</summary>
        public event Action<string> FirstUseInSession;

        public bool HasRunThisSession { get; private set; }
        public int CurrentIteration { get; set; }

        /// <summary>
        /// Commands that would take the session somewhere it cannot come back from — closing
        /// the document, quitting Rhino, or opening a modal the agent cannot dismiss. Rejected
        /// before execution rather than left to chance.
        /// </summary>
        private static readonly string[] BlockedCommands =
        {
            "_Exit", "_Quit", "_New", "_Open", "_Close", "_SaveAs", "_Revert",
            "_PlugInManager", "_Options", "_ToolbarLayout", "_ScriptEditor",
            "_EditPythonScript", "_RunPythonScript"
        };

        public ToolResult Run(string commandLine, string purpose)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return ToolResult.Fail("'commandLine' is required.");
            if (string.IsNullOrWhiteSpace(purpose))
                return ToolResult.Fail("'purpose' is required — it is what the command log is analysed by.");

            string blocked = FindBlocked(commandLine);
            if (blocked != null)
            {
                var rejected = ToolResult.Fail(
                    "Rejected before execution: '" + blocked + "' would close the document, quit Rhino, or " +
                    "open a dialog nobody can answer. Use the curated tools or run_rhinocommon_script instead.");
                Log(commandLine, purpose, rejected, 0, 0, 0, "blocked");
                return rejected;
            }

            if (!HasRunThisSession)
            {
                HasRunThisSession = true;
                // Non-blocking by design (plan §4.7): the user is told, not asked.
                try { FirstUseInSession?.Invoke(commandLine); } catch (Exception) { }
            }

            var doc = _query.Doc;
            var before = SnapshotIds(doc);
            var stopwatch = Stopwatch.StartNew();

            bool ok;
            string error = null;

            try
            {
                using (_snapshots.BeginMutation("command:" + Truncate(purpose, 60)))
                {
                    // echo:false keeps the command line quiet; the agent reads the object
                    // delta below rather than scraping Rhino's console output.
                    ok = RhinoApp.RunScript(doc.RuntimeSerialNumber, commandLine, false);
                    doc.Views.Redraw();
                }
            }
            catch (Exception ex)
            {
                ok = false;
                error = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                stopwatch.Stop();
            }

            var after = SnapshotIds(doc);
            var created = after.Except(before).Select(g => g.ToString()).ToList();
            var deleted = before.Except(after).Select(g => g.ToString()).ToList();

            string notes;
            if (!ok && error == null)
            {
                notes = "Rhino reported the command did not complete. It may not exist, may have been " +
                        "cancelled, or may have been waiting for input that never came. Scripted commands " +
                        "need a leading underscore and dash form, e.g. '_-Render'.";
            }
            else if (created.Count == 0 && deleted.Count == 0)
            {
                notes = "The command ran but changed no objects. If you expected geometry, the command " +
                        "may have needed a selection or extra arguments on the same line.";
            }
            else
            {
                notes = "Created " + created.Count + " and removed " + deleted.Count + " object(s).";
            }

            var result = new ToolResult
            {
                Success = ok && error == null,
                Error = error ?? (ok ? null : "The command did not complete."),
                Json = ToolJson.Serialize(new Dictionary<string, object>
                {
                    { "commandLine", ToolJson.Safe(commandLine) },
                    { "completed", ok },
                    { "executionMs", stopwatch.ElapsedMilliseconds },
                    { "createdObjectIds", created },
                    { "deletedObjectIds", deleted },
                    { "notes", notes }
                })
            };

            Log(commandLine, purpose, result, stopwatch.ElapsedMilliseconds,
                created.Count, deleted.Count, result.Success ? "ok" : "failed");

            return result;
        }

        private static string FindBlocked(string commandLine)
        {
            // Compare on word boundaries so "_-Render" is not blocked by "_Revert".
            var tokens = commandLine.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)
            {
                string normalized = token.TrimStart('!', '_', '-');
                foreach (var command in BlockedCommands)
                {
                    string bare = command.TrimStart('_');
                    if (string.Equals(normalized, bare, StringComparison.OrdinalIgnoreCase))
                        return command;
                }
            }
            return null;
        }

        private static HashSet<Guid> SnapshotIds(RhinoDoc doc)
        {
            var ids = new HashSet<Guid>();
            foreach (var obj in doc.Objects)
                if (!obj.IsDeleted) ids.Add(obj.Id);
            return ids;
        }

        private void Log(string commandLine, string purpose, ToolResult result,
                         long elapsedMs, int created, int deleted, string outcome)
        {
            _log?.Append(new Dictionary<string, object>
            {
                { "kind", "run_rhino_command" },
                { "sessionId", _sessionId.ToString() },
                { "iteration", CurrentIteration },
                { "commandLine", commandLine },
                { "purpose", purpose },
                { "outcome", outcome },
                { "success", result.Success },
                { "error", result.Error },
                { "durationMs", elapsedMs },
                { "createdCount", created },
                { "deletedCount", deleted }
            });
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
