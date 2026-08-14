using System;
using System.Collections.Generic;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// The two RhinoDoc undo calls, behind an interface so undo-group correctness can be
    /// tested without a running Rhino.
    /// </summary>
    public interface IUndoRecorder
    {
        uint BeginRecord(string description);
        bool EndRecord(uint serialNumber);
    }

    /// <summary>Notified as the session opens and closes undo records, so it can be reverted later.</summary>
    public interface IUndoRecordSink
    {
        void RecordOpened(uint serialNumber, string description);
        void RecordClosed(uint serialNumber);
    }

    /// <summary>
    /// Opens an undo record on construction and guarantees it is closed on dispose,
    /// including when the body throws. Every mutation tool runs inside one of these —
    /// non-negotiable per the plan's risk #4.
    ///
    /// A serial number of 0 means Rhino declined to open a record (already inside one,
    /// or undo recording disabled). In that case dispose is a no-op rather than an
    /// attempt to close somebody else's record.
    /// </summary>
    public sealed class UndoScope : IDisposable
    {
        private readonly IUndoRecorder _recorder;
        private readonly IUndoRecordSink _sink;
        private bool _closed;

        public uint SerialNumber { get; }
        public string Description { get; }
        public bool IsActive => SerialNumber != 0 && !_closed;

        public UndoScope(IUndoRecorder recorder, string description, IUndoRecordSink sink = null)
        {
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _sink = sink;
            Description = description ?? "agent";

            SerialNumber = _recorder.BeginRecord(Description);
            if (SerialNumber != 0)
                _sink?.RecordOpened(SerialNumber, Description);
        }

        public void Dispose()
        {
            if (_closed || SerialNumber == 0)
            {
                _closed = true;
                return;
            }
            _closed = true;
            try
            {
                _recorder.EndRecord(SerialNumber);
            }
            finally
            {
                _sink?.RecordClosed(SerialNumber);
            }
        }
    }

    /// <summary>
    /// Remembers every undo record the session opened, so "revert this session" can pop
    /// them all back off in one click.
    /// </summary>
    public sealed class UndoRecordLog : IUndoRecordSink
    {
        private readonly object _gate = new object();
        private readonly List<uint> _open = new List<uint>();
        private readonly List<string> _closed = new List<string>();

        /// <summary>Number of records opened and closed by this session.</summary>
        public int CompletedCount
        {
            get { lock (_gate) return _closed.Count; }
        }

        public IReadOnlyList<string> Descriptions
        {
            get { lock (_gate) return _closed.ToArray(); }
        }

        private readonly Dictionary<uint, string> _pending = new Dictionary<uint, string>();

        public void RecordOpened(uint serialNumber, string description)
        {
            lock (_gate)
            {
                _open.Add(serialNumber);
                _pending[serialNumber] = description;
            }
        }

        public void RecordClosed(uint serialNumber)
        {
            lock (_gate)
            {
                _open.Remove(serialNumber);
                if (_pending.TryGetValue(serialNumber, out var desc))
                {
                    _closed.Add(desc);
                    _pending.Remove(serialNumber);
                }
                else
                {
                    _closed.Add("agent");
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _open.Clear();
                _closed.Clear();
                _pending.Clear();
            }
        }
    }
}
