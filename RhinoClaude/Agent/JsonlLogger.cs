using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Append-only JSONL sink. Two instances are used: script_log.jsonl (every Tier 2
    /// script, the data source for deciding what to promote to Tier 1) and
    /// capture_log.jsonl (every capture_views call, for the cadence instrumentation
    /// in plan §6.5).
    ///
    /// Logging never throws — a failed write degrades to silence rather than killing
    /// the agent turn mid-mutation.
    /// </summary>
    public sealed class JsonlLogger
    {
        private readonly object _gate = new object();

        public string FilePath { get; }
        public Exception LastError { get; private set; }

        public JsonlLogger(string filePath)
        {
            FilePath = filePath;
        }

        public static string DefaultDirectory
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "RhinoClaude");
            }
        }

        public static JsonlLogger ScriptLog() => new JsonlLogger(Path.Combine(DefaultDirectory, "script_log.jsonl"));
        public static JsonlLogger CaptureLog() => new JsonlLogger(Path.Combine(DefaultDirectory, "capture_log.jsonl"));

        public void Append(IDictionary<string, object> record)
        {
            if (record == null) return;
            try
            {
                // Callers never set the timestamp; stamping here keeps every line comparable.
                if (!record.ContainsKey("timestamp"))
                    record["timestamp"] = DateTime.UtcNow.ToString("o");

                string line = ToolJson.Serialize(record);

                lock (_gate)
                {
                    string dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false));
                }
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex;
            }
        }
    }
}
