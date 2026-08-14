using System.Collections.Generic;
using System.Text;

namespace RhinoClaude.Schema
{
    /// <summary>
    /// Room-type "skills" — detailed, step-by-step build recipes that Claude follows
    /// to produce consistent, code-ready room layouts. Each skill is a complete
    /// algorithmic description that eliminates guesswork from the LLM.
    /// </summary>
    public static class RoomSkills
    {
        /// <summary>
        /// Get the full skill prompt for a given room type.
        /// Returns null if no skill exists (falls back to generic instructions).
        /// </summary>
        public static string GetSkill(string roomType)
        {
            switch (roomType)
            {
                case "Restroom": return GetRestroomSkill();
                default: return null;
            }
        }

        /// <summary>
        /// Returns a list of all room types that have defined skills.
        /// </summary>
        public static IReadOnlyList<string> AvailableSkills => new[]
        {
            "Restroom"
        };

        // ═══════════════════════════════════════════════════════════════
        //  RESTROOM SKILL
        // ═══════════════════════════════════════════════════════════════

        private static string GetRestroomSkill()
        {
            var sb = new StringBuilder();

            sb.AppendLine(@"ROOM SKILL: Single-User ADA Restroom
═══════════════════════════════════════════════════════════════

Follow these steps EXACTLY in order. All dimensions below are in INCHES.
Your script MUST convert them to document units first.

STEP 0 — UNIT SETUP
────────────────────
Read the document units and compute a conversion factor:
  unit_scale = 1.0  (if document is Inches)
  unit_scale = 1.0 / 12.0  (if document is Feet)
  unit_scale = 25.4  (if document is Millimeters)
  unit_scale = 2.54  (if document is Centimeters)
  unit_scale = 0.0254  (if document is Meters)
Apply unit_scale to ALL inch dimensions below before creating geometry.
Use a helper function: def u(inches): return inches * unit_scale

STEP 1 — ANALYZE THE BOUNDARY
──────────────────────────────
Given the boundary rectangle (minX, minY) to (maxX, maxY):
  room_width  = maxX - minX  (already in document units)
  room_depth  = maxY - minY  (already in document units)

Determine orientation:
  - If room_width >= room_depth: LANDSCAPE (wider than deep)
    wet_wall = 'north'   (maxY side — back wall)
    door_wall = 'south'  (minY side — front wall)
    toilet_side_wall = 'west'  (minX side)
  - If room_depth > room_width: PORTRAIT (deeper than wide)
    wet_wall = 'east'    (maxX side — back wall)
    door_wall = 'west'   (minX side — front wall)
    toilet_side_wall = 'south'  (minY side)

Wall thicknesses (convert to doc units):
  int_thick = u(4.625)   — interior partition
  wet_thick = u(6.625)   — wet wall / plumbing chase
  wall_ht   = u(120.0)   — wall height (10'-0"")

Compute interior face positions (these are critical reference lines):
  For LANDSCAPE:
    interior_south = minY + int_thick
    interior_north = maxY - wet_thick
    interior_west  = minX + int_thick
    interior_east  = maxX - int_thick
  For PORTRAIT:
    interior_west  = minX + int_thick
    interior_east  = maxX - wet_thick
    interior_south = minY + int_thick
    interior_north = maxY - int_thick

STEP 2 — BUILD WALLS
─────────────────────
Build 4 walls as solid boxes, INSET from the boundary edges.
Walls go from Z=0 to Z=wall_ht.

For LANDSCAPE layout (wet_wall = north):
  South wall (door wall):  Box from (minX, minY, 0) to (maxX, minY + int_thick, wall_ht)
  North wall (wet wall):   Box from (minX, maxY - wet_thick, 0) to (maxX, maxY, wall_ht)
  West wall:               Box from (minX, minY, 0) to (minX + int_thick, maxY, wall_ht)
  East wall:               Box from (maxX - int_thick, minY, 0) to (maxX, maxY, wall_ht)

For PORTRAIT layout (wet_wall = east):
  West wall (door wall):   Box from (minX, minY, 0) to (minX + int_thick, maxY, wall_ht)
  East wall (wet wall):    Box from (maxX - wet_thick, minY, 0) to (maxX, maxY, wall_ht)
  South wall:              Box from (minX, minY, 0) to (maxX, minY + int_thick, wall_ht)
  North wall:              Box from (minX, maxY - int_thick, 0) to (maxX, maxY, wall_ht)

Create each wall with:
  box = rg.Box(rg.Plane.WorldXY, rg.Interval(x0,x1), rg.Interval(y0,y1), rg.Interval(0, wall_ht))
  brep = box.ToBrep()
  id = sc.doc.Objects.AddBrep(brep)

Tag each wall:
  rs.SetUserText(id, 'RC:ElementType', 'Wall')
  rs.SetUserText(id, 'RC:IntExt', 'Interior')
  rs.SetUserText(id, 'RC:SystemType', 'Architectural')
  Wet wall additionally: rs.SetUserText(id, 'RC:AssemblyType', 'Wet Wall / Plumbing Chase')
  Other walls: rs.SetUserText(id, 'RC:AssemblyType', 'Interior Partition')

Set layer to 'RC-Walls' (create if needed).

STEP 3 — CUT DOOR OPENING + ADA DOOR CLEARANCES
─────────────────────────────────────────────────
Door dimensions (convert to doc units):
  door_w = u(36.0)    — clear width
  door_h = u(84.0)    — head height
  door_thick = u(1.75)

ADA DOOR MANEUVERING CLEARANCES (per ADA 404.2.4.1):
The door swings OUT of the restroom (pull side is inside the room).

  PULL SIDE (inside restroom):
    - u(60.0) depth perpendicular to the door plane
    - u(18.0) beyond the latch side of the door
    These clearances must be free of obstructions (no fixtures in this zone).

  PUSH SIDE (corridor side, outside room — not modeled but noted):
    - u(48.0) depth (with closer and latch)
    - u(12.0) beyond the latch side

Door placement strategy:
  - Place the door AWAY from the toilet corner to preserve the 60"" x 18"" pull-side clearance.
  - For LANDSCAPE: offset the door toward the EAST end of the south wall
    (toilet is in the west corner near the wet wall).
    door_latch_x = interior_east - u(18.0)
    opening_right = door_latch_x
    opening_left = opening_right - door_w
  - For PORTRAIT: offset the door toward the NORTH end of the west wall.
    door_latch_y = interior_north - u(18.0)
    opening_top = door_latch_y
    opening_bottom = opening_top - door_w

To cut the opening: DELETE the full door wall and REPLACE it with two wall segments
on either side of the opening. Do NOT use boolean operations.

For landscape (door in south wall):
  Left segment:  (minX, minY) to (opening_left, minY + int_thick) x wall_ht
  Right segment: (opening_right, minY) to (maxX, minY + int_thick) x wall_ht

For portrait (door in west wall):
  Bottom segment: (minX, minY) to (minX + int_thick, opening_bottom) x wall_ht
  Top segment:    (minX, opening_top) to (minX + int_thick, maxY) x wall_ht

Add a header piece above the opening (from door_h to wall_ht) to close the gap above the door.
Tag wall segments same as other interior partition walls.

STEP 4 — BUILD DOOR
────────────────────
Create the door panel as a flat box in the opening.
  - Panel dimensions: door_w x door_thick x door_h
  - Position it in the opening, flush with the interior face of the door wall.
  - The door panel occupies the opening; the swing arc is not modeled.
Tag: RC:ElementType=Door, RC:Description=Door
Layer: 'RC-Doors'

STEP 5 — PLACE TOILET + VERIFY CLEARANCE
─────────────────────────────────────────
Toilet dimensions (convert to doc units):
  toilet_w = u(20.0)
  toilet_d = u(28.0)
  toilet_h = u(17.0)

ADA TOILET CLEARANCES (per ADA 604.2 and 604.3.1):
  - Toilet centerline: u(17.0) from the side wall interior face.
    (ADA says 16""-18""; 17"" accommodates 1"" construction tolerance.)
  - Clear floor space: u(60.0) wide x u(56.0) deep.
    The 60"" width is measured from the side wall.
    The 56"" depth is measured from the rear (wet) wall.
    This zone must be clear of other fixtures but can overlap the door clearance zone.

Placement (LANDSCAPE — toilet in NW corner):
  toilet_cl_x = interior_west + u(17.0)
  toilet_left  = toilet_cl_x - toilet_w / 2.0
  toilet_right = toilet_cl_x + toilet_w / 2.0
  toilet_back  = interior_north    (flush against wet wall interior face)
  toilet_front = toilet_back - toilet_d
  Create box: (toilet_left, toilet_front, 0) to (toilet_right, toilet_back, toilet_h)

  Toilet clear floor zone (for verification):
    clear_x_min = interior_west
    clear_x_max = interior_west + u(60.0)
    clear_y_min = interior_north - u(56.0)
    clear_y_max = interior_north

Placement (PORTRAIT — toilet in SE corner):
  toilet_cl_y = interior_south + u(17.0)
  toilet_bottom = toilet_cl_y - toilet_w / 2.0
  toilet_top    = toilet_cl_y + toilet_w / 2.0
  toilet_back   = interior_east
  toilet_front  = toilet_back - toilet_d
  Create box: (toilet_front, toilet_bottom, 0) to (toilet_back, toilet_top, toilet_h)

Tag: RC:ElementType=Equipment, RC:Description=Toilet
Layer: 'RC-Fixtures'

STEP 6 — PLACE SINK + VERIFY CLEARANCE
───────────────────────────────────────
Sink dimensions (convert to doc units):
  sink_w = u(22.0)
  sink_d = u(18.0)   — must be between u(17.0) and u(25.0) per ADA 606.3
  sink_h = u(8.0)
  sink_top = u(34.0)  — top of basin AFF
  sink_support_w = u(4.0)
  sink_support_d = u(4.0)

ADA SINK CLEARANCES (per ADA 606.2):
  - Clear floor space: u(30.0) wide x u(48.0) deep, centered on the sink.
  - Sink depth from wall: u(17.0) min to u(25.0) max.

Placement (LANDSCAPE — sink on north wet wall, east of toilet):
  Sink must be far enough from toilet that their clearance zones don't conflict.
  sink_cl_x = toilet_cl_x + u(17.0)/2.0 + u(30.0)/2.0 + u(6.0)
    (ensure 30"" sink clear doesn't overlap 60"" toilet clear zone; add padding)
  If sink_cl_x + sink_w/2.0 > interior_east, shift left as needed.
  sink_back  = interior_north
  sink_front = sink_back - sink_d
  sink_left  = sink_cl_x - sink_w / 2.0
  sink_right = sink_cl_x + sink_w / 2.0

  Sink clear floor zone (for verification):
    clear_x_min = sink_cl_x - u(15.0)   (30""/2)
    clear_x_max = sink_cl_x + u(15.0)
    clear_y_min = interior_north - u(48.0)
    clear_y_max = interior_north

Placement (PORTRAIT — sink on east wet wall, north of toilet):
  sink_cl_y = toilet_cl_y + u(17.0)/2.0 + u(30.0)/2.0 + u(6.0)
  sink_back  = interior_east
  sink_front = sink_back - sink_d
  sink_bottom = sink_cl_y - sink_w / 2.0
  sink_top_y  = sink_cl_y + sink_w / 2.0

Build the sink (simplified geometry):
  1. Basin box: sink_w x sink_d x sink_h, bottom at Z = sink_top - sink_h
  2. Support/pedestal: sink_support_w x sink_support_d x (sink_top - sink_h),
     centered under basin, from Z=0 to Z = sink_top - sink_h
Tag all sink pieces: RC:ElementType=Equipment, RC:Description=Sink
Layer: 'RC-Fixtures'

STEP 7 — PLACE GRAB BARS
─────────────────────────
Grab bar dimensions (convert to doc units):
  bar_section = u(1.5)   — cross-section (square simplified)
  side_bar_len = u(42.0)
  rear_bar_len = u(36.0)
  bar_height   = u(34.0) — centerline AFF
  bar_z_bottom = bar_height - bar_section / 2.0
  bar_z_top    = bar_height + bar_section / 2.0

SIDE GRAB BAR (on the side wall adjacent to toilet):
  - Runs parallel to the wet wall, mounted on the side wall face.
  - Starts u(12.0) from the wet wall interior face toward the room.
  - Length = side_bar_len.

  For LANDSCAPE (bar on west wall interior face, running in Y):
    bar_x0 = interior_west
    bar_x1 = interior_west + bar_section
    bar_y1 = interior_north - u(12.0)     (12"" from rear/wet wall)
    bar_y0 = bar_y1 - side_bar_len
    Create box: (bar_x0, bar_y0, bar_z_bottom) to (bar_x1, bar_y1, bar_z_top)

  For PORTRAIT (bar on south wall interior face, running in X):
    bar_y0 = interior_south
    bar_y1 = interior_south + bar_section
    bar_x1 = interior_east - u(12.0)
    bar_x0 = bar_x1 - side_bar_len
    Create box: (bar_x0, bar_y0, bar_z_bottom) to (bar_x1, bar_y1, bar_z_top)

REAR GRAB BAR (on the wet wall behind toilet):
  - Runs perpendicular to the side bar, mounted on the wet wall face.
  - Centered on the toilet centerline.
  - Length = rear_bar_len.

  For LANDSCAPE (bar on north/wet wall interior face, running in X):
    bar_y0 = interior_north - bar_section
    bar_y1 = interior_north
    bar_x0 = toilet_cl_x - rear_bar_len / 2.0
    bar_x1 = toilet_cl_x + rear_bar_len / 2.0
    Create box: (bar_x0, bar_y0, bar_z_bottom) to (bar_x1, bar_y1, bar_z_top)

  For PORTRAIT (bar on east/wet wall interior face, running in Y):
    bar_x0 = interior_east - bar_section
    bar_x1 = interior_east
    bar_y0 = toilet_cl_y - rear_bar_len / 2.0
    bar_y1 = toilet_cl_y + rear_bar_len / 2.0
    Create box: (bar_x0, bar_y0, bar_z_bottom) to (bar_x1, bar_y1, bar_z_top)

Tag: RC:ElementType=Equipment, RC:Description=Grab Bar
Layer: 'RC-Fixtures'

STEP 8 — VERIFY CLEARANCES (print to console, do not fail)
───────────────────────────────────────────────────────────
After placing all elements, verify and PRINT clearance checks:

  1. TOILET CLEAR FLOOR (60"" x 56""):
     Check that the zone from the side wall extending 60"" and from the wet wall
     extending 56"" does not contain the sink or door panel.
     Print: ""Toilet clear floor: {w} x {d} — OK/WARNING""

  2. SINK CLEAR FLOOR (30"" x 48""):
     Check that 30"" wide x 48"" deep zone centered on sink is clear of toilet.
     Print: ""Sink clear floor: {w} x {d} — OK/WARNING""

  3. DOOR PULL-SIDE CLEARANCE (60"" x (door_w + 18"")):
     Check that the 60"" depth x (door width + 18"" latch side) zone inside the room
     at the door is not blocked by fixtures.
     Print: ""Door pull clearance: {d} x {w} — OK/WARNING""

  4. WHEELCHAIR TURNING (60"" diameter):
     Check that a 60"" diameter circle can fit somewhere in the room
     (it may overlap fixture clearance zones but not walls/fixtures).
     Print: ""Turning radius: OK/WARNING""

STEP 9 — FINAL CLEANUP
───────────────────────
  - Call sc.doc.Views.Redraw()
  - Print a summary of what was created with clearance check results.

IMPORTANT REMINDERS:
  - Use rg.Box with Intervals — do NOT use rs.AddBox (it needs 8 corner points).
  - All geometry IDs returned from sc.doc.Objects.AddBrep() are Guid objects.
    Convert to string for rs.SetUserText: rs.SetUserText(str(id), key, value)
  - Create layers with rs.AddLayer('RC-Walls') etc. before assigning.
  - Use rs.ObjectLayer(str(id), 'RC-Walls') to set the layer.
  - No f-strings! Use .format() or % formatting only.");

            return sb.ToString();
        }
    }
}
