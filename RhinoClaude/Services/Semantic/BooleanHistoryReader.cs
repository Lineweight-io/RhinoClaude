using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Rhino;
using Rhino.DocObjects;
using RhinoClaude.Semantic;

namespace RhinoClaude.Services.Semantic
{
    /// <summary>
    /// Walks Rhino's history records for a Mass into the normalized operation list
    /// <c>analyze_boolean_history</c> reports (semantic plan §4.2, §6.1).
    ///
    /// Most architects work with history recording off, so the common answer is
    /// <c>historyAvailable: false</c>. Nothing else in the plan may depend on this: the Brep
    /// topology paths for Openings and Cuts work regardless (plan risk #4). What comes back is
    /// opportunistic colour, never the source of truth.
    ///
    /// **Why reflection.** The history surface (<c>RhinoObject.HistoryRecord</c>,
    /// <c>HistoryRecord.TryGetGuids</c>) is the one part of this plan whose exact shape differs
    /// between the RhinoCommon 7.38 and 8.x reference assemblies, and the plugin multi-targets
    /// both. A hard reference would trade a compile error on one target for a feature the plan
    /// explicitly calls optional. Reflection degrades to "history unavailable", which is both
    /// the honest answer and the common one.
    /// </summary>
    public sealed class BooleanHistoryReader
    {
        private readonly uint _docSerialNumber;

        public BooleanHistoryReader(RhinoDoc doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _docSerialNumber = doc.RuntimeSerialNumber;
        }

        private RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(_docSerialNumber);

        /// <summary>
        /// Read the history for one object. Returns an empty list — never null — when history
        /// is off, absent, or unreadable, with <paramref name="historyAvailable"/> false.
        /// </summary>
        public List<BooleanOperationRecord> Read(string objectIdText, out bool historyAvailable)
        {
            historyAvailable = false;
            var operations = new List<BooleanOperationRecord>();

            var doc = Doc;
            if (doc == null || !Guid.TryParse(objectIdText, out var id)) return operations;

            RhinoObject obj;
            try
            {
                obj = doc.Objects.FindId(id);
            }
            catch (Exception)
            {
                return operations;
            }

            if (obj == null) return operations;

            object record = ReadHistoryRecord(obj);
            if (record == null) return operations;

            historyAvailable = true;

            var operation = new BooleanOperationRecord
            {
                CommandName = ReadCommandName(record),
                ResultId = objectIdText
            };
            operation.Kind = KindFromCommand(operation.CommandName);

            foreach (var input in ReadInputIds(record))
                operation.Inputs.Add(input);

            operation.Notes = operation.Inputs.Count == 0
                ? "Rhino recorded the operation but not its surviving inputs — most likely the " +
                  "boolean consumed them."
                : null;

            operations.Add(operation);
            return operations;
        }

        // ── Reflection helpers ────────────────────────────────────────

        private static PropertyInfo _historyRecordProperty;
        private static bool _historyRecordProbed;

        private static object ReadHistoryRecord(RhinoObject obj)
        {
            if (!_historyRecordProbed)
            {
                _historyRecordProbed = true;
                _historyRecordProperty = obj.GetType().GetProperty(
                    "HistoryRecord", BindingFlags.Public | BindingFlags.Instance);
            }

            if (_historyRecordProperty == null) return null;

            try
            {
                return _historyRecordProperty.GetValue(obj, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadCommandName(object record)
        {
            foreach (var name in new[] { "CommandName", "CommandId" })
            {
                try
                {
                    var property = record.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    var value = property?.GetValue(record, null);
                    if (value != null && !string.IsNullOrWhiteSpace(value.ToString()))
                        return value.ToString();
                }
                catch (Exception)
                {
                    // Try the next spelling.
                }
            }
            return "unknown";
        }

        /// <summary>
        /// Object ids stored on the record, when the operation kept references to its inputs.
        /// A boolean that deleted its inputs leaves ids that no longer resolve; those are
        /// reported anyway, because "this mass came from two solids that no longer exist" is
        /// exactly what the agent wants to know.
        /// </summary>
        private static IEnumerable<string> ReadInputIds(object record)
        {
            var ids = new List<string>();

            MethodInfo tryGetGuids;
            try
            {
                tryGetGuids = record.GetType().GetMethod(
                    "TryGetGuids", BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception)
            {
                return ids;
            }

            if (tryGetGuids == null) return ids;

            // History slot ids are command-specific and undocumented; the first handful cover
            // every boolean and extrude Rhino ships. A miss on one slot says nothing about the next.
            for (int slot = 0; slot < 16; slot++)
            {
                try
                {
                    var args = new object[] { slot, null };
                    var ok = tryGetGuids.Invoke(record, args);
                    if (!(ok is bool succeeded) || !succeeded) continue;
                    if (args[1] is Guid[] values)
                        ids.AddRange(values.Where(g => g != Guid.Empty).Select(g => g.ToString()));
                }
                catch (Exception)
                {
                    // Wrong arity or wrong slot type — nothing to learn, keep going.
                }
            }

            return ids.Distinct();
        }

        /// <summary>Map a producing command onto the plan's operation vocabulary.</summary>
        public static string KindFromCommand(string commandName)
        {
            if (string.IsNullOrWhiteSpace(commandName)) return "other";
            string name = commandName.ToLowerInvariant();

            if (name.Contains("booleanunion") || name.Contains("union")) return "union";
            if (name.Contains("booleandifference") || name.Contains("difference")) return "difference";
            if (name.Contains("booleanintersection") || name.Contains("intersection")) return "intersection";
            if (name.Contains("extrude")) return "extrude";
            if (name.Contains("split")) return "split";
            if (name.Contains("offset")) return "offset";
            if (name.Contains("fillet")) return "fillet";
            return "other";
        }
    }
}
