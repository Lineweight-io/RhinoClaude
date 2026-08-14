# RhinoClaude — Agent Refactor Plan

**Status:** Revision 2 — awaiting Bryan's greenlight.
**Author:** Claude (Cowork)
**Date:** 2026-08-13

---

## 0. What changed in Revision 2 (skim this first)

Bryan's feedback on Rev 1 turned into these material changes. Everything else in the plan is unchanged from Rev 1.

| # | Change | Where it lands |
|---|---|---|
| 1 | **Streaming is in Phase 1.** SSE from day one — same reasoning as the doc creator: perceived latency and mid-loop cancel matter too much to defer. | §2.1, §2.5, §7 |
| 2 | **`ClaudeAsk` is deleted.** No one-shot fallback command. The chat sidebar is the interface. | §2.5, §7 |
| 3 | **Primary UX is a dockable chat sidebar, not Rhino commands.** Modeled on the Lineweight document creator / Coworker chat panel. Streamed responses, live tool-call log, cost meter, session controls, `ask_user` inline. Rhino command surface becomes secondary/removed. Added a real UX section. | New §7, §2.5, §7 phase renumbering |
| 4 | **Roslyn C# only for the Tier 2 escape hatch. IronPython is dropped.** RhinoCommon is the one language surface. Roslyn moves *into* Phase 1 (was deferred). | §4 (rewritten), §7 |
| 5 | **Screenshot pipeline is a 3D camera controller with multi-shot.** Not "grab the current viewport." The agent supplies camera params or asks for named angle bundles (plan / N-elev / iso) and gets multiple images back in one call. | §6 (rewritten), §3.9 |
| 6 | **Three Tier 1 tools added:** `scale_1d`, `move_face`, `move_edge`. Total is now **34 tools**. | §3.3, §3.4 |
| 7 | **`RoomSkills` is deleted from the codebase** (was previously "unused, decide later"). | §1, §7 phase 9 |
| 8 | **`ClaudeAddReviewView` is in.** Small command that stamps the current camera as a named "Review" view the reviewer prefers. | §5.3, §7 phase 6 |
| 9 | All nine open questions from Rev 1 are now decisions in §8. | §8 |

Cost/scope impact: the sidebar UI (§7 Phase 8 in Rev 1) grows from 3 days to ~5 days now that it's the primary surface, streaming is +1 day added to Phase 1, Roslyn is +1 day pulled forward. Rev 1 estimate was ~19 ideal days; **Rev 2 is ~22 ideal days**.

---

## 1. Current-state audit (what actually exists today)

Grounded in the code as of this session — not assumptions.

### Bootstrap
`RhinoClaudePlugin` singleton owns two shared services: `ClaudeApiService` and `TagService`. On load it reads the Anthropic key from plugin settings (fallback to `ANTHROPIC_API_KEY` env var) and registers the docked `TagInspectorPanel`. Multi-targets `net48` (Rhino 7) and `net7.0` (Rhino 8) via `RhinoCommon 7.38` / `8.0`. Post-build copies the DLL to `.rhp`. Plugin GUID: `A1B2C3D4-E5F6-7890-ABCD-EF1234567890`.

### Commands
Claude-facing:

- **`ClaudeAsk`** — free-form Q&A with a per-command 40-message history. Collects a prompt, calls `SceneContextCollector.CollectContext(doc)`, sends one Messages API request, extracts a fenced Python block if present, prompts `Run/Cancel`, runs via `ScriptRunner.RunWithRetry`.
- **`ClaudeRunScript`** — same flow, but the prompt is a scripted "produce only a Python block" wrapper and there is no persistent history.
- **`ClaudeSetKey`** — stores the API key in plugin settings.
- **`ClaudeTag`** — takes a description of the current selection, sends `TagSchema.GetSchemaDescription()` in the prompt, expects a JSON `{RC:key: value}` map back, applies via `TagService.SetTags`. This is the one place the one-shot pattern actually holds up, because the output is small and structured.

Deterministic (not part of the refactor):

- **`RCSetTag`, `RCQuery`, `RCInspectTags`, `RCTagInspector`, `RCValidateTags`** — direct tag operations, no API calls.
- **`RCBuildFromDiagram`** — 2,170-line algorithmic single-user ADA restroom builder. No AI. Reads a labeled closed curve, picks door corner and toilet type, computes ADA clearances, imports fixture blocks, applies `RC:` tags. Stays as-is.

### Services
- **`ClaudeApiService`** — direct `HttpClient` to `POST https://api.anthropic.com/v1/messages`, model hard-coded `claude-sonnet-4-20250514`, `max_tokens = 16384`, `anthropic-version: 2023-06-01`. **No tool use.** No streaming. Errors caught and returned as strings; callers never see an exception. System prompt is a single hard-coded string telling Claude to emit IronPython 2.7 with mandatory imports, plus `TagSchema.GetSchemaDescription()`.
- **`SceneContextCollector`** — builds a plain-text (not JSON) scene summary: doc name, units, tolerance, per-layer object counts, object-type rollup, `RC:` tag rollup by ElementType/FireRating/Level, per-selected-object one-liner (bbox, curve length, brep face count, etc., capped at 20). This is inlined into the user message between `[SCENE CONTEXT]` markers.
- **`ScriptRunner`** — `PythonScript.Create()` (Rhino's IronPython), sets `ScriptContextDoc`, captures `python.Output`. `RunWithRetry` retries up to 3 times: on failure, sends a fix prompt (original task + failed script + error) back to Claude, extracts the corrected code, re-executes. **Slated for deletion in Rev 2** — IronPython is out.
- **`TagService`** — CRUD over `RhinoObject.Attributes` user text under the `RC:` prefix, plus `AuditDocument`.

### Schema
`TagSchema` defines 10 canonical `RC:` keys with a mix of constrained enums (ElementType, FireRating, IntExt, SystemType) and freeform text, plus `NormalizeValue` and `ResolveKey` (fuzzy key matching). `BuildingStandards` holds ADA dimensional constants + a `RoomLabelAliases` dictionary + a `FixtureFiles` dictionary. `RoomSkills.GetRestroomSkill()` is a ~300-line prompt-ready recipe — **currently uncalled by any command; slated for deletion in Rev 2.**

### UI
`TagInspectorPanel` (Eto.Forms, docked) — reads the current selection, renders one row per `TagSchema` key with a DropDown for constrained values and a TextBox for freeform, coalesces rapid selection events via `AsyncInvoke`, handles multi-select with `(mixed)` placeholders. Uses `TagService` directly. Untouched by the refactor.

### Request flow today (concrete)
`ClaudeAsk` → `Dialogs.ShowEditBox` → `SceneContextCollector.CollectContext` → `ClaudeApiService.SendMessageAsync` (single POST with `[SCENE CONTEXT]...[END SCENE CONTEXT]\n\n<user>`) → response comes back as one text blob → `ScriptRunner.ExtractPythonCode` regex-strips a fenced block → user confirms `Run` → `PythonScript.ExecuteScript` → on failure, up to 3 fix-and-retry roundtrips. Escape key cancels via `CancellationTokenSource`.

**The one-shot pattern is visible in every step.** Claude never queries the doc after its first read of the scene text, never sees the result of the script it just ran, never asks a follow-up. The retry loop only fires on Python exceptions — a script that runs cleanly but produces the wrong geometry gets no correction pass.

### Assets in root
`Initial Results.3dm`, `Restroom Test.3dm`, `Tagging_Tests.3dm` — test fixtures. `RhinoClaude_Roadmap.docx` — separate roadmap doc, not read. `API_Key.txt` — present at repo root (should be gitignored if we introduce git).

---

## 2. Target architecture

### 2.1 The loop

Replace the one-shot `SendMessageAsync` with an `AgentSession` that runs the Anthropic tool-use loop **with SSE streaming from day one**:

```
AgentSession.RunTurnAsync(userMessage, cancellationToken):
    messages.Add(user: userMessage)
    while iterations < MaxIterations and !budget.Exceeded:
        stream = api.MessagesStream(model, system, tools, messages)
        assistantContent = []
        await foreach event in stream:
            switch event:
              case ContentBlockStart:  panel.BeginBlock(event.block)
              case ContentBlockDelta:  panel.AppendDelta(event.delta)   // text chunk or tool_use partial JSON
              case ContentBlockStop:   panel.CloseBlock()
              case MessageDelta:       budget.Add(event.usage)
              case MessageStop:        break
        messages.Add(assistant: assistantContent)
        if stop_reason == "end_turn":  return
        toolResults = []
        foreach tool_use in assistantContent.OfType<ToolUse>():
            panel.ShowToolInvocation(tool_use)
            result = await ToolDispatcher.InvokeAsync(tool_use.name, tool_use.input, ct)
            panel.ShowToolResult(tool_use, result)
            toolResults.Add(tool_result: { tool_use_id, content: result.SerializeForClaude() })
        messages.Add(user: toolResults)
        iterations++
    // budget or iteration cap hit — see safety
```

Key properties:

- **Native tool use with streaming.** `stream: true` on every request. Consumes `content_block_delta` / `input_json_delta` events. The chat sidebar renders text as it arrives; tool-use blocks render as pending cards that resolve to results when the tool returns.
- **State machine:** `Idle → Streaming → DispatchingTools → Streaming → …→ Done | Cancelled | BudgetExceeded | Errored`. Reflected in the panel status bar.
- **Cost budget.** `MaxIterations = 25`, `MaxCostUsd = 0.50` per turn (Bryan noted this is a starting point and may grow with usage). Token counts arrive live via streaming `message_delta.usage`. When either cap hits, the loop sends one final turn asking the model to wrap up.
- **Cancel:** Escape key or the panel's Stop button. Cancels the HTTP read, halts the streaming reader, breaks the loop between tool calls. Any in-flight tool completes if it's already dispatched; nothing new fires.
- **Vision:** tool results may include image blocks (`type: "image"`, base64 PNG) — the API accepts them as `tool_result.content` array entries. `capture_views` returns these (multi-shot, see §6); other tools return text/JSON.

### 2.2 Services

New services under `RhinoClaude/Services/Agent/`:

| Service | Owns | Threading |
|---|---|---|
| `AgentSession` | The loop state machine, message list, iteration counter, cost budget, cancellation token, session id. Streams to the panel via `IAgentSessionObserver`. | Runs on a background task; marshals every RhinoCommon call to the main thread via `RhinoApp.InvokeOnUiThread`. |
| `AnthropicClient` | HTTP + SSE streaming layer. One `HttpClient`, retry on 429/5xx with exponential backoff, `text/event-stream` reader that yields typed events. Handles `anthropic-version`. Typed request/response models with content-block polymorphism (`text`, `tool_use`, `tool_result`, `image`). | Async, no thread affinity. |
| `ToolRegistry` | Static list of all tool definitions: name, description, JSON Schema for input, C# delegate for invocation. Serves both the `tools` array sent to Claude and the dispatcher. | None. |
| `ToolDispatcher` | Given a `tool_use` block, resolve to a delegate, validate input against schema, invoke on the UI thread, catch exceptions and return them as structured error results. | Marshals to UI thread. |
| `RhinoQueryService` | All read tools' implementations — no `doc.Undo` involvement. | UI-thread only. |
| `RhinoMutationService` | All write tools' implementations. Every public method opens an undo record, applies, closes; returns structured result. Every method also emits a `SessionMutation` entry. | UI-thread only. |
| `ScriptExecutorService` | Tier 2 escape hatch — **Roslyn C# only.** Runs a snippet in an isolated undo record, captures stdout/stderr, catches exceptions, serializes a bounded result. | UI-thread only. |
| `ViewCaptureService` | **3D camera controller.** Multi-shot capture from arbitrary camera params or named angle bundles (plan / elevations / iso). Base64 PNG encoding, size cap. | UI-thread only (`ViewCaptureToBitmap`). |
| `SelfReviewService` | Runs deterministic checks + multi-shot view capture, composes reviewer prompt, calls a *separate* short Anthropic call (no tools) with image blocks. Returns `ship / iterate / ask_user` verdict + notes. | Async; deterministic checks run on UI thread. |
| `SessionSnapshotService` | Records every mutation's undo record id at the start of the session, exposes `RevertSession()` which pops undo records back to the pre-session mark. | UI-thread only for the revert; recording is thread-safe. |
| `AgentConversationStore` | Per-document conversation persistence. Serializes the message list to `RhinoDoc.Strings` (document user text) under key `RhinoClaude:AgentConversation:<sessionId>`. | Any thread; final write on UI thread. |

`ClaudeApiService` is renamed `AnthropicClient` and rebuilt from scratch — streaming + tool use are core, not bolt-ons. `SceneContextCollector` is retained as a helper but is no longer the primary context payload — the agent calls `describe_scene` as a tool when it wants it.

### 2.3 Threading

RhinoCommon is UI-thread only. The rule for the refactor:

- The loop and the HTTP stream reader run on a background `Task`.
- Every tool implementation is wrapped by `ToolDispatcher.InvokeOnUiThread(...)` which internally does `RhinoApp.InvokeOnUiThread` and awaits a `TaskCompletionSource`.
- The sidebar is Eto — all UI updates from the streaming reader go through `Application.Instance.AsyncInvoke`.
- Cancellation: `CancellationTokenSource` linked to Escape and the Stop button; the loop checks between iterations, the stream reader honors the token, tool dispatcher honors it before each invocation.

### 2.4 Session state and lifecycle

- Each Rhino document gets one `AgentSession` (lazily created, keyed on `RhinoDoc.RuntimeSerialNumber`).
- The session survives across multiple user turns — a follow-up like "now do the same on the north wall" reuses the message list.
- Session state is persisted to document user text on save so it survives close/reopen. On load, if a session is present, the panel offers "resume", "clear", or "revert entire prior session". Default action: prompt with "New session" pre-selected (Bryan's call).
- Sessions have an id (GUID) and a display name (first user turn, truncated). The panel shows the current session and a history dropdown.
- After each `signal_done: ship`, the history is compacted — tool-use/tool-result pairs older than the last 3 turns are replaced with a short summary line to control token growth.

### 2.5 Command surface (post-refactor)

Radically smaller than Rev 1:

- **`ClaudeChat`** — opens/focuses the sidebar. Aliased so users who type it get the panel.
- **`ClaudeSetKey`** — retained (unchanged).
- **`ClaudeTag`** — retained. Still one-shot; the pattern works for it. Rewritten to use `AnthropicClient` directly (no loop, no tools). Kept as a command because it's a tight, selection-driven flow that doesn't benefit from the sidebar.
- **`ClaudeRevertSession`** — pops undo records back to the session start.
- **`ClaudeAddReviewView`** — stamps the current camera as a named `Claude:Review` view that `SelfReviewService` prefers when it exists.
- **`RCSetTag`, `RCQuery`, `RCInspectTags`, `RCTagInspector`, `RCValidateTags`, `RCBuildFromDiagram`** — untouched.

**Removed:** `ClaudeAsk`, `ClaudeRunScript` (its role is now "the chat sidebar with a `run_rhinocommon_script` tool call"). `ClaudeShowLastScreenshot` from Rev 1 is folded into the sidebar's tool-call log — every screenshot appears inline.

---

## 3. Tier 1 tool inventory

**Design contract for every tool:**

- **Name:** `snake_case`, verb-first for actions, noun-first for pure reads.
- **Input:** typed JSON Schema with descriptions. Coordinates always as `[x, y, z]` arrays; GUIDs as string. All lengths in the document's model units (agent gets units from `describe_document`).
- **Output:** always an object with `success: bool`, `error: string|null`, and tool-specific fields. Never bare booleans, never bare GUIDs.
- **Undo:** every mutation opens `doc.Undo.BeginRecord("agent:<tool_name>")` and closes it. On exception, `try/finally` guarantees `EndRecord`.
- **Idempotency:** where reasonable (layer create, material create), return the existing entity's id if a matching one already exists rather than creating a duplicate.

Count: **34 tools.** Grouped:

### 3.1 Query (7)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `describe_document` | High-level doc info the agent needs on first tool call. | `{}` | `{name, path, units, tolerance, angleTolerance, layerCount, objectCount, selectionCount, activeViewName, activeViewCameraLocation, activeViewCameraTarget}` |
| `list_layers` | Enumerate layers. | `{includeHidden?: bool = true}` | `{layers: [{id, fullPath, visible, locked, objectCount, colorHex}]}` |
| `list_objects` | Enumerate objects with filters. | `{layerFullPath?: string, objectType?: string, hasTagKey?: string, hasTagValue?: {key, value}, boundingBoxOverlaps?: [[x,y,z],[x,y,z]], limit?: int = 200}` | `{objects: [{id, type, layer, name, bbox, tags: {...}}], truncated: bool, totalMatched: int}` |
| `get_object` | Full detail for one object — including per-face and per-edge indexing for `move_face`/`move_edge`. | `{id: string, includeSubobjects?: bool = false}` | `{id, type, layer, name, bbox, tags, geometrySummary: {…}, faces?: [{index, area, centroid, normal, isPlanar}], edges?: [{index, length, startPoint, endPoint, isLinear}]}` |
| `get_selection` | Currently selected object ids. | `{}` | `{ids: [...], count}` |
| `list_named_views` | Named views (including any `Claude:Review`). | `{}` | `{views: [{name, cameraLocation, cameraTarget}]}` |
| `list_blocks` | Block definitions. | `{}` | `{blocks: [{name, id, instanceCount}]}` |

### 3.2 Create-Geometry (6)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `create_point` | Add a point. | `{location, layer?, name?}` | `{id, bbox}` |
| `create_line_curve` | Line/polyline. | `{points: [[x,y,z],...], layer?, name?}` | `{id, length, bbox}` |
| `create_arc_curve` | Arc from 3 points or center/radius/plane/angle. | `{mode: "threePoint"|"centerRadius", ...}` | `{id, length, bbox}` |
| `create_circle` | Circle. | `{center, radius, plane?, layer?, name?}` | `{id, bbox}` |
| `create_rectangle` | Planar rectangle as closed curve. | `{plane?, corner, width, depth, layer?, name?}` | `{id, bbox}` |
| `create_box` | Axis-aligned or planar box (Brep). | `{corner1, corner2, plane?, layer?, name?}` | `{id, bbox, volume}` |

Sphere, cylinder, cone, torus, ellipse deliberately excluded from Tier 1 — they belong in the script escape hatch until the log shows the agent reaching for them repeatedly.

### 3.3 Transform (5)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `translate_objects` | Move by vector. | `{ids: [...], vector: [x,y,z], copy?: bool = false}` | `{updatedIds: [...], newBboxes: {id: bbox}}` |
| `rotate_objects` | Rotate around axis. | `{ids, center, axis, angleDegrees, copy?: bool = false}` | `{updatedIds, newBboxes}` |
| `scale_objects` | Uniform or per-axis scale. | `{ids, center, factor: number|[x,y,z], copy?: bool = false}` | `{updatedIds, newBboxes}` |
| **`scale_1d`** | Scale in one direction defined by two reference points to a target length — the RhinoCommon equivalent of `Scale1D`. Bryan's most-used scaling operator. | `{ids, basePoint, referencePoint, targetLength, copy?: bool = false}` | `{updatedIds, newBboxes, computedScaleFactor}` |
| `mirror_objects` | Mirror across plane. | `{ids, planeOrigin, planeNormal, copy?: bool = false}` | `{updatedIds, newBboxes}` |

### 3.4 Boolean / Modify (7)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `boolean_union` | Union of Breps. | `{ids: [...], deleteInputs?: bool = true}` | `{resultIds: [...], notes: string}` |
| `boolean_difference` | Difference. | `{minuendIds, subtrahendIds, deleteInputs?: bool = true}` | `{resultIds, notes}` |
| `boolean_intersection` | Intersection. | `{ids, deleteInputs?: bool = true}` | `{resultIds, notes}` |
| `offset_curve` | Offset a curve. | `{id, distance, direction?, closed?: bool, plane?}` | `{resultIds, notes}` |
| `extrude_curve` | Extrude curve to Brep. | `{id, direction: [x,y,z], distance, cap?: bool = true, deleteInput?: bool = false}` | `{resultId, bbox, volume}` |
| **`move_face`** | Push/pull a Brep face along a direction. Operates on existing geometry — the modify-what's-there complement to primitive creation. Face indices come from `get_object` with `includeSubobjects: true`. | `{brepId, faceIndex, direction: [x,y,z], distance}` | `{resultId, newBbox, notes}` |
| **`move_edge`** | Push/pull a Brep edge along a direction. Same pattern as `move_face`. | `{brepId, edgeIndex, direction: [x,y,z], distance}` | `{resultId, newBbox, notes}` |

`notes` carries messages like "one input was open, extrusion is a surface not a solid" so the agent can react without guessing.

### 3.5 Layer (2)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `ensure_layer` | Idempotent layer creation with optional parent path and color. | `{fullPath, colorHex?, parentPath?}` | `{id, created: bool, fullPath}` |
| `assign_objects_to_layer` | Move objects to a layer. | `{ids, layerFullPath}` | `{updatedIds}` |

### 3.6 Block (2)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `insert_block` | Insert an existing block instance. | `{blockName, location, rotationDegrees?, scale?, layer?}` | `{id, bbox}` |
| `import_3dm_as_block` | Import a `.3dm` file as a block definition (used by fixture library). | `{path, blockName?}` | `{blockName, definitionId}` |

### 3.7 Material (1)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `assign_material` | Assign a material by name to objects (create render material by name if missing). | `{ids, materialName, diffuseHex?, transparency?}` | `{updatedIds, materialIndex}` |

### 3.8 Selection (2)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `select_objects` | Set selection. | `{ids, replace?: bool = true}` | `{selectedCount}` |
| `deselect_all` | Clear selection. | `{}` | `{ok: true}` |

### 3.9 View (2)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `capture_views` | **Multi-shot 3D capture.** Agent supplies one or more camera specs and gets back the same number of images in one call. See §6 for the full camera control surface. | `{shots: [CameraShot], width?: int = 1280, height?: int = 800, displayMode?: "wireframe"|"shaded"|"rendered" = "shaded", showGrid?: bool = false}` | `{images: [{shot, base64, actualWidth, actualHeight, bytes}]}` |
| `zoom_extents` | Reset current view or a named view. | `{viewName?: string, ids?: [...]}` | `{ok, resultCameraLocation, resultCameraTarget}` |

`CameraShot` union: `{namedView: string}` | `{cameraLocation, cameraTarget, cameraUp?}` | `{preset: "plan"|"front"|"back"|"left"|"right"|"iso_ne"|"iso_nw"|"iso_se"|"iso_sw", framing: "extents"|{ids: [...]}}` | `{orbitFromCurrent: {yawDegrees, pitchDegrees, framing?}}`.

### 3.10 Meta (2)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `set_object_tags` | Write `RC:` tags via `TagService`. | `{ids, tags: {key: value, ...}}` | `{updatedIds, errors: [...]}` |
| `signal_done` | Explicit signal from the agent that it believes the task is complete. Triggers self-review. | `{summary: string, expectedOutcome: string}` | `{reviewVerdict: "ship"|"iterate"|"ask_user", notes: string}` |

**Total: 34 Tier 1 tools.** Room to grow as script-log data justifies promotion.

---

## 4. Tier 2 escape hatch — `run_rhinocommon_script` (Roslyn C# only)

### 4.1 Schema

```json
{
  "name": "run_rhinocommon_script",
  "description": "Escape hatch for anything the curated tools don't cover. Runs a C# script with full RhinoCommon access inside an isolated undo record. Prefer curated tools when they exist — use this for one-offs, unusual geometry, or when a curated tool is missing a parameter you need. Every call is logged.",
  "input_schema": {
    "type": "object",
    "required": ["code", "purpose"],
    "properties": {
      "code": {"type": "string", "description": "C# script body. See context section for globals. No `using` block needed — common namespaces are pre-imported. Assign to `Result` to return a value."},
      "purpose": {"type": "string", "description": "One-sentence description of what this script is meant to do. Logged for later analysis."},
      "expectedResultShape": {"type": "string", "description": "Optional. What the script should set Result to, e.g. 'list of GUIDs of created objects'."},
      "timeoutSeconds": {"type": "integer", "default": 15, "maximum": 60}
    }
  }
}
```

### 4.2 Language: C# only

Bryan's call: RhinoCommon is the one language surface. IronPython is dropped from the plugin entirely — the current `ScriptRunner` and its retry loop are deleted, and `Rhino.Runtime.PythonScript` is never invoked from the new code.

Dependency: `Microsoft.CodeAnalysis.CSharp.Scripting` (~5 MB), added to both `net48` and `net7.0` targets. Roslyn scripting supports both.

### 4.3 Execution context

Globals type exposed to the script:

```csharp
public class ScriptGlobals
{
    public RhinoDoc Doc;                          // = RhinoDoc.ActiveDoc at script start
    public TagService Tags;                       // RhinoClaudePlugin.Instance.TagService
    public Action<string> Log;                    // append to captured stdout; do not use Console.WriteLine
    public object Result;                         // assign to return a value to the agent
    public CancellationToken Cancellation;        // honored by the script for cooperative cancel
}
```

Pre-imported namespaces (via `ScriptOptions.WithImports(...)`):

```
System, System.Collections.Generic, System.Linq, System.IO
Rhino, Rhino.Geometry, Rhino.DocObjects, Rhino.Commands
```

Referenced assemblies: `RhinoCommon`, `System.Core`, `System.Linq`, `mscorlib`, `System.Runtime`, `Rhino.UI`.

Typical script body from the agent's perspective:

```csharp
var box = new Box(Plane.WorldXY, new Interval(0, 10), new Interval(0, 20), new Interval(0, 5));
var id = Doc.Objects.AddBrep(box.ToBrep());
Log($"added box {id}");
Result = id;
```

### 4.4 Serialization back to Claude

After execution:

1. Capture stdout via `Log` delegate into a `StringBuilder`.
2. Read `Globals.Result`. Serialization rules:
   - `Guid`/`Guid[]`/`List<Guid>` → string(s).
   - `RhinoObject` or `GeometryBase` → `{id, type, bbox}` (add to doc if not already there).
   - `Dictionary`/`List`/primitive → JSON-serialize directly.
   - Anything else → `ToString()` with a `note: "opaque result serialized as string"`.
3. Enforce a **32 KB serialized result cap.** If exceeded, truncate and add `"truncated": true`.
4. Return:

```json
{
  "success": true|false,
  "stdout": "...",
  "stderr": "...",
  "compileErrors": [ { "line": 4, "message": "..." } ] | null,
  "result": <serialized>,
  "resultTruncated": bool,
  "executionMs": number,
  "createdObjectIds": ["guid", ...],
  "deletedObjectIds": ["guid", ...],
  "modifiedObjectIds": ["guid", ...]
}
```

Delta detection: snapshot `doc.Objects.GetObjectList(...)` guids before and after — cheap free-feedback for the agent.

### 4.5 Guardrails

- **Timeout:** the script runs on the UI thread inside an isolated undo record. Hard-cancel via cooperative `CancellationToken` (default 15s, max 60s). If the script won't cancel, mark it as timed out and let Escape handle it.
- **Isolated undo record:** `doc.Undo.BeginRecord($"agent:script:{purpose}")` / `EndRecord` in `try/finally`.
- **Disallowed operations:** static analysis pass on the syntax tree before execution rejects references to `System.IO.File.Delete`, `System.IO.Directory.Delete`, `System.Diagnostics.Process`, `Microsoft.Win32.Registry`, `System.Environment.Exit`, `System.Reflection.Assembly.Load`. Not a hard sandbox — it's a speed bump.
- **No network:** system prompt tells the agent scripts must not do network I/O. Roslyn scripting doesn't run under a CAS sandbox in modern .NET, so this is convention-only.
- **Log everything.** Every call: purpose, code, result summary, duration, agent session id → append-only JSONL at `%APPDATA%/RhinoClaude/script_log.jsonl`. This is the data source for deciding which scripts to promote to Tier 1.

### 4.6 Compile caching

Roslyn compilation has non-trivial cost. Cache compiled `Script<object>` instances keyed on a hash of `(code + imports + refs)`. In practice the agent will re-run the same or slightly modified scripts within a session; a warm cache turns second-run cost to microseconds.

### 4.7 Tier 3 — `run_rhino_command`

```json
{
  "name": "run_rhino_command",
  "description": "Last resort. Runs a Rhino command as if typed at the command line (e.g. '_-Render'). Only use when neither a curated tool nor a RhinoCommon script covers it — mostly for rendering, specific legacy commands, or plugin-provided commands with no API. Non-atomic and hard to undo cleanly.",
  "input_schema": {
    "type": "object",
    "required": ["commandLine", "purpose"],
    "properties": {
      "commandLine": {"type": "string", "description": "Scripted command line, e.g. '_-Render' with dash prefix and Enter behavior."},
      "purpose": {"type": "string"}
    }
  }
}
```

Implemented via `RhinoApp.RunScript(commandLine, echo: false)`, wrapped in an undo record. First use per session surfaces a confirmation banner in the sidebar (visible, non-blocking).

---

## 5. Self-review

### 5.1 Trigger

Runs when:

1. The agent calls `signal_done`, OR
2. `iterationsSinceLastReview >= 10` (defensive), OR
3. User clicks "Review now" in the sidebar.

### 5.2 Deterministic checks

Cheap, run every review, results feed the reviewer prompt:

- **Object count delta vs plan.** If the agent claimed "add 3 walls" but the session delta is 0 or 12, flag.
- **Bounding-box sanity.** Every created object's bbox size in expected units range.
- **Layer assignments.** No objects on the default layer if the agent used any `ensure_layer` calls this session.
- **No zero-length curves or degenerate breps.** `curve.GetLength() > tol`, `brep.IsValid`.
- **Tag coverage.** If the task involved tagging (heuristic: prompt contained "tag", "type", or a schema key), every created object has at least an `RC:ElementType`.
- **No orphaned layers created but unused.**
- **Doc validity.** `doc.IsValid`.

Each check returns `{name, passed, details}`. Composed into a "facts" block.

### 5.3 Screenshots for review

Always multi-shot. If a `Claude:Review` named view exists (created by `ClaudeAddReviewView`), the reviewer bundle includes that view *plus* auto-generated iso and plan of the session's affected bounding box. If no `Claude:Review` view exists, the bundle is: `plan`, `iso_ne`, `front` — all framed to the session's affected bounding box (union of all `newBboxes` from the session's mutations).

Downsample to 1280×800 each. Base64 PNG.

### 5.4 Reviewer prompt

**Separate short API call, no tools available.** Opus 5 (per Bryan's answer on model choice). System prompt:

> You are reviewing an autonomous agent's work in a Rhino 3D document. You will see the user's original request, the agent's summary, a list of deterministic check results, and 3+ screenshots showing the affected region from different angles. Decide: ship / iterate / ask_user.
>
> - **ship**: the work matches the user's intent and passes checks.
> - **iterate**: something is clearly wrong or missing that the agent should try to fix. Include specific notes.
> - **ask_user**: the request was ambiguous and you can't tell whether it's correct. Include the question to ask.

Content: text (original request + agent summary + check JSON) + N image blocks. Returns `{verdict, notes, questionsForUser?}`. Fed back to the agent as `signal_done`'s return value. If `iterate`, the agent goes another round. If `ask_user`, the loop suspends and the sidebar surfaces the question inline.

### 5.5 Decision tree

```
signal_done
  → run deterministic checks
  → capture multi-shot screenshots
  → reviewer call (Opus 5)
    ├─ ship        → present result to user in sidebar, close loop
    ├─ iterate     → append review notes as tool_result, resume loop
    └─ ask_user    → suspend, show question in sidebar, wait for user reply
```

Iterate cap: 2 self-review cycles per user turn. Beyond that, force `ask_user` regardless of verdict.

---

## 6. Screenshot / visual feedback pipeline — 3D camera controller

Rev 1 treated this as "screenshot the current viewport." Rev 2 treats it as **camera control with multi-shot capture** — the agent orbits the model to understand it.

### 6.1 Capture surface

`ViewCaptureService.CaptureAsync(request)` where `request.shots` is a list of `CameraShot` union values (see §3.9). Executed in the order given. Returns `List<CaptureResult>`.

`CameraShot` variants and semantics:

| Variant | Behavior |
|---|---|
| `{namedView: "Claude:Review"}` | Look up the named view, set camera + target from it. |
| `{cameraLocation, cameraTarget, cameraUp?}` | Explicit camera pose. `cameraUp` defaults to world Z. |
| `{preset: "plan", framing: "extents"\|{ids}}` | Top-down orthographic. Framing = zoom to doc extents or to a specific id set. |
| `{preset: "iso_ne"\|"iso_nw"\|"iso_se"\|"iso_sw", framing}` | 30°-elevation isometric from the named corner. |
| `{preset: "front"\|"back"\|"left"\|"right", framing}` | Orthographic elevation. |
| `{orbitFromCurrent: {yawDegrees, pitchDegrees, framing?}}` | Rotate the current camera around the framing centroid. Enables "give me the same view rotated 45° right." |

All shots share the request's `width`, `height`, `displayMode`, `showGrid`.

### 6.2 Multi-shot benefits

- **One tool call → three angles.** Reviewer and self-orientation both benefit — a plan + iso + front costs one round-trip, not three.
- **Consistent tokenization.** All shots share input scaffolding; only the image bytes differ.
- **Rendering hint:** the service can render all shots in a single off-screen viewport session (create → configure → capture → configure → capture → dispose) rather than churning viewport state per call.

### 6.3 Encoding + size cap

- PNG in memory. Hard max per image: 1600×1200 or 750 KB, whichever hits first.
- If a shot would exceed 750 KB PNG, fall back to JPEG q85 automatically.
- Total per-call cap: 6 shots per `capture_views` call. Beyond that, the tool returns an error asking the agent to split the request.

### 6.4 Delivery

Tool result becomes a mixed content array:

```json
{
  "type": "tool_result",
  "tool_use_id": "...",
  "content": [
    { "type": "text", "text": "{ \"success\": true, \"shots\": [ { \"index\": 0, \"width\": 1280, \"height\": 800, \"cameraLocation\": [...], \"cameraTarget\": [...] }, ... ] }" },
    { "type": "image", "source": { "type": "base64", "media_type": "image/png", "data": "..." } },
    { "type": "image", "source": { "type": "base64", "media_type": "image/png", "data": "..." } },
    { "type": "image", "source": { "type": "base64", "media_type": "image/png", "data": "..." } }
  ]
}
```

### 6.5 Cadence

**Model-driven, not fixed.** Bryan's call: the agent takes screenshots when it decides they'd help. No plugin-enforced auto-capture except in self-review.

Instrumentation: every `capture_views` invocation is logged with shot count, session id, iteration index, and the user prompt that started the turn. If we see the agent taking >5 captures per turn on average, we add a soft cap in the system prompt telling it to be sparing. Log target: same JSONL location as scripts, separate file `%APPDATA%/RhinoClaude/capture_log.jsonl`.

Every screenshot is also cached to `%TEMP%/RhinoClaude/screenshots/<sessionId>/<n>.png` and appears inline in the sidebar's tool-call log.

---

## 7. Sidebar UX (new section)

**The chat sidebar is the primary interface to the plugin.** Modeled on the Lineweight document creator / Claude Coworker chat panel: streaming responses, live tool visibility, direct-manipulation controls. The Rhino command line is no longer the driver.

### 7.1 Layout

Eto docked panel (`RhinoClaude.UI.AgentChatPanel`), registered in `RhinoClaudePlugin.OnLoad` alongside `TagInspectorPanel`. Default dock: right side, ~380px wide.

Top-to-bottom:

```
┌─ Header ─────────────────────────────────────────┐
│  RhinoClaude  ▾ Session: "restroom-01"    ⚙︎    │
│  ● Ready  |  $0.14 / $0.50  |  iter 3/25        │
├─ Message stream (scrollable) ────────────────────┤
│                                                  │
│  You  ▸  Build a 10×12 ft office…                │
│                                                  │
│  Claude  ▸  Let me look at the doc first.        │
│  ▸ tool: describe_document       ✓ 42ms          │
│    { objects: 0, units: "in" }                   │
│  ▸ tool: ensure_layer "Walls"    ✓ 8ms           │
│  ▸ tool: create_box × 4          ✓ 120ms         │
│  ▸ tool: capture_views (3 shots) ✓ 620ms         │
│    [ thumbnail ] [ thumbnail ] [ thumbnail ]     │
│                                                  │
│  Claude  ▸  signal_done…                         │
│    Reviewer: SHIP — walls match spec, layer      │
│    "Walls" created, no degenerate geom.          │
│                                                  │
├─ Composer ───────────────────────────────────────┤
│  [ ask Claude to modify this scene…      ] [↩]  │
│  [ ⏸ Stop ] [ ↶ Revert session ] [ ⟲ New ]    │
└──────────────────────────────────────────────────┘
```

### 7.2 Behaviors

- **Streaming text renders live.** The message bubble fills as `content_block_delta` events arrive. Cursor blinks at the end while streaming.
- **Tool calls render as collapsed cards.** Header shows tool name and status (`pending`, `✓ NNms`, `✗ error`). Click to expand; expanded view shows the input JSON and the result JSON side-by-side. Screenshot tool cards show thumbnails inline; click to view full-size in Rhino's default image viewer.
- **Tool call badges** are compact when the agent runs many. `create_box × 4` collapses four identical calls into one card.
- **`ask_user` surfaces inline.** When the reviewer returns `ask_user`, the sidebar posts the question as a Claude message with an inline answer box directly below. User's answer becomes the next turn.
- **Cost meter** in the header shows spent / budget. Cell color drifts amber past 60%, red past 90%. Clicking it opens a session cost breakdown (tokens in/out per iteration + image tokens).
- **Iteration counter** for transparency into the loop.
- **Status LED** shows `Ready`, `Streaming`, `Working` (tool dispatch), `Waiting for you`, `Cancelled`, `Error`.
- **Stop button** cancels the current turn. In-flight tool completes; nothing new fires. State goes to `Cancelled`.
- **Revert session** pops the session's stacked undo records — one click, whole session gone. Confirmation dialog.
- **New session** starts a fresh conversation, prompts for confirmation if the current session has unshipped work.
- **Session dropdown** in the header shows the current session and lets Bryan switch to a prior session on this document (persisted via `AgentConversationStore`).
- **Settings gear** opens: model choice per role, `MaxCostUsd`, `MaxIterations`, log locations, "always confirm before `run_rhino_command`" toggle.

### 7.3 Selection integration

The chat is document-aware: the current Rhino selection is displayed at the top of the composer ("3 objects selected — send with message?"). Toggling on injects a `[SELECTION: <ids>]` marker into the user turn so the agent knows to prefer those ids when a query would otherwise be ambiguous.

### 7.4 Notifications

When the sidebar is closed or unfocused and the agent needs input (`ask_user`) or finishes a long turn, the panel tab flashes and a small banner appears at the bottom of the Rhino window ("RhinoClaude needs your input"). Never modal.

### 7.5 Panel-vs-command relationship

- The panel is the front door.
- `ClaudeChat` command opens/focuses the panel.
- `ClaudeSetKey`, `ClaudeTag`, `ClaudeRevertSession`, `ClaudeAddReviewView` remain as commands because they're small and useful from the command line.
- All chat-driven work happens in the sidebar. No modal dialogs, no `Dialogs.ShowEditBox` for the agent loop.

---

## 8. Answered questions (was §8 "open" in Rev 1)

All answers below reflect Bryan's decisions.

1. **Model choice:** Sonnet 4.5 for the loop, Opus 5 for `SelfReviewService`. Configurable in the settings gear; defaults ship as chosen.
2. **Cost budget:** `MaxCostUsd = 0.50` per turn. Noted as a starting value; may grow with usage patterns. Instrument spent-vs-budget in the JSONL logs.
3. **Screenshot cadence:** model-driven, no plugin-enforced cadence. Instrumented (§6.5). We'll dial back via system prompt if the agent over-captures.
4. **`RoomSkills`:** deleted from the codebase. Recipe belongs elsewhere if it lives at all.
5. **Session persistence UX:** prompt on doc reopen with "New session" pre-selected. Options: New / Resume / Revert prior session.
6. **Roslyn C# in Tier 2:** in Phase 1. IronPython dropped entirely. Old `ScriptRunner` deleted.
7. **Grasshopper components as tools:** scoped out for Phase 1. No changes.
8. **Script log location:** `%APPDATA%/RhinoClaude/script_log.jsonl`. Companion `capture_log.jsonl` for screenshots at the same path.
9. **`ClaudeAddReviewView`:** included. Small command that stamps the current camera as a `Claude:Review` named view; `SelfReviewService` prefers it when present.

---

## 9. Migration plan

Phased. Each phase is shippable in isolation and leaves the plugin functional. Rev 2 shifts weight into UX (Phase 8), pulls Roslyn and streaming forward, and deletes IronPython.

### Phase 0 — Housekeeping (0.5 days)
- `git init`, `.gitignore` (bin/, obj/, `API_Key.txt`, `.3dm`, `.3dmbak`), branch `refactor/agent-loop`.
- Move `API_Key.txt` out of the repo, document env-var setup in README.

### Phase 1 — Anthropic client with streaming + tool registry scaffolding + Roslyn (4 days)
- Add `Services/Agent/`.
- Build `AnthropicClient` with SSE streaming from the start. Typed content-block polymorphism, `input_json_delta` handling, `message_delta.usage` accumulation.
- Build `ToolRegistry` + `ToolDispatcher` shells. Register `describe_document` and `signal_done` end-to-end.
- Add `Microsoft.CodeAnalysis.CSharp.Scripting` NuGet ref to both TFMs. Prove a hello-world script compiles and runs against `RhinoDoc.ActiveDoc`.
- New command `ClaudeChatTest` runs a hard-coded prompt through the loop with those two tools, prints streaming output to the command line. Verify tool-use round-trip.
- **Deliverable:** streaming tool-use loop against real Rhino state.

### Phase 2 — Tier 1 read tools + query service (2 days)
- Implement `RhinoQueryService` and all 7 Query tools (including sub-object indexing on `get_object`).
- Register them.
- **Test:** `ClaudeChatTest` can answer "how many objects are on layer X" using `list_objects`.

### Phase 3 — Mutation service + undo integration (3 days)
- Implement `RhinoMutationService` with `try/finally BeginRecord`/`EndRecord` helper.
- Implement Create-Geometry (6), Transform (5, including `scale_1d`), Layer (2), Block (2), Material (1), Selection (2), Meta (2) tools.
- `SessionSnapshotService` for stacked undo.
- **Test:** "draw a 10ft cube on a new layer called Walls" → one atomic undo removes everything.

### Phase 4 — Tier 2 escape hatch (Roslyn) (2 days)
- Wire `run_rhinocommon_script` to a Roslyn `Script<object>` executor with the `ScriptGlobals` type, pre-imports, and cached compilation.
- Delta detection + result serialization + 32 KB cap.
- Static-analysis blocklist pass.
- JSONL logging to `%APPDATA%/RhinoClaude/script_log.jsonl`.
- **Test:** "make a torus" (no Tier 1 tool) → escape hatch fires and returns useful output.

### Phase 5 — Boolean / Modify tools including `move_face` / `move_edge` (2 days)
- `boolean_union`, `boolean_difference`, `boolean_intersection`, `offset_curve`, `extrude_curve`.
- `move_face` and `move_edge` (Brep sub-object editing — validate face/edge indices against the target Brep, use `Brep.Faces[i].Translate` + `Brep.Faces.RemoveSlit` cleanup or `TransformComponent` equivalent).
- Test: "make a box, then push the top face up 5ft."

### Phase 6 — 3D view capture (multi-shot) + `ClaudeAddReviewView` (2 days)
- `ViewCaptureService` with all `CameraShot` variants including presets, orbit-from-current, and named-view lookup.
- `capture_views` tool.
- `ClaudeAddReviewView` command.
- Update `ToolDispatcher` to lift each image field of a multi-shot result into proper image content blocks.
- **Test:** "look at the model from plan, front, and iso — do the walls line up?" → three images returned in one call, agent reasons about them.

### Phase 7 — Self-review (Opus 5) (2 days)
- Deterministic checks per §5.2.
- `SelfReviewService` orchestration using `AnthropicClient` (non-streaming for the review call) targeting Opus 5.
- Wire `signal_done` to trigger review; feed verdict back through the loop.

### Phase 8 — Chat sidebar UI (5 days) — expanded from Rev 1
- `AgentChatPanel` (Eto) with the layout from §7.1 and all the behaviors from §7.2.
- Streaming text rendering (subscribes to `IAgentSessionObserver` events emitted by `AnthropicClient`).
- Tool-call cards, collapsible, thumbnail inline, click-to-open-full-image.
- Cost meter, iteration counter, status LED.
- Composer with selection integration.
- Session dropdown, New / Revert buttons.
- Notifications when unfocused.
- Settings dialog behind the gear.
- Per-document `AgentConversationStore` persistence with prompt-on-reopen.
- `ClaudeChat` command opens/focuses it.

### Phase 9 — Delete IronPython + RoomSkills, migrate `ClaudeTag`, remove `ClaudeAsk` / `ClaudeRunScript` (1 day)
- Delete `Services/ScriptRunner.cs`.
- Delete `Schema/RoomSkills.cs`.
- Delete `Commands/ClaudeAskCommand.cs`.
- Delete `Commands/ClaudeRunScriptCommand.cs`.
- Rewrite `ClaudeTag` to use `AnthropicClient` directly (still one-shot, no tools) — remove its `ScriptRunner` dependency and its inline scene-context serialization; call `SceneContextCollector.CollectBriefContext` for the prompt only.
- Update `RhinoClaudePlugin.OnLoad` command list.

### Phase 10 — Tier 3 (`run_rhino_command`) + cleanup (0.5 days)
- `run_rhino_command` tool with sidebar confirmation banner on first per-session use.
- Doc pass, README, script-log analysis note.

**Rough total: ~22 ideal days** (Rev 1 was ~19; sidebar expansion, Roslyn pulled forward, and streaming account for the delta).

### What runs alongside vs. rip-and-replace

- **Alongside (no risk):** every new service (`AgentSession`, `AnthropicClient`, `Query/Mutation/View/Script/Review/Snapshot`, `AgentConversationStore`), the new `ClaudeChat` command, the new panel, tag/schema commands, `RCBuildFromDiagram`, `TagInspectorPanel`.
- **Rip-and-replace:** `ClaudeApiService` (renamed and rewritten as `AnthropicClient` with streaming — old class removed after Phase 9).
- **Deleted outright:** `ScriptRunner`, `RoomSkills`, `ClaudeAskCommand`, `ClaudeRunScriptCommand`.
- **Untouched:** `TagSchema`, `BuildingStandards`, `TagService`, `TagInspectorPanel`, `RCBuildFromDiagram`, all `RC*` tag commands.

---

## 10. Risks

1. **UI-thread contention under many rapid tool calls.** Every tool marshals to the main thread. If the agent fires 10+ per iteration, Rhino UI could feel stuck. Mitigation: batch tool-result flushing, "agent is working" cursor, 30ms tick to `RhinoApp.Wait` in the panel's background loop.
2. **Streaming + Rhino main loop interaction.** The SSE reader runs on a background thread; every UI append hits `Application.Instance.AsyncInvoke`. If the model streams very fast, we can queue thousands of tiny UI updates. Mitigation: coalesce text deltas into a fixed-interval flush (e.g. 33ms), which also gives readable rather than jittery animation.
3. **Roslyn cold-compile latency.** First script compile per session is ~500ms. Mitigation: warm the Roslyn engine on `AgentSession` creation with a trivial `Result = 1;` compile.
4. **Undo record hygiene.** Every mutation must use `try/finally` around `BeginRecord`/`EndRecord`. Non-negotiable. Consider a base helper class to prevent forgetting.
5. **Image token cost.** One 1280×800 screenshot ≈ 1500 tokens; a 3-shot bundle ≈ 4500. Watch the cost budget. If `capture_views` bundles routinely bust budget, add per-turn cap.
6. **Message history growth across turns.** Sessions across turns are a token bomb if we keep every tool result. Mitigation: compact after `signal_done: ship` — replace tool-use/result pairs older than 3 turns with summary lines.
7. **Prompt injection via layer / object names.** A malicious name could try to hijack the agent. Low risk in Bryan's context. Mitigation: strip control characters, wrap string fields in `<user_content>` markers when serializing tool results.
8. **RhinoCommon behavior differences between Rhino 7 (net48) and Rhino 8 (net7.0).** Every tool needs to be tested against both targets. Add a smoke-test 3dm.
9. **`RhinoDoc.Strings` size limits for session persistence.** Long conversations could exceed. Fallback: sidecar file next to the .3dm.
10. **Roslyn scripting dependency size.** ~5 MB added to build output. Confirmed acceptable per Bryan.
11. **`move_face` / `move_edge` on non-planar geometry.** These operators require the target face to be planar for a clean push/pull; free-form surfaces need `Brep.Faces[i].TransformControlPoints`, which is a different code path. Mitigation: detect and return a clear `notes` message directing the agent to the script escape hatch for the non-planar case.

---

## 11. Explicitly deferred (not in this plan)

- Grasshopper component invocation.
- Multi-agent orchestration (separate reviewer/planner/executor agents).
- Automatic Tier 1 tool promotion from script-log analysis (manual quarterly pass for now).
- MCP server exposure (Rhino-as-MCP is compelling, but out of scope until the internal loop is solid).
- Fine-tuning the model on Wold-specific patterns.
- Additional primitives (sphere/cylinder/cone/torus) — will be promoted from Tier 2 script log once usage justifies.

---

## 12. What Bryan is greenlighting

By approving Rev 2, you're greenlighting:

1. The three-tier tool architecture with **34 Tier 1 tools** (including `scale_1d`, `move_face`, `move_edge`).
2. **Streaming tool-use loop from Phase 1.**
3. **Roslyn C# as the sole scripting language** for the Tier 2 escape hatch. IronPython deleted.
4. The service topology (`AgentSession`, `AnthropicClient`, `Query/Mutation/View/Script/Review/Snapshot`, `ConversationStore`).
5. **A dockable chat sidebar as the primary interface**, per §7. `ClaudeAsk` and `ClaudeRunScript` deleted; `RoomSkills` deleted; `RCBuildFromDiagram` and tag commands untouched.
6. **`capture_views` as a 3D camera controller with multi-shot** and named-view + preset + orbit support.
7. **`ClaudeAddReviewView`** as a small named-view stamp command feeding self-review.
8. Self-review powered by **Opus 5**; loop powered by **Sonnet 4.5**.
9. A **~22 ideal-day** phased rollout, each phase shippable.
