using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoClaude.Agent;
using RhinoClaude.Schema;

namespace RhinoClaude.Services.Agent
{
    /// <summary>
    /// Plan §5. When the agent says it is finished, check whether it actually is: run cheap
    /// deterministic checks against the document, photograph the affected region, and put both
    /// in front of a separate reviewer model with no tools.
    ///
    /// The reviewer is a second opinion, not a gate. Every failure path here returns
    /// <see cref="ReviewVerdict.Unavailable"/>, which the loop treats as ship — a broken
    /// reviewer must not strand work the agent already completed in the document.
    /// </summary>
    public sealed class SelfReviewService
    {
        /// <summary>The named view a reviewer prefers, stamped by ClaudeAddReviewView.</summary>
        public const string ReviewViewName = "Claude:Review";

        private readonly RhinoQueryService _query;
        private readonly ViewCaptureService _capture;
        private ILlmClient _client;
        private readonly SessionMutationLog _mutations;

        public SelfReviewService(
            RhinoQueryService query,
            ViewCaptureService capture,
            ILlmClient client,
            SessionMutationLog mutations)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _capture = capture ?? throw new ArgumentNullException(nameof(capture));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
        }

        /// <summary>
        /// Follow the loop onto a new provider. The reviewer always runs on the same service as
        /// the loop — a review that needed a second API key configured would just be off.
        /// </summary>
        public void UseClient(ILlmClient client)
        {
            if (client != null) _client = client;
        }

        public string ReviewerModel { get; set; } = AgentSettings.DefaultReviewerModel;
        public int MaxTokens { get; set; } = 4096;

        /// <summary>
        /// Supplies check_massing_composition's deterministic facts to the reviewer prompt
        /// (semantic plan phase E). Set by <c>AgentHost</c> when the semantic layer is on; null
        /// otherwise, and the reviewer prompt is unchanged from phase 1.
        ///
        /// Injected rather than referenced so this service keeps knowing nothing about the
        /// semantic layer — the phase 1 reviewer still works with the semantic tools switched off.
        /// </summary>
        public Func<string> MassingCompositionProvider { get; set; }

        // ── Deterministic checks (plan §5.2) ──────────────────────────

        /// <summary>UI-thread only. Cheap enough to run on every review.</summary>
        public ReviewFacts CollectFacts(string userRequest, string agentSummary, string expectedOutcome, int mark)
        {
            var doc = _query.Doc;
            var facts = new ReviewFacts
            {
                UserRequest = userRequest,
                AgentSummary = agentSummary,
                AgentExpectedOutcome = expectedOutcome,
                Units = doc.ModelUnitSystem.ToString()
            };

            var since = _mutations.Since(mark);
            facts.ObjectsCreated = since.Sum(m => m.CreatedIds.Count);
            facts.ObjectsDeleted = since.Sum(m => m.DeletedIds.Count);
            facts.LayersTouched.AddRange(_mutations.LayersTouched(mark));

            var surviving = _mutations.SurvivingCreatedIds(mark)
                .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();

            var objects = surviving
                .Select(id => doc.Objects.FindId(id))
                .Where(o => o != null && !o.IsDeleted)
                .ToList();

            facts.Checks.Add(CheckObjectsStillExist(surviving.Count, objects.Count));
            facts.Checks.Add(CheckDocumentValid(doc));
            facts.Checks.Add(CheckDegenerateGeometry(doc, objects));
            facts.Checks.Add(CheckBoundingBoxSanity(doc, objects));
            facts.Checks.Add(CheckLayerAssignment(doc, objects, facts.LayersTouched));
            facts.Checks.Add(CheckOrphanedLayers(doc, facts.LayersTouched));

            var tagCheck = CheckTagCoverage(userRequest, objects);
            if (tagCheck != null) facts.Checks.Add(tagCheck);

            facts.MassingComposition = SafeMassingComposition();
            facts.MeasuredEnvelope = MeasureEnvelope(objects, facts.Units);

            return facts;
        }

        /// <summary>
        /// The overall size of what survived, in model units. Handed to the reviewer as a number
        /// so a claim about the building's dimensions can be checked rather than eyeballed.
        /// </summary>
        private static string MeasureEnvelope(List<RhinoObject> objects, string units)
        {
            if (objects == null || objects.Count == 0) return null;

            var union = BoundingBox.Unset;
            foreach (var obj in objects)
            {
                var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
                if (box.IsValid) union.Union(box);
            }

            if (!union.IsValid) return null;

            var size = union.Max - union.Min;
            return string.Format(
                "min ({0}, {1}, {2}) → max ({3}, {4}, {5}); size {6} × {7} × {8} {9}",
                N(union.Min.X), N(union.Min.Y), N(union.Min.Z),
                N(union.Max.X), N(union.Max.Y), N(union.Max.Z),
                N(size.X), N(size.Y), N(size.Z), units ?? "model units");
        }

        private static string N(double value) =>
            value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// A classifier failure must not cost the review. Whatever goes wrong in there, the
        /// reviewer still gets the checks and the screenshots.
        /// </summary>
        private string SafeMassingComposition()
        {
            if (MassingCompositionProvider == null) return null;
            try
            {
                return MassingCompositionProvider();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static CheckResult CheckObjectsStillExist(int expected, int actual)
        {
            if (expected == 0)
                return CheckResult.Pass("objects_created", "The agent created nothing this turn.");

            return expected == actual
                ? CheckResult.Pass("objects_created", actual + " object(s) created and still present.")
                : CheckResult.Fail("objects_created",
                    string.Format("{0} object(s) were created but only {1} are still in the document.",
                        expected, actual));
        }

        /// <summary>
        /// The plan called for <c>doc.IsValid</c>, which is not a RhinoCommon API. The nearest
        /// real equivalent is sweeping the document for invalid geometry — which also catches
        /// damage the agent did to objects it did not create. Capped so a large model does not
        /// make every review expensive.
        /// </summary>
        private static CheckResult CheckDocumentValid(RhinoDoc doc)
        {
            const int scanLimit = 5000;

            try
            {
                int scanned = 0;
                var invalid = new List<string>();

                foreach (var obj in doc.Objects)
                {
                    if (obj.IsDeleted || obj.Geometry == null) continue;
                    if (++scanned > scanLimit) break;
                    if (!obj.Geometry.IsValid) invalid.Add(Short(obj.Id));
                }

                string scope = scanned > scanLimit
                    ? "first " + scanLimit + " objects"
                    : scanned + " object(s)";

                return invalid.Count == 0
                    ? CheckResult.Pass("document_geometry_valid", scope + " checked, all valid.")
                    : CheckResult.Fail("document_geometry_valid",
                        invalid.Count + " object(s) in the document have invalid geometry: " +
                        string.Join(", ", invalid.Take(5)));
            }
            catch (Exception ex)
            {
                return CheckResult.Pass("document_geometry_valid", "Could not be checked: " + ex.Message);
            }
        }

        private static CheckResult CheckDegenerateGeometry(RhinoDoc doc, List<RhinoObject> objects)
        {
            double tolerance = doc.ModelAbsoluteTolerance;
            var problems = new List<string>();

            foreach (var obj in objects)
            {
                switch (obj.Geometry)
                {
                    case Curve curve when curve.GetLength() <= tolerance:
                        problems.Add("curve " + Short(obj.Id) + " has zero length");
                        break;
                    case Brep brep when !brep.IsValid:
                        problems.Add("brep " + Short(obj.Id) + " is not valid");
                        break;
                    case Extrusion extrusion when !extrusion.IsValid:
                        problems.Add("extrusion " + Short(obj.Id) + " is not valid");
                        break;
                }
            }

            return problems.Count == 0
                ? CheckResult.Pass("no_degenerate_geometry", objects.Count + " object(s) checked.")
                : CheckResult.Fail("no_degenerate_geometry", string.Join("; ", problems.Take(5)));
        }

        private static CheckResult CheckBoundingBoxSanity(RhinoDoc doc, List<RhinoObject> objects)
        {
            if (objects.Count == 0)
                return CheckResult.Pass("bbox_sanity", "Nothing to measure.");

            double tolerance = doc.ModelAbsoluteTolerance;
            var problems = new List<string>();

            foreach (var obj in objects)
            {
                var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
                if (!box.IsValid)
                {
                    problems.Add("object " + Short(obj.Id) + " has no valid bounding box");
                    continue;
                }

                double diagonal = box.Diagonal.Length;

                // A point legitimately has a zero-size box; anything else should not.
                if (diagonal <= tolerance && !(obj.Geometry is Rhino.Geometry.Point))
                    problems.Add("object " + Short(obj.Id) + " is smaller than the document tolerance");

                // 1e7 model units is 158 miles in inches — always a unit-conversion mistake
                // rather than a building.
                if (diagonal > 1e7)
                    problems.Add("object " + Short(obj.Id) + " spans " + diagonal.ToString("n0") +
                                 " model units, which looks like a units mistake");
            }

            return problems.Count == 0
                ? CheckResult.Pass("bbox_sanity", "All created objects are a plausible size.")
                : CheckResult.Fail("bbox_sanity", string.Join("; ", problems.Take(5)));
        }

        private static CheckResult CheckLayerAssignment(RhinoDoc doc, List<RhinoObject> objects, List<string> layersTouched)
        {
            // Only meaningful if the agent bothered with layers at all this session.
            if (layersTouched.Count == 0)
                return CheckResult.Pass("layer_assignment", "The agent did not create or use any named layer.");

            int defaultLayerIndex = 0;
            var stranded = objects
                .Where(o => o.Attributes.LayerIndex == defaultLayerIndex)
                .Select(o => Short(o.Id))
                .ToList();

            return stranded.Count == 0
                ? CheckResult.Pass("layer_assignment", "Everything created is on a named layer.")
                : CheckResult.Fail("layer_assignment",
                    stranded.Count + " object(s) were left on the default layer despite the agent " +
                    "creating layers this session: " + string.Join(", ", stranded.Take(5)));
        }

        private static CheckResult CheckOrphanedLayers(RhinoDoc doc, List<string> layersTouched)
        {
            var empty = new List<string>();

            foreach (var path in layersTouched)
            {
                int index = doc.Layers.FindByFullPath(path, -1);
                if (index < 0) continue;
                var layer = doc.Layers[index];
                if (layer == null || layer.IsDeleted) continue;

                int count = doc.Objects.FindByLayer(layer)?.Length ?? 0;
                bool hasChildren = doc.Layers.Any(l => !l.IsDeleted && l.ParentLayerId == layer.Id);

                if (count == 0 && !hasChildren) empty.Add(path);
            }

            return empty.Count == 0
                ? CheckResult.Pass("no_orphaned_layers")
                : CheckResult.Fail("no_orphaned_layers",
                    "Layer(s) created but left empty: " + string.Join(", ", empty.Take(5)));
        }

        /// <summary>
        /// Only runs when the request looks like it was about tagging — otherwise every
        /// ordinary geometry turn would fail a check nobody asked for.
        /// </summary>
        private static CheckResult CheckTagCoverage(string userRequest, List<RhinoObject> objects)
        {
            if (string.IsNullOrWhiteSpace(userRequest) || objects.Count == 0) return null;

            string lowered = userRequest.ToLowerInvariant();
            bool tagRelated = lowered.Contains("tag") ||
                              lowered.Contains("element type") ||
                              TagSchema.AllKeys.Any(k => lowered.Contains(k.ToLowerInvariant()));

            if (!tagRelated) return null;

            var untagged = objects
                .Where(o => string.IsNullOrEmpty(o.Attributes.GetUserString(TagSchema.ElementType)))
                .Select(o => Short(o.Id))
                .ToList();

            return untagged.Count == 0
                ? CheckResult.Pass("tag_coverage", "Every created object has an RC:ElementType.")
                : CheckResult.Fail("tag_coverage",
                    untagged.Count + " created object(s) have no RC:ElementType even though the " +
                    "request was about tagging: " + string.Join(", ", untagged.Take(5)));
        }

        private static string Short(Guid id) => id.ToString().Substring(0, 8);

        // ── Screenshots (plan §5.3) ───────────────────────────────────

        /// <summary>
        /// Always multi-shot. If a Claude:Review named view exists it leads the bundle, because
        /// the user pointed the camera there deliberately; the generated angles follow.
        /// UI-thread only.
        /// </summary>
        public List<CaptureResult> CaptureForReview(int mark)
        {
            var doc = _query.Doc;

            var framingIds = _mutations.SurvivingCreatedIds(mark)
                .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();

            var request = new CaptureRequest
            {
                Width = 1280,
                Height = 800,
                DisplayMode = "shaded",
                ShowGrid = false
            };

            bool hasReviewView = doc.NamedViews.FindByName(ReviewViewName) >= 0;
            if (hasReviewView)
                request.Shots.Add(new CameraShot { NamedView = ReviewViewName });

            var framing = framingIds.Count > 0 ? framingIds : null;
            request.Shots.Add(new CameraShot { Preset = "iso_ne", FramingIds = framing });
            request.Shots.Add(new CameraShot { Preset = "plan", FramingIds = framing });
            if (!hasReviewView)
                request.Shots.Add(new CameraShot { Preset = "front", FramingIds = framing });

            try
            {
                return _capture.Capture(request);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("RhinoClaude: review capture failed — " + ex.Message);
                return new List<CaptureResult>();
            }
        }

        // ── The reviewer call (plan §5.4) ─────────────────────────────

        /// <summary>
        /// A separate, short, tool-less call. Runs on a background thread; the facts and
        /// captures must already have been gathered on the UI thread.
        /// </summary>
        public async Task<ReviewOutcome> ReviewAsync(
            ReviewFacts facts,
            List<CaptureResult> captures,
            CancellationToken cancellationToken)
        {
            facts.ShotCount = captures?.Count ?? 0;

            var content = new List<ContentBlock> { new TextBlock(ReviewPrompt.BuildUserText(facts)) };

            if (captures != null)
            {
                foreach (var capture in captures)
                {
                    if (string.IsNullOrEmpty(capture.Base64)) continue;
                    content.Add(new ImageBlock { MediaType = capture.MediaType, Data = capture.Base64 });
                }
            }

            var request = new MessagesRequest
            {
                Model = ReviewerModel,
                MaxTokens = MaxTokens,
                System = ReviewPrompt.System,
                Messages = { new AgentMessage("user", content.ToArray()) }
            };

            // No tools by design. Structured output pins the verdict to the three allowed
            // values so the loop never has to guess what the reviewer meant.
            request.OutputConfig = new OutputConfig
            {
                Format = new OutputFormat { SchemaJson = ReviewPrompt.OutputSchema }
            };

            if (ModelCapabilities.SupportsEffort(ReviewerModel))
                request.OutputConfig.Effort = "medium";

            try
            {
                var usage = new TokenUsage();
                var message = await _client.SendAsync(request, cancellationToken, usage).ConfigureAwait(false);

                var outcome = ReviewPrompt.Parse(message.TextContent());
                outcome.ModelId = ReviewerModel;
                outcome.Usage = usage;
                return outcome;
            }
            catch (OperationCanceledException)
            {
                return new ReviewOutcome
                {
                    Verdict = ReviewVerdict.Unavailable,
                    Notes = "Review was cancelled."
                };
            }
            catch (Exception ex)
            {
                // Deliberately non-fatal: the geometry is already in the document.
                return new ReviewOutcome
                {
                    Verdict = ReviewVerdict.Unavailable,
                    Notes = "The reviewer call failed (" + ex.Message + "). The work was left as-is."
                };
            }
        }
    }
}
