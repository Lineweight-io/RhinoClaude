using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using RhinoClaude.Agent;

namespace RhinoClaude.Services.Agent
{
    /// <summary>
    /// Per-document conversation persistence (plan §2.2 / §2.4).
    ///
    /// Conversations live in the document's own string table, so they travel with the .3dm and
    /// survive close/reopen without a sidecar file to lose. Large conversations are chunked
    /// because the string table is not meant for megabyte values (plan risk #9).
    ///
    /// Every operation is best-effort: persistence failing must never take down a working
    /// session or stop a document saving.
    /// </summary>
    public sealed class AgentConversationStore
    {
        private const string Section = "RhinoClaude";
        private const string KeyPrefix = "AgentConversation";

        private readonly uint _docSerialNumber;

        public AgentConversationStore(RhinoDoc doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _docSerialNumber = doc.RuntimeSerialNumber;
        }

        private RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(_docSerialNumber);

        public Exception LastError { get; private set; }

        private static string HeaderKey(string sessionId) => Section + ":" + KeyPrefix + ":" + sessionId + ":header";
        private static string ChunkKey(string sessionId, int index) =>
            Section + ":" + KeyPrefix + ":" + sessionId + ":" + index.ToString("D4");

        // ── Save ──────────────────────────────────────────────────────

        public bool Save(AgentSession session, int undoRecordCount)
        {
            if (session == null) return false;

            var doc = Doc;
            if (doc == null) return false;

            try
            {
                var snapshot = new ConversationSnapshot
                {
                    SessionId = session.Id.ToString(),
                    DisplayName = session.DisplayName,
                    CreatedUtc = session.CreatedUtc.ToString("o"),
                    SavedUtc = DateTime.UtcNow.ToString("o"),
                    Model = session.Settings.LoopModel,
                    UndoRecordCount = undoRecordCount,
                    Messages = session.Messages
                };

                string json = snapshot.ToJson();
                var chunks = ConversationSnapshot.Chunk(json);

                // Clear first so a shorter conversation cannot leave stale trailing chunks
                // that would corrupt the reassembled JSON.
                Delete(snapshot.SessionId);

                for (int i = 0; i < chunks.Count; i++)
                    doc.Strings.SetString(ChunkKey(snapshot.SessionId, i), chunks[i]);

                doc.Strings.SetString(HeaderKey(snapshot.SessionId), string.Join("|", new[]
                {
                    ConversationSnapshot.CurrentVersion.ToString(),
                    chunks.Count.ToString(),
                    snapshot.SavedUtc,
                    Escape(snapshot.DisplayName)
                }));

                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex;
                return false;
            }
        }

        // ── Load ──────────────────────────────────────────────────────

        /// <summary>Session ids stored in this document, most recently saved first.</summary>
        public List<string> ListSessionIds()
        {
            var doc = Doc;
            if (doc == null) return new List<string>();

            var found = new List<KeyValuePair<string, DateTime>>();

            try
            {
                string prefix = Section + ":" + KeyPrefix + ":";
                for (int i = 0; i < doc.Strings.Count; i++)
                {
                    string key = doc.Strings.GetKey(i);
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (!key.EndsWith(":header", StringComparison.Ordinal)) continue;

                    string sessionId = key.Substring(prefix.Length, key.Length - prefix.Length - ":header".Length);
                    var header = ParseHeader(doc.Strings.GetValue(key));
                    found.Add(new KeyValuePair<string, DateTime>(sessionId, header.SavedUtc));
                }
            }
            catch (Exception ex)
            {
                LastError = ex;
            }

            return found.OrderByDescending(f => f.Value).Select(f => f.Key).ToList();
        }

        public ConversationSnapshot Load(string sessionId)
        {
            var doc = Doc;
            if (doc == null || string.IsNullOrWhiteSpace(sessionId)) return null;

            try
            {
                string headerValue = doc.Strings.GetValue(HeaderKey(sessionId));
                if (string.IsNullOrEmpty(headerValue)) return null;

                var header = ParseHeader(headerValue);
                if (header.ChunkCount <= 0) return null;

                var chunks = new List<string>();
                for (int i = 0; i < header.ChunkCount; i++)
                {
                    string chunk = doc.Strings.GetValue(ChunkKey(sessionId, i));
                    if (chunk == null) return null;   // a missing chunk means the blob is broken
                    chunks.Add(chunk);
                }

                return ConversationSnapshot.FromJson(ConversationSnapshot.Join(chunks));
            }
            catch (Exception ex)
            {
                LastError = ex;
                return null;
            }
        }

        /// <summary>The most recently saved conversation, or null.</summary>
        public ConversationSnapshot LoadMostRecent()
        {
            foreach (var id in ListSessionIds())
            {
                var snapshot = Load(id);
                if (snapshot != null) return snapshot;
            }
            return null;
        }

        // ── Delete ────────────────────────────────────────────────────

        public void Delete(string sessionId)
        {
            var doc = Doc;
            if (doc == null || string.IsNullOrWhiteSpace(sessionId)) return;

            try
            {
                string prefix = Section + ":" + KeyPrefix + ":" + sessionId + ":";

                // Collect first: deleting while enumerating the table shifts the indices.
                var keys = new List<string>();
                for (int i = 0; i < doc.Strings.Count; i++)
                {
                    string key = doc.Strings.GetKey(i);
                    if (!string.IsNullOrEmpty(key) && key.StartsWith(prefix, StringComparison.Ordinal))
                        keys.Add(key);
                }

                foreach (var key in keys)
                    doc.Strings.Delete(key);
            }
            catch (Exception ex)
            {
                LastError = ex;
            }
        }

        public void DeleteAll()
        {
            foreach (var id in ListSessionIds()) Delete(id);
        }

        // ── Header encoding ───────────────────────────────────────────

        public struct Header
        {
            public int Version;
            public int ChunkCount;
            public DateTime SavedUtc;
            public string DisplayName;
        }

        private static Header ParseHeader(string value)
        {
            var header = new Header { SavedUtc = DateTime.MinValue };
            if (string.IsNullOrEmpty(value)) return header;

            var parts = value.Split('|');
            if (parts.Length > 0) int.TryParse(parts[0], out header.Version);
            if (parts.Length > 1) int.TryParse(parts[1], out header.ChunkCount);
            if (parts.Length > 2)
                DateTime.TryParse(parts[2], null, System.Globalization.DateTimeStyles.RoundtripKind, out header.SavedUtc);
            if (parts.Length > 3) header.DisplayName = Unescape(parts[3]);

            return header;
        }

        private static string Escape(string value) =>
            string.IsNullOrEmpty(value) ? string.Empty : value.Replace("|", "¦");

        private static string Unescape(string value) =>
            string.IsNullOrEmpty(value) ? string.Empty : value.Replace("¦", "|");
    }
}
