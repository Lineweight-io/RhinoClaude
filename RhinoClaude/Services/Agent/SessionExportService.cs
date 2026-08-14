using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.FileIO;
using RhinoClaude.Agent;

namespace RhinoClaude.Services.Agent
{
    /// <summary>What the mutation log's ids resolve to in the document right now.</summary>
    public sealed class ResultExportPlan
    {
        /// <summary>Ids still present in the document, in the order the agent first touched them.</summary>
        public List<Guid> Objects { get; } = new List<Guid>();

        /// <summary>Ids the log knows about that are no longer in the document.</summary>
        public List<string> Missing { get; } = new List<string>();

        /// <summary>Ids in the log that were not parseable as GUIDs — a bug if it ever happens.</summary>
        public List<string> Unparseable { get; } = new List<string>();

        public bool HasAnything => Objects.Count > 0;

        public string Describe()
        {
            var parts = new List<string> { Objects.Count + " object(s)" };
            if (Missing.Count > 0) parts.Add(Missing.Count + " since deleted");
            if (Unparseable.Count > 0) parts.Add(Unparseable.Count + " unreadable id(s)");
            return string.Join(", ", parts);
        }
    }

    /// <summary>Outcome of writing an export file.</summary>
    public sealed class ExportOutcome
    {
        public bool Success { get; set; }
        public string Path { get; set; }
        public string Error { get; set; }
        /// <summary>Objects actually written — lower than requested if Rhino refused a selection.</summary>
        public int ObjectsWritten { get; set; }
        public long Bytes { get; set; }

        public static ExportOutcome Fail(string error) => new ExportOutcome { Success = false, Error = error };
    }

    /// <summary>
    /// The two sidebar exports: the conversation as markdown, and the objects the agent
    /// produced as a standalone .3dm.
    ///
    /// UI-thread only — it reads and briefly changes the document's selection. The formatting
    /// it delegates to <see cref="ConversationExport"/>, which knows nothing about Rhino.
    /// </summary>
    public sealed class SessionExportService
    {
        private readonly uint _docSerialNumber;

        public SessionExportService(RhinoDoc doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _docSerialNumber = doc.RuntimeSerialNumber;
        }

        private RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(_docSerialNumber);

        /// <summary>The document's file name, or empty for a document that was never saved.</summary>
        public string DocumentName()
        {
            var doc = Doc;
            if (doc == null) return string.Empty;

            string name = doc.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name;

            try
            {
                return string.IsNullOrWhiteSpace(doc.Path) ? string.Empty : Path.GetFileName(doc.Path);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Where the save dialog should open: next to the .3dm if it has been saved, otherwise
        /// the Desktop, otherwise Documents.
        /// </summary>
        public string SuggestedDirectory()
        {
            var doc = Doc;
            try
            {
                if (doc != null && !string.IsNullOrWhiteSpace(doc.Path))
                {
                    string directory = Path.GetDirectoryName(doc.Path);
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) return directory;
                }
            }
            catch (Exception) { /* fall through to the shell folders */ }

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) return desktop;

            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        // ── Conversation ──────────────────────────────────────────────

        /// <summary>
        /// Gather everything the markdown needs. Called on the UI thread; the formatting that
        /// follows is pure.
        /// </summary>
        public ConversationExportRequest BuildConversationRequest(
            AgentSession session, SessionMutationLog mutations, AgentSettings settings, int pendingUndoCount)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            return new ConversationExportRequest
            {
                DocumentName = DocumentName(),
                SessionDisplayName = session.DisplayName,
                SessionId = session.Id.ToString(),
                StartedLocal = session.CreatedUtc.ToLocalTime(),
                ExportedLocal = DateTime.Now,
                Model = session.Settings?.LoopModel ?? settings?.LoopModel,
                ReviewerModel = settings != null && settings.EnableSelfReview ? settings.ReviewerModel : null,
                Messages = session.Messages.ToList(),
                Invocations = session.Invocations.ToList(),
                SessionUsage = session.SessionUsage,
                Mutations = mutations?.All,
                PendingUndoCount = pendingUndoCount
            };
        }

        public ExportOutcome WriteMarkdown(string path, string markdown)
        {
            if (string.IsNullOrWhiteSpace(path)) return ExportOutcome.Fail("No file path was given.");

            try
            {
                EnsureDirectory(path);
                File.WriteAllText(path, ConversationExport.ForFile(markdown), new System.Text.UTF8Encoding(false));

                return new ExportOutcome
                {
                    Success = true,
                    Path = path,
                    Bytes = new FileInfo(path).Length
                };
            }
            catch (Exception ex)
            {
                return ExportOutcome.Fail(ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ── Model results ─────────────────────────────────────────────

        /// <summary>
        /// Resolve the session's touched ids against the live document. Ids that no longer
        /// exist are reported rather than silently dropped — "12 objects, 3 since deleted" is
        /// information the reviewer wants before they open the file.
        /// </summary>
        public ResultExportPlan PlanResultExport(SessionMutationLog log)
        {
            var plan = new ResultExportPlan();
            var doc = Doc;
            if (log == null || doc == null) return plan;

            foreach (var idText in log.SurvivingTouchedIds())
            {
                if (!Guid.TryParse(idText, out var id))
                {
                    plan.Unparseable.Add(idText);
                    continue;
                }

                var obj = doc.Objects.FindId(id);
                if (obj == null || obj.IsDeleted) plan.Missing.Add(idText);
                else plan.Objects.Add(id);
            }

            return plan;
        }

        /// <summary>
        /// Write the given objects to a standalone .3dm.
        ///
        /// Rhino's own writer is used with <c>WriteSelectedObjectsOnly</c> rather than building
        /// a File3dm by hand, because that is what carries layers, materials, block definitions
        /// and user data across with the geometry — a hand-built file would quietly lose them.
        /// The cost is that the document's selection has to be borrowed for the duration; it is
        /// put back in the finally block.
        /// </summary>
        public ExportOutcome WriteResult3dm(IEnumerable<Guid> ids, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return ExportOutcome.Fail("No file path was given.");

            var doc = Doc;
            if (doc == null) return ExportOutcome.Fail("The document is no longer open.");

            var wanted = (ids ?? Enumerable.Empty<Guid>()).Distinct().ToList();
            if (wanted.Count == 0)
                return ExportOutcome.Fail("There are no agent-created or agent-modified objects to export.");

            // The review file is a copy alongside the working file, never the working file
            // itself — writing selected-objects-only over the document would destroy it.
            if (IsSameFileAsDocument(doc, path))
            {
                return ExportOutcome.Fail(
                    "That is the document's own file. Pick a different name — the export writes " +
                    "only the agent's objects, so it would replace your model.");
            }

            List<Guid> previousSelection;
            try
            {
                previousSelection = doc.Objects.GetSelectedObjects(true, true).Select(o => o.Id).ToList();
            }
            catch (Exception)
            {
                previousSelection = new List<Guid>();
            }

            try
            {
                EnsureDirectory(path);

                doc.Objects.UnselectAll();

                foreach (var id in wanted)
                {
                    // Ignore grip state, layer locking and layer visibility: an object the agent
                    // made on a layer the user has since locked or hidden is still part of the
                    // work under review.
                    doc.Objects.Select(id, true, true, true, true, true, true);
                }

                int selected = doc.Objects.GetSelectedObjects(true, true).Count();
                if (selected == 0)
                {
                    return ExportOutcome.Fail(
                        "Rhino would not select any of the " + wanted.Count +
                        " object(s) to export — they may all have been deleted or locked.");
                }

                using (var options = new FileWriteOptions())
                {
                    options.WriteSelectedObjectsOnly = true;
                    options.SuppressDialogBoxes = true;
                    options.SuppressAllInput = true;
                    options.UpdateDocumentPath = false;   // this must not become the document's own path
                    options.IncludeRenderMeshes = true;
                    options.IncludeBitmapTable = true;
                    options.WriteUserData = true;

                    if (!doc.WriteFile(path, options))
                        return ExportOutcome.Fail("Rhino refused to write " + path + ".");
                }

                return new ExportOutcome
                {
                    Success = true,
                    Path = path,
                    ObjectsWritten = selected,
                    Bytes = File.Exists(path) ? new FileInfo(path).Length : 0
                };
            }
            catch (Exception ex)
            {
                return ExportOutcome.Fail(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try
                {
                    doc.Objects.UnselectAll();
                    foreach (var id in previousSelection) doc.Objects.Select(id, true);
                    doc.Views.Redraw();
                }
                catch (Exception) { /* restoring the selection is a courtesy, not the export */ }
            }
        }

        private static bool IsSameFileAsDocument(RhinoDoc doc, string path)
        {
            try
            {
                if (doc == null || string.IsNullOrWhiteSpace(doc.Path)) return false;
                return string.Equals(
                    Path.GetFullPath(doc.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;   // an unparseable path is the file dialog's problem, not ours
            }
        }

        private static void EnsureDirectory(string path)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }
    }
}
