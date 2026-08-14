# RhinoClaude — Claude AI Plugin for Rhinoceros 3D

An agent that works inside a live Rhino document. You describe what you want in a docked chat
sidebar; Claude inspects the model, creates and edits geometry through a curated tool set,
looks at what it built, and reports back — all inside a single undo group you can revert with
one click.

> **Status:** the streaming tool-use loop, the sidebar, the full 38-tool Tier 1 set, multi-shot
> view capture, the Roslyn C# escape hatch and self-review are all in. `run_rhino_command` and
> conversation persistence come next. See `AGENT_REFACTOR_PLAN.md`.

## Features

- **Docked chat sidebar** (`ClaudeChat`) — the primary interface. Streamed responses, inline
  tool-call cards with input/result JSON, screenshot thumbnails, a live cost meter and
  iteration counter, session dropdown, and a settings gear.
- **Streaming tool-use loop** — SSE from the first request. Claude plans, calls tools, reads
  the results, and iterates until it signals done or hits a guardrail. Summarized reasoning
  streams into a collapsed card above each answer.
- **Guardrails** — $0.50 per turn and 25 iterations by default, both configurable. The loop
  stops before the next model call rather than mid-mutation.
- **One-click revert** — every mutation opens its own Rhino undo record; "Revert session"
  (or `ClaudeRevertSession`) pops all of them.
- **Vision** — `capture_views` is a 3D camera controller, not a screenshot button: named
  views, explicit camera poses, orthographic/isometric presets, and orbit-from-current, up
  to 6 shots returned from one call.
- **C# escape hatch** — `run_rhinocommon_script` runs a snippet with full RhinoCommon access
  inside an isolated undo record, with a timeout, a static-analysis blocklist, and a JSONL log.
- **Semantic tagging** — the existing `RC:` tag system, the Tag Inspector panel, and the
  deterministic `RC*` commands are unchanged.

### Tier 1 tools (38)

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
| Meta | `set_object_tags`, `signal_done` |

`delete_objects` is an addition to the plan's inventory — without it an agent that mis-creates
geometry cannot clean up after itself.

Selection and viewport tools live in `RhinoInteractionService`, deliberately apart from the
mutation service: Rhino does not put selection or camera changes on the undo stack, so wrapping
them in undo records would inflate the count that "Revert session" pops and undo real geometry
edits instead.

## Requirements

- **Rhino 7** (Windows, .NET Framework 4.8) and/or **Rhino 8** (Windows, .NET 7)
- **.NET SDK 7 or later** (for building)
- **Anthropic API key** — get one at [console.anthropic.com](https://console.anthropic.com)

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
│   │   └── AgentHost.cs             #   per-document object graph
│   ├── Services/Agent/              # everything that touches RhinoCommon
│   │   ├── RhinoQueryService.cs     #   read-only document access
│   │   ├── RhinoMutationService.cs  #   writes, each in its own undo record
│   │   ├── ViewCaptureService.cs    #   3D camera controller + multi-shot capture
│   │   ├── ScriptExecutorService.cs #   Roslyn C# escape hatch
│   │   ├── SessionSnapshotService.cs#   session undo log + revert
│   │   ├── RhinoInteractionService.cs # selection + viewport (no undo record)
│   │   └── SelfReviewService.cs     #   deterministic checks + reviewer call
│   ├── Tools/                       # tool schemas + handler wiring
│   │   ├── Phase1Tools.cs           #   query, create, transform, capture, script, done
│   │   └── Tier1Tools.cs            #   the rest of the plan §3 inventory
│   ├── UI/AgentChatPanel.cs         # the sidebar
│   ├── UI/AgentSettingsDialog.cs
│   ├── UI/TagInspectorPanel.cs      # unchanged
│   ├── Commands/                    # ClaudeChat, ClaudeSetKey, ClaudeTag,
│   │                                # ClaudeRevertSession, ClaudeAddReviewView,
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

### Other commands

| Command | What it does |
|---|---|
| `ClaudeChat` | Open/focus the sidebar |
| `ClaudeSetKey` | Store the API key |
| `ClaudeTag` | Describe a selection in prose → structured `RC:` tags (still one-shot) |
| `ClaudeRevertSession` | Same as the sidebar's revert button |
| `ClaudeAddReviewView` | Stamp the current camera as `Claude:Review` for self-review to judge from |
| `RCSetTag`, `RCQuery`, `RCInspectTags`, `RCValidateTags`, `RCTagInspector` | Deterministic tag operations |
| `RCBuildFromDiagram` | The algorithmic ADA restroom builder (no AI) |

## Logs

Both are append-only JSONL under `%APPDATA%\RhinoClaude\`:

- `script_log.jsonl` — every `run_rhinocommon_script` call: purpose, code, outcome, duration,
  object deltas. This is the data source for deciding which scripts deserve promotion to a
  curated Tier 1 tool.
- `capture_log.jsonl` — every `capture_views` call: shot count and kinds, size, session and
  iteration. Instrumentation for the screenshot-cadence question in the plan.

Screenshots are also cached to `%TEMP%\RhinoClaude\screenshots\<sessionId>\`.

## Settings

Behind the gear in the sidebar header:

| Setting | Default |
|---|---|
| Loop model | `claude-sonnet-5` |
| Effort | `high` |
| Show summarized reasoning | on |
| Self-review | on |
| Reviewer model | `claude-opus-5` |
| Max review cycles per turn | 2 |
| Cost budget per turn | $0.50 |
| Max iterations per turn | 25 |
| Max tokens per response | 32000 |
| Script tool enabled | yes |
| Script timeout | 15s (max 60s) |

Model, budget, iteration, token, effort and reasoning-display changes apply to the next turn.
Toggling the script tool changes the tool set, which is fixed for a session's lifetime — that
one takes effect on the next **⟲ New** session.

**Effort** controls how much the model thinks and how hard it works. `high` is the API default;
`xhigh` suits the hardest agentic work, `medium` is the cost-saving step down. It is greyed out
on models that have no effort parameter.

**Max tokens** caps thinking *plus* the response together on models that think by default
(Sonnet 5, Opus 5), which is why the default is 32000 rather than a value tuned for a
non-thinking model. Requests always stream, so there is no timeout reason to keep it small.

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
2. The affected region is photographed from several angles — `Claude:Review` first if you
   stamped one with `ClaudeAddReviewView`, then iso and plan framed on the session's geometry.
3. Both go to a **separate, tool-less call on Opus 5**, constrained by JSON schema to
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

Everything Rhino-facing is unverified — see `TESTING.md` for the smoke-test script.

## Not built yet

Nothing from `AGENT_REFACTOR_PLAN.md` §9 — phases 0 through 10 are all implemented. What
remains is verification inside Rhino, and whatever the script log suggests promoting to Tier 1.

**Nothing in the Rhino-facing layer has been exercised inside Rhino yet** — it compiles against
RhinoCommon, and the protocol layer is unit-tested, but RhinoCommon is a compile-only reference
so no test here touches a real document. Geometry results, camera control and undo behaviour all
need a session in Rhino before they should be trusted.

## License

MIT — use and modify freely.
