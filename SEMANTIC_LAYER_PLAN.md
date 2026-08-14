# RhinoClaude — Semantic Layer Plan

**Status:** Revision 2 — awaiting Bryan's greenlight.
**Author:** Claude (Cowork)
**Date:** 2026-08-14
**Depends on:** `AGENT_REFACTOR_PLAN.md` shipped and live-validated (Phases 0–10).

---

## 0. What changed in Revision 2 (skim this first)

Rev 1 modeled the vocabulary the way Revit does — as a kit of parts (Volumes + Facades + Walls + Openings + Roofs) that get *assembled* into a building. That's wrong for Rhino. **Architects working in Rhino do not compose parts. They start with solid masses and manipulate them: push/pull faces, boolean-union to add, boolean-difference to cut. The building emerges from operations on solids.** Facades are the vertical faces of the resulting solid. Roofs are the top faces. Openings are subtractions.

Bryan's feedback turned into these material changes:

| # | Change | Where it lands |
|---|---|---|
| 1 | **New §1.5 "How architects actually work in Rhino"** — grounds the whole plan in solid modeling with push/pull + booleans, not part composition. Everything downstream reads through this lens. | New §1.5 |
| 2 | **`Mass` is the single primary element type.** Not `Volume` (which implied a scheduled program mass to be assembled with other parts). `Mass` is a solid Brep, full stop. Function tag is a property, not a subtype. | §3 (rewritten) |
| 3 | **`Face` and `Edge` are first-class.** Facade / Roof / Wall are *labels the classifier applies to faces of masses at query time* — not element types users draw. A vertical face of a mass is a candidate facade; a top-normal face is a candidate roof. Same face can carry multiple labels. | §3.2, §3.3, §5 (rewritten) |
| 4 | **Derived elements reorganized around boolean/push-pull history:** `Opening`, `Overhang`, `Recess`, `Cut`. Not "parts embedded in walls" — geometric features that emerged from operations. | §3.5–3.8 |
| 5 | **Composition elements added:** `MassGroup`, `Composition`. `Level` and `Site` are secondary. | §3.9–3.12 |
| 6 | **Tool catalog reoriented around operations, not parts.** Push/pull, boolean union/difference, mass creation, face labeling, edge treatment. Not "add opening at position" as the primary move — that's a special case of "cut into a face." | §4 (rewritten) |
| 7 | **Two new tool groups: massing operations (write) and face/edge analysis (read).** The write tools are core to Phase 1 of this rollout now, not deferred — mass modeling *is* the SD workflow. | §4.3, §4.4, §7 folded in |
| 8 | **Classification section rewritten** — face labeling is a query-time operation (walk each mass's Brep faces, classify by normal + elevation + adjacency). Explicit-tag override still works. `MASS_Office` at the layer level, face roles at the geometry level. | §5 (rewritten) |
| 9 | **`analyze_boolean_history` tool** — if Rhino's history tracking captured the operations, walk them. Enables the agent to reason about how a mass was constructed, not just what it looks like now. | §4.6 |
| 10 | Rev 1's §7 ("semantic write tools as natural Phase 2") is deleted. Massing operations were the deferred Phase 2 in Rev 1 — in Rev 2 they're moved into the core rollout because they *are* the primary way the agent participates in design. | §7 removed; §9 phase order updated |
| 11 | Element count is now **~7 first-class types (Mass, Face, Edge, Opening, Overhang, Recess, Cut) + 4 composition/context types (MassGroup, Composition, Level, Site)**. Tool count reorganizes to ~17 read + ~7 write = **~24 semantic tools**. | §3, §4 |

Scope impact: the code volume grows because massing operations are now Phase 1, but the classifier work shrinks (fewer element types with dedicated detection). Rev 1 estimate was ~10 ideal days; **Rev 2 is ~12 ideal days** — the extra ~2 days go entirely to the massing operation tools.

**What stays from Rev 1:** the reviewer principle ("vision for judgment, semantic queries for facts, screenshots win when they disagree"), SD-scope-not-BIM boundary, the four-step classifier resolution rule (user-data tag → learned convention → shipped canonical → geometry inference), the `LearnNamingConvention` command, the `ElementRegistry` cache pattern, the compositional relationship with Phase 1 raw tools. Those aren't tied to the parts-vs-mass framing.

---

## 1. Purpose

### 1.1 What Phase 1 can't do

Phase 1's agent sees a `.3dm` as a bag of Breps, Curves, and Points tagged with layer names and optional `RC:` tags. Ask it "how tall is the office mass?" and it must: `list_objects` → pattern-match to guess which brep is "the office" → `get_object` for the bbox → subtract z-min from z-max. Every question re-derives structure from raw geometry. Ask "what's the wall-window ratio of the north face of the office mass?" and it must derive:

1. Which brep is the office mass?
2. Which face of that brep faces north?
3. Which sub-shapes on that face are openings vs. solid?
4. What is the face area minus the opening area?

Four inferences per question, redone every turn, with the model's own coherence as the only check. Errors compound. Tokens burn. And nothing the agent computes carries forward — the next turn starts from raw breps again.

Meanwhile the *user* thinks in masses, their faces, and the operations that shaped them. The gap between how the user thinks and how the agent thinks is the wall this plan is punching through.

### 1.2 Vision + semantic queries

Two orthogonal channels the agent uses simultaneously:

- **Screenshots (Phase 1's `capture_views`)** answer *does this feel right?* — proportion, character, hierarchy, tectonic quality. The reviewer principle. Judgment tasks belong here.
- **Semantic queries (this plan)** answer *what is true?* — mass volumes, face orientations, opening areas, boolean adjacencies, zoning envelope math. Factual and analytical tasks belong here.

The agent picks the right channel per sub-question and combines the answers. "The north face *looks* under-lit — check the WWR" is a two-step move: `capture_views` for the judgment, `check_wall_window_ratio` for the fact. Neither alone is enough.

### 1.3 The doc-engine parallel

The doc-engine "facts to reviewer" principle from Phase 1 §5.4 applies here almost verbatim. Where Phase 1 fed deterministic check results into the reviewer's context so it wasn't guessing about counts and validity, this plan feeds a deterministic *description of the massing state* — mass functions, face orientations, boolean relationships, quantities — into every turn the agent takes. Same principle, different consumer: Phase 1's reviewer, this plan's executor.

The plan also compounds with Phase 1's screenshot pipeline. `describe_massing` + `capture_views(iso_ne, iso_sw, plan)` in the same iteration is a much better foundation for the agent's next move than either alone. Structured facts anchor the model; images give it the parts words can't cover.

### 1.4 What this changes about the agent

Phase 1's agent is a good tool user. This plan's agent is a good tool user *that also holds a mental model of the design in the same terms the architect does.* Concretely:

- Turn 1: user says "make the north face more open." Phase 1 agent: hunt through breps for something facing +Y, cross-fingers on face selection. This plan's agent: `find_element("north face of office mass")` → mass id + face selector back → `check_wall_window_ratio` → decide → `cut_opening`.
- Follow-up: "and pull the top face of the office mass up 6 feet." Phase 1 agent: has `move_face` but has to guess the face index. This plan's agent: `push_pull_face(mass_id, {orientation: "up"}, 6)` — face selection by role, not by index.

The token budget savings alone probably pay for the plan within two turns of realistic use.

### 1.5 How architects actually work in Rhino

**This is the framing correction that drives every design choice in Rev 2.**

Rhino is a solid modeler. Architects doing SD work in Rhino do not draw floors, then draw walls on the floors, then draw a roof on the walls, then punch windows through the walls. That's the Revit workflow, and Revit exists because that assembly model matches how buildings get *built*, not how they get *designed*.

The Rhino SD workflow is different. Architects:

1. **Start with primitive solids.** A box for the main massing, another box for a wing, a cylinder for a rotunda. Rough shapes at rough dimensions.
2. **Push and pull faces** to refine proportions. Grab the north face, drag it 5 feet north. Grab the roof face, tilt it. Grab a side face, break it into three sub-faces to step it back.
3. **Boolean-union** to combine masses that read as one form. The office wing and the residential wing become one Brep once the massing decision is settled.
4. **Boolean-difference** to cut. A light well carved out of the middle. A recessed entry cut into the ground floor. Window openings subtracted from a facade after the mass is otherwise resolved.
5. **Extrude and split** to create secondary geometry. An overhang extruded from an edge. A parapet cap extruded from a top edge. Level slices for floor plates when needed.
6. **Very rarely, place components** — usually only for context (existing buildings, cars, trees, sometimes a stair or elevator core drawn schematically). Even those are typically drawn as simple solids, not "placed instances" of a family.

There are no wall families. There is no "window schedule" ready to consume. Openings exist because someone booleaned a smaller solid out of a larger one, or drew a rectangle on a face and cut it. Facades exist because a mass has a face that points sideways. Roofs exist because a mass has a face that points up.

**Everything in this plan follows from that observation.** The semantic layer's job is to look at the outputs of that workflow — solid Breps and their faces — and put labels on things the architect thinks of but hasn't explicitly drawn as separate objects. "This is the north face of the office mass." "This face has a hole in it that reads as a window." "This mass sits atop that mass." The agent's write operations mirror the same moves the architect makes: push a face, add a mass, cut a mass, extrude an edge.

Concretely, this means:

- **Mass is the atom.** Not a "Volume with a function" — a solid Brep. The function tag is a property of the mass, not a subtype.
- **Faces are labels, not objects.** No one draws "a facade" in Rhino. The classifier walks a mass's Brep faces and applies orientation labels at query time. The same face is "a facade" from one query's perspective and "the south-facing exterior surface" from another.
- **Operations are first-class.** Push/pull, boolean union, boolean difference, extrude edge, slice at elevation. These are the verbs the agent needs. "Add a window" is a special case of "cut a rectangular hole in a face" — implemented as `cut_opening`, but composed of the same primitives.
- **Boolean history matters.** When Rhino tracks history, the plugin can walk it: this mass was originally a box that had a smaller box subtracted. That history is often more legible than the resulting geometry.
- **Loose parts are the exception, not the rule.** A separate object drawn on the `OPENING_Window` layer is fine and the classifier handles it, but the primary path is "hole in a mass face detected as an opening."

If the plan reads like it's fighting the workflow, we got the framing wrong. If it reads like it's naming what the architect is already doing, we got it right.

---

## 2. Scope boundaries

### 2.1 In scope (the vocabulary)

Eleven types total: **7 first-class + 4 composition/context.** Full catalog in §3.

*Primary (first-class):*

- **Mass** — the atom. Solid Brep with function tag, orientation, adjacencies.
- **Face** — a face of a Mass with orientation, area, elevation range, role (candidate facade / candidate roof / candidate floor / other).
- **Edge** — significant edges of a Mass: parapet edge, outside corner, transition between roof surfaces.
- **Opening** — a hole in a Face (window, door, storefront). Detected as a boolean subtraction that pierced the mass, or as a distinct object on an `OPENING_*` layer.
- **Overhang** — a Face or extruded feature that projects past the Face below it.
- **Recess** — an inward push on a Face (loggia, recessed entry). The complement of a bump-out.
- **Cut** — a subtracted volume in a Mass (light well, atrium, courtyard). Distinct from Opening because it goes all the way through or forms a room-sized void, not a wall aperture.

*Composition and context:*

- **MassGroup** — a set of Masses treated as one building or one wing.
- **Composition** — the boolean/additive relationships between Masses (which sits on which, which was cut from which, which is unioned with which).
- **Level** — horizontal reference plane. Secondary — usually derived, not drawn.
- **Site** — property lines, topography, context buildings, streets. Unchanged from Rev 1.

That's the taxonomy. Deliberately minimum-viable for SD massing.

### 2.2 Out of scope (what this plan explicitly refuses to cover)

Bryan's rule: SD-level, not BIM. Not-covered means the agent falls back to raw-geometry tools or the Roslyn escape hatch — the plan does *not* silently under-model these things.

- **Wall assemblies.** No stud/insulation/sheathing layers, no U-values. Faces are faces, not multi-layer walls.
- **MEP.** No HVAC equipment, ducts, piping, electrical. No load calcs.
- **Structural sizing.** Beams and columns, if drawn, are Masses or raw geometry — not structural elements with tributary areas.
- **Furniture, casework, fixtures.** `RCBuildFromDiagram` handles the ADA restroom case; general FF&E is out.
- **Detailed schedules.** Door, window, finish, hardware schedules — out. The plan touches per-face opening area but does not maintain a scheduled inventory.
- **Code checking beyond zoning envelope.** `get_zoning_envelope` covers setbacks / height / FAR at the massing level. IBC egress paths, occupancy load math, energy code checks — all out.
- **Site engineering.** Grading, drainage design, utility layout — out. `Site` is context only.
- **Sun/shadow/daylight simulation.** Adjacent — `check_wall_window_ratio` is what we ship; radiance-style analysis is out.
- **Component families.** No window families, door families, curtain wall assemblies. If the architect draws a window on the `OPENING_Window` layer or subtracts a rectangular hole, the classifier picks it up; the plugin does not build a family library.

**This scoping is what makes the plan tractable.** Every time someone (including future Claude) proposes adding a category, the answer is "prove it belongs in SD-level thinking, then bring it back." Phase 1's Tier 1 tools + Roslyn escape hatch already exist for the things that don't fit.

### 2.3 What "SD-level" means concretely in this plan

Anchor for every scope call: **would a schematic-design deliverable to a client at 30% include this thing?** If yes, in. If no, out. Massing model + face relationships + primary openings + roof form + site context = yes. Stud spacing, glazing spec, hardware schedule = no.

---

## 3. Element type catalog

Seven first-class + four composition/context. Each has: definition, properties, valid geometry, computed metadata, detection heuristics. Written in the same design-contract voice as Phase 1 §3's tool contracts.

**Universal properties** (every element carries these):

- `elementId` — GUID, distinct from the underlying Rhino object id. Stable across sessions.
- `type` — one of the eleven below.
- `rhinoObjectIds` — the source Rhino object(s). A Mass typically maps 1:1; a Face maps to a face index on a Mass's Brep.
- `layer` — the source layer full path (for elements with underlying Rhino objects).
- `name` — human-readable, either from Rhino object name or synthesized (e.g. "Office Mass / North Face").
- `tags` — the object's existing `RC:` tags (Phase 1 schema), passed through unchanged.
- `classifiedBy` — `"user-data" | "learned-convention" | "canonical" | "geometry-inference"`. Debugging / trust surface.

### 3.1 Mass (first-class, primary)

**Definition.** A solid Brep. The atom of the semantic layer. Everything else is a face of a Mass, an edge of a Mass, a hole in a Mass, or a relationship between Masses.

**Properties.** `function: string` (Office | Residential | Retail | Institutional | Common | Other — a *property*, not a subtype), `volume: number`, `footprintArea: number` (projection onto XY), `heightAboveGrade: number`, `bbox`, `centroid`, `principalAxes: [x,y,z] × 3` (PCA on brep vertices), `isSolid: bool`, `faceCount: int`, `edgeCount: int`.

**Valid geometry.** Closed Brep (preferred, and the case the classifier handles fully). Open Brep allowed with a flag. Mesh Masses allowed for topography-adjacent cases but non-primary. Multi-Brep Masses (a set of Breps grouped as one Mass) supported through the `MassGroup` mechanism (§3.9), not through allowing a Mass to hold multiple Breps directly.

**Computed metadata.** `faces: [FaceId]`, `edges: [EdgeId]`, `openings: [OpeningId]`, `cuts: [CutId]`, `adjacentMasses: [{massId, relationship: "abuts" | "sits-on" | "sits-under" | "unioned-with"}]`, `booleanHistory: [BooleanOp]|null` (if Rhino history tracks it).

**Detection heuristics** (priority order):
1. Explicit user-data tag `RhinoClaude:Element:Mass` on the object.
2. Layer name matches learned or canonical convention (e.g. `MASS_Office`, `MASS_Residential`).
3. Object name starts with `Mass:` or `Massing:`.
4. Geometry inference fallback: closed Brep on a layer whose name is not `SITE_*` and not `OPENING_*` and not `OVERHANG_*`; volume above threshold (~1000 ft³ in doc units).

Ambiguity handling: geometry-inference Masses are flagged `classifiedBy: "geometry-inference"` so the agent knows to hedge.

### 3.2 Face (first-class, derived)

**Definition.** A face of a Mass. Not an object the architect draws — a *label the classifier applies* to a Brep face at query time. Same face can carry multiple labels (a "roof face" that is also "the south-facing top face").

**Properties.** `massId`, `faceIndex: int` (on the Mass's Brep), `orientation: "N" | "NE" | "E" | "SE" | "S" | "SW" | "W" | "NW" | "up" | "down" | "other"`, `roles: [Role]` where `Role = "facade" | "roof" | "floor" | "party-wall" | "interior" | "unclassified"`, `area: number`, `centroid`, `normal: [x,y,z]`, `isPlanar: bool`, `elevationRange: [zMin, zMax]`, `boundingSurface: {type: "planar"|"cylindrical"|"nurbs", …}`.

**Valid geometry.** A Brep face on a Mass. Never a separately-drawn object in the primary path; users don't draw facades in Rhino. Explicit face labeling still supported via user-data tag on the Mass's `UserString` keyed by face index (e.g. `RhinoClaude:FaceRole:12` → `"facade"`).

**Computed metadata.** `openings: [OpeningId]` (holes classified on this face), `overhangs: [OverhangId]` (features projecting from this face), `recesses: [RecessId]`, `edgesBounding: [EdgeId]`, `openingArea: number`, `wallWindowRatio: number|null` (only defined when `roles` includes `facade`).

**Classification into `roles`** (query-time, not stored):
- `facade` — outward-facing face whose normal has |Z| < 0.3 (mostly vertical). Interior party-wall candidates excluded by checking if the face is adjacent to another Mass across the same plane.
- `roof` — outward-facing face whose normal has Z > 0.3.
- `floor` — outward-facing face whose normal has Z < -0.3 (typically the ground-plane bottom of the Mass).
- `party-wall` — face coincident with another Mass's face; not exterior.
- `interior` — face bounding a `Cut` (light well interior, atrium interior).
- `unclassified` — anything else.

Note that a face can carry multiple roles (e.g. `["facade", "party-wall-candidate"]` if the check is inconclusive). Roles are cheap to recompute; not cached.

### 3.3 Edge (first-class, derived)

**Definition.** A significant edge of a Mass. Not every Brep edge is a semantic Edge — only ones the architect would refer to: parapet edges (top of a facade), outside corners (junction of two facades), roof ridges, transitions between roof surfaces of different slope.

**Properties.** `massId`, `edgeIndex: int`, `role: "parapet" | "outside-corner" | "inside-corner" | "roof-ridge" | "eave" | "other"`, `length: number`, `startPoint`, `endPoint`, `isLinear: bool`, `adjacentFaces: [FaceId, FaceId]`.

**Valid geometry.** A Brep edge on a Mass. Same explicit-tag override as Faces if the classifier misses one.

**Classification:**
- `parapet` — edge shared by a `roof` face and a `facade` face where the roof face is above (Z_roof > Z_facade at the edge).
- `eave` — edge shared by a `roof` face and empty space (the Face's normal on the roof points up-and-outward past the edge; there's no adjacent facade face at that height).
- `outside-corner` — edge shared by two `facade` faces whose normals point in different compass sectors and outward.
- `inside-corner` — same as outside-corner but the angle between normals is > 180° (concave).
- `roof-ridge` — edge shared by two `roof` faces meeting at a high point.
- `other` — anything else.

Cheap to compute; recomputed on demand, not cached.

### 3.4 Opening (first-class, derived)

**Definition.** A hole in a Face — window, door, storefront, curtain-wall bay. The result of the architect subtracting a smaller solid from a larger one, or drawing a curve on a face and cutting it, or drawing an object on an `OPENING_*` layer.

**Properties.** `massId`, `faceId`, `openingType: "Window" | "Door" | "Storefront" | "CurtainWall" | "Louver" | "Other"`, `width`, `height`, `sillHeight` (above nearest Level or Mass base), `area`, `centroidOnFace`, `depth: number|null` (if it's a punched opening with thickness; null if it's a planar hole), `origin: "subtracted" | "drawn-on-layer" | "explicit-tag"`.

**Valid geometry.** Three acceptable input forms:
1. A hole in the Brep face of a Mass (from a boolean-difference operation).
2. A planar curve or Brep on an `OPENING_*` layer, coincident with a Face of a Mass.
3. Any Rhino object with a `RhinoClaude:Element:Opening` explicit tag.

**Detection heuristics** (in the classifier):
1. Explicit tag.
2. Layer prefix `OPENING_` with subtype (`OPENING_Window`, `OPENING_Door`, `OPENING_Storefront`, `OPENING_Curtain-Wall`, `OPENING_Louver`).
3. **Hole-in-face detection.** For each Mass, iterate its Brep faces; for each face, check for inner trim loops (holes). Each inner loop with area above a small threshold classifies as an Opening. This is the mode the classifier is optimized for — it's how architects create openings in Rhino by default.
4. **Boolean-history detection.** If Rhino history is available and the Mass was produced by a boolean-difference, walk the subtracted solids and classify each one whose bbox intersects a face plane as an Opening.
5. Fallback subtype classification from geometry: sillHeight ≈ 0 and height > 6ft → Door candidate; large area and sillHeight ≈ 0 → Storefront candidate; small-to-medium area with sillHeight ~2.5ft → Window. Flagged.

The hole-in-face detection is the important change from Rev 1. Rev 1 assumed openings were separately-drawn planar objects; Rev 2 assumes the primary case is a hole punched in a Mass face by a boolean operation.

### 3.5 Overhang (first-class, derived)

**Definition.** A Face or extruded feature that projects past the Face below it. Canopy over an entry, deep eave, brise-soleil, cantilevered floor plate. Distinct from a Mass in that it's usually thin and attached to another Mass's exterior.

**Properties.** `attachedToFaceId`, `projectionDistance: number`, `width: number`, `thickness: number`, `area`, `centroid`, `origin: "separate-mass" | "extruded-edge" | "face-cantilever"`.

**Valid geometry.**
1. A thin Mass (Brep) coincident along one face with an existing Mass's exterior face — "the canopy is a small box glued to the wall."
2. An extruded edge or curve object on an `OVERHANG_*` layer.
3. A face of a Mass that steps out past the Mass below it (detected geometrically as a horizontal face whose projection onto its supporting Face extends past the supporting Face's extent).

**Detection heuristics:**
1. Explicit tag.
2. Layer prefix `OVERHANG_` / `CANOPY_` / `EAVE_` / `BRISE_`.
3. Thin Mass with one face coincident with another Mass's exterior face + a mostly-horizontal top face → Overhang.
4. Cantilevered Face — Mass geometry where an upper Face projects past a lower Face → Overhang detected on the projecting Face.

### 3.6 Recess (first-class, derived)

**Definition.** The inward complement of an Overhang — a loggia, a recessed entry, a covered porch. A Cut into a Mass that reads as a niche rather than a room or light well.

**Properties.** `massId`, `depthIntoMass: number`, `openingFaceId` (the outward-facing "mouth" of the recess), `interiorFaceIds: [FaceId]` (the faces bounding the recess interior), `area: number` (opening area), `volume: number` (recess volume).

**Valid geometry.** A subtraction cut into a Mass that does not fully penetrate it. Detected as a `Cut` (see §3.7) whose bounding volume is entirely inside the Mass except for one open face.

**Detection heuristics:**
1. Explicit tag.
2. Layer suffix `RECESS` on the cutter object if present.
3. Geometry inference: a subtracted region opening on one Face, with the other faces of the region being interior faces of the parent Mass.

The distinction between Recess and Cut is mostly semantic — a shallow one-sided subtraction is a Recess; a room-sized or through-cut void is a Cut.

### 3.7 Cut (first-class, derived)

**Definition.** A subtracted volume in a Mass that reads as a room-sized or through-going void: light well, atrium, courtyard, notched corner.

**Properties.** `massId`, `volume: number`, `bbox`, `topOpen: bool`, `bottomOpen: bool`, `interiorFaceIds: [FaceId]`, `centroid`.

**Valid geometry.** A subtracted region in a Mass Brep. Detected via boolean history (preferred) or via inner voids in the Mass's Brep topology.

**Detection heuristics:**
1. Explicit tag.
2. Boolean history: `boolean_difference` operations whose subtracted volume is above a threshold (~200 ft³) classify as Cuts (below threshold → Openings/Recesses).
3. Brep topology: a Mass Brep with inner shells (a solid with a void inside) has each void classified as a Cut.

### 3.8 (Reserved — Rev 1's `Entry` type folded into Opening's subtype system)

Rev 1 had `Entry` as a distinct element promoted from an Opening. In Rev 2 that promotion is a tag on the Opening (`isEntry: bool`, `entryType: "Main" | "Secondary" | "Service" | "Emergency" | null`) — same query surface, one less type to reason about. The `promote_opening_to_entry` write tool remains (§4.4), but it mutates an Opening property instead of creating a new element.

### 3.9 MassGroup (composition)

**Definition.** A set of Masses treated as one building or one wing. Enables "the office wing" queries when the wing is two separate Masses that haven't been boolean-unioned yet.

**Properties.** `name: string`, `masses: [MassId]`, `combinedFootprintArea`, `combinedVolume`, `bbox`, `dominantFunction: string`.

**Detection heuristics:**
1. Explicit tag `RhinoClaude:Element:MassGroup:<name>` on multiple Masses.
2. Rhino Group membership: Masses in the same Rhino Group get automatic MassGroup treatment.
3. Layer parent: Masses under a common parent layer path (e.g. `MASS_Office_Wing / …`) auto-group.

### 3.10 Composition (composition, derived)

**Definition.** The graph of boolean/additive relationships between Masses. "Mass A sits atop Mass B." "Mass C is a subtracted portion of Mass A." "Masses D and E share a party face." Not a stored element — a computed relation surfaced via queries.

**Properties.** `relationships: [{from: MassId, to: MassId, type: "abuts" | "sits-on" | "sits-under" | "coincident-face" | "was-unioned-with" | "was-subtracted-from"}]`.

**Detection:** query-time only. Walks Mass adjacencies and (if available) boolean history.

### 3.11 Level (composition, secondary)

**Definition.** Horizontal reference plane at a named elevation. Secondary in this plan — usually inferred from `slice_mass_at_elevation` operations rather than drawn as separate objects.

**Properties.** `name: string`, `elevation: number`, `floorToFloor: number|null`, `isRoofLevel: bool`.

**Valid geometry.** Either explicit (a plane object with `RhinoClaude:Element:Level` tag), a named `ConstructionPlane`, or fully inferred from Mass base elevations + user-configured floor-to-floor if the user wants a Level ladder without drawing planes.

**Detection heuristics:**
1. Explicit tag.
2. Layer prefix `LEVEL_` with elevation encoded (`LEVEL_02_+12ft`).
3. Named ConstructionPlanes matching `Level*` or `L*`.
4. Fallback: user configures a "floor-to-floor default" in plugin settings; classifier synthesizes Levels from Mass base elevation upward at that spacing.

### 3.12 Site (composition/context)

**Definition.** Context — property line, topography, adjacent buildings, streets, existing conditions. Not part of the design, but the design responds to it. Unchanged from Rev 1.

**Properties.** `siteType: "PropertyLine" | "Topography" | "ContextBuilding" | "Street" | "Curb" | "Utility" | "Other"`, `bbox`, and type-specific fields.

**Detection heuristics:**
1. Explicit tag.
2. Layer prefix `SITE_` with subtype.
3. Fallback: outside the building convex hull + on a layer whose name is not one of the building categories.

---

## 4. Semantic tool catalog

Same contract as Phase 1 §3: `snake_case`, verb-first for actions, noun-first for pure reads, typed inputs, structured outputs, always `{success, error, …}`. These tools live *alongside* Phase 1's tools — the agent still has raw geometry access when semantic tools don't answer the question.

**Count: ~24 tools.** Roughly 17 read + 7 write. Grouped.

**One vocabulary primitive used everywhere below:** `FaceSelector`. Any tool that operates on a Face accepts a union:

```
FaceSelector =
    { faceId: string }
  | { faceIndex: int }                     // on the target Mass's Brep
  | { orientation: "N"|"NE"|...|"up"|"down" }
  | { role: "facade"|"roof"|"floor" }
  | { role: "facade", orientation: "N" }   // compound
```

The classifier resolves it at tool-call time. This is the same pattern Phase 1 uses for its `CameraShot` union in `capture_views`.

### 4.1 Descriptive (3, read)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `describe_massing` | Narrative + structured summary of the whole massing state. Agent's default "orient me" call. Reflects boolean composition, not part assembly. | `{levelOfDetail?: "brief"|"standard"|"detailed" = "standard"}` | `{narrative, masses: [{id, function, volume, footprintArea, height, faceCountByRole, adjacentMassIds}], massGroups: [...], compositionRelationships: [...], totals: {grossVolume, footprintArea, massCount}, siteContext: {...}}` |
| `describe_context` | Nearby Site elements within a distance of the building envelope. | `{distance: number, includeTopography?, includeContextBuildings?, includeStreets?}` | `{contextBuildings: [...], streets: [...], topography: {...}, propertyLine: {area, setbacksFromBuilding: {N,E,S,W}}}` |
| `find_element` | Natural-language element lookup. "The north face of the office mass" → one FaceId. Rules-based parser, LLM fallback on 0 matches. | `{query: string, expect?: "one"|"any" = "any"}` | `{matches: [{elementId, type, name, confidence}], truncated: bool}` |

`describe_massing`'s narrative changes shape from Rev 1: instead of "three volumes assembled" language, it describes the boolean/mass composition — "One 3-story office mass on the north half; a smaller 2-story retail mass boolean-unioned into its south face; a light well cut through the office mass at the center."

### 4.2 Mass catalog (3, read)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `list_masses` | Enumerate Masses. | `{functionFilter?: string, massGroupId?: string}` | `{masses: [{id, function, volume, footprintArea, height, bbox, centroid, adjacentMassIds}]}` |
| `list_mass_groups` | Enumerate MassGroups. | `{}` | `{groups: [{id, name, masses, combinedVolume, dominantFunction}]}` |
| `analyze_boolean_history` | If Rhino history is tracked on a Mass, walk the operations that produced its current form. | `{massId: string}` | `{historyAvailable: bool, operations: [{kind: "union"|"difference"|"intersection"|"push-pull"|"extrude"|..., timestamp?, inputs: [MassId|BrepDescription], resultId, notes}]}` |

`analyze_boolean_history` returns `historyAvailable: false` if Rhino's history didn't capture the operations (common — most architects work with history off). The agent should treat availability as opportunistic, not guaranteed.

### 4.3 Face and edge analysis (5, read)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `get_mass_faces` | Enumerate a Mass's faces with role classification. | `{massId: string, filterByRole?: Role, filterByOrientation?: Orientation}` | `{faces: [{id, faceIndex, orientation, roles, area, elevationRange, isPlanar, openingArea, wallWindowRatio?}]}` |
| `get_face` | Full detail for one face. | `{faceSelector: FaceSelector, massId?: string}` | `{faceId, massId, faceIndex, orientation, roles, area, centroid, normal, elevationRange, isPlanar, openings: [OpeningSummary], overhangs, recesses, boundingEdges: [EdgeSummary]}` |
| `get_mass_edges` | Enumerate a Mass's edges with role classification. | `{massId: string, filterByRole?: EdgeRole}` | `{edges: [{id, edgeIndex, role, length, startPoint, endPoint, adjacentFaces}]}` |
| `check_face_relationships` | Coplanar / adjacent / parallel-perpendicular relationships across a scope of faces. Useful for "does the office mass's north face align with the retail mass's north face?" | `{scope: "all"|{massIds: [...]}, tolerance?: number}` | `{coplanarGroups: [[FaceId, ...], ...], parallelPairs: [{a, b, offset}], perpendicularPairs: [{a, b}], flushAlignments: [{faces, notes}]}` |
| `find_openings_in_face` | All Openings on a specified Face. | `{faceSelector: FaceSelector, massId?: string}` | `{faceId, openings: [{id, type, width, height, sillHeight, area, centroidOnFace, depth, origin}], totalOpeningArea, wallWindowRatio}` |

`get_mass_faces` is the tool the agent uses to see the world in terms the architect uses. Face role and orientation are the two axes almost every question runs along.

### 4.4 Envelope, program, composition (5, read)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `check_wall_window_ratio` | WWR per Face or aggregate by orientation. Faces filtered to `role: "facade"`. | `{scope: "byOrientation"|"byFace"|"whole", massId?: string, includeOverhangsAsShading?: bool = false}` | `{results: [{key, area, openingArea, ratio, glazingByType: {Window, Storefront, CurtainWall}}], overallRatio}` |
| `get_roof_analysis` | Roof form breakdown. Faces filtered to `role: "roof"`. | `{massId?: string}` | `{roofFaces: [{id, massId, area, slopePercent, drainageDirection, isPlanar, adjacentEdges: [{edgeId, role: "parapet"|"ridge"|"eave"}]}], totalRoofArea, predominantForm: "flat"|"sloped"|"complex", ridgeLengths, eaveLengths}` |
| `get_program_allocation` | Program area breakdown by Mass function. | `{}` | `{byFunction: {Office: {totalVolume, footprintArea, percentOfTotal, massCount}, Residential: {...}, ...}, totalVolume}` |
| `check_massing_composition` | **Deterministic composition facts.** Proportions, symmetry, mass hierarchy, boolean composition. | `{}` | `{proportions: {overallBbox, aspectRatios, dominantAxis}, symmetry: {aboutX, aboutY}, massHierarchy: {ranked: [{id, volume, percentOfTotal}], primaryMassId, ratioPrimaryToSecondary}, booleanComposition: {unionCount, differenceCount, cutVolumeTotal, additiveVolumeTotal}, verticalRhythm: {inferredLevelCount, floorToFloorConsistency}}` |
| `get_level_info` | Level list + per-Level cross-section (Levels usually inferred, not drawn). | `{levelName?: string, massId?: string}` | `{levels: [{id, name, elevation, floorToFloor, floorPlates: [{massId, netArea}], totalFloorArea}]}` |

`check_massing_composition` gains a `booleanComposition` field in Rev 2 — the reviewer principle again: hand the reviewer numbers about how the form was assembled, not just its final proportions.

### 4.5 Constraints (1, read)

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `get_zoning_envelope` | Given zoning parameters, compute the allowable envelope and compare to current design. | `{maxHeight: number, setbacks: {N,E,S,W}, farMax?: number, propertyLineElementId?: string}` | `{allowedEnvelope: {bbox, footprintArea, heightLimit}, currentBuilding: {bbox, footprintArea, height, grossVolume, far?}, violations: [{type, side?, amount, ids}], complianceStatus: "compliant"|"violations"|"warnings"}` |

Unchanged from Rev 1 except the "current building" numbers come from `list_masses` + `MassGroup` aggregation instead of the Rev 1 Volume abstraction.

### 4.6 Massing operations — the writes (7, write)

This group is what promotes Rev 1's deferred write tools into Phase 1. **Mass modeling *is* the SD workflow.** The agent needs to make the same moves the architect makes.

Each of these is composed of Phase 1 raw writes + tagging inside one undo record. `RhinoMutationService` (Phase 1) still does the physical mutation; these tools are the semantic wrapper.

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `push_pull_face` | **The fundamental massing operation.** Move a Face along its normal by a distance. Positive extends the mass outward; negative pushes it inward. | `{massId: string, faceSelector: FaceSelector, distance: number, propagate?: "auto"|"none" = "auto"}` | `{resultMassId, deltaVolume, newBbox, affectedFaceIds, notes}` |
| `add_mass` | Additive — create a new Mass with a function. Primitive shapes (box/cylinder) via input; complex footprints via a curve id. | `{shape: "box"|"cylinder"|"prism-from-curve", location?, dimensions?, footprintCurveId?, height?, function: string, name?, unionWithExisting?: string[]}` | `{massId, rhinoObjectIds, bbox, volume, footprintArea}` |
| `subtract_mass` | Subtractive — boolean-difference a cutter Mass from a base Mass. | `{baseMassId: string, cutterMassId: string, deleteCutter?: bool = true}` | `{resultMassId, subtractedVolume, cutId?, openingId?, notes}` |
| `cut_opening` | Convenience wrapper: boolean-subtract a rectangular (or curve-defined) opening from a Face. Classifies the result as an Opening automatically. | `{massId: string, faceSelector: FaceSelector, openingType: "Window"|"Door"|"Storefront"|"CurtainWall"|"Louver", width: number, height: number, sillHeight: number, positionOnFace: {distanceFromLeftEdge: number}|{centroidOnFace: [u,v]}, depth?: number = null}` | `{openingId, massId, faceId, actualPosition, updatedFaceWWR}` |
| `slice_mass_at_elevation` | Slice a Mass horizontally at a given z; used for creating FloorPlates or splitting a Mass into stacked Masses. | `{massId: string, elevation: number, mode: "generate-floorplate"|"split-mass"}` | `{floorPlateIds?: [...], resultMassIds?: [...]}` |
| `extrude_face_outward` | Extrude a Face outward as an Overhang or bump-out. Distinct from `push_pull_face` because the extrusion creates a new Mass or Overhang element rather than moving the existing face plane. | `{massId: string, faceSelector: FaceSelector, distance: number, asOverhang?: bool = true}` | `{overhangId?: string, newMassId?: string, resultBbox}` |
| `fillet_edges` | Fillet a set of Edges to a radius. Corner treatment — chamfered or rounded corners are an SD-level move. | `{massId: string, edgeSelectors: [EdgeSelector], radius: number}` where `EdgeSelector = {edgeId}|{role: "outside-corner"}|{edgeIndex: int}` | `{resultMassId, affectedEdgeCount, notes}` |

Plus one small mutation for Entry promotion (Rev 1 had it as a first-class type; Rev 2 folds it into an Opening property):

| Name | Purpose | Inputs | Outputs |
|---|---|---|---|
| `promote_opening_to_entry` | Mark an existing Opening as an Entry. Mutates the Opening's tag, does not create a new element. | `{openingId, entryType: "Main"|"Secondary"|"Service"|"Emergency"}` | `{openingId, isEntry: true, entryType}` |

**Total: 17 read + 7 write + 1 mutation = 24 semantic tools.**

### 4.7 When to use / when not to

Rule of thumb baked into the system prompt for the semantic tools:

- Use semantic queries **before** any raw geometry query when the question is about the design as massing. "What's the WWR?" → `check_wall_window_ratio`, not `list_objects` + math.
- Use semantic writes (`push_pull_face`, `cut_opening`, `add_mass`, `subtract_mass`) **as the default** for design moves. They preserve semantic labels; raw `move_face` / `boolean_difference` don't.
- Fall back to raw Phase 1 tools when the question is object-specific without architectural framing ("move this specific object by 5ft") or when semantic tools don't apply.
- Use `capture_views` **with** semantic queries, not instead of. Semantic queries answer *what*, screenshots answer *how does it feel*.
- When a semantic query returns `classifiedBy: "geometry-inference"` on any element, prefer to confirm with the user before a destructive move. The classifier is a guess; a wrong guess plus a bold move is how bad things happen.

---

## 5. Classification system

The single hardest problem in this plan. Rev 2's classifier has two distinct workloads because faces and edges are labels applied to Mass geometry at query time, while Masses (and Openings, Overhangs, Recesses, Cuts) are objects the classifier identifies from the doc.

### 5.1 The two workloads

- **Object-level classification.** Given a `RhinoDoc`, identify Masses, Openings drawn as separate objects, Overhangs drawn as separate objects, Site elements, and any other explicit tagged elements. Runs on cache invalidation. Output: `SemanticView` (Mass registry + object-derived elements).
- **Geometry-level classification.** Given a Mass, label its Brep faces with roles and orientations, its Brep edges with roles, its inner trim loops as Openings, its boolean-history-inferred Cuts. Runs on-demand, per Mass, when a query needs it. Output: a `MassGeometryView` cached per Mass and invalidated when the underlying Brep changes.

The split matters because face/edge labeling is fast per Mass but expensive across all Masses in a large doc. On-demand keeps the object-level cache small and the geometry-level cost proportional to what the agent asks about.

### 5.2 Priority order (the resolution rule)

For any Rhino object the classifier considers, and for any face/edge label it computes:

1. **Explicit user-data tag** on the object or face (`SetUserString` under key `RhinoClaude:Element:*` for objects; `RhinoClaude:FaceRole:<faceIndex>` for face role overrides on the parent Mass). Trump card.
2. **Learned convention** in the document (`RhinoDoc.Strings` entry `RhinoClaude:LayerConvention:v1` written by `LearnNamingConvention`).
3. **Shipped canonical convention** (§5.3).
4. **Geometry inference** — object-level: closed brep on non-context layer → Mass candidate. Face-level: normal direction + elevation → role. Edge-level: adjacent face roles → edge role. Element carries `classifiedBy: "geometry-inference"`.

If all four whiff at object level, the object stays *unclassified*. Face labels always resolve to at least `unclassified` — every Brep face gets a Face entry, just possibly a low-information one.

### 5.3 Shipped canonical convention

Concrete, opinionated set of layer names shipped with the plugin. Design principle: **`CATEGORY_Subcategory`**, PascalCase subcategory. Consistent, greppable, easy to read in Rhino's layer panel.

```
Masses
    MASS_Office
    MASS_Residential
    MASS_Retail
    MASS_Institutional
    MASS_Common
    MASS_Other

Levels (optional; usually inferred)
    LEVEL_01_+0ft
    LEVEL_02_+12ft
    LEVEL_Roof_+36ft

Openings (only used when the architect draws separate objects rather than boolean-cutting)
    OPENING_Window
    OPENING_Door
    OPENING_Door_Entry
    OPENING_Storefront
    OPENING_Storefront_Entry
    OPENING_Curtain-Wall
    OPENING_Louver

Overhangs / projections
    OVERHANG_Canopy
    OVERHANG_Eave
    OVERHANG_Brise-Soleil
    OVERHANG_Balcony

Site
    SITE_Property-Line
    SITE_Topography
    SITE_Context-Building
    SITE_Street
    SITE_Curb
    SITE_Utility

(Optional; usually derived)
    FLOOR_L01
    FLOOR_L02
```

**Rationale:**

- **`MASS_*` is the primary organization.** Whatever else is on the file, if the Masses are on `MASS_*` layers, everything downstream works. Face/edge labels are computed from geometry regardless of layer.
- **`OPENING_*` layers are secondary.** The classifier's primary opening detection path is "hole in a Mass face." Openings drawn on `OPENING_*` layers are supported for the case where an architect chose to represent them that way (say, on early SD before booleans), but the plan does not require them.
- **No `SHELL_Facade` / `SHELL_Roof` / `SHELL_Wall` layers.** Rev 1 had these; Rev 2 drops them. Facades and roofs are not things architects draw as separate objects. If a face has a specific role that geometry doesn't reveal (a curved-wall facade that ought to be one facade but is two Brep faces), the `RhinoClaude:FaceRole:<faceIndex>` tag on the parent Mass handles it.
- **Elevation encoded in Level layer names** when Levels are drawn. Redundant with the object itself, but explicit and human-readable.
- **Enums match Phase 1's `TagSchema.ElementType` where they overlap.** Mass `function` values (Office / Residential / Retail / etc.) are the same strings as Phase 1 tags.

Shipped as `LAYER_CONVENTIONS.md` in the plugin distribution and referenced from the sidebar's onboarding tooltip on first-use (§8).

### 5.4 Explicit tagging (the override path)

Two commands — small, direct, mirror the Phase 1 tagging command style.

- **`ClaudeSetElement`** — takes a selection and a target element type from a dropdown. Writes `RhinoClaude:Element:{Type}` to each object's `UserStrings`. For Masses, prompts for `function`. For Openings, prompts for `openingType`. New in Rev 2: for a Mass, also offers a "Set face role" sub-flow that lets the user pick a face by clicking and assign it a role (writes `RhinoClaude:FaceRole:<faceIndex>` on the Mass).
- **`ClaudeClearElement`** — removes semantic element tags from selection.

### 5.5 Geometry inference details

**Object-level.** Rules per type in §3's detection heuristics. General principles: never confidently mislabel, cheap first, deterministic tiebreakers.

**Face-level.** For each Brep face on a Mass:

- Compute outward normal at face centroid.
- **Orientation:** vertical faces (|Z_normal| < 0.3) get compass-sector orientation from the normal's XY projection; up-facing (Z_normal >= 0.3) → `"up"`; down-facing → `"down"`; anything else → `"other"` (used for curved or steeply-tilted faces that don't fit either bucket).
- **Role:**
  - Vertical + exterior (not coincident with another Mass's face) → `"facade"`.
  - Up-facing + exterior → `"roof"`.
  - Down-facing + at the Mass's bottom → `"floor"`.
  - Coincident with another Mass's face → `"party-wall"`.
  - Interior to a Cut (bounding a subtracted void) → `"interior"`.
  - Else `"unclassified"`.
- Multiple roles allowed. A face at the top of a facade wall that has a slight upward tilt might get `["facade", "roof"]` — the agent decides.

**Edge-level.** For each Brep edge on a Mass, use adjacent face roles (§3.3). Cheap post-processing after face labeling.

**Opening detection from holes.** For each Face, iterate inner trim loops in the Brep face. Each loop above ~1 ft² becomes an Opening. Subtype inferred from geometry (§3.4's fallback). Origin: `"subtracted"` (since it's a hole in the face, not a separate object).

**Cut detection.** Two paths:
- Boolean history if available: `boolean_difference` operations produce Cuts if the subtracted volume is above ~200 ft³.
- Brep topology: if the Mass Brep is `IsSolid` and contains multiple inner shells (interior voids), each void becomes a Cut.

### 5.6 `LearnNamingConvention` command

Unchanged from Rev 1 conceptually.

**Flow:**
1. User runs `ClaudeLearnNamingConvention` in a `.3dm` with representative layer names.
2. Command inventories every layer in the doc.
3. Command sends a *one-shot* API call to Claude with (a) the shipped canonical vocabulary from §5.3, (b) the list of layers in the doc, and (c) instructions to map each layer to its best-match element type (Mass function, Opening subtype, Overhang subtype, Site subtype) or `null` for "not architectural."
4. Command surfaces a dialog with the proposed mapping. User confirms with edits.
5. Mapping saved to `RhinoDoc.Strings` under `RhinoClaude:LayerConvention:v1`. Priority-order step 2 uses it thereafter.
6. Option to save mapping to plugin-level settings so it applies to all docs the user opens.

Rev 2 addition: the learn dialog also asks about a **firm-standard floor-to-floor default** (used by inferred Levels when Levels aren't drawn). One field, one number, saved with the mapping.

### 5.7 Handling messy input

Same principle as Rev 1. Unclassified objects don't crash the classifier — they don't appear in semantic query results. `describe_massing` narrative reports the unclassified count so the agent can decide. `find_element` can propose classifications for user confirmation via `ClaudeSetElement`. Progressive cleanup — the plugin never demands a fully cleaned doc up front.

Rev 2 adds one graceful-degradation case: a Mass with a face the classifier can't confidently label (rare — most faces resolve cleanly). The face carries `roles: ["unclassified"]` and `notes: "…"` explaining why. `check_wall_window_ratio` skips unclassified faces and reports how much area was skipped.

---

## 6. Services and architecture

Same architectural style as Phase 1 §2.2. New services under `RhinoClaude/Services/Semantic/`. Semantic tools live alongside raw tools in the same `ToolRegistry` — the agent doesn't know or care which tier a tool belongs to.

### 6.1 New services

| Service | Owns | Threading |
|---|---|---|
| `SemanticClassifier` | Object-level classification: `Classify(doc) → SemanticView`. Implements the four-step resolution rule. | UI-thread only. |
| `MassGeometryAnalyzer` | Geometry-level classification: `Analyze(mass) → MassGeometryView` with face roles, edge roles, hole-derived Openings, boolean-history-derived Cuts. | UI-thread only. |
| `ElementRegistry` | Cached `SemanticView` + per-Mass `MassGeometryView` cache. Invalidated on `RhinoDoc` events; per-Mass views invalidated when the Mass's Brep changes. | Cached data is immutable snapshots — safe read from any thread; refreshes happen on UI thread. |
| `SemanticQueryService` | Implements every semantic read tool. Reads from `ElementRegistry`. Serialization + `{success, error, …}` envelope. | UI-thread. |
| `SemanticMutationService` | Implements every semantic write tool. Composes Phase 1 `RhinoMutationService` calls + tagging inside one undo record per tool call. | UI-thread. |
| `BooleanHistoryReader` | If Rhino history is enabled on a Mass, walk it into a normalized operation list. Returns empty when history is off. | UI-thread. |
| `FaceSelectorResolver` | Given a `FaceSelector` union value + a Mass, return a concrete face index. Used by every write tool. | UI-thread. |
| `LayerConventionStore` | Loads and persists learned + canonical conventions + firm floor-to-floor default. | Any thread; final write on UI thread. |
| `SemanticClassifierPrompt` | Static prompt strings for `LearnNamingConvention`'s one-shot call. | None. |

### 6.2 The two-tier cache

- **`SemanticView`** — object-level. Small. Rebuilt on any doc-object event (add/delete/replace) and on layer table changes. Cost budget: <150 ms for a mid-scale SD model.
- **`MassGeometryView` per Mass** — face labels, edge labels, hole-openings, boolean-history-derived Cuts. Bigger, but only computed for Masses the agent asks about. Invalidated when the Mass's Brep changes (by watching `ReplaceRhinoObject` on the Mass's Rhino object id). Cost budget: <50 ms per Mass for typical face counts (<30 faces).

Total scaling behavior: querying "all faces of all Masses" on a doc with 20 Masses costs one object-level rebuild plus 20 geometry-level analyses, ~1 second worst case — still acceptable for a single tool call. Querying "the north face of the office mass" costs one object-level check + one geometry-level analysis — well under 200 ms.

Instrumentation: `%APPDATA%/RhinoClaude/classifier_timing.jsonl` with both object-level and per-Mass geometry-level durations. Same log location pattern as Phase 1.

### 6.3 Threading and the tool-use loop

`SemanticQueryService` and `SemanticMutationService` follow Phase 1's `RhinoQueryService` / `RhinoMutationService` patterns exactly: `ToolDispatcher.InvokeOnUiThread(...)` wraps every call.

Fallback plans if the classifier goes over budget:
- **First fallback:** background pre-compute the object-level `SemanticView` on `RhinoDoc.DocumentOpened` and on idle.
- **Second fallback:** incremental invalidation of `SemanticView` (track changed objects, reclassify neighborhoods).
- **Third fallback:** background pre-compute per-Mass `MassGeometryView` for the largest N Masses.

All additive to the phase-1 design.

### 6.4 Integration with existing plugin state

Two Phase 1 services live near this work:

- **`SessionSnapshotService`** records undo record ids for session revert. Semantic mutations open undo records via `RhinoMutationService`; they're already recorded. Zero integration surface change.
- **`AgentConversationStore`** persists conversation messages to `RhinoDoc.Strings`. `LayerConventionStore` uses distinct key namespace (`RhinoClaude:LayerConvention:v1`). No collision.

`RhinoMutationService` (Phase 1) does the physical mutation for every semantic write. `SemanticMutationService` is a thin layer that (a) resolves selectors, (b) invokes one or more `RhinoMutationService` operations, (c) tags the results, all inside one undo record. This keeps undo/session-revert consistent and prevents parallel mutation pipelines.

### 6.5 Where semantic tools sit in the loop

The agent's system prompt (updated as part of this plan) tells it:

> You have two families of tools: raw geometry tools (create/modify/query raw Rhino objects) and semantic tools (query and reason about massing and its faces, edges, openings). Architects working in Rhino model buildings as solid masses with push/pull face operations and boolean unions/differences — the semantic tools mirror that workflow. Prefer semantic tools when the question or move is about massing; fall back to raw tools when semantic tools don't apply or return unclassified results. The classifier can be wrong — if a semantic result contradicts what you see in a screenshot, believe the screenshot.

The last sentence is the reviewer principle inverted for the executor: **semantic tools give the agent facts; screenshots give it truth; when they disagree, the pixels win.**

---

## 7. (Reserved)

Rev 1's §7 was "semantic write tools as natural Phase 2." Rev 2 moved those into the core catalog (§4.6). Section number retained to keep §8/§9/etc. numbering consistent with Rev 1 outline; no content here.

---

## 8. Naming convention documentation

Ship as `LAYER_CONVENTIONS.md` in the plugin distribution. Content: the full canonical convention from §5.3, rationale for the format, examples applied to a hypothetical mixed-use project, `LearnNamingConvention` instructions.

Rev 2 addition: an explicit note in `LAYER_CONVENTIONS.md` explaining that **`MASS_*` layers are the important ones**. Openings, faces, edges are usually derived from Mass geometry; the layer standard is not something the architect has to fully embrace for the plugin to work. `MASS_Office` on the office mass is enough for `describe_massing` to be useful.

**Surfaces where the convention doc appears:**

- Plugin repo root, referenced from main README.
- Sidebar onboarding tooltip on first `ClaudeChat` use: "New to semantic queries? See LAYER_CONVENTIONS.md — at minimum, put your masses on `MASS_*` layers. Or run `ClaudeLearnNamingConvention` to teach Claude your firm's convention."
- Error path: when `describe_massing` returns 0 Masses, the response's `narrative` includes: "No Masses found. If your layers use a different naming convention, run `ClaudeLearnNamingConvention` or use `ClaudeSetElement` to tag your solid Breps as Masses."

**Firm-configurability.** `LearnNamingConvention` lets any firm keep their existing standard, saved at plugin level (all docs the user opens), doc level (baked into the `.3dm`), or both.

---

## 9. Migration plan

Phased, each phase shippable. Total estimate: **~12 ideal days** (Rev 1 was ~10; the extra 2 days go to the massing operation tools moving into core). Vocabulary and classification remain the hard parts.

### Phase A — Canonical vocabulary + convention doc (1 day)
- Author `LAYER_CONVENTIONS.md` with the Rev 2 mass-first framing.
- Encode the shipped canonical convention as a static resource.
- Plugin settings entries for firm-level convention + floor-to-floor default.
- **Deliverable:** the vocabulary is a written thing, reviewable independent of any code.

### Phase B — Object-level classifier + ElementRegistry (1.5 days)
- Implement `SemanticClassifier` (object-level) with the four-step resolution rule.
- Detection heuristics for Mass, Opening-as-object, Overhang-as-object, MassGroup, Site.
- `ElementRegistry` object-level cache + doc-event invalidation.
- `LayerConventionStore` for canonical/learned/per-doc lookup.
- **Test:** load Bryan's test 3dms — classifier produces sensible Mass counts. Small hand-built 3dm with all element types explicit and confirm classification.

### Phase C — MassGeometryAnalyzer (face + edge labeling + hole-openings) (2 days)
- Implement `MassGeometryAnalyzer`: face role/orientation labeling, edge role labeling, inner-loop opening detection.
- `BooleanHistoryReader` when Rhino history is available; graceful fallback when it isn't.
- Per-Mass geometry cache with Brep-change invalidation.
- `FaceSelectorResolver`.
- **Test:** build a box, boolean-cut a window hole in the north face, run analyzer — recognized as one Mass with a north Facade face containing one Window Opening.

### Phase D — Descriptive + structural read tools (2 days)
- Implement `SemanticQueryService`.
- Register: `describe_massing`, `describe_context`, `find_element`, `list_masses`, `list_mass_groups`, `analyze_boolean_history`, `get_mass_faces`, `get_face`, `get_mass_edges`, `check_face_relationships`, `find_openings_in_face`.
- Update the agent's system prompt with the Rev 2 mass/face framing + the "which channel to use when" rule.
- **Test:** `ClaudeChat` interaction — "what's on this site?" returns a coherent narrative with masses and their faces.

### Phase E — Analytical read tools (1.5 days)
- Register `check_wall_window_ratio`, `get_roof_analysis`, `get_program_allocation`, `check_massing_composition` (with the `booleanComposition` field), `get_level_info`.
- Wire `check_massing_composition`'s output into the reviewer prompt for `signal_done` self-review (composition facts feed the reviewer, same as Phase 1's deterministic checks).
- **Test:** "analyze wall-window ratio by orientation" runs end-to-end.

### Phase F — Zoning envelope (1 day)
- `get_zoning_envelope`.
- Test with property line polygon and setback inputs; validate against a manual calculation.

### Phase G — Massing operation write tools (2.5 days)
- Implement `SemanticMutationService`.
- Register `push_pull_face`, `add_mass`, `subtract_mass`, `cut_opening`, `slice_mass_at_elevation`, `extrude_face_outward`, `fillet_edges`, `promote_opening_to_entry`.
- Each composed of Phase 1 `RhinoMutationService` calls + tagging inside one undo record.
- **Test:** "make an office mass 30ft × 60ft × 3 floors on the east side of the site, then push its top face up 6ft, then cut a storefront on the south face" — one prompt, three semantic operations, resulting Mass appears in the next `describe_massing`.

### Phase H — Explicit tagging commands + `LearnNamingConvention` (1 day)
- `ClaudeSetElement` (with the face-role sub-flow), `ClaudeClearElement`.
- `ClaudeLearnNamingConvention` — dialog, one-shot API call, mapping save, floor-to-floor default.
- **Test:** load a doc with non-canonical layer names, run `LearnNamingConvention`, confirm classifier picks up the learned mapping on the next `describe_massing` call.

### Phase I — Instrumentation and hardening (0.5 days)
- `classifier_timing.jsonl` logging with object-level and per-Mass durations.
- Graceful-degradation narrative for unclassified objects/faces.
- Doc pass; update README with `LAYER_CONVENTIONS.md` reference and the mass-first framing.

**Rough total: ~12 ideal days.** Compared to Rev 1's ~10, the extra 2 days are entirely the massing operation tools (Phase G) moving into core. Compared to Phase 1's ~22 days, still much smaller because the plumbing is already in place.

### 9.1 What runs alongside vs. rip-and-replace

- **Alongside (no risk):** every new service (`SemanticClassifier`, `MassGeometryAnalyzer`, `ElementRegistry`, `SemanticQueryService`, `SemanticMutationService`, `BooleanHistoryReader`, `FaceSelectorResolver`, `LayerConventionStore`), the tagging + learn commands, the canonical convention static resource.
- **No rip-and-replace.** Nothing in Phase 1 is deleted or restructured.
- **Small additive edits:** agent system prompt gains the Rev 2 framing paragraph; `describe_massing`'s narrative composition uses composition facts. Neither is a breaking change.

---

## 10. Risks and open questions

### 10.1 Risks

1. **Loose geometry that doesn't fit any classification.** Weird one-off Breps, imported meshes with no context, half-finished sketches. Mitigation: unclassified is a valid state; `describe_massing` surfaces "n unclassified objects" so the agent knows to hedge or ask.
2. **A Mass that's actually multiple architectural volumes.** A mixed-use tower drawn as one Brep is one Mass of what function? Mitigation: `function: "Other"` + a note in the narrative that the Mass looks composite. Encourage users to split composite Breps if they want per-function reasoning; do not silently split them. Or use a MassGroup after splitting.
3. **Face labeling is wrong on a curved facade.** A cylindrical Mass has one curved Brep face that's "the whole exterior" — classifier calls it one Face with `orientation: "other"`. Query for "the north face" returns nothing useful. Mitigation: the explicit `RhinoClaude:FaceRole:<faceIndex>` tag on the parent Mass lets the user override; `ClaudeSetElement`'s face-role sub-flow provides UI for it. Also: `check_face_relationships` still works because it's about geometry, not orientation labels.
4. **Boolean history often isn't tracked.** Most architects work with Rhino history off. `analyze_boolean_history` returns `historyAvailable: false` most of the time. Mitigation: don't rely on history in any tool that must always work. The Brep topology paths (inner shells for Cuts, inner trim loops for Openings) work regardless of history.
5. **`push_pull_face` on a non-planar face.** Can't push/pull a curved face along a single normal cleanly. Mitigation: check face planarity in `SemanticMutationService`; return a `notes: "face is non-planar; use the Roslyn escape hatch for TransformControlPoints"` result. Don't fail; redirect.
6. **Layer convention drift.** User renames a layer mid-session — cache goes stale. Mitigation: `LayerTableEvent` invalidates the cache.
7. **Cache invalidation storms during bulk edits.** User pastes 500 objects. Mitigation: debounce with a 100ms coalesce window.
8. **Classifier performance at scale.** Object-level scales fine; per-Mass geometry analysis at 20+ Masses could cross 1 second on "all faces of all Masses" queries. Mitigation: instrument from day one; fallbacks designed (§6.3). In practice most queries scope to one or two Masses.
9. **`get_zoning_envelope` complexity creep.** Users will ask for FAR + open space + parking + district-specific rules. Mitigation: ship minimum — height, setbacks, optional FAR — refuse feature growth.
10. **Tag namespace collisions.** Phase 1 uses `RC:` on user attributes; this plan uses `RhinoClaude:Element:*` and `RhinoClaude:FaceRole:*` on user strings. Distinct prefixes, version tags on mapping JSON.
11. **Openings-as-drawn-objects vs. openings-as-holes.** A user draws an `OPENING_Window` object coincident with the outside of a Mass face; classifier picks it up as an Opening on that face. Now suppose the user *also* boolean-cuts the hole. Two Openings for the same location. Mitigation: dedup by centroid proximity when both are detected on the same face; prefer the hole-based one and warn.
12. **`AgentConversationStore` growth from richer semantic tool traffic.** Semantic query results are meatier than raw. Mitigation: Phase 1 already applies compaction after `signal_done: ship`.
13. **`push_pull_face` semantics when propagation is ambiguous.** Pushing one face outward on a Mass with connected neighboring faces — does the neighbor deform to follow, or does the pushed face detach? Mitigation: `propagate: "auto"` uses Brep sub-object move (RhinoCommon's `TransformComponent` — connected faces follow); `propagate: "none"` uses face-plane translation only (creates a new face, may need Brep repair). Default `auto` matches architect intent 90% of the time; agent picks the other 10% consciously.

### 10.2 Open questions worth flagging

1. **Should MassGroups support nesting?** A campus could have a "main building" MassGroup that contains an "office wing" MassGroup and a "residential wing" MassGroup. **Decision baked in:** no nesting in Phase 1 of this rollout. Minimum viable. Revisit if the log shows the agent trying to express it.
2. **How pluggable should the vocabulary be?** Layer naming is pluggable via `LearnNamingConvention`. Element types themselves (Mass, Face, Opening, etc.) are hard-coded. Should firms be able to add types? **Decision baked in:** no in Phase 1 of this rollout. Extending the type set means extending queries, prompts, reviewer behavior — non-trivial. Revisit as Phase 3.
3. **Does the classifier need to work on Rhino 7 (`net48`) AND Rhino 8 (`net7.0`)?** Yes, same as Phase 1. `RhinoDoc.Strings`, `UserString`, `LayerTable`, `Brep`, `Brep.Faces[i].TransformComponent` APIs are all cross-version. Rhino history API is also cross-version. No expected surprises.
4. **Should `find_element` use rules-based parsing or LLM?** Rules-based parses "north face of the office mass" cleanly — orientation words + role words + function words. **Decision baked in:** rules-based parser; LLM fallback only on 0 matches.
5. **What does `describe_massing`'s narrative field cap at?** **Decision baked in:** ~600-token target, hard cap at 1500. `levelOfDetail: "brief"` capped tighter.
6. **How does `get_zoning_envelope` handle multiple property lines?** Multi-property-line docs are rare at SD but they exist. **Decision baked in:** if multiple property-line curves exist, the tool returns an error asking the caller to specify `propertyLineElementId`. Never silently picks one.
7. **Should there be a `merge_masses` tool that boolean-unions two Masses into one?** It's a natural pair to `subtract_mass`. **Decision baked in:** yes, but folded into `add_mass` via the `unionWithExisting` parameter — creating a Mass and immediately unioning it with an existing one is one operation with one undo record. If the log shows the agent wanting to union two already-existing Masses without creating a new one, we'll add a `union_masses` tool.
8. **How does the classifier treat a Mass whose boolean history has an "unusual" root — e.g. imported from a `.step` file?** **Decision baked in:** history simply reads as "history-unavailable"; the classifier falls back to Brep-topology paths for Openings and Cuts. Same as history-off Masses.
9. **Should `push_pull_face` accept "distance to a target plane" as an alternative to a scalar distance?** ("Push the north face until it's flush with the retail mass's north face.") **Decision baked in:** yes, as a v2 addition to the tool's input schema — added after v1 lands. First version is scalar-distance only to keep the tool small.
10. **How does the agent select edges for `fillet_edges` without listing every edge id?** `EdgeSelector` supports `{role: "outside-corner"}` — "fillet all outside corners" is one selector value. That plus `{edgeId}` for specific edges covers most cases. **Decision baked in:** ship those two; add `{on_face: FaceSelector}` selectors later if the log demands it.

Any of these Bryan wants to overturn before implementation gets a Rev 3.

---

## 11. What this unblocks

Concrete tasks the agent can do after this plan that it can't do (well) after Phase 1 alone. Each maps to specific tools introduced above.

1. **"Make the north face of the office mass more open with a larger storefront near the entry."**
   `find_element("north face of office mass")` → `find_openings_in_face` → identify openings → `cut_opening(mass, face, "Storefront", …)` near the Entry → `check_wall_window_ratio` to confirm the increase.

2. **"Push the top face of the office mass up 6 feet and check zoning compliance."**
   `find_element("top face of office mass")` → `push_pull_face(massId, {role: "roof"}, 6)` → `get_zoning_envelope({maxHeight, setbacks, farMax})` → report compliance delta.

3. **"Suggest three roof form variations that keep the total volume similar."**
   `get_roof_analysis` for baseline → `list_masses` for total volume → for each variation: `push_pull_face` on a roof-role face → `capture_views` → semantic queries to confirm volume preservation → narrate.

4. **"Analyze wall-window ratio by orientation and suggest adjustments for daylighting."**
   `check_wall_window_ratio({scope: "byOrientation"})` → compare N/S/E/W ratios → identify laggards → `get_mass_faces({filterByOrientation})` for specific faces → propose `cut_opening` moves → `capture_views` before/after.

5. **"Cut a light well through the office mass at the center, 15ft × 15ft."**
   `find_element("office mass")` → `add_mass(box, 15×15×fullHeight, "Other")` at the centroid → `subtract_mass(officeMassId, newBoxId)` → classifier detects the result as a Cut → confirm via `describe_massing`.

6. **"Boolean-union the office mass and the retail mass into a single form."**
   `list_masses` → pick both → in one turn: `add_mass(shape:"prism-from-curve", … , unionWithExisting:[officeId, retailId])` OR future `union_masses` per open question #7 → single resulting Mass.

7. **"The building feels too squat. Show me the massing hierarchy and boolean composition."**
   `check_massing_composition()` → aspect ratios, primary/secondary Mass ratio, `booleanComposition` counts → `capture_views(iso + front)` → discuss what "too squat" means in numbers.

8. **"Add a canopy over the main entry."**
   `find_element("main entry")` → its `facadeId` → `extrude_face_outward(mass, {faceId}, 8)` sized to the entry area OR `add_mass(box, 12×8×1)` positioned above the entry → semantic classifier tags the result as an Overhang.

9. **"How much of the ground floor is Core vs. rentable?"**
   `get_level_info({levelName: "Level 01"})` → per-Mass floor plate + adjacent core-Mass intersections → math → report.

10. **"Rotate the whole building 15° to align with the site's dominant axis."**
    `describe_context` for site → identify dominant axis → `list_masses` for all Mass ids → Phase 1 `rotate_objects` with the site-derived angle. Semantic layer reasons about *why*, raw layer executes.

11. **"The east and west faces have wildly different WWR — what's going on?"**
    `check_wall_window_ratio({scope: "byFace", massId})` → compare → describe discrepancy → `capture_views` both faces → offer to normalize with `cut_opening` on the deficient side.

12. **"Does the roof drain toward the parking lot or away from it?"**
    `get_roof_analysis` → drainage direction → `describe_context({includeStreets: true, includeCurbs: true})` for parking bearing → dot product → report.

13. **"Give me a schematic report of the design so far — mass composition, envelope stats, roof form, entries."**
    `describe_massing({levelOfDetail: "detailed"})` + `get_program_allocation` + `check_wall_window_ratio` + `get_roof_analysis` + `list_masses` — one turn, coherent SD report.

14. **"Fillet all outside corners of the office mass at 2 feet."**
    `find_element("office mass")` → `fillet_edges(massId, [{role: "outside-corner"}], 2)`. One tool call.

15. **"Recess the ground-floor south face inward by 8 feet for a covered entry, then add a canopy above it."**
    `push_pull_face(officeMass, {orientation: "S", elevationRange: [0, 12]}, -8)` — the negative distance creates a Recess → classifier detects it → `extrude_face_outward` above the recess for a canopy → Overhang.

Every one of these tasks fails or costs 10+ raw-tool round-trips in Phase 1 alone. Every one is one to three semantic tool calls in this plan. That gap is the plan's value.

---

## 12. What Bryan is greenlighting

By approving this plan, you're greenlighting:

1. A **mass-first vocabulary**: Mass (solid Brep with function), Face and Edge as labels-on-mass-geometry, Opening as hole-in-face, Overhang/Recess/Cut as boolean/push-pull-derived features, MassGroup and Composition for how masses relate. Rev 1's assembly-of-parts framing is replaced.
2. **~24 semantic tools** (17 read + 7 write) living alongside Phase 1's raw geometry tools. Massing operations (`push_pull_face`, `add_mass`, `subtract_mass`, `cut_opening`, `slice_mass_at_elevation`, `extrude_face_outward`, `fillet_edges`) are core, not deferred.
3. A **two-tier classifier**: object-level (`SemanticClassifier`) + per-Mass geometry-level (`MassGeometryAnalyzer`) with independent cache tiers. Face and edge labels computed at query time, not stored as objects.
4. The **four-step classifier resolution rule**: explicit user-data tag → learned convention → shipped canonical → geometry inference. Every result carries `classifiedBy`.
5. The **shipped canonical layer convention** in §5.3, mass-first — `MASS_*` is the important prefix. `LAYER_CONVENTIONS.md` in the plugin distribution, referenced from the sidebar's onboarding tooltip.
6. `ClaudeSetElement` (with face-role sub-flow), `ClaudeClearElement`, and `ClaudeLearnNamingConvention` for explicit tagging and firm-standard adoption.
7. **Element types are hard-coded, layer conventions are pluggable.** Firms customize naming, not taxonomy.
8. **`ElementRegistry` two-tier cache** with doc-event invalidation. Object-level budget: <150 ms mid-scale. Per-Mass budget: <50 ms typical.
9. **Boolean history read** when available; graceful fallback to Brep topology when it isn't. `analyze_boolean_history` tool exposes the operations to the agent.
10. A **~12 ideal-day** phased rollout, each phase shippable in isolation.
11. **The reviewer principle applied to executor**: semantic tools give the agent facts; screenshots give it truth; when they disagree, the pixels win. Baked into the system prompt.
12. **Explicit grounding in how architects actually work in Rhino** (§1.5): solid modeling with push/pull and boolean operations, not part composition. Every design choice in the plan flows from this.
