using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RhinoClaude.Agent
{
    /// <summary>Result of one deterministic check (plan §5.2).</summary>
    public sealed class CheckResult
    {
        public string Name { get; set; }
        public bool Passed { get; set; }
        public string Details { get; set; }

        public static CheckResult Pass(string name, string details = null) =>
            new CheckResult { Name = name, Passed = true, Details = details };

        public static CheckResult Fail(string name, string details) =>
            new CheckResult { Name = name, Passed = false, Details = details };
    }

    /// <summary>Everything the reviewer is told, apart from the images.</summary>
    public sealed class ReviewFacts
    {
        public string UserRequest { get; set; }
        public string AgentSummary { get; set; }
        public string AgentExpectedOutcome { get; set; }
        public int ObjectsCreated { get; set; }
        public int ObjectsDeleted { get; set; }
        public List<string> LayersTouched { get; } = new List<string>();
        public List<CheckResult> Checks { get; } = new List<CheckResult>();
        public string Units { get; set; }
        public int ShotCount { get; set; }

        /// <summary>
        /// Deterministic composition facts from <c>check_massing_composition</c> — proportions,
        /// symmetry, mass hierarchy, boolean composition (semantic plan phase E).
        ///
        /// Same principle as the checks above, one level up: the reviewer is being asked
        /// whether the massing is right, and "the primary mass carries 71% of the volume, the
        /// envelope is 4.4:1, symmetry about Y is 0.12" is a far better basis for that judgment
        /// than the image alone. Null when the semantic layer is off or the document has no masses.
        /// </summary>
        public string MassingComposition { get; set; }

        /// <summary>
        /// The measured bounding box of what the agent left behind, as numbers.
        ///
        /// The reviewer was reading dimensions off the screenshots and getting them wrong — on one
        /// run it reported a "40×30×12 ft" envelope for geometry that actually measured 48×38×16,
        /// and shipped it. The figures were available the whole time; they just were not in the
        /// prompt. An overall size is the one fact a picture is worst at and the document is best at.
        /// Null when nothing survives to measure.
        /// </summary>
        public string MeasuredEnvelope { get; set; }

        public bool AllChecksPassed => Checks.All(c => c.Passed);
        public IEnumerable<CheckResult> Failures => Checks.Where(c => !c.Passed);
    }

    public enum ReviewVerdict
    {
        Ship,
        Iterate,
        AskUser,
        /// <summary>The reviewer call itself failed. Treated as ship so a broken reviewer
        /// cannot block work the agent already completed.</summary>
        Unavailable
    }

    public sealed class ReviewOutcome
    {
        public ReviewVerdict Verdict { get; set; } = ReviewVerdict.Unavailable;
        public string Notes { get; set; }
        public string QuestionForUser { get; set; }
        public TokenUsage Usage { get; set; }
        public string ModelId { get; set; }

        /// <summary>What goes back to the agent as signal_done's return value.</summary>
        public Dictionary<string, object> ToToolPayload()
        {
            return new Dictionary<string, object>
            {
                { "reviewVerdict", VerdictString(Verdict) },
                { "notes", Notes },
                { "questionForUser", QuestionForUser }
            };
        }

        public static string VerdictString(ReviewVerdict verdict)
        {
            switch (verdict)
            {
                case ReviewVerdict.Ship: return "ship";
                case ReviewVerdict.Iterate: return "iterate";
                case ReviewVerdict.AskUser: return "ask_user";
                default: return "unavailable";
            }
        }
    }

    /// <summary>
    /// One review, kept on the session so the export can show it.
    ///
    /// A verdict only reaches the transcript when it rides back to the agent through
    /// signal_done's tool payload. A defensive review has no tool call to ride on, so it
    /// appeared in the sidebar and then existed nowhere else — sessions shipped with a
    /// visible SHIP that the exported file had no record of. Recording every review here
    /// makes the exported conversation a complete account of what was judged.
    /// </summary>
    public sealed class ReviewRecord
    {
        /// <summary>Review cycle number, or 0 for a defensive mid-turn review.</summary>
        public int Cycle { get; set; }

        public bool Defensive => Cycle == 0;

        public ReviewVerdict Verdict { get; set; }
        public string Notes { get; set; }
        public string QuestionForUser { get; set; }
        public string ModelId { get; set; }

        public static ReviewRecord From(ReviewOutcome outcome, int cycle)
        {
            if (outcome == null) return null;
            return new ReviewRecord
            {
                Cycle = cycle,
                Verdict = outcome.Verdict,
                Notes = outcome.Notes,
                QuestionForUser = outcome.QuestionForUser,
                ModelId = outcome.ModelId
            };
        }
    }

    /// <summary>
    /// Composes the reviewer's prompt and parses its answer.
    ///
    /// Deliberately free of RhinoCommon so both directions can be tested: a reviewer that
    /// returns something unexpected must degrade to a usable verdict rather than throwing
    /// in the middle of a turn.
    /// </summary>
    public static class ReviewPrompt
    {
        public const string System =
@"You are reviewing an autonomous agent's work in a Rhino 3D document, on behalf of an architect.

You will see the user's original request, the agent's own summary of what it did, the results of
deterministic checks run against the document, and screenshots of the affected region from
several angles.

Decide one of:
- ship: the work matches the user's intent and nothing is clearly wrong.
- iterate: something is clearly wrong or missing that the agent could fix itself. Say
  specifically what, in terms of the model rather than the code.
- ask_user: the request was ambiguous enough that you cannot tell whether the result is right,
  and guessing would waste the user's time. Give the one question worth asking.

Judge the geometry, not the prose. The screenshots are the primary evidence — if the agent says
it built four walls and you see three, that is an iterate. Check proportion and placement, not
just presence. A deterministic check that failed is strong evidence but not automatically fatal;
say why it does or does not matter.

If the task was to modify an existing mass and the result contains new loose surfaces without
modifying the original mass, note this as a factual observation — it is yours to weigh, not a
rule: loose surfaces are the right answer for a canopy, an awning or a glazing panel, and the
wrong one for a shape the mass itself should have taken.

If the agent's result references, extends, or is derived from a mass that already existed when
the session started, rather than being constructed from what the user actually selected this
session, note that as a factual observation too. Ask yourself whether the pre-existing mass is
what the user meant, particularly when their selection was linework or curves rather than
solids — a plan's perimeter curves and a solid standing near them are not the same target, and
a footprint that came out rectangular from a selection that was not is the usual sign of the
wrong one having been picked.

Prefer ship. Iterating costs the user time and money, so reserve it for something a reasonable
architect would ask to have fixed before looking at the model themselves. Cosmetic preferences
are not defects.";

        /// <summary>The JSON schema the reviewer's response is constrained to.</summary>
        public const string OutputSchema = @"{
  ""type"": ""object"",
  ""required"": [""verdict"", ""notes""],
  ""properties"": {
    ""verdict"": { ""type"": ""string"", ""enum"": [""ship"", ""iterate"", ""ask_user""] },
    ""notes"": { ""type"": ""string"", ""description"": ""One short paragraph. What you saw, and why that verdict."" },
    ""questionForUser"": { ""type"": ""string"", ""description"": ""Only when the verdict is ask_user."" }
  },
  ""additionalProperties"": false
}";

        public static string BuildUserText(ReviewFacts facts)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));

            var sb = new StringBuilder();

            sb.AppendLine("<user_request>");
            sb.AppendLine(ToolJson.Safe(facts.UserRequest ?? "(not recorded)"));
            sb.AppendLine("</user_request>");
            sb.AppendLine();

            sb.AppendLine("<agent_summary>");
            sb.AppendLine(ToolJson.Safe(facts.AgentSummary ?? "(none given)"));
            if (!string.IsNullOrWhiteSpace(facts.AgentExpectedOutcome))
            {
                sb.AppendLine();
                sb.AppendLine("Expected outcome: " + ToolJson.Safe(facts.AgentExpectedOutcome));
            }
            sb.AppendLine("</agent_summary>");
            sb.AppendLine();

            sb.AppendLine("<document_facts>");
            sb.AppendLine("Model units: " + (facts.Units ?? "unknown"));
            sb.AppendLine("Objects created this session: " + facts.ObjectsCreated);
            sb.AppendLine("Objects deleted this session: " + facts.ObjectsDeleted);
            sb.AppendLine("Layers touched: " + (facts.LayersTouched.Count == 0
                ? "(none)"
                : string.Join(", ", facts.LayersTouched.Select(ToolJson.Safe))));
            if (!string.IsNullOrWhiteSpace(facts.MeasuredEnvelope))
            {
                sb.AppendLine("Measured envelope: " + facts.MeasuredEnvelope);
                sb.AppendLine("These figures are measured from the document and are exact. Where they " +
                              "disagree with what the screenshots appear to show, they are right and " +
                              "the image is being misread — state any dimension using these numbers.");
            }
            sb.AppendLine("</document_facts>");
            sb.AppendLine();

            sb.AppendLine("<checks>");
            if (facts.Checks.Count == 0)
            {
                sb.AppendLine("(no checks ran)");
            }
            else
            {
                foreach (var check in facts.Checks)
                {
                    sb.AppendLine(string.Format("[{0}] {1}{2}",
                        check.Passed ? "pass" : "FAIL",
                        check.Name,
                        string.IsNullOrWhiteSpace(check.Details) ? "" : " — " + check.Details));
                }
            }
            sb.AppendLine("</checks>");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(facts.MassingComposition))
            {
                sb.AppendLine("<massing_composition>");
                sb.AppendLine(facts.MassingComposition);
                sb.AppendLine("</massing_composition>");
                sb.AppendLine();
            }

            sb.Append(facts.ShotCount == 0
                ? "No screenshots were available — judge from the facts above, and prefer ship unless a check clearly failed."
                : "The " + facts.ShotCount + " image(s) below show the affected region of the model.");

            return sb.ToString();
        }

        /// <summary>
        /// Parse the reviewer's answer. Structured output should make this exact, but a
        /// reviewer that answers in prose still has to produce a usable verdict rather than
        /// an exception mid-turn.
        /// </summary>
        public static ReviewOutcome Parse(string responseText)
        {
            var outcome = new ReviewOutcome();

            if (string.IsNullOrWhiteSpace(responseText))
            {
                outcome.Verdict = ReviewVerdict.Unavailable;
                outcome.Notes = "The reviewer returned an empty response.";
                return outcome;
            }

            string json = ExtractJson(responseText);
            if (json != null)
            {
                try
                {
                    using (var doc = JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object &&
                            root.TryGetProperty("verdict", out var verdict) &&
                            verdict.ValueKind == JsonValueKind.String)
                        {
                            outcome.Verdict = ParseVerdict(verdict.GetString());
                            outcome.Notes = root.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.String
                                ? notes.GetString()
                                : null;
                            outcome.QuestionForUser =
                                root.TryGetProperty("questionForUser", out var q) && q.ValueKind == JsonValueKind.String
                                    ? q.GetString()
                                    : null;
                            return outcome;
                        }
                    }
                }
                catch (JsonException) { /* fall through to the prose path */ }
            }

            // Prose fallback: find whichever verdict word appears first.
            string lowered = responseText.ToLowerInvariant();
            int shipAt = lowered.IndexOf("ship", StringComparison.Ordinal);
            int iterateAt = lowered.IndexOf("iterate", StringComparison.Ordinal);
            int askAt = lowered.IndexOf("ask_user", StringComparison.Ordinal);

            outcome.Verdict = FirstMentioned(shipAt, iterateAt, askAt);
            outcome.Notes = responseText.Trim();
            if (outcome.Verdict == ReviewVerdict.AskUser)
                outcome.QuestionForUser = responseText.Trim();

            return outcome;
        }

        private static ReviewVerdict FirstMentioned(int shipAt, int iterateAt, int askAt)
        {
            var candidates = new List<KeyValuePair<int, ReviewVerdict>>();
            if (shipAt >= 0) candidates.Add(new KeyValuePair<int, ReviewVerdict>(shipAt, ReviewVerdict.Ship));
            if (iterateAt >= 0) candidates.Add(new KeyValuePair<int, ReviewVerdict>(iterateAt, ReviewVerdict.Iterate));
            if (askAt >= 0) candidates.Add(new KeyValuePair<int, ReviewVerdict>(askAt, ReviewVerdict.AskUser));

            if (candidates.Count == 0) return ReviewVerdict.Unavailable;
            return candidates.OrderBy(c => c.Key).First().Value;
        }

        public static ReviewVerdict ParseVerdict(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "ship": return ReviewVerdict.Ship;
                case "iterate": return ReviewVerdict.Iterate;
                case "ask_user":
                case "askuser": return ReviewVerdict.AskUser;
                default: return ReviewVerdict.Unavailable;
            }
        }

        /// <summary>Pull a JSON object out of a response that may be fenced or have prose around it.</summary>
        private static string ExtractJson(string text)
        {
            string trimmed = text.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNewline = trimmed.IndexOf('\n');
                if (firstNewline > 0) trimmed = trimmed.Substring(firstNewline + 1);
                int lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence > 0) trimmed = trimmed.Substring(0, lastFence);
                trimmed = trimmed.Trim();
            }

            if (trimmed.StartsWith("{", StringComparison.Ordinal)) return trimmed;

            int open = trimmed.IndexOf('{');
            int close = trimmed.LastIndexOf('}');
            if (open >= 0 && close > open) return trimmed.Substring(open, close - open + 1);

            return null;
        }
    }
}
