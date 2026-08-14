using System.Collections.Generic;

namespace RhinoClaude.Schema
{
    /// <summary>
    /// Standard dimensions and conventions for auto-modeling.
    /// All values are in inches — scripts convert based on document units.
    /// </summary>
    public static class BuildingStandards
    {
        // ── Wall Thicknesses (inches) ───────────────────────────────

        /// <summary>Standard interior partition: 2x4 stud + 1 layer 5/8" GWB each side.</summary>
        public const double InteriorWallThickness = 4.625;

        /// <summary>Wet wall / plumbing chase: 2x6 stud + 1 layer 5/8" GWB each side.</summary>
        public const double WetWallThickness = 6.625;

        /// <summary>Exterior wall: 2x6 stud + insulation + sheathing + GWB.</summary>
        public const double ExteriorWallThickness = 7.25;

        // ── Heights (inches) ────────────────────────────────────────

        /// <summary>Floor to top-of-wall (typical commercial).</summary>
        public const double WallHeight = 120.0; // 10'-0"

        /// <summary>Standard door head height.</summary>
        public const double DoorHeadHeight = 84.0; // 7'-0"

        // ── Door Sizes (inches) ──────────────────────────────────────

        /// <summary>Standard single restroom door width (ADA 36" clear).</summary>
        public const double RestroomDoorWidth = 36.0;

        /// <summary>Standard door thickness.</summary>
        public const double DoorThickness = 1.75;

        // ── ADA Clearances (inches) ──────────────────────────────────

        /// <summary>Wheelchair turning radius.</summary>
        public const double AdaTurningRadius = 60.0;

        // ── Toilet Clearances ──
        /// <summary>Side wall to toilet centerline (use 17" to accommodate 1" construction tolerance per ADA Fig 604.2).</summary>
        public const double AdaToiletSideClearance = 17.0;

        /// <summary>Clear floor space at toilet — width (perpendicular to wall). ADA 604.3.1.</summary>
        public const double AdaToiletClearWidth = 60.0;

        /// <summary>Clear floor space at toilet — depth (parallel to wall). ADA 604.3.1.</summary>
        public const double AdaToiletClearDepth = 56.0;

        // ── Sink / Lavatory Clearances ──
        /// <summary>Clear floor space at sink — width. ADA 606.2.</summary>
        public const double AdaSinkClearWidth = 30.0;

        /// <summary>Clear floor space at sink — depth. ADA 606.2.</summary>
        public const double AdaSinkClearDepth = 48.0;

        /// <summary>Sink/counter depth from wall — minimum. ADA 606.3.</summary>
        public const double AdaSinkDepthMin = 17.0;

        /// <summary>Sink/counter depth from wall — maximum. ADA 606.3.</summary>
        public const double AdaSinkDepthMax = 25.0;

        // ── Door Clearances ──
        /// <summary>Door pull-side maneuvering clearance depth. ADA 404.2.4.1.</summary>
        public const double AdaDoorPullDepth = 60.0;

        /// <summary>Door pull-side latch-side clearance. ADA 404.2.4.1.</summary>
        public const double AdaDoorPullLatchSide = 18.0;

        /// <summary>Door push-side maneuvering clearance depth (with closer + latch). ADA 404.2.4.1.</summary>
        public const double AdaDoorPushDepth = 48.0;

        /// <summary>Door push-side latch-side clearance (with closer + latch). ADA 404.2.4.1.</summary>
        public const double AdaDoorPushLatchSide = 12.0;

        /// <summary>Grab bar length — side wall.</summary>
        public const double GrabBarSideLength = 42.0;

        /// <summary>Grab bar length — rear wall.</summary>
        public const double GrabBarRearLength = 36.0;

        /// <summary>Grab bar mounting height (centerline).</summary>
        public const double GrabBarHeight = 34.0;

        // ── Fixture Rough Dimensions (inches) ────────────────────────
        // Used as placeholders when block library files are not available.

        public const double ToiletWidth = 20.0;
        public const double ToiletDepth = 28.0;
        public const double ToiletHeight = 17.0;

        public const double SinkWidth = 22.0;
        public const double SinkDepth = 18.0;
        public const double SinkHeight = 34.0;

        // ── Room Label Aliases ──────────────────────────────────────

        /// <summary>
        /// Maps common abbreviations / labels to canonical room type names.
        /// </summary>
        public static readonly Dictionary<string, string> RoomLabelAliases = new Dictionary<string, string>(
            System.StringComparer.OrdinalIgnoreCase)
        {
            { "restroom",       "Restroom" },
            { "rr",             "Restroom" },
            { "toilet",         "Restroom" },
            { "tlt",            "Restroom" },
            { "bathroom",       "Restroom" },
            { "wc",             "Restroom" },
            { "lavatory",       "Restroom" },
            { "lav",            "Restroom" },
            // Future room types
            { "office",         "Office" },
            { "ofc",            "Office" },
            { "corridor",       "Corridor" },
            { "corr",           "Corridor" },
            { "stair",          "Stair" },
            { "elev",           "Elevator" },
            { "elevator",       "Elevator" },
            { "mech",           "Mechanical" },
            { "mechanical",     "Mechanical" },
            { "elec",           "Electrical" },
            { "electrical",     "Electrical" },
            { "janitor",        "Janitor" },
            { "jan",            "Janitor" },
            { "storage",        "Storage" },
            { "stor",           "Storage" },
        };

        // ── Fixture Library ─────────────────────────────────────────

        /// <summary>
        /// Expected block file names in the fixture library folder.
        /// Keys are canonical fixture names used in prompts.
        /// Values are .3dm file names (without path).
        /// </summary>
        public static readonly Dictionary<string, string> FixtureFiles = new Dictionary<string, string>
        {
            { "Toilet",     "Toilet.3dm" },
            { "Sink",       "Sink.3dm" },
            { "GrabBar",    "GrabBar.3dm" },
            { "Door",       "Door.3dm" },
        };

        /// <summary>
        /// Build a summary string for inclusion in Claude prompts.
        /// </summary>
        public static string GetStandardsSummary()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("BUILDING STANDARDS (all dimensions in inches):");
            sb.AppendLine(string.Format("- Interior partition wall: {0}\"\" thick (2x4 + GWB)", InteriorWallThickness));
            sb.AppendLine(string.Format("- Wet wall / plumbing chase: {0}\"\" thick (2x6 + GWB)", WetWallThickness));
            sb.AppendLine(string.Format("- Wall height: {0}\"\" ({1}'-0\"\")", WallHeight, WallHeight / 12.0));
            sb.AppendLine(string.Format("- Door head height: {0}\"\" ({1}'-0\"\")", DoorHeadHeight, DoorHeadHeight / 12.0));
            sb.AppendLine(string.Format("- Restroom door: {0}\"\" wide x {1}\"\" thick", RestroomDoorWidth, DoorThickness));
            sb.AppendLine();
            sb.AppendLine("ADA CLEARANCES:");
            sb.AppendLine(string.Format("- Wheelchair turning radius: {0}\"\" diameter", AdaTurningRadius));
            sb.AppendLine(string.Format("- Toilet clear floor: {0}\"\" wide x {1}\"\" deep (ADA 604.3.1)", AdaToiletClearWidth, AdaToiletClearDepth));
            sb.AppendLine(string.Format("- Toilet centerline: {0}\"\" from side wall (ADA 604.2, use 17\"\" for tolerance)", AdaToiletSideClearance));
            sb.AppendLine(string.Format("- Sink clear floor: {0}\"\" wide x {1}\"\" deep (ADA 606.2)", AdaSinkClearWidth, AdaSinkClearDepth));
            sb.AppendLine(string.Format("- Sink depth from wall: {0}\"\"-{1}\"\" (ADA 606.3)", AdaSinkDepthMin, AdaSinkDepthMax));
            sb.AppendLine(string.Format("- Door pull side: {0}\"\" deep, {1}\"\" latch side (ADA 404.2.4.1)",
                AdaDoorPullDepth, AdaDoorPullLatchSide));
            sb.AppendLine(string.Format("- Door push side (closer+latch): {0}\"\" deep, {1}\"\" latch side",
                AdaDoorPushDepth, AdaDoorPushLatchSide));
            sb.AppendLine(string.Format("- Grab bars: {0}\"\" side wall, {1}\"\" rear wall, mounted at {2}\"\" AFF",
                GrabBarSideLength, GrabBarRearLength, GrabBarHeight));
            sb.AppendLine();
            sb.AppendLine("FIXTURE PLACEHOLDERS (if no block library):");
            sb.AppendLine(string.Format("- Toilet: {0}\"\" W x {1}\"\" D x {2}\"\" H", ToiletWidth, ToiletDepth, ToiletHeight));
            sb.AppendLine(string.Format("- Sink: {0}\"\" W x {1}\"\" D x {2}\"\" H", SinkWidth, SinkDepth, SinkHeight));
            return sb.ToString().TrimEnd();
        }
    }
}
