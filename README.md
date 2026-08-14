# RhinoClaude — Claude AI Plugin for Rhinoceros 3D

An agent that works inside a live Rhino document. You describe what you want in a docked chat
sidebar; Claude inspects the model, creates and edits geometry through a curated tool set,
looks at what it built, and reports back — all inside a single undo group you can revert with
one click.

> **Status:** phase 1 of the agent refactor (see `AGENT_REFACTOR_PLAN.md`). The streaming
> tool-use loop, the sidebar, a starter Tier 1 tool set, multi-shot view capture and the
> Roslyn C# escape hatch are in. Self-review and the remaining Tier 1 tools come in later phases.

## Features

- **Docked chat sidebar** (`ClaudeChat`) — the primary interface. Streamed responses, inline
  tool-call cards with input/result JSON, screenshot thumbnails, a live cost meter and
  iteration counter, session dropdown, and a settings gear.
- **Streaming tool-use loop** — SSE from the first request. Claude plans, calls tools, reads
  the results, and iterates until it signals done or hits a guardrail.
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

### Tools registered in phase 1

| Group | Tools |
|---|---|
| Query | `describe_document`, `list_layers`, `list_objects`, `get_object`, `get_selection` |
| Layer | `ensure_layer`, `assign_objects_to_layer` |
| Create | `create_box`, `create_line_curve` |
| Modify | `translate_objects`, `delete_objects` |
| View | `capture_views` |
| Tier 2 | `run_rhinocommon_script` |
| Meta | `signal_done` |

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
│   │   └── AgentHost.cs             #   per-document object graph
│   ├── Services/Agent/              # everything that touches RhinoCommon
│   │   ├── RhinoQueryService.cs     #   read-only document access
│   │   ├── RhinoMutationService.cs  #   writes, each in its own undo record
│   │   ├── ViewCaptureService.cs    #   3D camera controller + multi-shot capture
│   │   ├── ScriptExecutorService.cs #   Roslyn C# escape hatch
│   │   └── SessionSnapshotService.cs#   session undo log + revert
│   ├── Tools/Phase1Tools.cs         # schemas + handler wiring
│   ├── UI/AgentChatPanel.cs         # the sidebar
│   ├── UI/AgentSettingsDialog.cs
│   ├── UI/TagInspectorPanel.cs      # unchanged
│   ├── Commands/                    # ClaudeChat, ClaudeSetKey, ClaudeTag,
│   │                                # ClaudeRevertSession, RC* tag commands,
│   │                                # RCBuildFromDiagram
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
| Loop model | `claude-sonnet-4-5-20250929` |
| Cost budget per turn | $0.50 |
| Max iterations per turn | 25 |
| Max tokens per response | 16384 |
| Script tool enabled | yes |
| Script timeout | 15s (max 60s) |

Model, budget, iteration and token changes apply to the next turn. Toggling the script tool
changes the tool set, which is fixed for a session's lifetime — that one takes effect on the
next **⟲ New** session.

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

## Not in this phase

Self-review (`SelfReviewService`, `ClaudeAddReviewView`), the remaining Tier 1 tools
(booleans, `move_face`/`move_edge`, `scale_1d`, blocks, materials, arcs and circles),
`run_rhino_command`, and per-document conversation persistence. See `AGENT_REFACTOR_PLAN.md`
§9 for the phase order.

## License

MIT — use and modify freely.
