using System;
using System.Collections.Generic;
using RhinoClaude.Agent;
using RhinoClaude.Services.Agent;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The reviewer was reporting dimensions it had read off a screenshot, and getting them
    /// wrong: on one run it called a 48 × 38 × 16 ft envelope "40×30×12 ft" and shipped it. The
    /// numbers were in the document the whole time. These tests pin the fact that they now reach
    /// the prompt, and that the prompt says which source wins.
    /// </summary>
    public class ReviewEnvelopeTests
    {
        private static ReviewFacts Facts() => new ReviewFacts
        {
            UserRequest = "Make a modern 1-story building with a 4-foot overhang.",
            AgentSummary = "Built the mass and the roof slab.",
            Units = "Feet",
            ObjectsCreated = 2
        };

        [Fact]
        public void MeasuredEnvelopeReachesThePrompt()
        {
            var facts = Facts();
            facts.MeasuredEnvelope = "min (-4, -4, 0) → max (44, 34, 12); size 48 × 38 × 12 Feet";

            string prompt = ReviewPrompt.BuildUserText(facts);

            Assert.Contains("Measured envelope:", prompt);
            Assert.Contains("size 48 × 38 × 12 Feet", prompt);
        }

        /// <summary>
        /// The figures are worth little if the reviewer treats them as one more opinion. The
        /// prompt has to say outright that they beat the image.
        /// </summary>
        [Fact]
        public void PromptTellsTheReviewerToPreferMeasurementsOverTheImage()
        {
            var facts = Facts();
            facts.MeasuredEnvelope = "min (0, 0, 0) → max (40, 30, 14); size 40 × 30 × 14 Feet";

            string prompt = ReviewPrompt.BuildUserText(facts);

            Assert.Contains("measured from the document and are exact", prompt);
            Assert.Contains("the image is being misread", prompt);
        }

        /// <summary>Nothing survived to measure: say nothing rather than "size 0 × 0 × 0".</summary>
        [Fact]
        public void NoEnvelopeMeansNoEnvelopeSection()
        {
            string prompt = ReviewPrompt.BuildUserText(Facts());

            Assert.DoesNotContain("Measured envelope:", prompt);
        }
    }

    /// <summary>
    /// A verdict only rode into the transcript on signal_done's tool payload, so a defensive
    /// mid-turn review showed in the sidebar and appeared in the exported file nowhere at all —
    /// three of five runs in one benchmark shipped with a visible SHIP the export had no record
    /// of. The export is the audit trail; a verdict it cannot show is a verdict nobody can check.
    /// </summary>
    public class ReviewHistoryExportTests
    {
        private static ConversationExportRequest Request(params ReviewRecord[] reviews)
        {
            var request = new ConversationExportRequest
            {
                DocumentName = "House.3dm",
                SessionDisplayName = "Make a building",
                SessionId = Guid.Empty.ToString(),
                StartedLocal = new DateTime(2026, 8, 17, 9, 0, 0),
                ExportedLocal = new DateTime(2026, 8, 17, 9, 30, 0),
                Model = "claude-opus-5",
                Messages = new List<AgentMessage>()
            };

            request.Reviews.AddRange(reviews);
            return request;
        }

        [Fact]
        public void DefensiveReviewIsRecordedEvenThoughItNeverEntersTheTranscript()
        {
            string markdown = ConversationExport.ToMarkdown(Request(new ReviewRecord
            {
                Cycle = 0,
                Verdict = ReviewVerdict.Ship,
                Notes = "Mass reads as an L with a gable over the long wing.",
                ModelId = "claude-opus-5"
            }));

            Assert.Contains("## Review history", markdown);
            Assert.Contains("**ship**", markdown);
            Assert.Contains("automatic mid-turn check", markdown);
            Assert.Contains("Mass reads as an L", markdown);
        }

        [Fact]
        public void CycleReviewsAreNumbered()
        {
            string markdown = ConversationExport.ToMarkdown(Request(
                new ReviewRecord { Cycle = 1, Verdict = ReviewVerdict.Iterate, Notes = "Overhang is missing." },
                new ReviewRecord { Cycle = 2, Verdict = ReviewVerdict.Ship, Notes = "Overhang now reads at 4 ft." }));

            Assert.Contains("(cycle 1)", markdown);
            Assert.Contains("(cycle 2)", markdown);
            Assert.Contains("**iterate**", markdown);
            Assert.Contains("**ship**", markdown);
        }

        /// <summary>A question to the user is the one verdict that needs following up.</summary>
        [Fact]
        public void AskUserRecordsTheQuestion()
        {
            string markdown = ConversationExport.ToMarkdown(Request(new ReviewRecord
            {
                Cycle = 3,
                Verdict = ReviewVerdict.AskUser,
                Notes = "Cycle cap reached.",
                QuestionForUser = "Should the porch roof match the main ridge height?"
            }));

            Assert.Contains("**ask_user**", markdown);
            Assert.Contains("Asked the user:", markdown);
            Assert.Contains("Should the porch roof match", markdown);
        }

        [Fact]
        public void SessionWithNoReviewsGetsNoSection()
        {
            Assert.DoesNotContain("## Review history", ConversationExport.ToMarkdown(Request()));
        }

        /// <summary>
        /// The verdict stored is the one that took effect, not the one first returned — past the
        /// cycle cap an iterate becomes a question, and the export has to agree with what the
        /// loop actually did.
        /// </summary>
        [Fact]
        public void RecordIsBuiltFromTheOutcomeAsItStandsWhenStored()
        {
            var outcome = new ReviewOutcome
            {
                Verdict = ReviewVerdict.Iterate,
                Notes = "Still not right.",
                ModelId = "claude-opus-5"
            };

            outcome.Verdict = ReviewVerdict.AskUser;
            outcome.QuestionForUser = "How should I proceed?";

            var record = ReviewRecord.From(outcome, 3);

            Assert.Equal(ReviewVerdict.AskUser, record.Verdict);
            Assert.Equal("How should I proceed?", record.QuestionForUser);
            Assert.Equal(3, record.Cycle);
            Assert.False(record.Defensive);
        }
    }

    /// <summary>
    /// The API key was sitting verbatim in the settings XML, which made the file itself a usable
    /// credential for anything that could read it.
    /// </summary>
    public class SecretStoreTests
    {
        private const string Key = "sk-ant-api03-not-a-real-key-0123456789";

        [Fact]
        public void ProtectedValueRoundTrips()
        {
            string stored = SecretStore.Protect(Key);

            Assert.NotEqual(Key, stored);
            Assert.DoesNotContain("sk-ant", stored);
            Assert.Equal(Key, SecretStore.Unprotect(stored));
        }

        /// <summary>A key saved by an earlier build still has to work.</summary>
        [Fact]
        public void PlaintextIsReadBackUnchanged()
        {
            Assert.Equal(Key, SecretStore.Unprotect(Key));
            Assert.False(SecretStore.IsProtected(Key));
        }

        /// <summary>Re-saving must not encrypt an already-encrypted value a second time.</summary>
        [Fact]
        public void ProtectIsIdempotent()
        {
            string once = SecretStore.Protect(Key);
            string twice = SecretStore.Protect(once);

            Assert.Equal(once, twice);
            Assert.Equal(Key, SecretStore.Unprotect(twice));
        }

        [Fact]
        public void EmptyAndNullPassThrough()
        {
            Assert.Null(SecretStore.Protect(null));
            Assert.Equal(string.Empty, SecretStore.Protect(string.Empty));
            Assert.Null(SecretStore.Unprotect(null));
        }

        /// <summary>
        /// A value that claims to be encrypted but cannot be decrypted — a roamed profile, a
        /// different user — must come back null. Returning the ciphertext would send base64
        /// noise to the API as a credential and report a confusing auth failure.
        /// </summary>
        [Fact]
        public void UndecryptableProtectedValueBecomesNull()
        {
            Assert.Null(SecretStore.Unprotect("dpapi:bm90LXZhbGlkLWNpcGhlcnRleHQ="));
        }
    }
}
