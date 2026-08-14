using System;
using System.Collections.Generic;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// Undo-record hygiene is risk #4 in the plan: every mutation must close its record even
    /// when the body throws. These tests exercise that against a fake recorder.
    /// </summary>
    public class UndoScopeTests
    {
        private sealed class FakeRecorder : IUndoRecorder
        {
            private uint _next = 1;

            public List<string> Begun { get; } = new List<string>();
            public List<uint> Ended { get; } = new List<uint>();
            public bool RefuseToBegin { get; set; }

            public uint BeginRecord(string description)
            {
                if (RefuseToBegin) return 0;
                Begun.Add(description);
                return _next++;
            }

            public bool EndRecord(uint serialNumber)
            {
                Ended.Add(serialNumber);
                return true;
            }
        }

        [Fact]
        public void OpensAndClosesExactlyOneRecord()
        {
            var recorder = new FakeRecorder();

            using (var scope = new UndoScope(recorder, "agent:create_box"))
            {
                Assert.True(scope.IsActive);
                Assert.Equal(1u, scope.SerialNumber);
            }

            Assert.Equal(new[] { "agent:create_box" }, recorder.Begun);
            Assert.Equal(new uint[] { 1 }, recorder.Ended);
        }

        [Fact]
        public void ClosesTheRecordWhenTheBodyThrows()
        {
            var recorder = new FakeRecorder();

            // Typed as Action deliberately: a lambda whose body always throws is convertible
            // to Func<Task> too, and xunit's overload resolution then picks the obsolete async
            // overload.
            Action body = () =>
            {
                using (new UndoScope(recorder, "agent:create_box"))
                {
                    throw new InvalidOperationException("Rhino refused the operation.");
                }
            };

            Assert.Throws<InvalidOperationException>(body);

            // The record must still be closed, or every later undo record nests inside it.
            Assert.Single(recorder.Ended);
            Assert.Equal(1u, recorder.Ended[0]);
        }

        [Fact]
        public void DisposeIsIdempotent()
        {
            var recorder = new FakeRecorder();
            var scope = new UndoScope(recorder, "agent:x");

            scope.Dispose();
            scope.Dispose();
            scope.Dispose();

            Assert.Single(recorder.Ended);
        }

        [Fact]
        public void ARefusedBeginDoesNotCloseSomebodyElsesRecord()
        {
            // BeginUndoRecord returns 0 when Rhino declines (already inside a record, or
            // recording disabled). Calling EndUndoRecord(0) would be wrong.
            var recorder = new FakeRecorder { RefuseToBegin = true };

            using (var scope = new UndoScope(recorder, "agent:x"))
            {
                Assert.Equal(0u, scope.SerialNumber);
                Assert.False(scope.IsActive);
            }

            Assert.Empty(recorder.Ended);
        }

        [Fact]
        public void NestedScopesCloseInReverseOrder()
        {
            var recorder = new FakeRecorder();

            using (new UndoScope(recorder, "outer"))
            using (new UndoScope(recorder, "inner"))
            {
            }

            Assert.Equal(new uint[] { 2, 1 }, recorder.Ended);
        }

        // ── UndoRecordLog ─────────────────────────────────────────────

        [Fact]
        public void LogCountsOnlyCompletedRecords()
        {
            var recorder = new FakeRecorder();
            var log = new UndoRecordLog();

            var scope = new UndoScope(recorder, "agent:create_box", log);
            Assert.Equal(0, log.CompletedCount);   // still open

            scope.Dispose();
            Assert.Equal(1, log.CompletedCount);
        }

        [Fact]
        public void LogAccumulatesAcrossSeveralMutations()
        {
            var recorder = new FakeRecorder();
            var log = new UndoRecordLog();

            foreach (var name in new[] { "ensure_layer", "create_box", "translate_objects" })
                using (new UndoScope(recorder, "agent:" + name, log)) { }

            Assert.Equal(3, log.CompletedCount);
            Assert.Equal(new[] { "agent:ensure_layer", "agent:create_box", "agent:translate_objects" },
                log.Descriptions);
        }

        [Fact]
        public void LogRecordsAScopeWhoseBodyThrew()
        {
            // The change may be partial, but the record exists and must be revertible.
            var recorder = new FakeRecorder();
            var log = new UndoRecordLog();

            Action body = () =>
            {
                using (new UndoScope(recorder, "agent:create_box", log))
                    throw new Exception("boom");
            };

            Assert.Throws<Exception>(body);

            Assert.Equal(1, log.CompletedCount);
        }

        [Fact]
        public void ARefusedRecordIsNotLogged()
        {
            var recorder = new FakeRecorder { RefuseToBegin = true };
            var log = new UndoRecordLog();

            using (new UndoScope(recorder, "agent:x", log)) { }

            Assert.Equal(0, log.CompletedCount);
        }

        [Fact]
        public void ClearResetsTheLog()
        {
            var recorder = new FakeRecorder();
            var log = new UndoRecordLog();

            using (new UndoScope(recorder, "agent:x", log)) { }
            log.Clear();

            Assert.Equal(0, log.CompletedCount);
        }
    }
}
