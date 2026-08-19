using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Rhino.Geometry;
using RhinoClaude.Agent;
using RhinoClaude.Services.Agent;

namespace RhinoClaude.Tools
{
    /// <summary>
    /// The rest of the plan's §3 Tier 1 inventory, registered alongside
    /// <see cref="Phase1Tools"/>: the remaining primitives, the full transform set, booleans,
    /// curve/surface modification, blocks, materials, selection, and view navigation.
    /// </summary>
    public static class Tier1Tools
    {
        public static List<ToolDefinition> Build(
            RhinoQueryService query,
            RhinoMutationService mutation,
            RhinoInteractionService interaction,
            RhinoCommandService command = null)
        {
            var tools = new List<ToolDefinition>
            {
                // Query
                ListNamedViews(query),
                ListBlocks(query),

                // Create
                CreatePoint(mutation),
                CreateCircle(mutation),
                CreateRectangle(mutation),
                CreateArcCurve(mutation),

                // Transform
                RotateObjects(mutation),
                ScaleObjects(mutation),
                Scale1D(mutation),
                MirrorObjects(mutation),

                // Boolean / modify
                BooleanUnion(mutation),
                BooleanDifference(mutation),
                BooleanIntersection(mutation),
                OffsetCurve(mutation),
                ExtractFootprintFromCurves(mutation),
                ExtrudeCurve(mutation),
                MoveFace(mutation),
                MoveEdge(mutation),

                // Blocks + materials
                InsertBlock(mutation),
                Import3dmAsBlock(mutation),
                AssignMaterial(mutation),

                // Selection + view
                SelectObjects(interaction),
                DeselectAll(interaction),
                ZoomExtents(interaction),

                // Meta
                SetObjectTags(mutation)
            };

            // Tier 3 sits last so it reads as the bottom of the ladder in the tools array.
            if (command != null) tools.Add(RunRhinoCommand(command));

            return tools;
        }

        // ── Tier 3 ────────────────────────────────────────────────────

        private static ToolDefinition RunRhinoCommand(RhinoCommandService command) => new ToolDefinition
        {
            Name = "run_rhino_command",
            Description =
                "Last resort. Runs a Rhino command as if typed at the command line. Only reach for this " +
                "when neither a curated tool nor run_rhinocommon_script covers it — rendering, a legacy " +
                "command, or a command supplied by another plugin with no API. " +
                "Use the scripted form: a leading underscore suppresses localisation and a dash " +
                "suppresses the dialog, so '_-Render' rather than 'Render', with any arguments on the " +
                "same line. Commands that wait for input nobody can give will simply hang and fail. " +
                "This is non-atomic and undoes less cleanly than the other tools, so prefer them.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""commandLine"", ""purpose""],
  ""properties"": {
    ""commandLine"": { ""type"": ""string"", ""description"": ""Scripted command line, e.g. '_-Render' or '_-Export ...'."" },
    ""purpose"": { ""type"": ""string"", ""description"": ""One sentence on what this is meant to achieve. Logged."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => command.Run(RequireStr(input, "commandLine"), RequireStr(input, "purpose"))
        };

        // ── Query ─────────────────────────────────────────────────────

        private static ToolDefinition ListNamedViews(RhinoQueryService query) => new ToolDefinition
        {
            Name = "list_named_views",
            IsReadOnly = true,
            Description =
                "Saved named views in the document, with their camera positions. capture_views can " +
                "reproduce any of these by name — useful when the user refers to a view they set up.",
            InputSchemaJson = Empty,
            Handler = (input, ct) => ToolResult.Ok(query.ListNamedViews())
        };

        private static ToolDefinition ListBlocks(RhinoQueryService query) => new ToolDefinition
        {
            Name = "list_blocks",
            IsReadOnly = true,
            Description =
                "Block definitions in the document, with how many instances of each are placed. " +
                "Check here before insert_block — the name must match exactly.",
            InputSchemaJson = Empty,
            Handler = (input, ct) => ToolResult.Ok(query.ListBlocks())
        };

        // ── Create ────────────────────────────────────────────────────

        private static ToolDefinition CreatePoint(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "create_point",
            Description = "Add a point object. Useful as a marker or reference for later construction.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""location""],
  ""properties"": {
    ""location"": Vec3,
    ""layer"": { ""type"": ""string"" },
    ""name"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3),
            Handler = (input, ct) => ToolResult.Ok(mutation.CreatePoint(
                ReadPoint(input, "location"), Str(input, "layer"), Str(input, "name")))
        };

        private static ToolDefinition CreateCircle(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "create_circle",
            Description =
                "Add a circle as a closed curve. Lies in the world XY plane through the centre unless " +
                "'normal' gives a different plane orientation.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""center"", ""radius""],
  ""properties"": {
    ""center"": Vec3,
    ""radius"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
    ""normal"": Vec3,
    ""layer"": { ""type"": ""string"" },
    ""name"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3),
            Handler = (input, ct) => ToolResult.Ok(mutation.CreateCircle(
                ReadPoint(input, "center"), Num(input, "radius", 0),
                ReadVectorOrNull(input, "normal"), Str(input, "layer"), Str(input, "name")))
        };

        private static ToolDefinition CreateRectangle(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "create_rectangle",
            Description =
                "Add a planar rectangle as a closed curve, growing from 'corner' by 'width' along the " +
                "plane X axis and 'depth' along Y. Negative values grow the other way. Extrude it with " +
                "extrude_curve to make a solid.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""corner"", ""width"", ""depth""],
  ""properties"": {
    ""corner"": Vec3,
    ""width"": { ""type"": ""number"" },
    ""depth"": { ""type"": ""number"" },
    ""normal"": Vec3,
    ""layer"": { ""type"": ""string"" },
    ""name"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3),
            Handler = (input, ct) => ToolResult.Ok(mutation.CreateRectangle(
                ReadPoint(input, "corner"), Num(input, "width", 0), Num(input, "depth", 0),
                ReadVectorOrNull(input, "normal"), Str(input, "layer"), Str(input, "name")))
        };

        private static ToolDefinition CreateArcCurve(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "create_arc_curve",
            Description =
                "Add an arc, either through three points (threePoint: start, through, end) or from a " +
                "centre, radius and swept angle (centerRadius). Three collinear points, a zero radius " +
                "or a zero angle all fail.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""mode""],
  ""properties"": {
    ""mode"": { ""type"": ""string"", ""enum"": [""threePoint"", ""centerRadius""] },
    ""start"": Vec3,
    ""through"": Vec3,
    ""end"": Vec3,
    ""center"": Vec3,
    ""radius"": { ""type"": ""number"" },
    ""angleDegrees"": { ""type"": ""number"" },
    ""normal"": Vec3,
    ""layer"": { ""type"": ""string"" },
    ""name"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3),
            Handler = (input, ct) => ToolResult.Ok(mutation.CreateArcCurve(
                Str(input, "mode") ?? "threePoint",
                ReadPointOrNull(input, "start"), ReadPointOrNull(input, "through"), ReadPointOrNull(input, "end"),
                ReadPointOrNull(input, "center"), Num(input, "radius", 0), Num(input, "angleDegrees", 0),
                ReadVectorOrNull(input, "normal"), Str(input, "layer"), Str(input, "name")))
        };

        // ── Transform ─────────────────────────────────────────────────

        private static ToolDefinition RotateObjects(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "rotate_objects",
            Description =
                "Rotate objects about an axis through a centre point. For a plan rotation use axis " +
                "[0,0,1]. Positive angles are counter-clockwise looking down the axis.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids"", ""center"", ""axis"", ""angleDegrees""],
  ""properties"": {
    ""ids"": IdList,
    ""center"": Vec3,
    ""axis"": Vec3,
    ""angleDegrees"": { ""type"": ""number"" },
    ""copy"": { ""type"": ""boolean"", ""default"": false }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3).Replace("IdList", IdList),
            Handler = (input, ct) => ToolResult.Ok(mutation.RotateObjects(
                Ids(input), ReadPoint(input, "center"), ReadVector(input, "axis"),
                Num(input, "angleDegrees", 0), Bool(input, "copy", false)))
        };

        private static ToolDefinition ScaleObjects(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "scale_objects",
            Description =
                "Scale objects about a centre point. 'factor' is either one number for a uniform scale " +
                "or three for per-axis scaling. To scale to a known length instead of a ratio, use scale_1d.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids"", ""center"", ""factor""],
  ""properties"": {
    ""ids"": IdList,
    ""center"": Vec3,
    ""factor"": {
      ""oneOf"": [
        { ""type"": ""number"" },
        { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3 }
      ]
    },
    ""copy"": { ""type"": ""boolean"", ""default"": false }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3).Replace("IdList", IdList),
            Handler = (input, ct) => ToolResult.Ok(mutation.ScaleObjects(
                Ids(input), ReadPoint(input, "center"), ReadFactors(input), Bool(input, "copy", false)))
        };

        private static ToolDefinition Scale1D(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "scale_1d",
            Description =
                "Stretch or compress objects along one direction so the distance from basePoint to " +
                "referencePoint becomes targetLength. Nothing changes in the other two directions. " +
                "This is the tool for 'make this wall 12 feet long' — it works from the length you " +
                "want rather than a ratio you would have to compute.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids"", ""basePoint"", ""referencePoint"", ""targetLength""],
  ""properties"": {
    ""ids"": IdList,
    ""basePoint"": Vec3,
    ""referencePoint"": Vec3,
    ""targetLength"": { ""type"": ""number"", ""description"": ""Desired distance from basePoint to referencePoint, in model units."" },
    ""copy"": { ""type"": ""boolean"", ""default"": false }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3).Replace("IdList", IdList),
            Handler = (input, ct) => ToolResult.Ok(mutation.Scale1D(
                Ids(input), ReadPoint(input, "basePoint"), ReadPoint(input, "referencePoint"),
                Num(input, "targetLength", 0), Bool(input, "copy", false)))
        };

        private static ToolDefinition MirrorObjects(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "mirror_objects",
            Description =
                "Mirror objects across a plane given by a point on it and its normal. Set copy true to " +
                "keep the originals — that is the usual way to build a symmetrical layout.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids"", ""planeOrigin"", ""planeNormal""],
  ""properties"": {
    ""ids"": IdList,
    ""planeOrigin"": Vec3,
    ""planeNormal"": Vec3,
    ""copy"": { ""type"": ""boolean"", ""default"": false }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3).Replace("IdList", IdList),
            Handler = (input, ct) => ToolResult.Ok(mutation.MirrorObjects(
                Ids(input), ReadPoint(input, "planeOrigin"), ReadVector(input, "planeNormal"),
                Bool(input, "copy", false)))
        };

        // ── Boolean ───────────────────────────────────────────────────

        private static ToolDefinition BooleanUnion(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "boolean_union",
            Description =
                "Fuse solids into one. Inputs must be closed solids; open surfaces usually fail and the " +
                "result's 'notes' will say so. Nothing is changed when the boolean produces no result.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids""],
  ""properties"": {
    ""ids"": IdList,
    ""deleteInputs"": { ""type"": ""boolean"", ""default"": true }
  },
  ""additionalProperties"": false
}".Replace("IdList", IdList),
            Handler = (input, ct) => ToolResult.Ok(mutation.BooleanOperation(
                "union", Ids(input), null, Bool(input, "deleteInputs", true)))
        };

        private static ToolDefinition BooleanDifference(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "boolean_difference",
            Description =
                "Subtract the subtrahends from the minuends — this is how you cut a door or window " +
                "opening out of a wall. Both sets must be closed solids.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""minuendIds"", ""subtrahendIds""],
  ""properties"": {
    ""minuendIds"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1, ""description"": ""What to cut from."" },
    ""subtrahendIds"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1, ""description"": ""What to remove."" },
    ""deleteInputs"": { ""type"": ""boolean"", ""default"": true }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.BooleanOperation(
                "difference", IdsNamed(input, "minuendIds"), IdsNamed(input, "subtrahendIds"),
                Bool(input, "deleteInputs", true)))
        };

        private static ToolDefinition BooleanIntersection(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "boolean_intersection",
            Description = "Keep only the volume shared by both sets of solids.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids"", ""withIds""],
  ""properties"": {
    ""ids"": IdList,
    ""withIds"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1 },
    ""deleteInputs"": { ""type"": ""boolean"", ""default"": true }
  },
  ""additionalProperties"": false
}".Replace("IdList", IdList),
            Handler = (input, ct) => ToolResult.Ok(mutation.BooleanOperation(
                "intersection", Ids(input), IdsNamed(input, "withIds"), Bool(input, "deleteInputs", true)))
        };

        // ── Curve / surface modification ──────────────────────────────

        private static ToolDefinition OffsetCurve(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "offset_curve",
            Description =
                "Offset a curve by a distance in its own plane — the way to get a wall's inner face " +
                "from its outer one. Sign picks the side. Supply 'normal' when the curve is not planar " +
                "or the offset plane is ambiguous.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""id"", ""distance""],
  ""properties"": {
    ""id"": { ""type"": ""string"" },
    ""distance"": { ""type"": ""number"", ""description"": ""Signed offset distance in model units."" },
    ""normal"": Vec3,
    ""layer"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3),
            Handler = (input, ct) => ToolResult.Ok(mutation.OffsetCurve(
                RequireStr(input, "id"), Num(input, "distance", 0),
                ReadVectorOrNull(input, "normal"), Str(input, "layer")))
        };

        private static ToolDefinition ExtractFootprintFromCurves(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "extract_footprint_from_curves",
            Description =
                "Given a selection of curves/polylines that represent a building outline — typical CAD " +
                "floor plan linework, with walls, doors and dimensions all mixed together — joins them " +
                "and extracts the outer closed boundary as a single closed curve. Returns its id, " +
                "bounds, vertexCount, perimeter and area. Use this before extruding a footprint " +
                "whenever the user's selection is multiple perimeter curves rather than a single closed " +
                "polyline. Do NOT use the selection's axis-aligned bounding box as the footprint when " +
                "this tool is available — that silently turns an L-shaped plan into a rectangle. When " +
                "several closed loops survive the join, the largest-area one is taken as the outer " +
                "boundary and 'notes' says so.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids""],
  ""properties"": {
    ""ids"": IdList
  },
  ""additionalProperties"": false
}".Replace("IdList", IdList),
            Handler = (input, ct) => ToolResult.Ok(mutation.ExtractFootprintFromCurves(Ids(input)))
        };

        private static ToolDefinition ExtrudeCurve(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "extrude_curve",
            Description =
                "Extrude a curve into a surface or solid — the usual way to raise a wall from its plan " +
                "outline. A closed curve with cap true gives a solid; an open one gives a surface and " +
                "the result's 'notes' will say so.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""id"", ""direction"", ""distance""],
  ""properties"": {
    ""id"": { ""type"": ""string"" },
    ""direction"": Vec3,
    ""distance"": { ""type"": ""number"", ""description"": ""Extrusion length in model units. The direction vector is normalised first."" },
    ""cap"": { ""type"": ""boolean"", ""default"": true },
    ""deleteInput"": { ""type"": ""boolean"", ""default"": false },
    ""layer"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3),
            Handler = (input, ct) => ToolResult.Ok(mutation.ExtrudeCurve(
                RequireStr(input, "id"), ReadVector(input, "direction"), Num(input, "distance", 0),
                Bool(input, "cap", true), Bool(input, "deleteInput", false), Str(input, "layer")))
        };

        private static ToolDefinition MoveFace(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "move_face",
            Description =
                "Push or pull one face of a solid, letting the adjacent faces follow — how you make an " +
                "existing box taller without rebuilding it. Get faceIndex from get_object with " +
                "includeSubobjects; indices change after edits, so re-read before a second move. " +
                "Non-planar faces may not move cleanly and the result will say so.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""brepId"", ""faceIndex"", ""direction"", ""distance""],
  ""properties"": {
    ""brepId"": { ""type"": ""string"" },
    ""faceIndex"": { ""type"": ""integer"", ""minimum"": 0 },
    ""direction"": Vec3,
    ""distance"": { ""type"": ""number"" }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3),
            Handler = (input, ct) => ToolResult.Ok(mutation.MoveSubObject(
                true, RequireStr(input, "brepId"), Int(input, "faceIndex", -1),
                ReadVector(input, "direction"), Num(input, "distance", 0)))
        };

        private static ToolDefinition MoveEdge(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "move_edge",
            Description =
                "Push or pull one edge of a solid. Same pattern as move_face — edgeIndex comes from " +
                "get_object with includeSubobjects.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""brepId"", ""edgeIndex"", ""direction"", ""distance""],
  ""properties"": {
    ""brepId"": { ""type"": ""string"" },
    ""edgeIndex"": { ""type"": ""integer"", ""minimum"": 0 },
    ""direction"": Vec3,
    ""distance"": { ""type"": ""number"" }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3),
            Handler = (input, ct) => ToolResult.Ok(mutation.MoveSubObject(
                false, RequireStr(input, "brepId"), Int(input, "edgeIndex", -1),
                ReadVector(input, "direction"), Num(input, "distance", 0)))
        };

        // ── Blocks + materials ────────────────────────────────────────

        private static ToolDefinition InsertBlock(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "insert_block",
            Description =
                "Place an instance of an existing block definition. Check list_blocks for the exact " +
                "name; import_3dm_as_block creates one from a file if it is not there yet.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""blockName"", ""location""],
  ""properties"": {
    ""blockName"": { ""type"": ""string"" },
    ""location"": Vec3,
    ""rotationDegrees"": { ""type"": ""number"", ""default"": 0, ""description"": ""Rotation about world Z, applied at the origin before the move."" },
    ""scale"": { ""type"": ""number"", ""default"": 1 },
    ""layer"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}".Replace("Vec3", Vec3),
            Handler = (input, ct) => ToolResult.Ok(mutation.InsertBlock(
                RequireStr(input, "blockName"), ReadPoint(input, "location"),
                Num(input, "rotationDegrees", 0), Num(input, "scale", 1), Str(input, "layer")))
        };

        private static ToolDefinition Import3dmAsBlock(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "import_3dm_as_block",
            Description =
                "Create a block definition from a .3dm file — this is how the fixture library gets into " +
                "a document. It only defines the block; call insert_block afterwards to place one. " +
                "Idempotent: an existing block with the same name is reused.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""path""],
  ""properties"": {
    ""path"": { ""type"": ""string"", ""description"": ""Full path to a .3dm file."" },
    ""blockName"": { ""type"": ""string"", ""description"": ""Defaults to the file name without its extension."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.Import3dmAsBlock(
                RequireStr(input, "path"), Str(input, "blockName")))
        };

        private static ToolDefinition AssignMaterial(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "assign_material",
            Description =
                "Assign a render material to objects by name, creating it if it does not exist. " +
                "Idempotent by name, so calling it repeatedly with 'Concrete' reuses one material " +
                "rather than making duplicates.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids"", ""materialName""],
  ""properties"": {
    ""ids"": IdList,
    ""materialName"": { ""type"": ""string"" },
    ""diffuseHex"": { ""type"": ""string"", ""description"": ""6-digit hex colour, e.g. '#9AA0A6'."" },
    ""transparency"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 1 }
  },
  ""additionalProperties"": false
}".Replace("IdList", IdList),
            Handler = (input, ct) => ToolResult.Ok(mutation.AssignMaterial(
                Ids(input), RequireStr(input, "materialName"), Str(input, "diffuseHex"),
                NumOrNull(input, "transparency")))
        };

        // ── Selection + view ──────────────────────────────────────────

        private static ToolDefinition SelectObjects(RhinoInteractionService interaction) => new ToolDefinition
        {
            Name = "select_objects",
            Description =
                "Set the user's Rhino selection. Use this at the end of a turn to leave the objects you " +
                "made or changed selected, so the user can see and act on them immediately.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids""],
  ""properties"": {
    ""ids"": IdList,
    ""replace"": { ""type"": ""boolean"", ""default"": true, ""description"": ""False adds to the current selection."" }
  },
  ""additionalProperties"": false
}".Replace("IdList", IdList),
            Handler = (input, ct) => ToolResult.Ok(interaction.SelectObjects(
                Ids(input), Bool(input, "replace", true)))
        };

        private static ToolDefinition DeselectAll(RhinoInteractionService interaction) => new ToolDefinition
        {
            Name = "deselect_all",
            Description = "Clear the Rhino selection.",
            InputSchemaJson = Empty,
            Handler = (input, ct) => ToolResult.Ok(interaction.DeselectAll())
        };

        private static ToolDefinition ZoomExtents(RhinoInteractionService interaction) => new ToolDefinition
        {
            Name = "zoom_extents",
            Description =
                "Zoom the user's viewport to the whole model, or to specific objects. This changes what " +
                "the user sees; capture_views does not, so use this when you want to leave them looking " +
                "at what you built.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""properties"": {
    ""viewName"": { ""type"": ""string"", ""description"": ""Defaults to the active view."" },
    ""ids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Zoom to these objects instead of the whole model."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(interaction.ZoomExtents(
                Str(input, "viewName"), IdsOrNull(input, "ids")))
        };

        // ── Meta ──────────────────────────────────────────────────────

        private static ToolDefinition SetObjectTags(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "set_object_tags",
            Description =
                "Write RC: semantic tags onto objects. Keys and allowed values are listed in the system " +
                "prompt's tag schema; constrained keys reject anything outside their value list. Tag " +
                "geometry you create when the user's request is about building elements rather than " +
                "abstract shapes.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids"", ""tags""],
  ""properties"": {
    ""ids"": IdList,
    ""tags"": {
      ""type"": ""object"",
      ""description"": ""Map of tag key to value, e.g. {\""RC:ElementType\"": \""Wall\"", \""RC:FireRating\"": \""2-hr\""}."",
      ""additionalProperties"": { ""type"": ""string"" }
    }
  },
  ""additionalProperties"": false
}".Replace("IdList", IdList),
            Handler = (input, ct) => ToolResult.Ok(mutation.SetObjectTags(Ids(input), ReadTags(input)))
        };

        // ── Schema fragments ──────────────────────────────────────────

        private const string Empty = @"{""type"":""object"",""properties"":{},""additionalProperties"":false}";

        private const string Vec3 =
            @"{ ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3 }";

        private const string IdList =
            @"{ ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1 }";

        // ── JSON input helpers ────────────────────────────────────────

        private static JsonElement Require(JsonElement input, string name)
        {
            if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                throw new ArgumentException("Required parameter '" + name + "' is missing.");
            return value;
        }

        private static string RequireStr(JsonElement input, string name)
        {
            var value = Require(input, name);
            if (value.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Parameter '" + name + "' must be a string.");
            return value.GetString();
        }

        private static string Str(JsonElement input, string name) =>
            input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement input, string name, bool fallback)
        {
            if (!input.TryGetProperty(name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            return fallback;
        }

        private static double Num(JsonElement input, string name, double fallback) =>
            input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

        private static double? NumOrNull(JsonElement input, string name) =>
            input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null;

        private static int Int(JsonElement input, string name, int fallback) =>
            input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
                ? i : fallback;

        private static Point3d ReadPoint(JsonElement input, string name) =>
            RhinoQueryService.ReadPoint(Require(input, name));

        private static Point3d? ReadPointOrNull(JsonElement input, string name) =>
            input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? RhinoQueryService.ReadPoint(v) : (Point3d?)null;

        private static Vector3d ReadVector(JsonElement input, string name) =>
            RhinoQueryService.ReadVector(Require(input, name));

        private static Vector3d? ReadVectorOrNull(JsonElement input, string name) =>
            input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? RhinoQueryService.ReadVector(v) : (Vector3d?)null;

        private static List<string> Ids(JsonElement input) => IdsNamed(input, "ids");

        private static List<string> IdsNamed(JsonElement input, string name)
        {
            var value = Require(input, name);
            if (value.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Parameter '" + name + "' must be an array of object ids.");

            var list = value.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString())
                            .ToList();

            if (list.Count == 0)
                throw new ArgumentException("Parameter '" + name + "' must contain at least one object id.");

            return list;
        }

        private static List<string> IdsOrNull(JsonElement input, string name)
        {
            if (!input.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            var list = v.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString())
                        .ToList();
            return list.Count == 0 ? null : list;
        }

        private static double[] ReadFactors(JsonElement input)
        {
            var value = Require(input, "factor");
            if (value.ValueKind == JsonValueKind.Number)
                return new[] { value.GetDouble() };

            if (value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.Number)
                            .Select(e => e.GetDouble())
                            .ToArray();

            throw new ArgumentException("'factor' must be a number or an array of three numbers.");
        }

        private static Dictionary<string, string> ReadTags(JsonElement input)
        {
            var value = Require(input, "tags");
            if (value.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("'tags' must be an object mapping tag keys to values.");

            var tags = new Dictionary<string, string>();
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    tags[property.Name] = property.Value.GetString();
            }

            if (tags.Count == 0)
                throw new ArgumentException("'tags' must contain at least one key/value pair.");

            return tags;
        }
    }
}
