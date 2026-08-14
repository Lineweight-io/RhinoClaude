# RhinoClaude — Layer Conventions

**The one thing that matters: put your solid masses on `MASS_*` layers.**

Everything else in this document is optional. Faces, edges, openings, roofs, and corners are
*derived from the geometry of your masses* — the plugin reads them off the Brep, not off a layer
name. `MASS_Office` on the office mass is enough for `describe_massing` to be useful.

---

## 1. Why there is a convention at all

RhinoClaude's semantic layer looks at a `.3dm` and tries to answer architect questions —
"how tall is the office mass", "what's the wall-window ratio on the north face", "does the roof
drain toward the street". To do that it has to know which solid is a building mass and which is a
context building, a site curve, or a stray sketch.

It figures that out with a four-step rule, in strict priority order:

1. **An explicit tag on the object** (`ClaudeSetElement`). Trump card — always wins.
2. **Your firm's learned convention** (`ClaudeLearnNamingConvention`). Whatever layer names you
   already use.
3. **The shipped canonical convention** — the `MASS_*` / `OPENING_*` / `SITE_*` names below.
4. **Geometry inference** — a closed Brep, on a layer that isn't site or opening, above a small
   volume threshold, reads as a Mass.

Step 4 always runs, so the plugin does something sensible on a file that follows no convention at
all. But results classified that way are flagged `classifiedBy: "geometry-inference"`, and the
agent is told to hedge on them and to confirm before a destructive move. Steps 1–3 are how you
turn a guess into a fact.

---

## 2. How architects actually work in Rhino (and why the convention is small)

Rhino is a solid modeler. Schematic-design massing in Rhino is:

- start with primitive solids — a box for the main mass, another for a wing, a cylinder for a rotunda;
- **push and pull faces** to refine proportions;
- **boolean-union** masses that read as one form;
- **boolean-difference** to cut — a light well, a recessed entry, a window opening;
- extrude an edge for an overhang, slice horizontally for a floor plate;
- occasionally place a context object.

There are no wall families and no window schedules. **Openings exist because someone subtracted a
smaller solid from a bigger one. Facades exist because a mass has a face that points sideways.
Roofs exist because a mass has a face that points up.**

So the convention only names the things you actually *draw as objects*: masses, site context, and
the occasional opening or overhang you chose to model as a separate object. It deliberately does
**not** have `SHELL_Facade`, `SHELL_Roof`, or `WALL_*` layers, because you don't draw those.

---

## 3. The canonical convention

Format: **`CATEGORY_Subcategory`**, PascalCase subcategory. Consistent, greppable, readable in
Rhino's layer panel. Matching is case-insensitive, and nested layers inherit — `Building::MASS_Office`
works, and any child layer of `MASS_Office` is treated as office massing unless it names its own
category.

### Masses — the important ones

```
MASS_Office
MASS_Residential
MASS_Retail
MASS_Institutional
MASS_Common
MASS_Other
```

A Mass is a solid Brep. Its *function* (Office / Residential / …) is a property of the mass, not a
different kind of thing — the layer is just how you say it. These enum values are the same strings
phase 1's `RC:` tags use, so a doc tagged with either speaks the same vocabulary.

### Openings — only when you draw them as separate objects

```
OPENING_Window
OPENING_Door
OPENING_Door_Entry
OPENING_Storefront
OPENING_Storefront_Entry
OPENING_Curtain-Wall
OPENING_Louver
```

The plugin's *primary* opening path is "hole in a mass face" — it walks each mass's Brep faces and
treats an inner trim loop above ~1 ft² as an opening, whatever layer anything is on. These layers
exist for the case where you represented an opening as a distinct planar object sitting on a face,
which is common in early SD before the booleans happen.

The `_Entry` suffix marks an opening as the building's entry. You can also promote one later with
the `promote_opening_to_entry` tool or `ClaudeSetElement`.

> If you draw an `OPENING_Window` object **and** boolean-cut the same hole, the plugin dedupes by
> centroid proximity and keeps the hole — the geometry is the more reliable statement.

### Overhangs and projections

```
OVERHANG_Canopy
OVERHANG_Eave
OVERHANG_Brise-Soleil
OVERHANG_Balcony
```

`CANOPY_*`, `EAVE_*`, and `BRISE_*` prefixes are also recognized, since firms use those.

An overhang can equally be a thin box glued to a mass face, or a cantilevered upper face — both are
detected geometrically without any layer at all.

### Site and context

```
SITE_Property-Line
SITE_Topography
SITE_Context-Building
SITE_Street
SITE_Curb
SITE_Utility
```

Site is *context*, not design. Putting context buildings on `SITE_Context-Building` is worth the
five seconds: it stops them being classified as masses and counted in your program areas.

### Levels — usually inferred, occasionally drawn

```
LEVEL_01_+0ft
LEVEL_02_+12ft
LEVEL_Roof_+36ft
```

The elevation encoded after the `+` or `-` is read directly (`LEVEL_B1_-10ft` → −10). Redundant
with the object, but explicit and human-readable.

You do not need to draw levels. If you don't, set a firm floor-to-floor default in
`ClaudeLearnNamingConvention` and the plugin synthesizes a level ladder from each mass's base
elevation upward.

### Floor plates — derived

```
FLOOR_L01
FLOOR_L02
```

Written by `slice_mass_at_elevation`. You rarely author these by hand.

---

## 4. Worked example — a small mixed-use project

```
Design
    MASS_Office              ← 3-storey office bar, one closed Brep
    MASS_Retail              ← 2-storey retail plinth, boolean-unioned into the office's south face
    MASS_Common              ← lobby / circulation core
    OVERHANG_Canopy          ← thin box over the main entry
Site
    SITE_Property-Line       ← closed polyline
    SITE_Context-Building    ← neighbours, as simple extrusions
    SITE_Street
    SITE_Topography
Working
    Sketches                 ← unclassified; ignored by semantic queries, counted in the narrative
```

With just this, the agent can answer:

- `describe_massing` → "One 3-storey office mass on the north half; a 2-storey retail mass unioned
  into its south face; a common-function core between them. 4 unclassified objects on `Working::Sketches`."
- `check_wall_window_ratio({scope: "byOrientation"})` → per-compass WWR from the holes in the mass faces.
- `get_roof_analysis` → roof faces, slopes, drainage direction, parapet vs eave edges.
- `get_zoning_envelope` → setbacks measured against the `SITE_Property-Line` curve.

None of which required naming a single face, edge, or window.

---

## 5. Keeping your firm's own layer names

Run **`ClaudeLearnNamingConvention`** in a representative `.3dm`.

1. It inventories every layer in the document.
2. It sends the layer list plus the canonical vocabulary above to Claude in one call, and asks for
   a best-match mapping — or `null` for "not architectural".
3. It shows you the proposed mapping in a dialog. Edit anything that's wrong.
4. It asks for your firm-standard floor-to-floor (one number, used for inferred levels).
5. It saves the mapping — to the document, to your plugin settings so every document you open uses
   it, or both.

From then on, step 2 of the resolution rule uses your names. `BLDG-MASSING-OFFICE` works exactly as
well as `MASS_Office`.

To override a single object rather than a whole convention, use **`ClaudeSetElement`**: it writes a
`RhinoClaude:Element:*` user string that beats every convention. Its face-role sub-flow also lets
you click a face and label it directly — the fix for a curved facade the classifier reads as one
`orientation: "other"` face. **`ClaudeClearElement`** removes those tags again.

---

## 6. Naming rules, precisely

- **Separator.** `CATEGORY_Subcategory`. Underscore between category and subcategory; hyphens
  inside a multi-word subcategory (`Curtain-Wall`, `Property-Line`, `Context-Building`).
- **Case.** Matching is case-insensitive, but the canonical spelling is `SCREAMING_Pascal`.
- **Nesting.** Rhino's `::` path separator is respected. The *leaf* segment wins; if the leaf names
  no category, ancestors are checked outward. So `MASS_Office::Level 2` is office massing, and
  `MASS_Office::OPENING_Window` is an opening.
- **Unknown subcategory.** `MASS_Warehouse` still classifies as a Mass — the function falls back to
  `Other` rather than the layer being ignored.
- **Unclassified is fine.** Objects that match nothing simply don't appear in semantic query
  results. `describe_massing` reports the count so the agent knows how much of the file it can't see.

---

## 7. What the plugin will never infer for you

Out of scope by design (plan §2.2) — these fall back to raw geometry tools or the script escape
hatch, and no layer name changes that:

wall assemblies and U-values · MEP · structural sizing · furniture and casework · door/window/finish
schedules · code checking beyond the zoning envelope · site engineering · daylight simulation ·
component families.

The boundary is schematic design. If it wouldn't appear in a 30% SD deliverable to a client, the
semantic layer doesn't model it.
