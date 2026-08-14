using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The windowing behind the tool-response caps.
    ///
    /// The rule the query services rely on: the returned window is always inside the
    /// collection, never larger than the cap, and <c>Truncated</c> is true exactly when
    /// something was left out — so a tool can never silently drop rows.
    /// </summary>
    public class PayloadCapTests
    {
        // ── Default window ────────────────────────────────────────────

        [Fact]
        public void WithoutARangeTheFirstPageIsReturned()
        {
            var window = PayloadCaps.Resolve(total: 200, range: null, max: 30);

            Assert.Equal(0, window.Start);
            Assert.Equal(30, window.Count);
            Assert.Equal(29, window.End);
            Assert.Equal(200, window.Total);
            Assert.True(window.Truncated);
            Assert.Equal(30, window.NextStart);
        }

        [Fact]
        public void ACollectionInsideTheCapIsNotTruncated()
        {
            var window = PayloadCaps.Resolve(total: 6, range: null, max: 30);

            Assert.Equal(0, window.Start);
            Assert.Equal(6, window.Count);
            Assert.False(window.Truncated);
            Assert.Equal(-1, window.NextStart);
            Assert.Equal(new[] { 0, 5 }, window.AsRange());
        }

        [Fact]
        public void ExactlyTheCapIsNotTruncated()
        {
            var window = PayloadCaps.Resolve(total: 30, range: null, max: 30);

            Assert.Equal(30, window.Count);
            Assert.False(window.Truncated);
        }

        [Fact]
        public void AnEmptyCollectionYieldsAnEmptyWindow()
        {
            var window = PayloadCaps.Resolve(total: 0, range: null, max: 30);

            Assert.Equal(0, window.Start);
            Assert.Equal(0, window.Count);
            Assert.Equal(-1, window.End);
            Assert.False(window.Truncated);
            Assert.Null(window.AsRange());
        }

        // ── Paging ────────────────────────────────────────────────────

        [Fact]
        public void AnExplicitRangePagesFromWhereItAsks()
        {
            var window = PayloadCaps.Resolve(total: 200, range: new[] { 30, 59 }, max: 30);

            Assert.Equal(30, window.Start);
            Assert.Equal(30, window.Count);
            Assert.Equal(59, window.End);
            Assert.True(window.Truncated);
            Assert.Equal(60, window.NextStart);
        }

        [Fact]
        public void PagingWalksTheWholeCollectionWithoutGapsOrOverlap()
        {
            const int total = 74;
            const int max = 30;

            int start = 0;
            int seen = 0;
            var pages = 0;

            while (start >= 0)
            {
                var window = PayloadCaps.Resolve(total, new[] { start, start + max - 1 }, max);
                Assert.Equal(start, window.Start);
                seen += window.Count;
                pages++;
                start = window.NextStart;
                Assert.True(pages < 10, "paging failed to terminate");
            }

            Assert.Equal(total, seen);
            Assert.Equal(3, pages);
        }

        [Fact]
        public void AWideRangeIsStillCappedRatherThanHonoured()
        {
            // The point of the cap is that no single call can blow up the conversation, so an
            // over-wide ask comes back capped and truncated rather than in full.
            var window = PayloadCaps.Resolve(total: 500, range: new[] { 0, 499 }, max: 30);

            Assert.Equal(0, window.Start);
            Assert.Equal(30, window.Count);
            Assert.True(window.Truncated);
        }

        [Fact]
        public void ARangeNarrowerThanTheCapIsHonoured()
        {
            var window = PayloadCaps.Resolve(total: 200, range: new[] { 10, 14 }, max: 30);

            Assert.Equal(10, window.Start);
            Assert.Equal(5, window.Count);
            Assert.Equal(14, window.End);
        }

        [Fact]
        public void TheLastPageStopsAtTheEndOfTheCollection()
        {
            var window = PayloadCaps.Resolve(total: 35, range: new[] { 30, 59 }, max: 30);

            Assert.Equal(30, window.Start);
            Assert.Equal(5, window.Count);
            Assert.Equal(34, window.End);
            Assert.False(window.Truncated);
            Assert.Equal(-1, window.NextStart);
        }

        // ── Malformed input is clamped, not rejected ──────────────────

        [Theory]
        [InlineData(new[] { 500, 600 })]   // wholly past the end
        [InlineData(new[] { 20, 5 })]      // reversed
        [InlineData(new[] { -4, -1 })]     // negative
        [InlineData(new[] { 3 })]          // start only
        public void AMalformedRangeStillProducesAWindowInsideTheCollection(int[] range)
        {
            // A tool error costs the agent a whole round trip to recover from, so a bad range
            // pages from the nearest sensible place instead of failing.
            var window = PayloadCaps.Resolve(total: 10, range: range, max: 30);

            Assert.InRange(window.Start, 0, 9);
            Assert.InRange(window.Count, 1, 10);
            Assert.InRange(window.End, window.Start, 9);
        }

        [Fact]
        public void AStartOnlyRangeRunsToTheCap()
        {
            var window = PayloadCaps.Resolve(total: 200, range: new[] { 40 }, max: 30);

            Assert.Equal(40, window.Start);
            Assert.Equal(30, window.Count);
        }

        // ── The note the agent reads ──────────────────────────────────

        [Fact]
        public void ATruncatedResponseNamesTheArgumentThatFetchesTheNextPage()
        {
            var window = PayloadCaps.Resolve(total: 200, range: null, max: 30);
            string note = PayloadCaps.NoteFor("faces", "facesRange", window, 30);

            Assert.Contains("omitted 170 of 200 faces", note);
            Assert.Contains("facesRange: [30, 59]", note);
        }

        [Fact]
        public void TheFinalPageOfANoteStopsAtTheLastIndex()
        {
            var window = PayloadCaps.Resolve(total: 35, range: null, max: 30);
            string note = PayloadCaps.NoteFor("faces", "facesRange", window, 30);

            Assert.Contains("facesRange: [30, 34]", note);
        }

        [Fact]
        public void AnUntruncatedResponseCarriesNoNote()
        {
            var window = PayloadCaps.Resolve(total: 4, range: null, max: 30);

            Assert.Null(PayloadCaps.NoteFor("faces", "facesRange", window, 30));
            Assert.Null(PayloadCaps.CombineNotes(null, null));
        }

        [Fact]
        public void FacesAndEdgesCombineIntoOneNote()
        {
            var faces = PayloadCaps.Resolve(200, null, PayloadCaps.FacesPerCall);
            var edges = PayloadCaps.Resolve(600, null, PayloadCaps.EdgesPerCall);

            string note = PayloadCaps.CombineNotes(
                PayloadCaps.NoteFor("faces", "facesRange", faces, PayloadCaps.FacesPerCall),
                PayloadCaps.NoteFor("edges", "edgesRange", edges, PayloadCaps.EdgesPerCall));

            Assert.Contains("faces", note);
            Assert.Contains("edges", note);
            Assert.Contains("edgesRange: [60, 119]", note);
        }

        // ── The caps themselves ───────────────────────────────────────

        [Fact]
        public void TheDefaultsAreTheOnesTheToolSchemasAdvertise()
        {
            // The schemas state these numbers to the model in prose; if they drift apart the
            // model pages against a limit that is not the one being applied.
            Assert.Equal(50, PayloadCaps.ListObjectsDefaultLimit);
            Assert.Equal(1000, PayloadCaps.ListObjectsMaxLimit);
            Assert.Equal(100, PayloadCaps.ListLayersDefaultLimit);
            Assert.Equal(30, PayloadCaps.FacesPerCall);
            Assert.Equal(60, PayloadCaps.EdgesPerCall);
        }
    }
}
