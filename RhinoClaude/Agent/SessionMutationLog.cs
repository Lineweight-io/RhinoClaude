using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// One document change made by a tool. Bounding boxes are stored as
    /// <c>[minX, minY, minZ, maxX, maxY, maxZ]</c> rather than a RhinoCommon
    /// <c>BoundingBox</c> so this stays testable off the UI thread and without Rhino.
    /// </summary>
    public sealed class SessionMutation
    {
        public string ToolName { get; set; }
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public List<string> CreatedIds { get; } = new List<string>();
        public List<string> DeletedIds { get; } = new List<string>();
        public List<string> ModifiedIds { get; } = new List<string>();
        public List<string> LayersTouched { get; } = new List<string>();
        public double[] AffectedBox { get; set; }
    }

    /// <summary>
    /// What the agent has changed this session. Feeds the self-review's deterministic
    /// checks — "did the agent do roughly what it said it would" needs a record of what
    /// it actually did, not just what it claimed.
    /// </summary>
    public sealed class SessionMutationLog
    {
        private readonly object _gate = new object();
        private readonly List<SessionMutation> _mutations = new List<SessionMutation>();

        public void Add(SessionMutation mutation)
        {
            if (mutation == null) return;
            lock (_gate) _mutations.Add(mutation);
        }

        public IReadOnlyList<SessionMutation> All
        {
            get { lock (_gate) return _mutations.ToArray(); }
        }

        public void Clear()
        {
            lock (_gate) _mutations.Clear();
        }

        /// <summary>Index into <see cref="All"/> marking where the current turn began.</summary>
        public int Mark
        {
            get { lock (_gate) return _mutations.Count; }
        }

        public IReadOnlyList<SessionMutation> Since(int mark)
        {
            lock (_gate)
            {
                if (mark < 0) mark = 0;
                if (mark >= _mutations.Count) return new SessionMutation[0];
                return _mutations.Skip(mark).ToArray();
            }
        }

        /// <summary>
        /// Ids that exist now because of the agent: everything it created, minus anything it
        /// later deleted. This is what "the work" means for review purposes.
        /// </summary>
        public List<string> SurvivingCreatedIds(int mark = 0)
        {
            var created = new List<string>();
            var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mutation in Since(mark))
            {
                created.AddRange(mutation.CreatedIds);
                foreach (var id in mutation.DeletedIds) deleted.Add(id);
            }

            return created.Where(id => !deleted.Contains(id)).Distinct().ToList();
        }

        /// <summary>
        /// Everything the agent is responsible for that is still in the document: what it
        /// created plus what it edited in place, minus anything it later deleted.
        ///
        /// This is the set the "export result" button writes out. It is deliberately wider
        /// than <see cref="SurvivingCreatedIds"/> — an object the agent moved, re-layered or
        /// re-tagged belongs in a review file even though the agent did not make it. Order is
        /// first-touched-first, so the export reads in the order the agent worked.
        /// </summary>
        public List<string> SurvivingTouchedIds(int mark = 0)
        {
            var touched = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mutation in Since(mark))
            {
                foreach (var id in mutation.CreatedIds.Concat(mutation.ModifiedIds))
                {
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (seen.Add(id)) touched.Add(id);
                }
                foreach (var id in mutation.DeletedIds) deleted.Add(id);
            }

            return touched.Where(id => !deleted.Contains(id)).ToList();
        }

        public List<string> LayersTouched(int mark = 0)
        {
            return Since(mark)
                .SelectMany(m => m.LayersTouched)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public int NetObjectDelta(int mark = 0)
        {
            var since = Since(mark);
            return since.Sum(m => m.CreatedIds.Count) - since.Sum(m => m.DeletedIds.Count);
        }

        /// <summary>Union of every affected bounding box, or null when nothing has a box.</summary>
        public double[] AffectedBox(int mark = 0)
        {
            double[] union = null;

            foreach (var mutation in Since(mark))
            {
                var box = mutation.AffectedBox;
                if (box == null || box.Length != 6) continue;

                if (union == null)
                {
                    union = (double[])box.Clone();
                    continue;
                }

                for (int i = 0; i < 3; i++)
                {
                    if (box[i] < union[i]) union[i] = box[i];
                    if (box[i + 3] > union[i + 3]) union[i + 3] = box[i + 3];
                }
            }

            return union;
        }
    }
}
