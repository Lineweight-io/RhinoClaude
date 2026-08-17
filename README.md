# RhinoClaude — Claude AI Plugin for Rhinoceros 3D

An agent that works inside a live Rhino document. You describe what you want in a docked chat
sidebar; Claude inspects the model, creates and edits geometry through a curated tool set,
looks at what it built, and reports back — all inside a single undo group you can revert with
one click.

Built by [Lineweight](https://github.com/Lineweight-io). MIT licensed — see [`LICENSE`](LICENSE).

**Where to start reading:** [`AGENT_REFACTOR_PLAN.md`](AGENT_REFACTOR_PLAN.md) is the architecture
of the agent loop; [`SEMANTIC_LAYER_PLAN.md`](SEMANTIC_LAYER_PLAN.md) is the architecture of the
massing model on top of it; [`LAYER_CONVENTIONS.md`](LAYER_CONVENTIONS.md) is the one thing you
need to know to use it; [`TESTING.md`](TESTING.md) is the smoke-test script.

> **Status: code-complete against `AGENT_REFACTOR_PLAN.md` (phases 0–10) and
> `SEMANTIC_LAYER_PLAN.md` (phases A–I).** Both target frameworks build with no warnings and
> 367 unit tests pass.
>
> **Nothing Rhino-facing has been run inside Rhino yet.** RhinoCommon is a compile-only
> reference, so no test here touches a real document. See **`TESTING.md`** for the smoke-test
> script to run first.

## The semantic layer

Beyond the raw geometry tools, the agent holds a mental model of the design **in the same terms
the architect does**: masses, their faces and edges, and the openings cut into them.

That framing comes from how Rhino actually gets used. Architects doing SD work do not draw floors,
then walls on the floors, then a roof on the walls — that is the Revit workflow. They start with
solid masses, push and pull faces to refine proportion, boolean-union masses that read as one
form, and boolean-difference to cut light wells, recessed entries and window openings. So:

- **Mass is the atom** — a solid Brep. Its function (Office, Residential, …) is a property of it,
  not a different kind of thing.
- **Facade, roof and floor are labels on faces**, applied at query time from the face's normal and
  elevation. Nobody draws a facade in Rhino; a facade is a mass face that points sideways.
- **An opening is a hole** someone subtracted, detected from the Brep's inner trim loops.
- **Operations are first-class** — `push_pull_face`, `add_mass`, `subtract_mass`, `cut_opening`
  are the verbs, because mass modelling *is* the SD workflow.

**Start here: [`LAYER_CONVENTIONS.md`](LAYER_CONVENTIONS.md).** The one thing that matters is
putting your masses on `MASS_*` layers — everything else is derived from the geometry. Or run
`ClaudeLearnNamingConvention` and keep your firm's existing layer names.

The classifier resolves in strict priority order: an explicit tag from `ClaudeSetElement`, then
your learned convention, then the shipped canonical one, then geometry inference. Every result
carries `classifiedBy`, and the agent is told to hedge on anything inferred from geometry alone —
and to believe a screenshot over a semantic result when the two disagree.

## Features

- **Docked chat sidebar** (`ClaudeChat`) — the primary interface. Streamed responses, inline
  tool-call cards with input/result JSON, screenshot thumbnails, a live cost meter and
  iteration counter, session dropdown, and a settings gear.
- **Streaming tool-use loop** — SSE from the first request. Claude plans, calls tools, reads
  the results, and iterates until it signals done or hits a guardrail. Summarized reasoning
  streams into a collapsed card above each answer.
- **Guardrails** — $2.00 per turn and 25 iterations by default, both configurable. The loop
  stops before the next model call rather than mid-mutation.
- **Pluggable model provider** — Anthropic by default; DeepSeek, Moonshot (Kimi), Qwen, OpenAI,
  a local Ollama, or any OpenAI-compatible endpoint are selectable in settings. See
  [Model providers](#model-providers).
- **One-click revert** — every mutation opens its own Rhino undo record; "Revert session"
  (or `ClaudeRevertSession`) pops all of them.
- **Review exports** — one button writes the conversation as shareable markdown (messages,
  tool calls, timing, cost); another writes just the objects the agent created or changed to
  a separate .3dm.
- **Vision** — `capture_views` is a 3D camera controller, not a screenshot button: named
  views, explicit camera poses, orthographic/isometric presets, and orbit-from-current, up
  to 6 shots returned from one call.
- **C# escape hatch** — `run_rhinocommon_script` runs a snippet with full RhinoCommon access
  inside an isolated undo record, with a timeout, a static-analysis blocklist, and a JSONL log.
- **Self-review** — `signal_done` triggers deterministic checks plus a multi-shot look at the
  model, judged by a separate tool-less call on Opus 5. See below.
- **Conversation persistence** — the conversation is stored in the `.3dm` itself, so reopening
  a file offers to resume where you left off.
- **Semantic tagging** — the existing `RC:` tag system, the Tag Inspector panel, and the
  deterministic `RC*` commands are unchanged.

### Tools (64)

**Raw geometry (39)** — the phase 1 set, unchanged. The semantic tools sit on top of these;
nothing was replaced.

| Group | Tools |
|---|---|
| Query | `describe_document`, `list_layers`, `list_objects`, `get_object`, `get_selection`, `list_named_views`, `list_blocks` |
| Create | `create_point`, `create_line_curve`, `create_arc_curve`, `create_circle`, `create_rectangle`, `create_box` |
| Transform | `translate_objects`, `rotate_objects`, `scale_objects`, `scale_1d`, `mirror_objects` |
| Boolean / modify | `boolean_union`, `boolean_difference`, `boolean_intersection`, `offset_curve`, `extrude_curve`, `move_face`, `move_edge`, `delete_objects` |
| Layer | `ensure_layer`, `assign_objects_to_layer` |
| Block | `insert_block`, `import_3dm_as_block` |
| Material | `assign_material` |
| Selection / view | `select_objects`, `deselect_all`, `zoom_extents`, `capture_views` |
| Tier 2 | `run_rhinocommon_script` |
| Tier 3 | `run_rhino_command` — **off by default**, see settings |
| Meta | `set_object_tags`, `signal_done` |

`delete_objects` is an addition to the plan's inventory — without it an agent that mis-creates
geometry cannot clean up after itself.

`run_rhino_command` defaults off. The plan includes it but calls it a last resort: scripted
commands are non-atomic and undo poorly, and the curated tools plus the C# hatch cover nearly
everything. A blocklist rejects `_Exit`, `_New`, `_Open`, `_Close`, `_SaveAs`, `_Options` and the
script editors before execution, and the first use in a session raises a notice in the panel.

Selection and viewport tools live in `RhinoInteractionService`, deliberately apart from the
mutation service: Rhino does not put selection or camera changes on the undo stack, so wrapping
them in undo records would inflate the count that "Revert session" pops and undo real geometry
edits instead.

**Semantic (25)** — 17 read, 7 massing operations, plus `promote_opening_to_entry`. (The plan
calls this "24"; its own breakdown of 17 + 7 + 1 is 25.) Switchable off in settings, which gives
the agent exactly the phase 1 tool set.

| Group | Tools |
|---|---|
| Descriptive | `describe_massing`, `describe_context`, `find_element` |
| Mass catalog | `list_masses`, `list_mass_groups`, `analyze_boolean_history` |
| Face and edge | `get_mass_faces`, `get_face`, `get_mass_edges`, `check_face_relationships`, `find_openings_in_face` |
| Envelope / program | `check_wall_window_ratio`, `get_roof_analysis`, `get_program_allocation`, `check_massing_composition`, `get_level_info` |
| Constraints | `get_zoning_envelope` |
| Massing operations | `push_pull_face`, `add_mass`, `subtract_mass`, `cut_opening`, `slice_mass_at_elevation`, `extrude_face_outward`, `fillet_edges`, `promote_opening_to_entry` |
| Solid-preserving moves | `subdivide_face`, `move_face`, `move_edge`, `create_gable_roof` |

Every tool that operates on a face takes the same `FaceSelector` union — by id, by index, by
orientation, by role, by role *and* orientation, optionally narrowed to an elevation band. So
"pull the top face up 6 feet" is `push_pull_face(massId, {role: "roof"}, 6)` rather than a guessed
face index, and "recess the ground-floor south face" is
`push_pull_face(massId, {orientation: "S", elevationRange: [0, 12]}, -8)`.

The semantic writes go through the same `RhinoMutationService` as the raw ones, one undo record
per tool call — a `cut_opening` is a cutter solid, a boolean, a tag and a delete, and it undoes
as one window rather than four steps.

Reshaping a mass keeps it a solid. `subdivide_face` splits one face of a closed Brep in two and
hands back the id of the edge it created; `move_edge` then lifts that edge, and the box becomes
a gable that is still one closed manifold. `create_gable_roof` composes the pair for the case
that comes up most. Loose surfaces stay available — a canopy or a glazing panel is genuinely
not part of the solid — but they are no longer the path of least resistance for a shape the
mass itself should have taken.

**Out of scope by design** (plan §2.2): wall assemblies, MEP, structural sizing, FF&E, detailed
schedules, code checking beyond the zoning envelope, site engineering, daylight simulation,
component families. The boundary is schematic design — if it wouldn't appear in a 30% SD
deliverable, the semantic layer doesn't model it, and the raw tools plus the script hatch are
there for the rest.

## Requirements

- **Rhino 7** (Windows, .NET Framework 4.8) and/or **Rhino 8** (Windows, .NET 7)
- **.NET SDK 7 or later** (for building)
- **Anthropic API key** — get one at [console.anthropic.com](https://console.anthropic.com).
  Optional if you point the plugin at another provider or a local Ollama; see
  [Model providers](#model-providers).

## Project structure

```
RhinoClaude.sln
├── RhinoClaude/                     # the plugin
│   ├── Agent/                       # protocol + loop (no RhinoCommon below AgentHost)
│   │   ├── AnthropicModels.cs       #   wire format: content blocks, messages, requests
│   │   ├── AnthropicClient.cs       #   HTTP + SSE streaming, retry/backoff
│   │   ├── SseParser.cs             #   SSE framing
│   │   ├── StreamAccumulator.cs     #   deltas → assembled message + usage
│   │   ├── AgentSession.cs          #   the tool-use loop state machine
│   │   ├── ToolRegistry.cs          #   tool definitions sent to Claude
│   │   ├── ToolDispatcher.cs        #   resolve + run on Rhino's UI thread
│   │   ├── CostBudget.cs            #   pricing table + per-turn guardrails
│   │   ├── UndoScope.cs             #   undo-record RAII + session log
│   │   ├── SystemPrompt.cs
│   │   ├── AgentSettings.cs
│   │   ├── JsonlLogger.cs
│   │   ├── ReviewModels.cs          #   review prompt + verdict parsing
│   │   ├── SessionMutationLog.cs    #   what the agent actually changed
│   │   ├── HistoryCompactor.cs      #   shrink old tool results (risk #6)
│   │   ├── ConversationSnapshot.cs  #   persisted conversation format
│   │   ├── ConversationExport.cs    #   session → shareable markdown
│   │   ├── ExportNaming.cs          #   export filename rules
│   │   ├── ModelCapabilities.cs     #   per-model request shaping
│   │   └── AgentHost.cs             #   per-document object graph
│   ├── Services/Agent/              # everything that touches RhinoCommon
│   │   ├── RhinoQueryService.cs     #   read-only document access
│   │   ├── RhinoMutationService.cs  #   writes, each in its own undo record
│   │   ├── ViewCaptureService.cs    #   3D camera controller + multi-shot capture
│   │   ├── ScriptExecutorService.cs #   Roslyn C# escape hatch
│   │   ├── SessionSnapshotService.cs#   session undo log + revert
│   │   ├── RhinoInteractionService.cs # selection + viewport (no undo record)
│   │   ├── SelfReviewService.cs     #   deterministic checks + reviewer call
│   │   ├── AgentConversationStore.cs#   conversation storage in the .3dm
│   │   ├── SessionExportService.cs  #   markdown + selected-objects .3dm exports
│   │   └── RhinoCommandService.cs   #   Tier 3 scripted commands
│   ├── Semantic/                    # the semantic core — deliberately Rhino-free
│   │   ├── SemanticVocabulary.cs    #   the eleven element types and their enums
│   │   ├── CanonicalConvention.cs   #   the shipped MASS_/OPENING_/SITE_ layer names
│   │   ├── LayerConventionMap.cs    #   learned conventions + the resolution rule
│   │   ├── SemanticModels.cs        #   Vec3/BoxView + every element view
│   │   ├── UnitContext.cs           #   feet-declared thresholds → model units
│   │   ├── ObjectClassifier.cs      #   the four-step rule, object level
│   │   ├── FaceClassifier.cs        #   orientation + role from normal and elevation
│   │   ├── EdgeClassifier.cs        #   parapet / corner / ridge / eave
│   │   ├── OpeningClassifier.cs     #   subtype from dimensions
│   │   ├── FaceSelector.cs          #   the selector unions + resolvers
│   │   ├── CompositionAnalyzer.cs   #   sits-on / abuts / unioned-with, grouping
│   │   ├── ElementQueryParser.cs    #   find_element's rules-based parser
│   │   ├── MassingNarrator.cs       #   describe_massing's narrative
│   │   ├── EnvelopeAnalytics.cs     #   WWR, roof form, program allocation
│   │   ├── MassingComposition.cs    #   proportions, symmetry, hierarchy, booleans
│   │   ├── FaceRelationships.cs     #   coplanar / parallel / perpendicular / flush
│   │   ├── ZoningEnvelope.cs        #   height, setbacks, FAR
│   │   └── GeometryMath.cs          #   PCA principal axes, symmetry scoring
│   ├── Services/Semantic/           # the semantic layer's RhinoCommon half
│   │   ├── SemanticClassifier.cs    #   doc → SemanticView (object level)
│   │   ├── MassGeometryAnalyzer.cs  #   mass → MassGeometryView (geometry level)
│   │   ├── ElementRegistry.cs       #   the two-tier cache + doc-event invalidation
│   │   ├── SemanticQueryService.cs  #   the 17 read tools
│   │   ├── SemanticMutationService.cs #  the 7 massing operations + entry promotion
│   │   ├── BooleanHistoryReader.cs  #   Rhino history when it exists
│   │   ├── LayerConventionStore.cs  #   doc-level and firm-level convention storage
│   │   └── SemanticClassifierPrompt.cs # the LearnNamingConvention one-shot
│   ├── Tools/                       # tool schemas + handler wiring
│   │   ├── Phase1Tools.cs           #   query, create, transform, capture, script, done
│   │   ├── Tier1Tools.cs            #   the rest of the plan §3 inventory
│   │   ├── SemanticReadTools.cs     #   the 17 semantic reads
│   │   ├── SemanticWriteTools.cs    #   the 7 massing operations + entry promotion
│   │   └── ToolInput.cs             #   shared argument reading
│   ├── UI/AgentChatPanel.cs         # the sidebar
│   ├── UI/AgentSettingsDialog.cs
│   ├── UI/TagInspectorPanel.cs      # unchanged
│   ├── Commands/                    # ClaudeChat, ClaudeSetKey, ClaudeTag,
│   │                                # ClaudeRevertSession, ClaudeAddReviewView,
│   │                                # ClaudeSetElement, ClaudeClearElement,
│   │                                # ClaudeLearnNamingConvention,
│   │                                # RC* tag commands, RCBuildFromDiagram
│   ├── Schema/                      # TagSchema, BuildingStandards
│   └── Services/                    # TagService, SceneContextCollector
└── RhinoClaude.Tests/               # xunit; links the RhinoCommon-free sources
```

## Building

```
dotnet build RhinoClaude.sln
dotnet test  RhinoClaude.Tests/RhinoClaude.Tests.csproj
```

Output lands in `RhinoClaude/bin/Build/`. The post-build step copies `RhinoClaude.dll` to
`RhinoClaude.rhp`, which is what Rhino loads. Both target frameworks write to the same
folder, so the `.rhp` there is whichever TFM built last — build the one matching your Rhino:

```
dotnet build RhinoClaude/RhinoClaude.csproj -f net48    # Rhino 7
dotnet build RhinoClaude/RhinoClaude.csproj -f net7.0   # Rhino 8
```

The test project deliberately does not reference the plugin assembly. RhinoCommon is a
compile-only reference, so its types are absent at test runtime; instead the RhinoCommon-free
parts of the agent core are linked in as source. Anything listed in that `ItemGroup` must stay
free of `using Rhino…`.

## Installation

Drag `RhinoClaude/bin/Build/RhinoClaude.rhp` into an open Rhino window, or run `PlugInManager`
→ Install and browse to it. Restart Rhino.

Rhino copies the `.rhp` into its own plugin folder on install, so rebuilding does not
automatically update an installed copy — re-drag after a rebuild, or point Rhino at the build
output directly.

> **The `.rhp` is not self-contained.** It needs the sibling DLLs in `bin/Build` —
> System.Text.Json and, since phase 1, Roslyn (`Microsoft.CodeAnalysis.*`, ~13 MB, larger than
> the plan's 5 MB estimate). Those are gitignored, so a fresh clone must run `dotnet build`
> before the committed `.rhp` will load. The `.rhp` is tracked only so a machine without the
> toolchain still has the last known build to look at.

## Setup

```
ClaudeSetKey
```

Enter your Anthropic API key (starts with `sk-ant-`). It is stored in the plugin's settings and
persists across sessions. Alternatively set the `ANTHROPIC_API_KEY` environment variable —
plugin settings win if both are present.

`API_Key.txt` at the repo root is gitignored and is not read by the plugin. Use `ClaudeSetKey`
or the environment variable.

To run against something other than Claude, open the gear in the sidebar, pick a provider, and
enter that provider's key there — see [Model providers](#model-providers). Ollama needs no key.

## Usage

```
ClaudeChat
```

Opens the sidebar. Type a request and press Enter. If you have objects selected, tick
"send with message" to pass their ids along so Claude prefers them over guessing.

While a turn runs:
- **⏸ Stop** cancels. Any tool already dispatched finishes; nothing new fires.
- The **cost meter** shows spend against the per-turn budget; click it for a per-iteration
  breakdown. It turns amber past 60% and red past 90%.
- **Tool cards** collapse by default. Click to see the exact input and result. Cards with
  screenshots and cards that failed open automatically.

After a turn:
- **↶ Revert session** undoes everything the session changed. It issues one Rhino undo step
  per mutation, so hand edits made since the session started are undone too — the confirmation
  dialog says so.
- **⟲ New** starts a fresh conversation.

### Exporting for review

A second button row under the session controls turns a session into something you can hand
to someone who does not have Rhino open. Both buttons are disabled, with a tooltip saying
why, until there is something to export.

- **⭳ Export chat…** writes the conversation as markdown: every user turn, every answer,
  every tool call with its arguments and result, per-call timing, the token totals and the
  estimated cost, plus a summary of what the agent changed in the document. Screenshots are
  noted but not embedded. Suggested name
  `RhinoClaude_conversation_{docname}_{yyyyMMdd_HHmm}.md`. It exports whichever session the
  header dropdown is showing; for a session restored from the .3dm, tool results come from
  the saved conversation and timings are marked as not saved.
- **⭳ Export result…** writes just the objects the agent created or changed this session to a
  standalone .3dm, so a reviewer opens the agent's work rather than the whole model. The set
  comes from the session mutation log — everything created or edited in place, minus anything
  the agent later deleted. Rhino's own writer does the work, so layers, materials and block
  definitions come across. Suggested name `RhinoClaude_result_{docname}_{yyyyMMdd_HHmm}.3dm`.
  The working .3dm is never overwritten: picking its own path is refused.

Both save through the standard Windows save dialog, opening next to the .3dm when it has
been saved and on the Desktop when it has not.

### Other commands

| Command | What it does |
|---|---|
| `ClaudeChat` | Open/focus the sidebar |
| `ClaudeSetKey` | Store the API key |
| `ClaudeTag` | Describe a selection in prose → structured `RC:` tags (still one-shot) |
| `ClaudeRevertSession` | Same as the sidebar's revert button |
| `ClaudeAddReviewView` | Stamp the current camera as `Claude:Review` for self-review to judge from |
| `ClaudeSetElement` | Tag a selection as Mass / Opening / Overhang / MassGroup / Level / Site — beats every layer convention. Its `SetFaceRole` option labels a clicked face directly |
| `ClaudeClearElement` | Remove semantic tags from a selection (leaves `RC:` tags alone) |
| `ClaudeLearnNamingConvention` | Teach Claude your firm's layer names, and set the firm floor-to-floor |
| `RCSetTag`, `RCQuery`, `RCInspectTags`, `RCValidateTags`, `RCTagInspector` | Deterministic tag operations |
| `RCBuildFromDiagram` | The algorithmic ADA restroom builder (no AI) |

## Logs

All append-only JSONL under `%APPDATA%\RhinoClaude\`:

- `script_log.jsonl` — every `run_rhinocommon_script` call: purpose, code, outcome, duration,
  object deltas. This is the data source for deciding which scripts deserve promotion to a
  curated Tier 1 tool.
- `capture_log.jsonl` — every `capture_views` call: shot count and kinds, size, session and
  iteration. Instrumentation for the screenshot-cadence question in the plan.
- `classifier_timing.jsonl` — every classifier rebuild, both tiers: object-level duration with
  mass and unclassified counts, and per-mass geometry duration with face, edge, opening and cut
  counts. The budgets to watch are <150 ms object-level on a mid-scale model and <50 ms per mass;
  the semantic plan's §6.3 fallbacks exist for when they are missed.

Screenshots are also cached to `%TEMP%\RhinoClaude\screenshots\<sessionId>\`.

## Settings

Behind the gear in the sidebar header:

| Setting | Default |
|---|---|
| Provider | Anthropic |
| Loop model | `claude-haiku-4-5-20251001` |
| Effort | `high` |
| Show summarized reasoning | on |
| Self-review | on |
| Reviewer model | `claude-opus-5` |
| Max review cycles per turn | 2 |
| Cost budget per turn | $2.00 |
| Max iterations per turn | 25 |
| Max tokens per response | 32000 |
| Script tool enabled | yes |
| Script timeout | 15s (max 60s) |
| Semantic layer enabled | yes |
| Firm floor-to-floor | 0 (unset) |

Model, budget, iteration, token, effort and reasoning-display changes apply to the next turn.
Toggling the script tool or the semantic layer changes the tool set, which is fixed for a
session's lifetime — those take effect on the next **⟲ New** session.

**Firm floor-to-floor** is what inferred Levels are spaced at when nobody drew any, and what
`check_massing_composition` measures vertical rhythm against. `ClaudeLearnNamingConvention` also
asks for it, and either place sets the same value.

**Effort** controls how much the model thinks and how hard it works. `high` is the API default;
`xhigh` suits the hardest agentic work, `medium` is the cost-saving step down. It is greyed out
on models that have no effort parameter.

**Max tokens** caps thinking *plus* the response together on models that think by default
(Sonnet 5, Opus 5), which is why the default is 32000 rather than a value tuned for a
non-thinking model. Requests always stream, so there is no timeout reason to keep it small.

### Model providers

Anthropic is the default and the only one the loop was designed against. Everything else goes
through an OpenAI-compatible translation layer behind `ILlmClient`, so the agent loop, the tool
schemas and the self-review path are unchanged whichever you pick.

| Provider | Endpoint | Key |
|---|---|---|
| Anthropic (Claude) — default | `https://api.anthropic.com/v1` | `ANTHROPIC_API_KEY` |
| DeepSeek | `https://api.deepseek.com/v1` | `DEEPSEEK_API_KEY` |
| Moonshot (Kimi) | `https://api.moonshot.ai/v1` | per-provider, set in settings |
| Alibaba Qwen (Model Studio) | `https://dashscope-intl.aliyuncs.com/compatible-mode/v1` | per-provider, set in settings |
| OpenAI | `https://api.openai.com/v1` | per-provider, set in settings |
| Ollama (local) | `http://localhost:11434/v1` | none required |
| Custom OpenAI-compatible | whatever you enter | per-provider, set in settings |

Keys are stored per provider, so switching back and forth does not make you re-enter anything,
and the loop and reviewer model ids are remembered per provider too — selecting DeepSeek will
not leave a Claude model id pinned against it.

The translation is a pure layer with no HTTP in it: outgoing, the Anthropic request shape is
rewritten into OpenAI chat-completions; incoming, the OpenAI SSE stream is rewritten back into
Anthropic-shaped events, so the accumulator and the panel see one format. Features an
OpenAI-compatible endpoint has no equivalent for — signed thinking blocks, prompt-cache
breakpoints — are dropped rather than faked.

**A caveat worth stating plainly:** tool-use reliability varies a lot by model. The loop asks for
many sequential tool calls with strict JSON arguments, and models that are weaker at that will
stall or emit malformed input more often. Cost accounting also falls back to a conservative
estimate on models whose pricing isn't in `CostBudget`'s table, which makes the budget guardrail
approximate rather than exact on those.

### Model compatibility

The request shape is built per model rather than pinned to one, because the parameters are not
interchangeable — sending `thinking` or `output_config.effort` to Sonnet 4.5 is a 400, while
*omitting* `thinking` on Sonnet 5 silently leaves adaptive thinking on. `ModelCapabilities`
holds that matrix and the settings dialog greys out whatever the selected model won't accept.

Thinking blocks are accumulated with their **signature** and replayed unchanged on every
iteration. That is load-bearing, not cosmetic: the API validates the signature, and a tool-use
loop replays the whole conversation on each pass.

## Troubleshooting

- **"No API key configured"** — run `ClaudeSetKey`.
- **Authentication failed (401)** — the stored key is wrong or revoked; run `ClaudeSetKey` again.
- **Rate limited (429)** — the client retries with backoff up to 3 times, honouring
  `Retry-After`, then surfaces the error.
- **"Budget reached"** — the turn stopped at the cost or iteration ceiling. Raise it in
  settings, or send a narrower follow-up; the conversation is preserved.
- **A tool failed** — expand its card. The error text is what Claude sees, so it is written to
  be actionable rather than terse.
- **Revert did less than expected** — revert issues one undo step per mutation. If you undid
  something by hand mid-session, the counts drift.

## Self-review

When the agent calls `signal_done`, the turn does not end straight away:

1. Deterministic checks run against the document — do the created objects still exist, is any
   geometry degenerate or invalid, are the bounding boxes a plausible size, did anything get
   stranded on the default layer, was a layer created and left empty, and (only when the
   request was about tagging) does everything have an `RC:ElementType`.
2. With the semantic layer on, `check_massing_composition`'s facts go in too — envelope
   proportions, symmetry, the mass hierarchy, the boolean composition, vertical rhythm. The
   reviewer is being asked whether the massing works, and a screenshot alone makes that a guess.
3. The affected region is photographed from several angles — `Claude:Review` first if you
   stamped one with `ClaudeAddReviewView`, then iso and plan framed on the session's geometry.
4. All of it goes to a **separate, tool-less call on Opus 5**, constrained by JSON schema to
   `ship` / `iterate` / `ask_user`.

The verdict *is* `signal_done`'s return value, so an `iterate` lands in the agent's context as
a tool result it can act on, and the loop carries on. `ask_user` ends the turn and puts the
question in the sidebar with an answer box — answering is just the next turn, so context is kept.

Guardrails: at most 2 iterate cycles per turn (a third becomes `ask_user`), and a defensive
review every 10 iterations for a loop that never signals done. The reviewer's cost is billed
against the same per-turn budget but priced on its own model.

**Review never blocks work.** Every failure path — reviewer errored, timed out, returned
nonsense — reports `unavailable` and the turn ends normally. The geometry is already in the
document; a broken second opinion must not strand it.

## Testing

**`TESTING.md` is the smoke-test script — run it first.** It walks the whole surface in the
order that finds problems fastest: one turn that exercises streaming, the tool loop and
UI-thread marshalling together, then undo/revert, then vision, the script hatch, self-review,
persistence and the guardrails. It also separates expected-and-harmless failures from real ones.

Unit tests cover what can be tested without Rhino — the SSE parser, delta assembly, content-block
round-trips, model-capability request shaping, cost accounting, undo-scope correctness against a
fake recorder, review parsing, compaction, persistence, and the conversation invariants the API
enforces.

The semantic layer is deliberately structured so most of it falls on the testable side of that
line. Everything under `RhinoClaude/Semantic/` is free of `using Rhino…` and operates on view
models rather than Breps, so the classifier's resolution rule, face and edge labelling, opening
subtype inference, selector resolution, the `find_element` parser, the narrative, and every
analytical read tool — wall-window ratio, roof form, composition, zoning — all have tests.

**Everything that touches RhinoCommon is unverified**: the Brep measurement that feeds those view
models, geometry results, camera control, and undo behaviour all need a live session before they
should be trusted.

## What's left

Nothing from `AGENT_REFACTOR_PLAN.md` §9 or `SEMANTIC_LAYER_PLAN.md` §9 — phases 0–10 and A–I are
all implemented. What remains is verification inside Rhino, and then whatever `script_log.jsonl`
and `classifier_timing.jsonl` say: which scripts deserve promotion from the Tier 2 escape hatch,
and whether the classifier's cache tiers stay inside their budgets on a real project file.

## Contributing

The most useful thing anyone can contribute right now is **verification inside a real Rhino
session**. Everything below `RhinoCommon` is compile-only here, so the geometry results, camera
control and undo behaviour have never been exercised against a live document — see the caveat at
the top and the script in [`TESTING.md`](TESTING.md). Open issues are tagged
`help wanted` and `good first issue` where a new contributor can realistically start.

Branches are kept unsquashed so the reasoning is readable: `main` is the integrated state, and
each `feature/*` and `fix/*` branch is the work that went into it.

## License

MIT — see [`LICENSE`](LICENSE). Use and modify freely.

## About

RhinoClaude is a [Lineweight](https://github.com/Lineweight-io) project — tooling for architects
who work in Rhino. Other Lineweight work lives under the same org.
