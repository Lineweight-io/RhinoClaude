using System;
using System.Collections.Generic;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// Soft caps for tool responses, and the paging window they hand back.
    ///
    /// Everything a tool returns lands in the conversation and is then re-sent on every later
    /// iteration of the turn, so an unbounded list is not paid once — it is paid once per
    /// iteration for the rest of the turn. A 200-face Brep enumerated face-by-face and
    /// edge-by-edge is roughly 19,000 tokens from a single <c>get_object</c> call.
    ///
    /// The caps are deliberately generous enough to answer the usual question in one call and
    /// small enough that a wrong guess is cheap. Every capped response says what was omitted
    /// and how to ask for the rest, so nothing is silently lost.
    /// </summary>
    public static class PayloadCaps
    {
        /// <summary>
        /// Default row count for <c>list_objects</c>. Each row carries a bounding box and the
        /// object's tags — roughly 60 tokens — so 200 was ~12,000 tokens per call on a busy
        /// layer. 50 answers "what is on this layer" while staying under ~3,000.
        /// </summary>
        public const int ListObjectsDefaultLimit = 50;

        /// <summary>Ceiling for an explicit <c>limit</c>, matching the tool schema's maximum.</summary>
        public const int ListObjectsMaxLimit = 1000;

        /// <summary>Rows per <c>list_layers</c> call. Firm templates routinely carry hundreds.</summary>
        public const int ListLayersDefaultLimit = 100;

        /// <summary>Rows per <c>list_blocks</c> call.</summary>
        public const int ListBlocksDefaultLimit = 100;

        /// <summary>Faces per <c>get_object</c> / <c>get_mass_faces</c> call.</summary>
        public const int FacesPerCall = 30;

        /// <summary>
        /// Edges per <c>get_object</c> / <c>get_mass_edges</c> call. Twice the face cap because
        /// a solid has roughly twice as many edges as faces, and an edge row is smaller.
        /// </summary>
        public const int EdgesPerCall = 60;

        /// <summary>The slice of a collection a single call returns.</summary>
        public struct Window
        {
            /// <summary>First index returned. 0 unless the caller asked for a later page.</summary>
            public int Start { get; set; }

            /// <summary>How many items are returned. 0 when the collection is empty.</summary>
            public int Count { get; set; }

            /// <summary>Total available, ignoring the cap.</summary>
            public int Total { get; set; }

            /// <summary>
            /// True when items remain <em>after</em> this window — the signal to page again.
            /// Deliberately not "the window is smaller than the total": on the last page of a
            /// paged read there is nothing more to fetch, and saying otherwise would send the
            /// agent round the loop for a page that does not exist. What was skipped ahead of
            /// <see cref="Start"/> the caller asked to skip, and <c>Total</c> is reported
            /// alongside either way.
            /// </summary>
            public bool Truncated => Start + Count < Total;

            /// <summary>Last index returned, or -1 when nothing was.</summary>
            public int End => Count == 0 ? -1 : Start + Count - 1;

            /// <summary>Index to pass as the start of the next page, or -1 when there is none.</summary>
            public int NextStart => Start + Count >= Total ? -1 : Start + Count;

            /// <summary>Inclusive <c>[start, end]</c> pair for the response, or null when empty.</summary>
            public int[] AsRange() => Count == 0 ? null : new[] { Start, End };
        }

        /// <summary>
        /// Resolve the slice to return from a collection of <paramref name="total"/> items.
        ///
        /// <paramref name="range"/> is the caller's optional inclusive <c>[start, end]</c>. It
        /// is clamped rather than rejected: an out-of-order or out-of-range pair pages from the
        /// nearest sensible place instead of erroring, because a tool error costs the agent a
        /// whole round trip to recover from. The window is always capped at
        /// <paramref name="max"/>, so a caller asking for <c>[0, 500]</c> gets the first
        /// <paramref name="max"/> items and a truncation note rather than 500 items.
        /// </summary>
        public static Window Resolve(int total, int[] range, int max)
        {
            if (total < 0) total = 0;
            if (max < 1) max = 1;

            int start = 0;
            int requested = max;

            if (range != null && range.Length > 0)
            {
                start = Math.Max(0, range[0]);
                if (start >= total) start = Math.Max(0, total - 1);

                if (range.Length > 1)
                {
                    int end = range[1];
                    requested = end < start ? 1 : end - start + 1;
                }
            }

            int count = Math.Min(Math.Min(requested, max), Math.Max(0, total - start));
            if (total == 0) start = 0;

            return new Window { Start = start, Count = count, Total = total };
        }

        /// <summary>
        /// The sentence a truncated response carries. Names what was left out and the exact
        /// argument that fetches the next page, so the agent does not have to work it out.
        /// </summary>
        public static string NoteFor(string label, string parameterName, Window window, int max)
        {
            if (!window.Truncated) return null;

            int omitted = window.Total - (window.Start + window.Count);
            int nextEnd = window.NextStart < 0 ? 0 : Math.Min(window.Total - 1, window.NextStart + max - 1);

            return string.Format(
                "omitted {0} of {1} {2} — call again with {3}: [{4}, {5}] to page through.",
                omitted, window.Total, label, parameterName, window.NextStart, nextEnd);
        }

        /// <summary>Join the per-collection notes a single response produced.</summary>
        public static string CombineNotes(params string[] notes)
        {
            var kept = new List<string>();
            foreach (var note in notes)
                if (!string.IsNullOrEmpty(note)) kept.Add(note);

            return kept.Count == 0 ? null : string.Join(" ", kept);
        }
    }
}
