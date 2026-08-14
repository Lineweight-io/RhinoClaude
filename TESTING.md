# RhinoClaude — Rhino smoke test

Everything in `Services/Agent/`, `Services/Semantic/`, `Tools/` and `UI/` compiles against
RhinoCommon but has **never been run inside Rhino** — RhinoCommon is a compile-only reference, so
no unit test here touches a real document. This is the script for the first live session.

Steps 0–8 cover the phase 1 agent. **Step 9 covers the semantic layer**, and is the part with the
most unverified RhinoCommon surface — if you are short of time, do steps 0–2 and then step 9.

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
RhinoClaude plugin loaded (agent refactor + semantic layer).
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

## 9. The semantic layer

Everything under `Services/Semantic/` is in the same position the phase 1 services were: it
compiles against RhinoCommon and has never run. The Rhino-free half is unit tested; the Brep
measurement that feeds it is not.

Start from a **fresh document**. Draw the fixture by hand, so a classifier mistake is obvious:

1. `Box` — 0,0,0 to 100,60,36. Put it on a layer named `MASS_Office`.
2. `Box` — 100,0,0 to 160,60,24. Layer `MASS_Retail`.
3. `Rectangle` — a closed curve well outside both, on `SITE_Property-Line`.

### 9a. Object-level classification

> **Describe the massing.**

**Expect** `describe_massing` to report **exactly two masses**, one Office and one Retail, the
office at 216,000 ft³ and the retail at 86,400 ft³, with `classifiedBy: "canonical"` on both, and
the narrative saying the retail mass abuts the office one.

Check especially:
- **`footprintArea` is 6,000 and 3,600**, not doubled. It is computed from down-facing faces, and
  a normal-direction sign error would double or zero it.
- **The property-line curve is not counted as a mass.**
- The narrative's storey counts are absent unless a floor-to-floor is set (set 12 in the gear and
  re-ask — it should then read "3-storey" and "2-storey").

### 9b. Face labelling — the highest-risk step

> **List the faces of the office mass.**

**Expect** six faces: four `facade` with orientations **N, S, E, W**, one `roof` (`up`), one
`floor` (`down`).

**This is the single thing most worth checking.** `BrepFace.OrientationIsReversed` is what makes
the outward normal outward, and getting it wrong inverts every label — facades become party
walls, the roof becomes the floor. If N and S are swapped, the compass sector maths is wrong; if
roof and floor are swapped, the normal sign is.

The face between the two masses (the office's east face) should read **`party-wall`**, not
`facade`. If it reads facade, the adjacency probe in `IsPartyWall` is off.

### 9c. Openings from holes

> **Cut a 20 by 10 foot storefront in the south face of the office mass, sill at ground level.**

**Expect** `cut_opening` to succeed and report where it actually landed. Then:

> **What's the wall-window ratio by orientation?**

**Expect** the south face at 200/3600 ≈ **5.6%** and the other three at 0. If the opening is
found but the ratio is null, the face lost its `facade` role in the re-classification; if the
opening is not found at all, inner-trim-loop detection is not seeing it.

Check the model visually too: the hole should be **through** the wall, in the right place, and
the mass should still be a closed solid.

### 9d. Massing operations

> **Push the top face of the office mass up 6 feet.**

**Expect** `push_pull_face` with `{role: "roof"}`, a `deltaVolume` of about 36,000, and the box
visibly taller. Undo once — **the whole move should come back in one step**, not several.

> **Fillet all the outside corners of the office mass at 2 feet.**

**Expect** four edges filleted in one call. A radius Rhino cannot absorb should fail with a
message saying so rather than a bare exception.

> **Cut a 15 by 15 foot light well through the office mass at its centre.**

Expect `add_mass` then `subtract_mass`, and the result reported as classifying as a `Cut`.

### 9e. Conventions

Rename `MASS_Office` to something non-canonical (`BLDG-MASSING-01`) and re-ask for the massing.
**Expect** it still to be found — as `classifiedBy: "geometry-inference"`, with the narrative
warning that it was a guess.

Then run `ClaudeLearnNamingConvention`. **Expect** one API call, a printed mapping, accept /
edit / cancel, a floor-to-floor prompt, and a scope prompt. Accept, then re-ask — it should now
read `classifiedBy: "learned-convention"` with the right function.

Then `ClaudeSetElement` → `Mass` → `Institutional` on the same box. **Expect** `user-data` to win
over the learned convention on the next describe.

Finally `ClaudeSetElement` → `SetFaceRole`: Ctrl+Shift-click a face and label it. **Expect**
`get_mass_faces` to report that role afterwards.

### 9f. Timing

Open `%APPDATA%\RhinoClaude\classifier_timing.jsonl`. **Expect** one `object-level` line per
rebuild and one `mass-geometry` line per mass analysed. On this three-object fixture both should
be single-digit milliseconds. Then open a **real project file** and repeat 9a — the budgets are
<150 ms object-level and <50 ms per mass, and anything wildly over is worth reporting with the
model's object count.

---

## Failure triage

**Expected and harmless**

- Occasional `notes` about open surfaces from booleans — the tools report these deliberately.
- `move_face` warning that a face is non-planar; it says to use the script hatch.
- Review returning `unavailable` if the Opus call fails — by design it never blocks work.
- `analyze_boolean_history` reporting `historyAvailable: false`. That is the normal answer —
  most architects work with history off, and nothing else depends on it.
- A light well cut all the way through showing up as openings rather than a `Cut`. A through cut
  is welded to the outer skin, so the topology path cannot see it as a void; only history can.
- Recesses and cantilever overhangs carrying "inferred" notes — those two are geometric guesses
  by design.

**Report these — they are real**

- Wrong units (a 10-unit box when the document is in inches).
- The viewport not returning to where it was after `capture_views`.
- Revert session leaving geometry behind, or removing more than the agent made.
- A tool card showing `✗` with a RhinoCommon exception name — that is an API I got wrong.
  `TransformComponent` (move_face/move_edge), `SetViewProjection` (capture) and
  `InstanceDefinitions.Add` (blocks) are the three I would suspect first, since they are the
  least common APIs in the codebase.
- The plugin failing to load at all on Rhino 8 (see step 0).
- **Face roles inverted** — the roof reading as `floor`, or facades as `party-wall`. That is
  `BrepFace.OrientationIsReversed` handled wrongly in `MassGeometryAnalyzer.OutwardNormal`, and
  it invalidates every semantic answer downstream.
- **Compass orientations rotated or mirrored** — the north face reporting `S`, or `E` and `W`
  swapped. That is `FaceClassifier.CompassSector`'s bearing convention (north is +Y, clockwise).
- **Footprint area doubled or zero** — the down-facing-face projection in
  `SemanticClassifier.FootprintArea`.
- A semantic write leaving **more than one undo step** for one tool call — `RunComposite` is not
  wrapping what it should.
- `cut_opening` putting the hole in the wrong place on the face. The face frame in
  `SemanticMutationService.FaceFrame` derives width from area ÷ height, which is exact for a
  rectangular face and approximate for anything else.

**Where to look**

| What | Where |
|---|---|
| Script calls | `%APPDATA%\RhinoClaude\script_log.jsonl` |
| Capture calls | `%APPDATA%\RhinoClaude\capture_log.jsonl` |
| Classifier timings | `%APPDATA%\RhinoClaude\classifier_timing.jsonl` |
| Screenshots | `%TEMP%\RhinoClaude\screenshots\<sessionId>\` |
| Tool input/result | Click any tool card in the panel |
| Cost detail | Click the cost meter |

Tool cards show the exact JSON that went in and came back, which is usually faster than reading
the logs — the failing call is right there in the transcript.
