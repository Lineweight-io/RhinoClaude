using System;
using Rhino;
using RhinoClaude.Agent;

namespace RhinoClaude.Services.Agent
{
    /// <summary>
    /// Adapts <see cref="RhinoDoc"/>'s undo API to the testable interface.
    ///
    /// Note the deviation from the plan text: the plan writes <c>doc.Undo.Begin/End</c>,
    /// which is not a RhinoCommon API. The real calls are
    /// <c>RhinoDoc.BeginUndoRecord</c> / <c>RhinoDoc.EndUndoRecord</c>.
    /// </summary>
    public sealed class RhinoUndoRecorder : IUndoRecorder
    {
        private readonly uint _docSerialNumber;

        public RhinoUndoRecorder(RhinoDoc doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _docSerialNumber = doc.RuntimeSerialNumber;
        }

        private RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(_docSerialNumber);

        public uint BeginRecord(string description)
        {
            var doc = Doc;
            if (doc == null) return 0;
            try { return doc.BeginUndoRecord(description); }
            catch (Exception) { return 0; }
        }

        public bool EndRecord(uint serialNumber)
        {
            var doc = Doc;
            if (doc == null || serialNumber == 0) return false;
            try { return doc.EndUndoRecord(serialNumber); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Tracks the undo records this agent session created and can pop them all back off.
    ///
    /// Rhino has no "rewind to mark" API, so revert works by counting the records the
    /// session opened and issuing that many _Undo commands. That is exact only if the
    /// user has not interleaved their own edits — hence the confirmation dialog and the
    /// explicit warning in the sidebar.
    /// </summary>
    public sealed class SessionSnapshotService
    {
        private readonly uint _docSerialNumber;

        public SessionSnapshotService(RhinoDoc doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _docSerialNumber = doc.RuntimeSerialNumber;
            Recorder = new RhinoUndoRecorder(doc);
        }

        public RhinoUndoRecorder Recorder { get; }

        /// <summary>Every undo record the session opened, in order.</summary>
        public UndoRecordLog Log { get; } = new UndoRecordLog();

        public int PendingUndoCount => Log.CompletedCount;

        /// <summary>Open an undo record for one mutation. Always use inside a <c>using</c>.</summary>
        public UndoScope BeginMutation(string toolName)
        {
            return new UndoScope(Recorder, "agent:" + toolName, Log);
        }

        /// <summary>
        /// Pop every undo record this session created. Must run on the UI thread.
        /// Returns the number of undo steps actually performed.
        /// </summary>
        public int RevertSession()
        {
            var doc = RhinoDoc.FromRuntimeSerialNumber(_docSerialNumber);
            if (doc == null) return 0;

            int target = Log.CompletedCount;
            int performed = 0;

            for (int i = 0; i < target; i++)
            {
                // RunScript is used rather than a direct API call because RhinoDoc exposes
                // no supported "undo one record" method across both Rhino 7 and Rhino 8.
                if (!RhinoApp.RunScript(doc.RuntimeSerialNumber, "_Undo", false))
                    break;
                performed++;
            }

            doc.Views.Redraw();
            Log.Clear();
            return performed;
        }

        /// <summary>Forget the session's undo history without undoing anything (used by "New session").</summary>
        public void Forget() => Log.Clear();
    }
}
