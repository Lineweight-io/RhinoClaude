using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoClaude.Schema;
using RhinoClaude.Services;

namespace RhinoClaude.Commands
{
    /// <summary>
    /// Command: RCBuildFromDiagram
    /// Reads a labeled rectangle (block diagram) and deterministically generates
    /// a 3D single-occupancy restroom model with interactive placement prompts.
    ///
    /// Build order: Walls → Door → Toilet → Sink
    /// All placement is algorithmic — no Claude API call for geometry layout.
    /// </summary>
    public class RCBuildFromDiagramCommand : Command
    {
        public override string EnglishName => "RCBuildFromDiagram";

        // ═══════════════════════════════════════════════════════════════
        //  CONSTANTS (all in inches — converted to doc units at runtime)
        // ═══════════════════════════════════════════════════════════════

        // Wall dimensions
        private const double StandardWallThickness = 4.875;    // 4-7/8"
        private const double WetWallThickness = 7.125;         // 7-1/8" (floor-mounted back wall)
        private const double ChaseWallThickness = 4.125;       // 4-1/8" (wall-hung chase)
        private const double ChaseWallClearance = 12.0;        // 12" clear from back wall to chase face
        private const double WallHeight = 120.0;               // 10'-0"

        // Door dimensions
        private const double DoorWidth = 36.0;                 // 36" clear opening
        private const double DoorHeight = 84.0;                // 7'-0" head height
        private const double DoorOffsetFromWall = 4.0;         // 4" from inside face of perpendicular wall
        private const double DoorPanelThickness = 1.75;        // 1-3/4" door panel

        // Door clearances (ADA)
        private const double DoorClearanceDepth = 48.0;        // 48" out from door
        private const double DoorClearanceLatchBeyond = 12.0;  // 12" beyond latch side

        // Toilet placement
        private const double ToiletOffsetFromWall = 17.0;      // 17" CL from perpendicular inside face

        // Toilet clearances (from wall faces, not centerlines)
        private const double ToiletClearDepthWallHung = 56.0;  // 56" from back wall (wall-hung)
        private const double ToiletClearDepthFloorMtd = 59.0;  // 59" from back wall (floor-mounted)
        private const double ToiletClearWidth = 60.0;          // 60" from side wall

        // Sink placement
        private const double SinkOffsetFromWall = 16.0;        // 16" CL from perpendicular inside face

        // Sink clearances
        private const double SinkClearWidth = 30.0;            // 30" wide
        private const double SinkClearDepth = 48.0;            // 48" deep

        // Turning circle (Colorado requirement)
        private const double TurningCircleDiameter = 69.0;    // 69" (5'-9") Colorado ADA
        private const double SinkUnderhang = 3.0;              // Circle can extend 3" under sink

        // ═══════════════════════════════════════════════════════════════
        //  ENUMS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// The 8 possible door corner positions.
        /// Named as "Wall Side" — e.g., SouthEast = south wall, east side.
        /// The door hinge is at the corner, swing is outward.
        /// </summary>
        private enum DoorCorner
        {
            NorthWest, NorthEast,
            EastNorth, EastSouth,
            SouthEast, SouthWest,
            WestSouth, WestNorth
        }

        /// <summary>Cardinal wall direction.</summary>
        private enum Wall { North, East, South, West }

        /// <summary>Which side of the wall the fixture is near.</summary>
        private enum WallSide { Start, End }

        private enum ToiletType { WallHung, FloorMounted }

        // ═══════════════════════════════════════════════════════════════
        //  ROOM GEOMETRY CONTEXT (computed once from boundary + choices)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Holds all computed room geometry after wall placement.
        /// All values are in document units.
        /// </summary>
        private class RoomContext
        {
            // Boundary rectangle (from selected curve)
            public double BndMinX, BndMinY, BndMaxX, BndMaxY;

            // Inside face positions (after wall inset from centerline)
            public double InsideNorth; // Y of south face of north wall
            public double InsideSouth; // Y of north face of south wall
            public double InsideEast;  // X of west face of east wall
            public double InsideWest;  // X of east face of west wall

            // Outside face positions
            public double OutsideNorth, OutsideSouth, OutsideEast, OutsideWest;

            // Wall thicknesses per side (may vary if wet wall)
            public double ThickNorth, ThickSouth, ThickEast, ThickWest;

            // Interior clear dimensions
            public double ClearWidth  => InsideEast - InsideWest;
            public double ClearHeight => InsideNorth - InsideSouth;

            // Unit scale factor (inches to doc units)
            public double Scale;

            // User choices
            public DoorCorner DoorPosition;
            public ToiletType Toilet;

            // Computed fixture positions (doc units, insertion point = back wall CL on floor)
            public Point3d ToiletInsertPt;
            public double ToiletRotationDeg;
            public Wall ToiletWall;
            public WallSide ToiletSide;

            public Point3d SinkInsertPt;
            public double SinkRotationDeg;
            public Wall SinkWall;
            public WallSide SinkSide;

            // Chase wall (wall-hung only)
            public bool HasChaseWall;
            public Point3d ChaseWallMin, ChaseWallMax;
        }

        // ═══════════════════════════════════════════════════════════════
        //  MAIN COMMAND
        // ═══════════════════════════════════════════════════════════════

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // ── Step 1: Select the block diagram rectangle ───────────
            var go = new GetObject();
            go.SetCommandPrompt("Select block diagram rectangle (closed curve)");
            go.GeometryFilter = ObjectType.Curve;
            go.GeometryAttributeFilter = GeometryAttributeFilter.ClosedCurve;
            go.Get();
            if (go.CommandResult() != Result.Success)
                return go.CommandResult();

            var curveObj = go.Object(0).Object();
            var curve = go.Object(0).Curve();
            if (curve == null || !curve.IsClosed)
            {
                RhinoApp.WriteLine("RhinoClaude: Selected object is not a closed curve.");
                return Result.Failure;
            }

            // ── Step 2: Resolve room label ───────────────────────────
            string roomLabel = ResolveRoomLabel(doc, curveObj, curve);
            if (string.IsNullOrEmpty(roomLabel))
                return Result.Cancel;

            string roomType;
            if (!BuildingStandards.RoomLabelAliases.TryGetValue(roomLabel, out roomType))
                roomType = roomLabel;

            RhinoApp.WriteLine("RhinoClaude: Room type resolved to: {0}", roomType);

            // For now, only the deterministic builder handles "Restroom"
            if (roomType != "Restroom")
            {
                RhinoApp.WriteLine("RhinoClaude: Deterministic builder only supports 'Restroom' currently.");
                RhinoApp.WriteLine("RhinoClaude: Use ClaudeRunScript for other room types.");
                return Result.Cancel;
            }

            // ── Step 3: Compute boundary and unit scale ──────────────
            var bbox = curve.GetBoundingBox(true);
            if (!bbox.IsValid)
            {
                RhinoApp.WriteLine("RhinoClaude: Could not compute bounding box.");
                return Result.Failure;
            }

            double scale = RhinoMath.UnitScale(UnitSystem.Inches, doc.ModelUnitSystem);
            var ctx = new RoomContext
            {
                BndMinX = bbox.Min.X,
                BndMinY = bbox.Min.Y,
                BndMaxX = bbox.Max.X,
                BndMaxY = bbox.Max.Y,
                Scale = scale,
            };

            double bndWidth = ctx.BndMaxX - ctx.BndMinX;
            double bndHeight = ctx.BndMaxY - ctx.BndMinY;

            RhinoApp.WriteLine("RhinoClaude: Boundary {0:F2} x {1:F2} {2} (centerline to centerline)",
                bndWidth, bndHeight, doc.ModelUnitSystem);
            RhinoApp.WriteLine("RhinoClaude: Scale factor (inches → {0}): {1:F6}",
                doc.ModelUnitSystem, scale);

            // ── Step 4: Prompt for door location ─────────────────────
            var doorResult = PromptDoorLocation();
            if (doorResult == null)
                return Result.Cancel;
            ctx.DoorPosition = doorResult.Value;

            // ── Step 5: Prompt for toilet type ───────────────────────
            var toiletResult = PromptToiletType();
            if (toiletResult == null)
                return Result.Cancel;
            ctx.Toilet = toiletResult.Value;

            // ── Step 6: Compute wall geometry ────────────────────────
            //   Walls are centered on the boundary lines.
            //   Default all walls to standard thickness.
            //   The toilet's back wall will be thickened later.
            ctx.ThickNorth = StandardWallThickness * scale;
            ctx.ThickSouth = StandardWallThickness * scale;
            ctx.ThickEast  = StandardWallThickness * scale;
            ctx.ThickWest  = StandardWallThickness * scale;

            // Compute inside/outside faces (standard thickness for now)
            RecomputeWallFaces(ctx);

            RhinoApp.WriteLine("RhinoClaude: Interior clear space: {0:F2} x {1:F2} {2}",
                ctx.ClearWidth, ctx.ClearHeight, doc.ModelUnitSystem);

            // ── Step 7: Place toilet (determines which wall is wet) ──
            if (!PlaceToilet(ctx))
            {
                RhinoApp.WriteLine("RhinoClaude: Could not find a valid toilet placement. Room may be too small.");
                return Result.Failure;
            }

            // Now thicken the toilet's back wall
            ApplyWetWallThickness(ctx);
            RecomputeWallFaces(ctx);

            // Recompute toilet insertion point — wall faces have changed
            ComputeToiletInsertionPoint(ctx);

            RhinoApp.WriteLine("RhinoClaude: Toilet placed on {0} wall, {1} side",
                ctx.ToiletWall, ctx.ToiletSide);
            RhinoApp.WriteLine("RhinoClaude: Toilet insertion: ({0:F2}, {1:F2}), rotation: {2}°",
                ctx.ToiletInsertPt.X, ctx.ToiletInsertPt.Y, ctx.ToiletRotationDeg);

            // ── Step 8: Place sink ───────────────────────────────────
            if (!PlaceSink(ctx))
            {
                RhinoApp.WriteLine("RhinoClaude: Could not find a valid sink placement. Room may be too small.");
                return Result.Failure;
            }

            RhinoApp.WriteLine("RhinoClaude: Sink placed on {0} wall, {1} side",
                ctx.SinkWall, ctx.SinkSide);
            RhinoApp.WriteLine("RhinoClaude: Sink insertion: ({0:F2}, {1:F2}), rotation: {2}°",
                ctx.SinkInsertPt.X, ctx.SinkInsertPt.Y, ctx.SinkRotationDeg);

            // ── Step 8b: Turning circle check ────────────────────────
            if (!CheckTurningCircle(ctx))
            {
                RhinoApp.WriteLine("RhinoClaude: FAILED — 5'-9\" (69\") turning circle does NOT fit in this room.");
                RhinoApp.WriteLine("RhinoClaude: Room does not meet Colorado ADA requirements. Increase room size.");
                return Result.Failure;
            }
            else
            {
                RhinoApp.WriteLine("RhinoClaude: Turning circle check PASSED.");
            }

            // ── Step 9: Confirm before building ──────────────────────
            PrintBuildPlan(ctx, doc);

            var getConfirm = new GetOption();
            getConfirm.SetCommandPrompt("Build this restroom?");
            getConfirm.AddOption("Build");
            int cancelIndex = getConfirm.AddOption("Cancel");

            if (getConfirm.Get() != GetResult.Option || getConfirm.OptionIndex() == cancelIndex)
            {
                RhinoApp.WriteLine("RhinoClaude: Build cancelled.");
                return Result.Cancel;
            }

            // ── Step 10: Build all geometry ──────────────────────────
            BuildRoom(doc, ctx);

            RhinoApp.WriteLine("RhinoClaude: Restroom built successfully.");
            doc.Views.Redraw();
            return Result.Success;
        }

        // ═══════════════════════════════════════════════════════════════
        //  INTERACTIVE PROMPTS
        // ═══════════════════════════════════════════════════════════════

        private DoorCorner? PromptDoorLocation()
        {
            var getOpt = new GetOption();
            getOpt.SetCommandPrompt("Door location (Wall_Side)");

            var options = new Dictionary<int, DoorCorner>();
            options[getOpt.AddOption("NorthWest")]  = DoorCorner.NorthWest;
            options[getOpt.AddOption("NorthEast")]  = DoorCorner.NorthEast;
            options[getOpt.AddOption("EastNorth")]   = DoorCorner.EastNorth;
            options[getOpt.AddOption("EastSouth")]   = DoorCorner.EastSouth;
            options[getOpt.AddOption("SouthEast")]   = DoorCorner.SouthEast;
            options[getOpt.AddOption("SouthWest")]   = DoorCorner.SouthWest;
            options[getOpt.AddOption("WestSouth")]   = DoorCorner.WestSouth;
            options[getOpt.AddOption("WestNorth")]   = DoorCorner.WestNorth;

            if (getOpt.Get() != GetResult.Option)
                return null;

            DoorCorner result;
            if (options.TryGetValue(getOpt.OptionIndex(), out result))
                return result;
            return null;
        }

        private ToiletType? PromptToiletType()
        {
            var getOpt = new GetOption();
            getOpt.SetCommandPrompt("Toilet type");
            int whIdx = getOpt.AddOption("WallHung");
            int fmIdx = getOpt.AddOption("FloorMounted");

            if (getOpt.Get() != GetResult.Option)
                return null;

            return getOpt.OptionIndex() == whIdx ? ToiletType.WallHung : ToiletType.FloorMounted;
        }

        // ═══════════════════════════════════════════════════════════════
        //  WALL GEOMETRY
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Recompute inside/outside face positions from boundary + thicknesses.
        /// Walls are centered on the boundary lines: half thickness goes in, half goes out.
        /// </summary>
        private void RecomputeWallFaces(RoomContext ctx)
        {
            double halfN = ctx.ThickNorth / 2.0;
            double halfS = ctx.ThickSouth / 2.0;
            double halfE = ctx.ThickEast / 2.0;
            double halfW = ctx.ThickWest / 2.0;

            ctx.InsideNorth  = ctx.BndMaxY - halfN;   // south face of north wall
            ctx.OutsideNorth = ctx.BndMaxY + halfN;

            ctx.InsideSouth  = ctx.BndMinY + halfS;   // north face of south wall
            ctx.OutsideSouth = ctx.BndMinY - halfS;

            ctx.InsideEast   = ctx.BndMaxX - halfE;   // west face of east wall
            ctx.OutsideEast  = ctx.BndMaxX + halfE;

            ctx.InsideWest   = ctx.BndMinX + halfW;   // east face of west wall
            ctx.OutsideWest  = ctx.BndMinX - halfW;
        }

        /// <summary>
        /// Thicken the toilet's back wall for wet wall / plumbing chase.
        /// For floor-mounted: back wall becomes WetWallThickness.
        /// For wall-hung: back wall stays standard, but a chase wall is added inside.
        /// </summary>
        private void ApplyWetWallThickness(RoomContext ctx)
        {
            double s = ctx.Scale;

            if (ctx.Toilet == ToiletType.FloorMounted)
            {
                // Thicken the back wall to wet wall thickness
                double wetThick = WetWallThickness * s;
                switch (ctx.ToiletWall)
                {
                    case Wall.North: ctx.ThickNorth = wetThick; break;
                    case Wall.South: ctx.ThickSouth = wetThick; break;
                    case Wall.East:  ctx.ThickEast  = wetThick; break;
                    case Wall.West:  ctx.ThickWest  = wetThick; break;
                }
            }
            else // WallHung
            {
                // Back wall stays standard thickness.
                // Chase wall is added 12" clear from inside face of back wall.
                // The chase wall is ChaseWallThickness thick.
                // Chase runs the full length of the back wall.
                ctx.HasChaseWall = true;

                double chaseClear = ChaseWallClearance * s;
                double chaseThick = ChaseWallThickness * s;

                switch (ctx.ToiletWall)
                {
                    case Wall.North:
                        // Chase wall is a horizontal slab below the north wall
                        ctx.ChaseWallMin = new Point3d(ctx.InsideWest, ctx.InsideNorth - chaseClear - chaseThick, 0);
                        ctx.ChaseWallMax = new Point3d(ctx.InsideEast, ctx.InsideNorth - chaseClear, WallHeight * s);
                        break;
                    case Wall.South:
                        ctx.ChaseWallMin = new Point3d(ctx.InsideWest, ctx.InsideSouth + chaseClear, 0);
                        ctx.ChaseWallMax = new Point3d(ctx.InsideEast, ctx.InsideSouth + chaseClear + chaseThick, WallHeight * s);
                        break;
                    case Wall.East:
                        ctx.ChaseWallMin = new Point3d(ctx.InsideEast - chaseClear - chaseThick, ctx.InsideSouth, 0);
                        ctx.ChaseWallMax = new Point3d(ctx.InsideEast - chaseClear, ctx.InsideNorth, WallHeight * s);
                        break;
                    case Wall.West:
                        ctx.ChaseWallMin = new Point3d(ctx.InsideWest + chaseClear, ctx.InsideSouth, 0);
                        ctx.ChaseWallMax = new Point3d(ctx.InsideWest + chaseClear + chaseThick, ctx.InsideNorth, WallHeight * s);
                        break;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  DOOR GEOMETRY HELPERS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Get the wall and side for a door corner position.
        /// </summary>
        private void GetDoorWallAndSide(DoorCorner corner, out Wall wall, out WallSide side)
        {
            switch (corner)
            {
                case DoorCorner.NorthWest:  wall = Wall.North; side = WallSide.Start; break;
                case DoorCorner.NorthEast:  wall = Wall.North; side = WallSide.End;   break;
                case DoorCorner.EastNorth:  wall = Wall.East;  side = WallSide.End;   break;
                case DoorCorner.EastSouth:  wall = Wall.East;  side = WallSide.Start; break;
                case DoorCorner.SouthEast:  wall = Wall.South; side = WallSide.End;   break;
                case DoorCorner.SouthWest:  wall = Wall.South; side = WallSide.Start; break;
                case DoorCorner.WestSouth:  wall = Wall.West;  side = WallSide.Start; break;
                case DoorCorner.WestNorth:  wall = Wall.West;  side = WallSide.End;   break;
                default:                    wall = Wall.South; side = WallSide.Start; break;
            }
        }

        /// <summary>
        /// Compute the door opening rectangle in the wall (min/max of the opening void).
        /// Returns the opening box min and max corners in plan (X,Y) and the Z range.
        /// The opening is DoorWidth wide, starting DoorOffsetFromWall from the inside
        /// face of the perpendicular wall at the hinge corner.
        /// </summary>
        private void ComputeDoorOpening(RoomContext ctx,
            out double openMinX, out double openMinY,
            out double openMaxX, out double openMaxY)
        {
            double s = ctx.Scale;
            double doorW = DoorWidth * s;
            double doorOff = DoorOffsetFromWall * s;

            Wall wall;
            WallSide side;
            GetDoorWallAndSide(ctx.DoorPosition, out wall, out side);

            openMinX = openMinY = openMaxX = openMaxY = 0;

            switch (wall)
            {
                case Wall.North:
                case Wall.South:
                    // Door is in a horizontal wall (runs along X axis)
                    double wallY1, wallY2;
                    if (wall == Wall.North)
                    {
                        wallY1 = ctx.InsideNorth;
                        wallY2 = ctx.OutsideNorth;
                    }
                    else
                    {
                        wallY1 = ctx.OutsideSouth;
                        wallY2 = ctx.InsideSouth;
                    }
                    openMinY = Math.Min(wallY1, wallY2);
                    openMaxY = Math.Max(wallY1, wallY2);

                    if (side == WallSide.Start) // West side
                    {
                        openMinX = ctx.InsideWest + doorOff;
                        openMaxX = openMinX + doorW;
                    }
                    else // East side
                    {
                        openMaxX = ctx.InsideEast - doorOff;
                        openMinX = openMaxX - doorW;
                    }
                    break;

                case Wall.East:
                case Wall.West:
                    // Door is in a vertical wall (runs along Y axis)
                    double wallX1, wallX2;
                    if (wall == Wall.East)
                    {
                        wallX1 = ctx.InsideEast;
                        wallX2 = ctx.OutsideEast;
                    }
                    else
                    {
                        wallX1 = ctx.OutsideWest;
                        wallX2 = ctx.InsideWest;
                    }
                    openMinX = Math.Min(wallX1, wallX2);
                    openMaxX = Math.Max(wallX1, wallX2);

                    if (side == WallSide.Start) // South side
                    {
                        openMinY = ctx.InsideSouth + doorOff;
                        openMaxY = openMinY + doorW;
                    }
                    else // North side
                    {
                        openMaxY = ctx.InsideNorth - doorOff;
                        openMinY = openMaxY - doorW;
                    }
                    break;
            }
        }

        /// <summary>
        /// Compute the door clearance rectangle (inside the room).
        /// 48" deep from the door opening, full door width + 12" beyond latch side.
        /// The latch side is the side away from the hinge corner.
        /// </summary>
        private void ComputeDoorClearance(RoomContext ctx,
            out double clrMinX, out double clrMinY,
            out double clrMaxX, out double clrMaxY)
        {
            double s = ctx.Scale;
            double clrDepth = DoorClearanceDepth * s;
            double latchExtra = DoorClearanceLatchBeyond * s;

            double openMinX, openMinY, openMaxX, openMaxY;
            ComputeDoorOpening(ctx, out openMinX, out openMinY, out openMaxX, out openMaxY);

            Wall wall;
            WallSide side;
            GetDoorWallAndSide(ctx.DoorPosition, out wall, out side);

            clrMinX = clrMinY = clrMaxX = clrMaxY = 0;

            switch (wall)
            {
                case Wall.North:
                    // Door is in the north wall; clearance extends south (into room, toward -Y)
                    clrMinY = ctx.InsideNorth - clrDepth;
                    clrMaxY = ctx.InsideNorth;
                    if (side == WallSide.Start) // hinge at west, latch at east
                    {
                        clrMinX = openMinX;
                        clrMaxX = openMaxX + latchExtra;
                    }
                    else // hinge at east, latch at west
                    {
                        clrMinX = openMinX - latchExtra;
                        clrMaxX = openMaxX;
                    }
                    break;

                case Wall.South:
                    // Clearance extends north (into room, toward +Y)
                    clrMinY = ctx.InsideSouth;
                    clrMaxY = ctx.InsideSouth + clrDepth;
                    if (side == WallSide.Start) // hinge at west, latch at east
                    {
                        clrMinX = openMinX;
                        clrMaxX = openMaxX + latchExtra;
                    }
                    else // hinge at east, latch at west
                    {
                        clrMinX = openMinX - latchExtra;
                        clrMaxX = openMaxX;
                    }
                    break;

                case Wall.East:
                    // Clearance extends west (into room, toward -X)
                    clrMinX = ctx.InsideEast - clrDepth;
                    clrMaxX = ctx.InsideEast;
                    if (side == WallSide.Start) // hinge at south, latch at north
                    {
                        clrMinY = openMinY;
                        clrMaxY = openMaxY + latchExtra;
                    }
                    else // hinge at north, latch at south
                    {
                        clrMinY = openMinY - latchExtra;
                        clrMaxY = openMaxY;
                    }
                    break;

                case Wall.West:
                    // Clearance extends east (into room, toward +X)
                    clrMinX = ctx.InsideWest;
                    clrMaxX = ctx.InsideWest + clrDepth;
                    if (side == WallSide.Start) // hinge at south, latch at north
                    {
                        clrMinY = openMinY;
                        clrMaxY = openMaxY + latchExtra;
                    }
                    else // hinge at north, latch at south
                    {
                        clrMinY = openMinY - latchExtra;
                        clrMaxY = openMaxY;
                    }
                    break;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  CLEARANCE OVERLAP CHECK
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns true if two axis-aligned rectangles overlap.
        /// </summary>
        private bool RectsOverlap(
            double aMinX, double aMinY, double aMaxX, double aMaxY,
            double bMinX, double bMinY, double bMaxX, double bMaxY)
        {
            // No overlap if one is entirely to the left/right/above/below the other
            if (aMaxX <= bMinX || bMaxX <= aMinX) return false;
            if (aMaxY <= bMinY || bMaxY <= aMinY) return false;
            return true;
        }

        // ═══════════════════════════════════════════════════════════════
        //  TOILET PLACEMENT
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Compute the toilet fixture bounding rectangle (the physical toilet footprint)
        /// for a candidate placement, used to check it doesn't sit inside another clearance.
        /// We approximate the toilet as ~20" wide x 28" deep for a floor-mounted,
        /// or ~20" wide x 24" deep for wall-hung (these are conservative footprints).
        /// </summary>
        private void ComputeToiletFootprint(RoomContext ctx, Wall wall, WallSide side,
            out double fMinX, out double fMinY, out double fMaxX, out double fMaxY)
        {
            double s = ctx.Scale;
            double offset = ToiletOffsetFromWall * s;
            double halfWidth = 10.0 * s;
            double depth = (ctx.Toilet == ToiletType.WallHung ? 24.0 : 28.0) * s;

            // Effective faces: account for chase wall on any side
            double effN = GetEffectiveInsideFace(ctx, Wall.North);
            double effS = GetEffectiveInsideFace(ctx, Wall.South);
            double effE = GetEffectiveInsideFace(ctx, Wall.East);
            double effW = GetEffectiveInsideFace(ctx, Wall.West);
            double effectiveFace = GetEffectiveFaceForBackWall(ctx, wall);
            fMinX = fMinY = fMaxX = fMaxY = 0;

            switch (wall)
            {
                case Wall.North:
                    fMinY = effectiveFace - depth; fMaxY = effectiveFace;
                    if (side == WallSide.Start)
                    { fMinX = effW + offset - halfWidth; fMaxX = effW + offset + halfWidth; }
                    else
                    { fMinX = effE - offset - halfWidth; fMaxX = effE - offset + halfWidth; }
                    break;
                case Wall.South:
                    fMinY = effectiveFace; fMaxY = effectiveFace + depth;
                    if (side == WallSide.Start)
                    { fMinX = effW + offset - halfWidth; fMaxX = effW + offset + halfWidth; }
                    else
                    { fMinX = effE - offset - halfWidth; fMaxX = effE - offset + halfWidth; }
                    break;
                case Wall.East:
                    fMinX = effectiveFace - depth; fMaxX = effectiveFace;
                    if (side == WallSide.Start)
                    { fMinY = effS + offset - halfWidth; fMaxY = effS + offset + halfWidth; }
                    else
                    { fMinY = effN - offset - halfWidth; fMaxY = effN - offset + halfWidth; }
                    break;
                case Wall.West:
                    fMinX = effectiveFace; fMaxX = effectiveFace + depth;
                    if (side == WallSide.Start)
                    { fMinY = effS + offset - halfWidth; fMaxY = effS + offset + halfWidth; }
                    else
                    { fMinY = effN - offset - halfWidth; fMaxY = effN - offset + halfWidth; }
                    break;
            }
        }

        /// <summary>
        /// Compute the toilet clearance rectangle for a candidate placement.
        /// Clearance extends from the back wall (inside face) out by the depth clearance,
        /// and from the perpendicular wall out by the width clearance.
        /// </summary>
        private void ComputeToiletClearance(RoomContext ctx, Wall wall, WallSide side,
            out double clrMinX, out double clrMinY, out double clrMaxX, out double clrMaxY)
        {
            double s = ctx.Scale;
            double clearDepth = (ctx.Toilet == ToiletType.WallHung
                ? ToiletClearDepthWallHung : ToiletClearDepthFloorMtd) * s;
            double clearWidth = ToiletClearWidth * s;

            // Use effective face: clearances are measured from the finished wall surface
            // (chase face for wall-hung, structural face for floor-mounted)
            double effectiveFace = GetEffectiveFaceForBackWall(ctx, wall);
            double effN = GetEffectiveInsideFace(ctx, Wall.North);
            double effS = GetEffectiveInsideFace(ctx, Wall.South);
            double effE = GetEffectiveInsideFace(ctx, Wall.East);
            double effW = GetEffectiveInsideFace(ctx, Wall.West);
            clrMinX = clrMinY = clrMaxX = clrMaxY = 0;

            switch (wall)
            {
                case Wall.North:
                    clrMinY = effectiveFace - clearDepth;
                    clrMaxY = effectiveFace;
                    if (side == WallSide.Start)
                    { clrMinX = effW; clrMaxX = effW + clearWidth; }
                    else
                    { clrMaxX = effE; clrMinX = effE - clearWidth; }
                    break;
                case Wall.South:
                    clrMinY = effectiveFace;
                    clrMaxY = effectiveFace + clearDepth;
                    if (side == WallSide.Start)
                    { clrMinX = effW; clrMaxX = effW + clearWidth; }
                    else
                    { clrMaxX = effE; clrMinX = effE - clearWidth; }
                    break;
                case Wall.East:
                    clrMinX = effectiveFace - clearDepth;
                    clrMaxX = effectiveFace;
                    if (side == WallSide.Start)
                    { clrMinY = effS; clrMaxY = effS + clearWidth; }
                    else
                    { clrMaxY = effN; clrMinY = effN - clearWidth; }
                    break;
                case Wall.West:
                    clrMinX = effectiveFace;
                    clrMaxX = effectiveFace + clearWidth;
                    if (side == WallSide.Start)
                    { clrMinY = effS; clrMaxY = effS + clearWidth; }
                    else
                    { clrMaxY = effN; clrMinY = effN - clearWidth; }
                    break;
            }
        }

        /// <summary>
        /// Get the inside face coordinate for a given wall (structural wall only).
        /// </summary>
        private double GetInsideFace(RoomContext ctx, Wall wall)
        {
            switch (wall)
            {
                case Wall.North: return ctx.InsideNorth;
                case Wall.South: return ctx.InsideSouth;
                case Wall.East:  return ctx.InsideEast;
                case Wall.West:  return ctx.InsideWest;
                default: return 0;
            }
        }

        /// <summary>
        /// Get the effective inside face for ANY wall, accounting for the chase wall.
        /// If the toilet is wall-hung and the chase wall is on this wall,
        /// return the room-facing chase surface instead of the structural wall face.
        /// This is used for perpendicular offsets — e.g., if the chase is on the west wall,
        /// a fixture on the south wall near the west side must measure its offset from the
        /// chase face, not the structural west wall.
        /// </summary>
        private double GetEffectiveInsideFace(RoomContext ctx, Wall wall)
        {
            if (ctx.Toilet == ToiletType.WallHung && wall == ctx.ToiletWall)
            {
                return GetEffectiveFaceForBackWall(ctx, wall);
            }
            return GetInsideFace(ctx, wall);
        }

        /// <summary>
        /// Get the effective inside face for a candidate back wall, accounting for
        /// the chase wall offset when the toilet is wall-hung.
        /// Use this for clearance and footprint calculations on the toilet's back wall.
        /// For wall-hung: the effective face is the room-facing surface of the chase
        ///   (structural inside face + 12" clear + 4-1/8" chase thickness into room).
        /// For floor-mounted or non-back-walls: returns the normal inside face.
        /// </summary>
        private double GetEffectiveFaceForBackWall(RoomContext ctx, Wall candidateBackWall)
        {
            double s = ctx.Scale;
            double insideFace = GetInsideFace(ctx, candidateBackWall);

            if (ctx.Toilet != ToiletType.WallHung)
                return insideFace;

            // Wall-hung: offset into room by chase clear + chase thickness
            double chaseOffset = (ChaseWallClearance + ChaseWallThickness) * s;

            switch (candidateBackWall)
            {
                case Wall.North: return insideFace - chaseOffset;  // moves south into room
                case Wall.South: return insideFace + chaseOffset;  // moves north into room
                case Wall.East:  return insideFace - chaseOffset;  // moves west into room
                case Wall.West:  return insideFace + chaseOffset;  // moves east into room
                default: return insideFace;
            }
        }

        /// <summary>
        /// Try to place the toilet. Preference order:
        /// 1. Adjacent wall, away from door — where sink can also fit on same wall (plumbing consolidation)
        /// 2. Adjacent wall, away from door — even if sink must go elsewhere
        /// 3. Other adjacent wall corners
        /// 4. Opposite wall corners
        /// Never on the same wall as the door.
        /// Must not conflict with door clearance.
        /// Must fit within room dimensions.
        /// </summary>
        private bool PlaceToilet(RoomContext ctx)
        {
            Wall doorWall;
            WallSide doorSide;
            GetDoorWallAndSide(ctx.DoorPosition, out doorWall, out doorSide);

            double s = ctx.Scale;
            double buffer = 1.0 * s; // 1" clearance buffer to prevent near-overlap

            // Build the preferred placement order
            var candidates = BuildToiletCandidates(doorWall, doorSide);

            // Get door clearance for checking
            double dClrMinX, dClrMinY, dClrMaxX, dClrMaxY;
            ComputeDoorClearance(ctx, out dClrMinX, out dClrMinY, out dClrMaxX, out dClrMaxY);

            // Two passes: first pass only accepts placements where sink can share the wall.
            // Second pass accepts any valid placement.
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (var candidate in candidates)
                {
                    Wall cWall = candidate.Item1;
                    WallSide cSide = candidate.Item2;

                    // Skip same wall as door
                    if (cWall == doorWall)
                        continue;

                    // Check toilet clearance fits in room
                    double tClrMinX, tClrMinY, tClrMaxX, tClrMaxY;
                    ComputeToiletClearance(ctx, cWall, cSide, out tClrMinX, out tClrMinY, out tClrMaxX, out tClrMaxY);

                    if (tClrMinX < ctx.InsideWest - 0.01 || tClrMaxX > ctx.InsideEast + 0.01 ||
                        tClrMinY < ctx.InsideSouth - 0.01 || tClrMaxY > ctx.InsideNorth + 0.01)
                        continue;

                    // Check toilet footprint doesn't overlap door clearance (with buffer)
                    double fMinX, fMinY, fMaxX, fMaxY;
                    ComputeToiletFootprint(ctx, cWall, cSide, out fMinX, out fMinY, out fMaxX, out fMaxY);

                    if (RectsOverlap(fMinX - buffer, fMinY - buffer, fMaxX + buffer, fMaxY + buffer,
                        dClrMinX, dClrMinY, dClrMaxX, dClrMaxY))
                        continue;

                    // Pass 0: also require that the sink can fit on the opposite corner of this wall
                    if (pass == 0)
                    {
                        WallSide sinkSide = (cSide == WallSide.Start) ? WallSide.End : WallSide.Start;
                        if (!CanSinkFitAt(ctx, cWall, sinkSide, dClrMinX, dClrMinY, dClrMaxX, dClrMaxY,
                            tClrMinX, tClrMinY, tClrMaxX, tClrMaxY, buffer))
                            continue;
                    }

                    // Valid placement found
                    ctx.ToiletWall = cWall;
                    ctx.ToiletSide = cSide;
                    ComputeToiletInsertionPoint(ctx);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Quick check: can a sink fit at a given wall/side without overlapping
        /// the door clearance or toilet clearance?
        /// </summary>
        private bool CanSinkFitAt(RoomContext ctx, Wall wall, WallSide side,
            double dClrMinX, double dClrMinY, double dClrMaxX, double dClrMaxY,
            double tClrMinX, double tClrMinY, double tClrMaxX, double tClrMaxY,
            double buffer)
        {
            // Check sink footprint against door clearance
            double sfMinX, sfMinY, sfMaxX, sfMaxY;
            ComputeSinkFootprint(ctx, wall, side, out sfMinX, out sfMinY, out sfMaxX, out sfMaxY);

            if (RectsOverlap(sfMinX - buffer, sfMinY - buffer, sfMaxX + buffer, sfMaxY + buffer,
                dClrMinX, dClrMinY, dClrMaxX, dClrMaxY))
                return false;

            // Check sink footprint against toilet clearance
            if (RectsOverlap(sfMinX - buffer, sfMinY - buffer, sfMaxX + buffer, sfMaxY + buffer,
                tClrMinX, tClrMinY, tClrMaxX, tClrMaxY))
                return false;

            // Check sink clearance fits in room
            double scMinX, scMinY, scMaxX, scMaxY;
            ComputeSinkClearance(ctx, wall, side, out scMinX, out scMinY, out scMaxX, out scMaxY);

            if (scMinX < ctx.InsideWest - 0.01 || scMaxX > ctx.InsideEast + 0.01 ||
                scMinY < ctx.InsideSouth - 0.01 || scMaxY > ctx.InsideNorth + 0.01)
                return false;

            return true;
        }

        /// <summary>
        /// Build preferred toilet placement candidates.
        /// Ideal: adjacent wall, away from door. Then other adjacent corners.
        /// Then opposite wall corners. Never same wall as door.
        /// </summary>
        private List<Tuple<Wall, WallSide>> BuildToiletCandidates(Wall doorWall, WallSide doorSide)
        {
            var result = new List<Tuple<Wall, WallSide>>();

            // Get the two walls adjacent to the door wall
            Wall adjLeft, adjRight, opposite;
            GetAdjacentWalls(doorWall, out adjLeft, out adjRight, out opposite);

            // "Away from door" means: if door is on NorthEast (north wall, east side),
            // the ideal toilet is on the adjacent wall furthest from the door corner.
            // For NorthEast door: door hinge is at NE corner.
            //   adjRight = East, adjLeft = West
            //   Ideal = WestSouth (west wall, south side — far from NE corner)

            // The "away" side on each adjacent wall is the side away from the door's corner
            WallSide awayOnLeft = GetAwaySide(adjLeft, doorWall, doorSide);
            WallSide awayOnRight = GetAwaySide(adjRight, doorWall, doorSide);
            WallSide nearOnLeft = awayOnLeft == WallSide.Start ? WallSide.End : WallSide.Start;
            WallSide nearOnRight = awayOnRight == WallSide.Start ? WallSide.End : WallSide.Start;

            // Priority 1: adjacent wall, away side (ideal — maximally distant from door)
            result.Add(Tuple.Create(adjLeft, awayOnLeft));
            result.Add(Tuple.Create(adjRight, awayOnRight));

            // Priority 2: adjacent wall, near side (still not on door wall)
            result.Add(Tuple.Create(adjLeft, nearOnLeft));
            result.Add(Tuple.Create(adjRight, nearOnRight));

            // Priority 3: opposite wall (last resort)
            result.Add(Tuple.Create(opposite, WallSide.Start));
            result.Add(Tuple.Create(opposite, WallSide.End));

            return result;
        }

        private void GetAdjacentWalls(Wall wall, out Wall left, out Wall right, out Wall opposite)
        {
            switch (wall)
            {
                case Wall.North: left = Wall.West;  right = Wall.East;  opposite = Wall.South; break;
                case Wall.South: left = Wall.West;  right = Wall.East;  opposite = Wall.North; break;
                case Wall.East:  left = Wall.North;  right = Wall.South; opposite = Wall.West;  break;
                case Wall.West:  left = Wall.North;  right = Wall.South; opposite = Wall.East;  break;
                default:         left = Wall.West;  right = Wall.East;  opposite = Wall.South; break;
            }
        }

        /// <summary>
        /// Determine which side of an adjacent wall is "away" from the door corner.
        /// </summary>
        private WallSide GetAwaySide(Wall adjWall, Wall doorWall, WallSide doorSide)
        {
            // The door corner is at the intersection of doorWall and the perpendicular wall
            // at the doorSide end. We want the side of adjWall that is far from that corner.

            // For N/S walls: Start = west end, End = east end
            // For E/W walls: Start = south end, End = north end

            // Example: door at NorthEast (north wall, east end)
            //   Door corner is at NE corner of room
            //   adjLeft = West wall: "away" = south end = Start
            //   adjRight = East wall: "away" = south end = Start

            // Example: door at SouthWest (south wall, west end)
            //   Door corner is at SW corner of room
            //   adjLeft = West wall: "away" = north end = End
            //   adjRight = East wall: "away" = north end = End

            bool doorAtNorthEnd = (doorWall == Wall.North) ||
                                  (doorWall == Wall.East && doorSide == WallSide.End) ||
                                  (doorWall == Wall.West && doorSide == WallSide.End);
            bool doorAtSouthEnd = (doorWall == Wall.South) ||
                                  (doorWall == Wall.East && doorSide == WallSide.Start) ||
                                  (doorWall == Wall.West && doorSide == WallSide.Start);
            bool doorAtEastEnd  = (doorWall == Wall.East) ||
                                  (doorWall == Wall.North && doorSide == WallSide.End) ||
                                  (doorWall == Wall.South && doorSide == WallSide.End);
            bool doorAtWestEnd  = (doorWall == Wall.West) ||
                                  (doorWall == Wall.North && doorSide == WallSide.Start) ||
                                  (doorWall == Wall.South && doorSide == WallSide.Start);

            switch (adjWall)
            {
                case Wall.North:
                case Wall.South:
                    // Start = west, End = east
                    // "Away" from door = opposite end from where the door corner is
                    return doorAtEastEnd ? WallSide.Start : WallSide.End;

                case Wall.East:
                case Wall.West:
                    // Start = south, End = north
                    return doorAtNorthEnd ? WallSide.Start : WallSide.End;

                default:
                    return WallSide.Start;
            }
        }

        /// <summary>
        /// Get the effective inside face of the toilet's back wall.
        /// For wall-hung: this is the room-facing surface of the chase wall
        ///   (fixtures mount to the chase, not the structural back wall).
        /// For floor-mounted: this is the inside face of the thickened wet wall.
        /// </summary>
        private double GetEffectiveBackWallFace(RoomContext ctx)
        {
            double s = ctx.Scale;
            double chaseClear = ChaseWallClearance * s;
            double chaseThick = ChaseWallThickness * s;
            double insideFace = GetInsideFace(ctx, ctx.ToiletWall);

            if (ctx.Toilet == ToiletType.FloorMounted)
            {
                // Floor-mounted: fixtures sit against the inside face of the wet wall
                return insideFace;
            }

            // Wall-hung: fixtures mount to the room-facing surface of the chase wall.
            // Chase is positioned: insideFace → (chaseClear gap) → (chaseThick wall)
            // The room-facing surface is inset from the back wall by (chaseClear + chaseThick).
            switch (ctx.ToiletWall)
            {
                case Wall.North: return insideFace - chaseClear - chaseThick;
                case Wall.South: return insideFace + chaseClear + chaseThick;
                case Wall.East:  return insideFace - chaseClear - chaseThick;
                case Wall.West:  return insideFace + chaseClear + chaseThick;
                default: return insideFace;
            }
        }

        /// <summary>
        /// Compute the toilet insertion point and rotation.
        /// Insertion point = on the effective back wall face (chase face if wall-hung,
        ///   wet wall face if floor-mounted), at 17" from perpendicular wall.
        /// Rotation = angle to rotate from +Y (home orientation) to face into the room.
        /// </summary>
        private void ComputeToiletInsertionPoint(RoomContext ctx)
        {
            double s = ctx.Scale;
            double offset = ToiletOffsetFromWall * s;
            double backFace = GetEffectiveBackWallFace(ctx);

            // Use effective faces for perpendicular wall offsets
            double effN = GetEffectiveInsideFace(ctx, Wall.North);
            double effS = GetEffectiveInsideFace(ctx, Wall.South);
            double effE = GetEffectiveInsideFace(ctx, Wall.East);
            double effW = GetEffectiveInsideFace(ctx, Wall.West);

            double x = 0, y = 0;
            double rot = 0;

            switch (ctx.ToiletWall)
            {
                case Wall.North:
                    y = backFace;
                    x = (ctx.ToiletSide == WallSide.Start) ? effW + offset : effE - offset;
                    rot = 180.0;
                    break;
                case Wall.South:
                    y = backFace;
                    x = (ctx.ToiletSide == WallSide.Start) ? effW + offset : effE - offset;
                    rot = 0.0;
                    break;
                case Wall.East:
                    x = backFace;
                    y = (ctx.ToiletSide == WallSide.Start) ? effS + offset : effN - offset;
                    rot = 90.0;
                    break;
                case Wall.West:
                    x = backFace;
                    y = (ctx.ToiletSide == WallSide.Start) ? effS + offset : effN - offset;
                    rot = -90.0;
                    break;
            }

            ctx.ToiletInsertPt = new Point3d(x, y, 0);
            ctx.ToiletRotationDeg = rot;
        }

        /// <summary>
        /// Determine whether the toilet needs the _Left or _Right grab bar variant.
        ///
        /// In the .3dm files (home orientation: front faces +Y):
        ///   _Right = grab bars on +X side (right when facing toilet from front)
        ///   _Left  = grab bars on -X side (left when facing toilet from front)
        ///
        /// The grab bars must be on the perpendicular wall side (the wall 17" from toilet CL).
        ///
        /// RhinoCommon rotation is counter-clockwise. After rotation, home +X maps to:
        ///   0° (south wall): +X → +X = east
        /// 180° (north wall): +X → -X = west
        ///  90° (east wall):  +X → +Y = north  (CCW 90°: (1,0)→(0,1))
        /// -90° (west wall):  +X → -Y = south  (CW 90°: (1,0)→(0,-1))
        ///
        /// Perpendicular wall sides:
        ///   N/S walls: Start = west, End = east
        ///   E/W walls: Start = south, End = north
        ///
        /// Use _Right when perp wall is on the side where +X ends up.
        /// Use _Left when perp wall is on the opposite side.
        /// </summary>
        private bool NeedsGrabBarsOnLeft(RoomContext ctx)
        {
            switch (ctx.ToiletWall)
            {
                case Wall.South:
                    return ctx.ToiletSide == WallSide.End;

                case Wall.North:
                    return ctx.ToiletSide == WallSide.Start;

                case Wall.East:
                    return ctx.ToiletSide == WallSide.End;

                case Wall.West:
                    return ctx.ToiletSide == WallSide.Start;

                default:
                    return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  SINK PLACEMENT
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Compute the sink clearance rectangle for a candidate placement.
        /// 30" wide × 48" deep, measured from the wall face.
        /// </summary>
        private void ComputeSinkClearance(RoomContext ctx, Wall wall, WallSide side,
            out double clrMinX, out double clrMinY, out double clrMaxX, out double clrMaxY)
        {
            double s = ctx.Scale;
            double clearW = SinkClearWidth * s;
            double clearD = SinkClearDepth * s;
            double offset = SinkOffsetFromWall * s;

            // Use effective faces for both the back wall and perpendicular walls
            double insideFace = GetEffectiveInsideFace(ctx, wall);
            double effN = GetEffectiveInsideFace(ctx, Wall.North);
            double effS = GetEffectiveInsideFace(ctx, Wall.South);
            double effE = GetEffectiveInsideFace(ctx, Wall.East);
            double effW = GetEffectiveInsideFace(ctx, Wall.West);
            clrMinX = clrMinY = clrMaxX = clrMaxY = 0;

            switch (wall)
            {
                case Wall.North:
                    clrMinY = insideFace - clearD; clrMaxY = insideFace;
                    if (side == WallSide.Start)
                    { double cx = effW + offset; clrMinX = cx - clearW / 2; clrMaxX = cx + clearW / 2; }
                    else
                    { double cx = effE - offset; clrMinX = cx - clearW / 2; clrMaxX = cx + clearW / 2; }
                    break;
                case Wall.South:
                    clrMinY = insideFace; clrMaxY = insideFace + clearD;
                    if (side == WallSide.Start)
                    { double cx = effW + offset; clrMinX = cx - clearW / 2; clrMaxX = cx + clearW / 2; }
                    else
                    { double cx = effE - offset; clrMinX = cx - clearW / 2; clrMaxX = cx + clearW / 2; }
                    break;
                case Wall.East:
                    clrMinX = insideFace - clearD; clrMaxX = insideFace;
                    if (side == WallSide.Start)
                    { double cy = effS + offset; clrMinY = cy - clearW / 2; clrMaxY = cy + clearW / 2; }
                    else
                    { double cy = effN - offset; clrMinY = cy - clearW / 2; clrMaxY = cy + clearW / 2; }
                    break;
                case Wall.West:
                    clrMinX = insideFace; clrMaxX = insideFace + clearD;
                    if (side == WallSide.Start)
                    { double cy = effS + offset; clrMinY = cy - clearW / 2; clrMaxY = cy + clearW / 2; }
                    else
                    { double cy = effN - offset; clrMinY = cy - clearW / 2; clrMaxY = cy + clearW / 2; }
                    break;
            }
        }

        /// <summary>
        /// Compute sink footprint (approximate physical sink bounds).
        /// ~22" wide x 18" deep.
        /// </summary>
        private void ComputeSinkFootprint(RoomContext ctx, Wall wall, WallSide side,
            out double fMinX, out double fMinY, out double fMaxX, out double fMaxY)
        {
            double s = ctx.Scale;
            double offset = SinkOffsetFromWall * s;
            double halfW = 11.0 * s;  // ~22" wide
            double depth = 18.0 * s;

            // Use effective faces for both the back wall and perpendicular walls
            bool onToiletWall = (ctx.ToiletWall == wall);
            double insideFace = onToiletWall ? GetEffectiveFaceForBackWall(ctx, wall) : GetInsideFace(ctx, wall);
            double effN = GetEffectiveInsideFace(ctx, Wall.North);
            double effS = GetEffectiveInsideFace(ctx, Wall.South);
            double effE = GetEffectiveInsideFace(ctx, Wall.East);
            double effW = GetEffectiveInsideFace(ctx, Wall.West);
            fMinX = fMinY = fMaxX = fMaxY = 0;

            switch (wall)
            {
                case Wall.North:
                    fMinY = insideFace - depth; fMaxY = insideFace;
                    if (side == WallSide.Start)
                    { fMinX = effW + offset - halfW; fMaxX = effW + offset + halfW; }
                    else
                    { fMinX = effE - offset - halfW; fMaxX = effE - offset + halfW; }
                    break;
                case Wall.South:
                    fMinY = insideFace; fMaxY = insideFace + depth;
                    if (side == WallSide.Start)
                    { fMinX = effW + offset - halfW; fMaxX = effW + offset + halfW; }
                    else
                    { fMinX = effE - offset - halfW; fMaxX = effE - offset + halfW; }
                    break;
                case Wall.East:
                    fMinX = insideFace - depth; fMaxX = insideFace;
                    if (side == WallSide.Start)
                    { fMinY = effS + offset - halfW; fMaxY = effS + offset + halfW; }
                    else
                    { fMinY = effN - offset - halfW; fMaxY = effN - offset + halfW; }
                    break;
                case Wall.West:
                    fMinX = insideFace; fMaxX = insideFace + depth;
                    if (side == WallSide.Start)
                    { fMinY = effS + offset - halfW; fMaxY = effS + offset + halfW; }
                    else
                    { fMinY = effN - offset - halfW; fMaxY = effN - offset + halfW; }
                    break;
            }
        }

        /// <summary>
        /// Place the sink. Preference order:
        /// 1. Same wall as toilet, opposite corner
        /// 2. Adjacent walls
        /// 3. Opposite wall from toilet
        /// Must not overlap door clearance or toilet clearance.
        /// </summary>
        private bool PlaceSink(RoomContext ctx)
        {
            double s = ctx.Scale;
            double buffer = 1.0 * s; // 1" clearance buffer

            // Get door clearance
            double dClrMinX, dClrMinY, dClrMaxX, dClrMaxY;
            ComputeDoorClearance(ctx, out dClrMinX, out dClrMinY, out dClrMaxX, out dClrMaxY);

            // Get toilet clearance
            double tClrMinX, tClrMinY, tClrMaxX, tClrMaxY;
            ComputeToiletClearance(ctx, ctx.ToiletWall, ctx.ToiletSide,
                out tClrMinX, out tClrMinY, out tClrMaxX, out tClrMaxY);

            // Build candidate list
            var candidates = BuildSinkCandidates(ctx);

            Wall doorWall;
            WallSide doorSide;
            GetDoorWallAndSide(ctx.DoorPosition, out doorWall, out doorSide);

            foreach (var candidate in candidates)
            {
                Wall cWall = candidate.Item1;
                WallSide cSide = candidate.Item2;

                // Don't place sink at same position as toilet
                if (cWall == ctx.ToiletWall && cSide == ctx.ToiletSide)
                    continue;

                // Check sink footprint doesn't overlap door clearance (with buffer)
                double sfMinX, sfMinY, sfMaxX, sfMaxY;
                ComputeSinkFootprint(ctx, cWall, cSide, out sfMinX, out sfMinY, out sfMaxX, out sfMaxY);

                if (RectsOverlap(sfMinX - buffer, sfMinY - buffer, sfMaxX + buffer, sfMaxY + buffer,
                    dClrMinX, dClrMinY, dClrMaxX, dClrMaxY))
                    continue;

                // Check sink footprint doesn't overlap toilet clearance (with buffer)
                if (RectsOverlap(sfMinX - buffer, sfMinY - buffer, sfMaxX + buffer, sfMaxY + buffer,
                    tClrMinX, tClrMinY, tClrMaxX, tClrMaxY))
                    continue;

                // Check sink clearance fits in room
                double scMinX, scMinY, scMaxX, scMaxY;
                ComputeSinkClearance(ctx, cWall, cSide, out scMinX, out scMinY, out scMaxX, out scMaxY);

                if (scMinX < ctx.InsideWest - 0.01 || scMaxX > ctx.InsideEast + 0.01 ||
                    scMinY < ctx.InsideSouth - 0.01 || scMaxY > ctx.InsideNorth + 0.01)
                    continue;

                // Valid placement
                ctx.SinkWall = cWall;
                ctx.SinkSide = cSide;
                ComputeSinkInsertionPoint(ctx);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Build preferred sink placement candidates.
        /// 1. Same wall as toilet, opposite corner
        /// 2. Other walls (not door wall first, then door wall as last resort)
        /// </summary>
        private List<Tuple<Wall, WallSide>> BuildSinkCandidates(RoomContext ctx)
        {
            var result = new List<Tuple<Wall, WallSide>>();
            Wall doorWall;
            WallSide doorSide;
            GetDoorWallAndSide(ctx.DoorPosition, out doorWall, out doorSide);

            // Priority 1: same wall as toilet, opposite corner
            WallSide oppSide = ctx.ToiletSide == WallSide.Start ? WallSide.End : WallSide.Start;
            result.Add(Tuple.Create(ctx.ToiletWall, oppSide));

            // Priority 2: other walls (not door wall), both sides
            Wall[] allWalls = { Wall.North, Wall.East, Wall.South, Wall.West };
            foreach (var w in allWalls)
            {
                if (w == ctx.ToiletWall || w == doorWall) continue;
                result.Add(Tuple.Create(w, WallSide.Start));
                result.Add(Tuple.Create(w, WallSide.End));
            }

            // Priority 3: door wall (least preferred, but possible)
            result.Add(Tuple.Create(doorWall, WallSide.Start));
            result.Add(Tuple.Create(doorWall, WallSide.End));

            return result;
        }

        /// <summary>
        /// Compute sink insertion point and rotation.
        /// If the sink is on the same wall as the toilet, it mounts to the
        /// effective back wall face (chase face for wall-hung).
        /// </summary>
        private void ComputeSinkInsertionPoint(RoomContext ctx)
        {
            double s = ctx.Scale;
            double offset = SinkOffsetFromWall * s;

            // Use effective faces for all walls (accounts for chase)
            double effN = GetEffectiveInsideFace(ctx, Wall.North);
            double effS = GetEffectiveInsideFace(ctx, Wall.South);
            double effE = GetEffectiveInsideFace(ctx, Wall.East);
            double effW = GetEffectiveInsideFace(ctx, Wall.West);

            double x = 0, y = 0;
            double rot = 0;

            // If sink is on the toilet's back wall, use the effective face (chase if wall-hung)
            bool onToiletWall = (ctx.SinkWall == ctx.ToiletWall);

            switch (ctx.SinkWall)
            {
                case Wall.North:
                    y = onToiletWall ? GetEffectiveBackWallFace(ctx) : ctx.InsideNorth;
                    x = (ctx.SinkSide == WallSide.Start) ? effW + offset : effE - offset;
                    rot = 180.0;
                    break;
                case Wall.South:
                    y = onToiletWall ? GetEffectiveBackWallFace(ctx) : ctx.InsideSouth;
                    x = (ctx.SinkSide == WallSide.Start) ? effW + offset : effE - offset;
                    rot = 0.0;
                    break;
                case Wall.East:
                    x = onToiletWall ? GetEffectiveBackWallFace(ctx) : ctx.InsideEast;
                    y = (ctx.SinkSide == WallSide.Start) ? effS + offset : effN - offset;
                    rot = 90.0;
                    break;
                case Wall.West:
                    x = onToiletWall ? GetEffectiveBackWallFace(ctx) : ctx.InsideWest;
                    y = (ctx.SinkSide == WallSide.Start) ? effS + offset : effN - offset;
                    rot = -90.0;
                    break;
            }

            ctx.SinkInsertPt = new Point3d(x, y, 0);
            ctx.SinkRotationDeg = rot;
        }

        // ═══════════════════════════════════════════════════════════════
        //  TURNING CIRCLE CHECK
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Check whether a 69" (5'-9") turning circle fits in the room.
        /// The circle can overlap fixture clearances (per ADA) but cannot
        /// overlap walls, chase walls, or fixture bodies — EXCEPT it can
        /// extend up to 3" under the sink.
        ///
        /// Approach: the room is bounded by effective wall faces (accounting
        /// for chase). The turning circle must fit within these bounds.
        /// The sink projects from the wall by its depth (~18"), but we allow
        /// the circle to extend 3" into that zone. We test whether the
        /// circle of radius R can be placed with its center somewhere in
        /// the room such that it doesn't hit any wall. Since the room is
        /// rectangular (with possibly a chase wall reducing one dimension),
        /// the circle fits if the effective clear dimensions in both X and Y
        /// are >= the circle diameter, OR if the 3" sink underhang allowance
        /// makes it work.
        /// </summary>
        private bool CheckTurningCircle(RoomContext ctx)
        {
            double s = ctx.Scale;

            // Get effective room bounds (accounts for chase wall)
            double effN = GetEffectiveInsideFace(ctx, Wall.North);
            double effS = GetEffectiveInsideFace(ctx, Wall.South);
            double effE = GetEffectiveInsideFace(ctx, Wall.East);
            double effW = GetEffectiveInsideFace(ctx, Wall.West);

            // Convert clear dimensions back to inches for comparison
            double clearXInches = (effE - effW) / s;
            double clearYInches = (effN - effS) / s;

            RhinoApp.WriteLine(string.Format(
                "RhinoClaude: Turning circle check — need {0:F1}\", room clear: {1:F1}\" x {2:F1}\"",
                TurningCircleDiameter, clearXInches, clearYInches));

            // First check: does the circle fit in the raw clear space?
            if (clearXInches >= TurningCircleDiameter && clearYInches >= TurningCircleDiameter)
            {
                RhinoApp.WriteLine("RhinoClaude: Turning circle fits in clear space.");
                return true;
            }

            // Second check: with 3" sink underhang allowance on the sink's wall
            double allowedXInches = clearXInches;
            double allowedYInches = clearYInches;

            switch (ctx.SinkWall)
            {
                case Wall.North:
                case Wall.South:
                    allowedYInches += SinkUnderhang;
                    break;
                case Wall.East:
                case Wall.West:
                    allowedXInches += SinkUnderhang;
                    break;
            }

            if (allowedXInches >= TurningCircleDiameter && allowedYInches >= TurningCircleDiameter)
            {
                RhinoApp.WriteLine(string.Format(
                    "RhinoClaude: Turning circle fits with 3\" sink underhang ({0:F1}\" x {1:F1}\")",
                    allowedXInches, allowedYInches));
                return true;
            }

            RhinoApp.WriteLine(string.Format(
                "RhinoClaude: Turning circle FAILS. Need {0:F1}\", have {1:F1}\" x {2:F1}\" (with sink: {3:F1}\" x {4:F1}\")",
                TurningCircleDiameter, clearXInches, clearYInches, allowedXInches, allowedYInches));
            return false;
        }

        // ═══════════════════════════════════════════════════════════════
        //  BUILD PLAN DISPLAY
        // ═══════════════════════════════════════════════════════════════

        private void PrintBuildPlan(RoomContext ctx, RhinoDoc doc)
        {
            string units = doc.ModelUnitSystem.ToString();
            RhinoApp.WriteLine("═══════════════════════════════════════════");
            RhinoApp.WriteLine("  RESTROOM BUILD PLAN");
            RhinoApp.WriteLine("═══════════════════════════════════════════");
            RhinoApp.WriteLine(string.Format("  Boundary: ({0:F2}, {1:F2}) to ({2:F2}, {3:F2})",
                ctx.BndMinX, ctx.BndMinY, ctx.BndMaxX, ctx.BndMaxY));
            RhinoApp.WriteLine("  Interior clear: {0:F2} x {1:F2} {2}",
                ctx.ClearWidth, ctx.ClearHeight, units);
            RhinoApp.WriteLine("  Wall height: {0:F2} {1}", WallHeight * ctx.Scale, units);
            RhinoApp.WriteLine("─────────────────────────────────────────");
            RhinoApp.WriteLine("  Door: {0} (36\" clear, swings outward)", ctx.DoorPosition);
            RhinoApp.WriteLine("  Toilet: {0} on {1} wall, {2} side",
                ctx.Toilet, ctx.ToiletWall, ctx.ToiletSide);
            RhinoApp.WriteLine("    Insert: ({0:F2}, {1:F2}), rotation: {2}°",
                ctx.ToiletInsertPt.X, ctx.ToiletInsertPt.Y, ctx.ToiletRotationDeg);
            RhinoApp.WriteLine("  Sink: {0} wall, {1} side", ctx.SinkWall, ctx.SinkSide);
            RhinoApp.WriteLine("    Insert: ({0:F2}, {1:F2}), rotation: {2}°",
                ctx.SinkInsertPt.X, ctx.SinkInsertPt.Y, ctx.SinkRotationDeg);
            if (ctx.HasChaseWall)
                RhinoApp.WriteLine("  Chase wall: YES (wall-hung toilet, 12\" clear, 4-1/8\" thick)");

            Wall doorWall;
            WallSide doorSide;
            GetDoorWallAndSide(ctx.DoorPosition, out doorWall, out doorSide);
            string wetWallLabel = ctx.ToiletWall.ToString();
            RhinoApp.WriteLine("  Wet wall: {0} ({1})",
                wetWallLabel,
                ctx.Toilet == ToiletType.FloorMounted ? "7-1/8\" thick" : "standard + chase");
            RhinoApp.WriteLine("═══════════════════════════════════════════");
        }

        // ═══════════════════════════════════════════════════════════════
        //  GEOMETRY BUILDING
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Build all room geometry: walls (with door opening), chase wall, fixtures.
        /// </summary>
        private void BuildRoom(RhinoDoc doc, RoomContext ctx)
        {
            double s = ctx.Scale;
            double wallH = WallHeight * s;

            // Ensure layers exist
            int wallLayer = EnsureLayer(doc, "RC-Walls", System.Drawing.Color.Gray);
            int fixtureLayer = EnsureLayer(doc, "RC-Fixtures", System.Drawing.Color.SteelBlue);
            int doorLayer = EnsureLayer(doc, "RC-Doors", System.Drawing.Color.SaddleBrown);

            // ── Build 4 walls as boxes, then boolean-subtract the door opening ──

            Wall doorWall;
            WallSide doorSide;
            GetDoorWallAndSide(ctx.DoorPosition, out doorWall, out doorSide);

            double openMinX, openMinY, openMaxX, openMaxY;
            ComputeDoorOpening(ctx, out openMinX, out openMinY, out openMaxX, out openMaxY);

            // Build each wall
            BuildWall(doc, ctx, Wall.North, wallH, wallLayer, doorWall, openMinX, openMinY, openMaxX, openMaxY);
            BuildWall(doc, ctx, Wall.South, wallH, wallLayer, doorWall, openMinX, openMinY, openMaxX, openMaxY);
            BuildWall(doc, ctx, Wall.East,  wallH, wallLayer, doorWall, openMinX, openMinY, openMaxX, openMaxY);
            BuildWall(doc, ctx, Wall.West,  wallH, wallLayer, doorWall, openMinX, openMinY, openMaxX, openMaxY);

            // ── Chase wall (wall-hung only) ──
            if (ctx.HasChaseWall)
            {
                var chaseBox = new Box(Plane.WorldXY,
                    new Interval(ctx.ChaseWallMin.X, ctx.ChaseWallMax.X),
                    new Interval(ctx.ChaseWallMin.Y, ctx.ChaseWallMax.Y),
                    new Interval(0, wallH));
                var chaseBrep = chaseBox.ToBrep();
                if (chaseBrep != null)
                {
                    var attr = MakeAttributes(wallLayer);
                    attr.SetUserString("RC:ElementType", "Wall");
                    attr.SetUserString("RC:IntExt", "Interior");
                    attr.SetUserString("RC:SystemType", "Architectural");
                    attr.SetUserString("RC:AssemblyType", "Plumbing Chase");
                    attr.SetUserString("RC:Description", "Chase Wall");
                    doc.Objects.AddBrep(chaseBrep, attr);
                }
            }

            // ── Door panel (simple flat box in the opening) ──
            BuildDoorPanel(doc, ctx, doorLayer, openMinX, openMinY, openMaxX, openMaxY);

            // ── Import toilet fixture ──
            string libraryPath = FindLibraryPath();

            // Determine grab bar side: grab bars go on the perpendicular wall side.
            // Files: _Right = grab bars on right when facing toilet, _Left = on left.
            // "Right when facing" = +X side in home orientation (front faces +Y).
            bool grabBarsOnLeft = NeedsGrabBarsOnLeft(ctx);
            string side_suffix = grabBarsOnLeft ? "Left" : "Right";

            string toiletFile;
            if (ctx.Toilet == ToiletType.WallHung)
                toiletFile = string.Format("Toilet_WallHung_{0}.3dm", side_suffix);
            else
                toiletFile = string.Format("Toilet_FloorMounted_{0}.3dm", side_suffix);

            RhinoApp.WriteLine(string.Format("RhinoClaude: Toilet grab bars on {0} → using {1}",
                grabBarsOnLeft ? "left" : "right", toiletFile));

            if (libraryPath != null)
            {
                string toiletPath = Path.Combine(libraryPath, toiletFile);
                if (File.Exists(toiletPath))
                {
                    var ids = ImportFixture(doc, toiletPath, ctx.ToiletInsertPt,
                        ctx.ToiletRotationDeg, fixtureLayer);
                    TagImportedObjects(doc, ids, "Equipment", "Toilet");
                    RhinoApp.WriteLine(string.Format("RhinoClaude: Imported toilet ({0} objects)", ids.Count));
                }
                else
                {
                    RhinoApp.WriteLine("RhinoClaude: Toilet file not found: {0}", toiletPath);
                    BuildPlaceholderBox(doc, ctx.ToiletInsertPt, ctx.ToiletRotationDeg,
                        20.0 * s, 28.0 * s, 17.0 * s, fixtureLayer, "Equipment", "Toilet");
                }

                // ── Import sink fixture ──
                string sinkPath = Path.Combine(libraryPath, "Sink.3dm");
                if (File.Exists(sinkPath))
                {
                    var ids = ImportFixture(doc, sinkPath, ctx.SinkInsertPt,
                        ctx.SinkRotationDeg, fixtureLayer);
                    TagImportedObjects(doc, ids, "Equipment", "Sink");
                    RhinoApp.WriteLine("RhinoClaude: Imported sink ({0} objects)", ids.Count);
                }
                else
                {
                    RhinoApp.WriteLine("RhinoClaude: Sink file not found: {0}", sinkPath);
                    BuildPlaceholderBox(doc, ctx.SinkInsertPt, ctx.SinkRotationDeg,
                        22.0 * s, 18.0 * s, 8.0 * s, fixtureLayer, "Equipment", "Sink");
                }
            }
            else
            {
                RhinoApp.WriteLine("RhinoClaude: No fixture library found. Building placeholder boxes.");
                BuildPlaceholderBox(doc, ctx.ToiletInsertPt, ctx.ToiletRotationDeg,
                    20.0 * s, 28.0 * s, 17.0 * s, fixtureLayer, "Equipment", "Toilet");
                BuildPlaceholderBox(doc, ctx.SinkInsertPt, ctx.SinkRotationDeg,
                    22.0 * s, 18.0 * s, 8.0 * s, fixtureLayer, "Equipment", "Sink");
            }
        }

        /// <summary>
        /// Build a single wall as a box (or two boxes if the door is in this wall).
        /// The door opening splits the wall into segments.
        /// </summary>
        private void BuildWall(RhinoDoc doc, RoomContext ctx, Wall wall,
            double wallH, int layerIndex, Wall doorWall,
            double openMinX, double openMinY, double openMaxX, double openMaxY)
        {
            double s = ctx.Scale;
            double doorH = DoorHeight * s;

            // Compute wall box bounds
            double wMinX, wMinY, wMaxX, wMaxY;
            ComputeWallBounds(ctx, wall, out wMinX, out wMinY, out wMaxX, out wMaxY);

            bool isDoorWall = (wall == doorWall);
            bool isWetWall = (wall == ctx.ToiletWall);

            string assemblyType = isWetWall
                ? (ctx.Toilet == ToiletType.FloorMounted ? "Wet Wall" : "Wet Wall / Plumbing Chase")
                : "Interior Partition";

            if (!isDoorWall)
            {
                // Simple solid wall — no opening
                AddWallBox(doc, wMinX, wMinY, wMaxX, wMaxY, 0, wallH,
                    layerIndex, assemblyType, wall.ToString() + " Wall");
            }
            else
            {
                // Wall with door opening — split into segments
                // The opening cuts through the full wall thickness

                bool isHorizontalWall = (wall == Wall.North || wall == Wall.South);

                if (isHorizontalWall)
                {
                    // Wall runs along X. Door opening splits it into:
                    //   Left segment (wMinX to openMinX)
                    //   Right segment (openMaxX to wMaxX)
                    //   Header above opening (openMinX to openMaxX, doorH to wallH)

                    // Left segment
                    if (openMinX > wMinX + 0.001)
                    {
                        AddWallBox(doc, wMinX, wMinY, openMinX, wMaxY, 0, wallH,
                            layerIndex, assemblyType, wall.ToString() + " Wall (left of door)");
                    }

                    // Right segment
                    if (openMaxX < wMaxX - 0.001)
                    {
                        AddWallBox(doc, openMaxX, wMinY, wMaxX, wMaxY, 0, wallH,
                            layerIndex, assemblyType, wall.ToString() + " Wall (right of door)");
                    }

                    // Header above door
                    AddWallBox(doc, openMinX, wMinY, openMaxX, wMaxY, doorH, wallH,
                        layerIndex, assemblyType, wall.ToString() + " Wall (header)");
                }
                else
                {
                    // Wall runs along Y. Door opening splits along Y axis.

                    // Bottom segment
                    if (openMinY > wMinY + 0.001)
                    {
                        AddWallBox(doc, wMinX, wMinY, wMaxX, openMinY, 0, wallH,
                            layerIndex, assemblyType, wall.ToString() + " Wall (below door)");
                    }

                    // Top segment
                    if (openMaxY < wMaxY - 0.001)
                    {
                        AddWallBox(doc, wMinX, openMaxY, wMaxX, wMaxY, 0, wallH,
                            layerIndex, assemblyType, wall.ToString() + " Wall (above door)");
                    }

                    // Header above door
                    AddWallBox(doc, wMinX, openMinY, wMaxX, openMaxY, doorH, wallH,
                        layerIndex, assemblyType, wall.ToString() + " Wall (header)");
                }
            }
        }

        /// <summary>
        /// Compute the full extent of a wall box in plan (X,Y).
        /// </summary>
        private void ComputeWallBounds(RoomContext ctx, Wall wall,
            out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = maxX = maxY = 0;

            switch (wall)
            {
                case Wall.North:
                    // North wall runs full width, from outside west to outside east
                    minX = ctx.OutsideWest;
                    maxX = ctx.OutsideEast;
                    minY = ctx.InsideNorth;
                    maxY = ctx.OutsideNorth;
                    break;
                case Wall.South:
                    minX = ctx.OutsideWest;
                    maxX = ctx.OutsideEast;
                    minY = ctx.OutsideSouth;
                    maxY = ctx.InsideSouth;
                    break;
                case Wall.East:
                    // East wall runs between the inside faces of north and south walls
                    // (avoids double-thickness at corners)
                    minX = ctx.InsideEast;
                    maxX = ctx.OutsideEast;
                    minY = ctx.InsideSouth;
                    maxY = ctx.InsideNorth;
                    break;
                case Wall.West:
                    minX = ctx.OutsideWest;
                    maxX = ctx.InsideWest;
                    minY = ctx.InsideSouth;
                    maxY = ctx.InsideNorth;
                    break;
            }
        }

        private void AddWallBox(RhinoDoc doc,
            double minX, double minY, double maxX, double maxY,
            double minZ, double maxZ,
            int layerIndex, string assemblyType, string description)
        {
            if (Math.Abs(maxX - minX) < 0.0001 || Math.Abs(maxY - minY) < 0.0001)
                return;

            var box = new Box(Plane.WorldXY,
                new Interval(minX, maxX),
                new Interval(minY, maxY),
                new Interval(minZ, maxZ));
            var brep = box.ToBrep();
            if (brep == null) return;

            var attr = MakeAttributes(layerIndex);
            attr.SetUserString("RC:ElementType", "Wall");
            attr.SetUserString("RC:IntExt", "Interior");
            attr.SetUserString("RC:SystemType", "Architectural");
            attr.SetUserString("RC:AssemblyType", assemblyType);
            attr.SetUserString("RC:Description", description);
            doc.Objects.AddBrep(brep, attr);
        }

        /// <summary>
        /// Build a simplified door panel as a flat box in the opening.
        /// </summary>
        private void BuildDoorPanel(RhinoDoc doc, RoomContext ctx, int doorLayer,
            double openMinX, double openMinY, double openMaxX, double openMaxY)
        {
            double s = ctx.Scale;
            double doorH = DoorHeight * s;
            double panelThick = DoorPanelThickness * s;

            Wall doorWall;
            WallSide doorSide;
            GetDoorWallAndSide(ctx.DoorPosition, out doorWall, out doorSide);

            double pMinX, pMinY, pMaxX, pMaxY;
            bool isHoriz = (doorWall == Wall.North || doorWall == Wall.South);

            if (isHoriz)
            {
                // Panel is a thin slab along the X direction
                pMinX = openMinX;
                pMaxX = openMaxX;
                // Center panel in the wall thickness
                double wallCenterY = (openMinY + openMaxY) / 2.0;
                pMinY = wallCenterY - panelThick / 2.0;
                pMaxY = wallCenterY + panelThick / 2.0;
            }
            else
            {
                pMinY = openMinY;
                pMaxY = openMaxY;
                double wallCenterX = (openMinX + openMaxX) / 2.0;
                pMinX = wallCenterX - panelThick / 2.0;
                pMaxX = wallCenterX + panelThick / 2.0;
            }

            var box = new Box(Plane.WorldXY,
                new Interval(pMinX, pMaxX),
                new Interval(pMinY, pMaxY),
                new Interval(0, doorH));
            var brep = box.ToBrep();
            if (brep == null) return;

            var attr = MakeAttributes(doorLayer);
            attr.SetUserString("RC:ElementType", "Door");
            attr.SetUserString("RC:Description", "Door");
            doc.Objects.AddBrep(brep, attr);
        }

        // ═══════════════════════════════════════════════════════════════
        //  FIXTURE IMPORT
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Import a .3dm fixture file at the given insertion point with rotation.
        /// The fixture file is in inches with origin at back-wall centerline on floor,
        /// front facing +Y.
        /// Handles both loose geometry and block instances (explodes blocks to get
        /// the underlying geometry with correct relative positioning).
        /// </summary>
        private List<Guid> ImportFixture(RhinoDoc doc, string filePath,
            Point3d insertionPoint, double rotationDeg, int layerIndex)
        {
            var ids = new List<Guid>();

            try
            {
                var file3dm = File3dm.Read(filePath);
                if (file3dm == null)
                {
                    RhinoApp.WriteLine("RhinoClaude: Could not read {0}", Path.GetFileName(filePath));
                    return ids;
                }

                // Collect all geometry, resolving block instances to their underlying shapes.
                // Each entry is (geometry, transform) — loose geometry has Identity transform,
                // block instances carry their instance transform.
                var geoList = new List<Tuple<GeometryBase, Transform>>();

                // First, collect all object IDs that belong to block definitions.
                // These should NOT be added as loose geometry — they only appear via instances.
                var blockDefObjectIds = new HashSet<Guid>();
                foreach (var idef in file3dm.AllInstanceDefinitions)
                {
                    foreach (var objId in idef.GetObjectIds())
                        blockDefObjectIds.Add(objId);
                }

                foreach (var obj in file3dm.Objects)
                {
                    var geo = obj.Geometry;
                    if (geo == null) continue;

                    if (geo is InstanceReferenceGeometry instRef)
                    {
                        // Block instance — resolve to definition geometry + instance transform
                        var instXform = instRef.Xform;
                        var parentId = instRef.ParentIdefId;

                        var idef = file3dm.AllInstanceDefinitions.FindId(parentId);
                        if (idef != null)
                        {
                            var objectIds = idef.GetObjectIds();

                            foreach (var objId in objectIds)
                            {
                                // Find the object in the file by ID
                                foreach (var defObj in file3dm.Objects)
                                {
                                    if (defObj.Attributes.ObjectId == objId)
                                    {
                                        var defGeo = defObj.Geometry;
                                        if (defGeo != null && (defGeo is Brep || defGeo is Mesh ||
                                            defGeo is Extrusion || defGeo is Surface))
                                        {
                                            geoList.Add(Tuple.Create(defGeo, instXform));
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        else
                        {
                            RhinoApp.WriteLine(string.Format(
                                "RhinoClaude: Warning — block instance references unknown definition {0}",
                                parentId));
                        }
                    }
                    else if (geo is Brep || geo is Mesh || geo is Extrusion || geo is Surface)
                    {
                        // Only add as loose geometry if it's NOT part of a block definition
                        if (!blockDefObjectIds.Contains(obj.Attributes.ObjectId))
                        {
                            geoList.Add(Tuple.Create(geo, Transform.Identity));
                        }
                    }
                }

                if (geoList.Count == 0)
                {
                    RhinoApp.WriteLine(string.Format("RhinoClaude: {0} contains no usable geometry.",
                        Path.GetFileName(filePath)));
                    return ids;
                }

                RhinoApp.WriteLine(string.Format("RhinoClaude: {0} contains {1} geometry pieces ({2} from blocks)",
                    Path.GetFileName(filePath), geoList.Count,
                    geoList.Count(g => g.Item2 != Transform.Identity)));

                // Unit scale: fixture file → document units
                double unitScale = RhinoMath.UnitScale(
                    file3dm.Settings.ModelUnitSystem, doc.ModelUnitSystem);

                // Build the placement transform chain:
                // 1. Scale from fixture units to doc units (around origin)
                // 2. Rotate around origin (from +Y home orientation to target direction)
                // 3. Translate origin to insertion point
                var placementXform = Transform.Scale(Point3d.Origin, unitScale);

                if (Math.Abs(rotationDeg) > 0.01)
                {
                    double radians = RhinoMath.ToRadians(rotationDeg);
                    var rotTransform = Transform.Rotation(radians, Vector3d.ZAxis, Point3d.Origin);
                    placementXform = rotTransform * placementXform;
                }

                var translateTransform = Transform.Translation(
                    insertionPoint.X, insertionPoint.Y, insertionPoint.Z);
                placementXform = translateTransform * placementXform;

                // Add each geometry piece to the document
                foreach (var entry in geoList)
                {
                    var geo = entry.Item1;
                    var instanceXform = entry.Item2;

                    var copy = geo.Duplicate();

                    // Apply instance transform first (positions the piece within the block),
                    // then apply the placement transform (scales, rotates, translates to room)
                    var finalXform = placementXform * instanceXform;
                    copy.Transform(finalXform);

                    var attr = new ObjectAttributes();
                    attr.LayerIndex = layerIndex;

                    var id = doc.Objects.Add(copy, attr);
                    if (id != Guid.Empty)
                        ids.Add(id);
                }

                RhinoApp.WriteLine(string.Format("RhinoClaude: Imported {0} objects from {1} (scale: {2:F4}, rot: {3}°)",
                    ids.Count, Path.GetFileName(filePath), unitScale, rotationDeg));
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("RhinoClaude: Error importing {0}: {1}",
                    Path.GetFileName(filePath), ex.Message);
            }

            return ids;
        }

        /// <summary>
        /// Tag imported fixture objects with RC: metadata.
        /// </summary>
        private void TagImportedObjects(RhinoDoc doc, List<Guid> ids,
            string elementType, string description)
        {
            foreach (var id in ids)
            {
                var obj = doc.Objects.FindId(id);
                if (obj == null) continue;
                obj.Attributes.SetUserString("RC:ElementType", elementType);
                obj.Attributes.SetUserString("RC:Description", description);
                obj.CommitChanges();
            }
        }

        /// <summary>
        /// Build a simple placeholder box when no .3dm file is available.
        /// Width and depth are in doc units. Height from floor.
        /// </summary>
        private void BuildPlaceholderBox(RhinoDoc doc, Point3d insertPt, double rotationDeg,
            double width, double depth, double height, int layerIndex,
            string elementType, string description)
        {
            // Build box centered on origin at the fixture's home orientation (+Y = front)
            var box = new Box(Plane.WorldXY,
                new Interval(-width / 2, width / 2),  // centered on X
                new Interval(0, depth),                 // extends in +Y (front)
                new Interval(0, height));
            var brep = box.ToBrep();
            if (brep == null) return;

            // Rotate and translate
            var xform = Transform.Identity;
            if (Math.Abs(rotationDeg) > 0.01)
            {
                double radians = RhinoMath.ToRadians(rotationDeg);
                xform = Transform.Rotation(radians, Vector3d.ZAxis, Point3d.Origin);
            }
            xform = Transform.Translation(insertPt.X, insertPt.Y, insertPt.Z) * xform;
            brep.Transform(xform);

            var attr = MakeAttributes(layerIndex);
            attr.SetUserString("RC:ElementType", elementType);
            attr.SetUserString("RC:Description", description);
            doc.Objects.AddBrep(brep, attr);
        }

        // ═══════════════════════════════════════════════════════════════
        //  UTILITY HELPERS
        // ═══════════════════════════════════════════════════════════════

        private int EnsureLayer(RhinoDoc doc, string name, System.Drawing.Color color)
        {
            int idx = doc.Layers.FindByFullPath(name, -1);
            if (idx >= 0) return idx;

            var layer = new Layer();
            layer.Name = name;
            layer.Color = color;
            idx = doc.Layers.Add(layer);
            return idx >= 0 ? idx : 0;
        }

        private ObjectAttributes MakeAttributes(int layerIndex)
        {
            var attr = new ObjectAttributes();
            attr.LayerIndex = layerIndex;
            return attr;
        }

        /// <summary>
        /// Find the fixture library folder next to the plugin .rhp file.
        /// </summary>
        private string FindLibraryPath()
        {
            try
            {
                string pluginPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string pluginDir = Path.GetDirectoryName(pluginPath);
                string libDir = Path.Combine(pluginDir, "Library");
                if (Directory.Exists(libDir))
                    return libDir;

                string parentLib = Path.Combine(Path.GetDirectoryName(pluginDir), "Library");
                if (Directory.Exists(parentLib))
                    return parentLib;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Try to read the room label from the object name, user text, or nearby text.
        /// Falls back to prompting the user.
        /// </summary>
        private string ResolveRoomLabel(RhinoDoc doc, RhinoObject curveObj, Curve curve)
        {
            // 1. Object name
            if (!string.IsNullOrEmpty(curveObj.Name))
            {
                RhinoApp.WriteLine("RhinoClaude: Found label from object name: \"{0}\"", curveObj.Name);
                return curveObj.Name.Trim();
            }

            // 2. User text keys
            string[] labelKeys = { "Label", "Name", "Room", "RoomType", "Type", "RC:Description" };
            foreach (var key in labelKeys)
            {
                string val = curveObj.Attributes.GetUserString(key);
                if (!string.IsNullOrEmpty(val))
                {
                    RhinoApp.WriteLine("RhinoClaude: Found label from user text ({0}): \"{1}\"", key, val);
                    return val.Trim();
                }
            }

            // 3. Nearby text objects
            var bboxSearch = curve.GetBoundingBox(true);
            var searchBox = new BoundingBox(
                bboxSearch.Min - new Vector3d(1, 1, 1),
                bboxSearch.Max + new Vector3d(1, 1, 1));

            foreach (var obj in doc.Objects)
            {
                if (obj.IsDeleted) continue;

                if (obj.ObjectType == ObjectType.Annotation)
                {
                    var textObj = obj.Geometry as TextEntity;
                    if (textObj != null)
                    {
                        var textBbox = textObj.GetBoundingBox(true);
                        if (searchBox.Contains(textBbox.Center))
                        {
                            string text = textObj.PlainText;
                            if (!string.IsNullOrEmpty(text))
                            {
                                RhinoApp.WriteLine("RhinoClaude: Found label from text object: \"{0}\"", text);
                                return text.Trim();
                            }
                        }
                    }
                }
                else if (obj.ObjectType == ObjectType.TextDot)
                {
                    var dot = obj.Geometry as TextDot;
                    if (dot != null && searchBox.Contains(dot.Point))
                    {
                        string text = dot.Text;
                        if (!string.IsNullOrEmpty(text))
                        {
                            RhinoApp.WriteLine("RhinoClaude: Found label from text dot: \"{0}\"", text);
                            return text.Trim();
                        }
                    }
                }
            }

            // 4. Ask user
            RhinoApp.WriteLine("RhinoClaude: No label found on or near the selected rectangle.");
            string userLabel = string.Empty;
            var result = RhinoGet.GetString("Enter room type label", false, ref userLabel);
            if (result != Result.Success || string.IsNullOrWhiteSpace(userLabel))
                return null;

            return userLabel.Trim();
        }
    }
}
