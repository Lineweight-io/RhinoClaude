using System;
using RhinoClaude.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The set of objects the "export result" button writes, and the filenames both exports
    /// suggest. Resolving ids against a live document needs Rhino; deciding which ids to ask
    /// for does not, and that is the part that can be wrong.
    /// </summary>
    public class ResultExportTests
    {
        private static SessionMutation Mutation(
            string tool, string[] created = null, string[] modified = null, string[] deleted = null)
        {
            var mutation = new SessionMutation { ToolName = tool };
            if (created != null) mutation.CreatedIds.AddRange(created);
            if (modified != null) mutation.ModifiedIds.AddRange(modified);
            if (deleted != null) mutation.DeletedIds.AddRange(deleted);
            return mutation;
        }

        // ── Which objects get exported ────────────────────────────────

        [Fact]
        public void CreatedAndModifiedObjectsAreBothExported()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("create_box", created: new[] { "a" }));
            log.Add(Mutation("assign_objects_to_layer", modified: new[] { "existing" }));

            Assert.Equal(new[] { "a", "existing" }, log.SurvivingTouchedIds());
        }

        [Fact]
        public void ObjectsTheAgentLaterDeletedAreNotExported()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("create_box", created: new[] { "cutter", "wall" }));
            log.Add(Mutation("boolean_difference", deleted: new[] { "cutter" }));

            Assert.Equal(new[] { "wall" }, log.SurvivingTouchedIds());
        }

        [Fact]
        public void AnObjectModifiedThenDeletedIsNotExported()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("translate_objects", modified: new[] { "slab" }));
            log.Add(Mutation("delete_objects", deleted: new[] { "slab" }));

            Assert.Empty(log.SurvivingTouchedIds());
        }

        [Fact]
        public void AnObjectTouchedRepeatedlyAppearsOnceInFirstTouchOrder()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("create_box", created: new[] { "b" }));
            log.Add(Mutation("create_box", created: new[] { "a" }));
            log.Add(Mutation("translate_objects", modified: new[] { "b", "a" }));

            Assert.Equal(new[] { "b", "a" }, log.SurvivingTouchedIds());
        }

        [Fact]
        public void IdCasingDoesNotSmuggleADeletedObjectBackIn()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("create_box", created: new[] { "AAAA-BBBB" }));
            log.Add(Mutation("delete_objects", deleted: new[] { "aaaa-bbbb" }));

            Assert.Empty(log.SurvivingTouchedIds());
        }

        [Fact]
        public void TheMarkLimitsTheExportToWorkDoneSinceIt()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("create_box", created: new[] { "before" }));
            int mark = log.Mark;
            log.Add(Mutation("create_box", created: new[] { "after" }));

            Assert.Equal(new[] { "after" }, log.SurvivingTouchedIds(mark));
            Assert.Equal(new[] { "before", "after" }, log.SurvivingTouchedIds());
        }

        [Fact]
        public void ASessionThatOnlyReadIsEmpty()
        {
            var log = new SessionMutationLog();
            log.Add(Mutation("ensure_layer"));

            Assert.Empty(log.SurvivingTouchedIds());
        }

        [Fact]
        public void EveryExportedIdParsesAsAGuid()
        {
            // The export resolves these against the document with Guid.TryParse, so a log full
            // of Rhino ids has to survive the round trip.
            var log = new SessionMutationLog();
            var id = Guid.NewGuid();
            log.Add(Mutation("create_box", created: new[] { id.ToString() }));

            foreach (var text in log.SurvivingTouchedIds())
            {
                Assert.True(Guid.TryParse(text, out var parsed));
                Assert.Equal(id, parsed);
            }
        }

        // ── Filenames ─────────────────────────────────────────────────

        private static readonly DateTime When = new DateTime(2026, 8, 14, 14, 7, 0);

        [Fact]
        public void FilenamesFollowTheAgreedPattern()
        {
            Assert.Equal("RhinoClaude_conversation_Restroom_Test_20260814_1407.md",
                ExportNaming.ConversationFileName("Restroom Test.3dm", When));

            Assert.Equal("RhinoClaude_result_Restroom_Test_20260814_1407.3dm",
                ExportNaming.ResultFileName("Restroom Test.3dm", When));
        }

        [Fact]
        public void AFullPathIsReducedToTheFileName()
        {
            // Either separator: RhinoDoc.Path is a Windows path, but the name is also allowed
            // to arrive already bare.
            Assert.Equal("Restroom_Test",
                ExportNaming.SanitizeDocumentName(@"C:\Users\Bryan\Documents\Restroom Test.3dm"));
            Assert.Equal("Restroom_Test",
                ExportNaming.SanitizeDocumentName("C:/Users/Bryan/Restroom Test.3dm"));
        }

        [Fact]
        public void CharactersWindowsRejectsAreReplaced()
        {
            Assert.Equal("Plan_A_B_rev2", ExportNaming.SanitizeDocumentName("Plan: A|B <rev2>"));
        }

        [Fact]
        public void AnUnsavedDocumentGetsAStableName()
        {
            Assert.Equal("Untitled", ExportNaming.SanitizeDocumentName(null));
            Assert.Equal("Untitled", ExportNaming.SanitizeDocumentName("   "));
            Assert.Equal("Untitled", ExportNaming.SanitizeDocumentName("///"));
        }

        [Fact]
        public void AVeryLongNameIsClippedRatherThanThreateningThePathLimit()
        {
            string name = ExportNaming.SanitizeDocumentName(new string('n', 200) + ".3dm");

            Assert.Equal(60, name.Length);
        }

        [Fact]
        public void TheExtensionIsDroppedOnlyWhenItIsA3dm()
        {
            Assert.Equal("Test", ExportNaming.SanitizeDocumentName("Test.3DM"));
            Assert.Equal("Test.backup", ExportNaming.SanitizeDocumentName("Test.backup"));
        }
    }
}
