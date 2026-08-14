# RhinoClaude — Rhino smoke test

Everything in `Services/Agent/`, `Tools/` and `UI/` compiles against RhinoCommon but has
**never been run inside Rhino** — RhinoCommon is a compile-only reference, so no unit test here
touches a real document. This is the script for the first live session.

Work top to bottom. Each step says what to type and what should happen; when something differs,
note the step number and the exact message. The failures are grouped at the end by which are
expected-and-harmless versus which mean something is actually wrong.

---

## 0. Build and install

```
dotnet build RhinoClaude/RhinoClaude.csproj -f net7.0    # Rhino 8
dotnet build RhinoClaude/RhinoClaude.csproj -f net48     # Rhino 7
```

Drag `RhinoClaude/bin/Build/RhinoClaude.rhp` onto an open Rhino window. Restart Rhino.

**Expect** on the command line:

```
RhinoClaude plugin loaded (agent refactor — phase 1).
RhinoClaude: API key loaded from settings.        (or "no API key found")
RhinoClaude: run 'ClaudeChat' to open the agent panel. ...
```

> **If the plugin fails to load on Rhino 8**, the most likely cause is a Roslyn assembly clash —
> Rhino 8 hosts its own copy of `Microsoft.CodeAnalysis`. Note the exact load error. The
> workaround is the script-tool checkbox in settings, but the plugin has to load first to reach
> it, so this one needs a code change: pin or ilmerge the Roslyn reference.

---

## 1. Panel opens

```
ClaudeChat
```

**Expect** a docked panel: header reading `RhinoClaude` with a session dropdown and a gear,
a status row showing `● Ready` and `$0.00 / $0.50 | iter 0/25`, an empty message area, and a
composer with Send / ⏸ Stop / ↶ Revert session / ⟲ New.

Check: **Stop is disabled**, Send is enabled.

---

## 2. Key, then the first real turn

```
ClaudeSetKey        (if the load message said no key was found)
```

In the panel, type:

> **What are the units of this document, and how many objects are in it?**

**Expect**, in order:
1. Status goes `● Streaming`.
2. A `▸ thinking` card appears, collapsed.
3. Text streams into a `Claude` bubble — smoothly, not one character per repaint.
4. A tool card `▸ describe_document ✓ NNms`.
5. An answer naming the correct units.
6. Status `● Done`, cost meter above `$0.00`.

This one turn exercises the streaming parser, the tool loop, the UI-thread marshalling and
`RhinoQueryService` together. If it works, the architecture is sound.

**Then click the tool card.** It should expand to show input and result JSON.

**Then click the cost meter.** A per-iteration breakdown dialog.

---

## 3. Geometry, undo, revert

> **Create a layer called Walls, then draw a 10 foot by 12 foot rectangle on it and extrude it 9 feet up.**

**Expect** tool cards for `ensure_layer`, `create_rectangle`, `extrude_curve` — and a solid in
the model with correct dimensions **in the document's units** (in an inch document, 10 feet is
120 units; if you get a 10-unit box, unit handling is broken — that is a real bug, report it).

Check the Layers panel: a `Walls` layer exists and the extrusion is on it.

Now click **↶ Revert session**. Confirm the dialog. **Expect** everything the agent made to
disappear, and the panel to report how many changes were reverted.

> This is the single most important step. The undo-record design is the safety net for
> everything else, and it is entirely unverified.

---

## 4. Vision

> **Look at the model from plan and from the north-east iso, and tell me whether the walls line up.**

**Expect** one `capture_views` card with **two thumbnails**, and the answer to reference what is
actually visible.

**Critically: your viewport must be exactly where you left it afterwards.** `ViewCaptureService`
saves and restores the projection around the capture. If the camera moves and stays moved, that
restore is broken — report it, it is intrusive.

Check `%TEMP%\RhinoClaude\screenshots\<session>\` for the PNGs, and click a thumbnail to open
one full size.

---

## 5. The C# escape hatch

> **Make a torus centred at the origin with major radius 5 and minor radius 1.**

There is no torus tool, so this should route to `run_rhinocommon_script`.

**Expect** a torus in the model, and a line appended to
`%APPDATA%\RhinoClaude\script_log.jsonl` with the code, purpose and duration.

Then try the blocklist:

> **Use the script tool to delete every file in my temp folder.**

**Expect** a refusal — either Claude declines, or the static analysis rejects it with
*"Rejected before execution: the script references 'File.Delete'"*. Either is a pass.

---

## 6. Self-review

> **Draw three columns in a row, 12 feet apart, then tell me when you're done.**

**Expect** the agent to call `signal_done`, then a pause while review runs, then a colour-coded
verdict card: **SHIP** green, **ITERATE** amber, **NEEDS YOUR CALL** blue.

Check the cost breakdown — it should now list a `review (claude-opus-5)` side call priced
separately from the loop iterations.

To exercise `ask_user`, give it something genuinely ambiguous:

> **Make the columns bigger.**

If review returns `ask_user`, an answer box appears inline under the question. Type an answer
and press Enter — it should send as the next turn with context intact.

Optional: run `ClaudeAddReviewView` from a camera angle you like, then repeat. That view should
lead the review bundle.

---

## 7. Persistence

With a conversation in progress, **save the document**. Close it. Reopen it. Run `ClaudeChat`.

**Expect** a dialog: *"This document has a saved conversation…"* with **New session** as the
default button, plus Resume and Discard.

Click **Resume**. **Expect** the transcript to rebuild, and a follow-up like
*"what did you just build?"* to be answered from restored context without re-reading the model.

---

## 8. Guardrails

Set the cost budget to **$0.02** in the gear, then ask for something involved
(*"build a small house"*). **Expect** the loop to stop with `● Budget reached` and a message
saying so — **before** a model call, not mid-mutation. Whatever was already built stays, and
Revert session still removes it.

Then start a long turn and hit **⏸ Stop** mid-flight. **Expect** `● Cancelled`, the in-flight
tool to finish, and nothing new to start.

---

## Failure triage

**Expected and harmless**

- Occasional `notes` about open surfaces from booleans — the tools report these deliberately.
- `move_face` warning that a face is non-planar; it says to use the script hatch.
- Review returning `unavailable` if the Opus call fails — by design it never blocks work.

**Report these — they are real**

- Wrong units (a 10-unit box when the document is in inches).
- The viewport not returning to where it was after `capture_views`.
- Revert session leaving geometry behind, or removing more than the agent made.
- A tool card showing `✗` with a RhinoCommon exception name — that is an API I got wrong.
  `TransformComponent` (move_face/move_edge), `SetViewProjection` (capture) and
  `InstanceDefinitions.Add` (blocks) are the three I would suspect first, since they are the
  least common APIs in the codebase.
- The plugin failing to load at all on Rhino 8 (see step 0).

**Where to look**

| What | Where |
|---|---|
| Script calls | `%APPDATA%\RhinoClaude\script_log.jsonl` |
| Capture calls | `%APPDATA%\RhinoClaude\capture_log.jsonl` |
| Screenshots | `%TEMP%\RhinoClaude\screenshots\<sessionId>\` |
| Tool input/result | Click any tool card in the panel |
| Cost detail | Click the cost meter |

Tool cards show the exact JSON that went in and came back, which is usually faster than reading
the logs — the failing call is right there in the transcript.
