using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RhinoClaude.Agent;
using RhinoClaude.Semantic;
using RhinoClaude.Services.Semantic;

namespace RhinoClaude.Tools
{
    /// <summary>
    /// The massing operations (semantic plan §4.6) — seven writes plus the Entry promotion.
    ///
    /// These are the plan's core claim about what the agent is for: mass modelling *is* the SD
    /// workflow, so the agent has to be able to make the same moves the architect makes. Each
    /// is one undo record, composed of phase 1 mutations plus tagging.
    /// </summary>
    public static class SemanticWriteTools
    {
        public static List<ToolDefinition> Build(SemanticMutationService mutation)
        {
            return new List<ToolDefinition>
            {
                PushPullFace(mutation),
                AddMass(mutation),
                SubtractMass(mutation),
                CutOpening(mutation),
                SliceMassAtElevation(mutation),
                ExtrudeFaceOutward(mutation),
                FilletEdges(mutation),
                PromoteOpeningToEntry(mutation)
            };
        }

        private static ToolDefinition PushPullFace(SemanticMutationService mutation) => new ToolDefinition
        {
            Name = "push_pull_face",
            Description =
                "The fundamental massing move: pick a face by role or orientation and move it along its own " +
                "normal. Positive extends the mass outward, negative pushes it in — and a negative push into " +
                "a facade is how a recessed entry or loggia gets made. Prefer this over the raw move_face: " +
                "it selects by role rather than by a face index you would have to guess, and it keeps the " +
                "mass's semantic tags. Face indices change after every edit, so re-read get_mass_faces " +
                "between operations rather than reusing an id.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""massId"", ""faceSelector"", ""distance""],
  ""properties"": {
    ""massId"": { ""type"": ""string"" },
    ""faceSelector"": " + SemanticReadTools.FaceSelectorSchema + @",
    ""distance"": { ""type"": ""number"", ""description"": ""Model units along the face normal. Positive is outward."" },
    ""propagate"": { ""type"": ""string"", ""enum"": [""auto"", ""none""], ""default"": ""auto"", ""description"": ""auto lets the connected faces follow the move, which is what an architect means 9 times in 10."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.PushPullFace(
                ToolInput.RequireString(input, "massId"),
                FaceSelector.Parse(ToolInput.Require(input, "faceSelector")),
                ToolInput.RequireDouble(input, "distance"),
                ToolInput.String(input, "propagate", "auto")))
        };

        private static ToolDefinition AddMass(SemanticMutationService mutation) => new ToolDefinition
        {
            Name = "add_mass",
            Description =
                "Create a building mass — a box, a cylinder, or a prism extruded from a closed curve — tag it " +
                "with its function, and put it on the matching MASS_* layer so it appears in describe_massing " +
                "straight away. Pass unionWithExisting to boolean-union it into masses that are already there " +
                "in the same operation, which is how two wings become one form in one undo step.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""shape"", ""function""],
  ""properties"": {
    ""shape"": { ""type"": ""string"", ""enum"": [""box"", ""cylinder"", ""prism-from-curve""] },
    ""location"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3, ""description"": ""Base corner for a box, base centre for a cylinder. Defaults to the origin."" },
    ""dimensions"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""description"": ""[width, depth, height] for a box; [radius, height] for a cylinder."" },
    ""footprintCurveId"": { ""type"": ""string"", ""description"": ""Closed planar curve, for prism-from-curve."" },
    ""height"": { ""type"": ""number"", ""description"": ""Extrusion height, for prism-from-curve."" },
    ""function"": { ""type"": ""string"", ""enum"": [""Office"",""Residential"",""Retail"",""Institutional"",""Common"",""Other""] },
    ""name"": { ""type"": ""string"" },
    ""unionWithExisting"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Mass ids to boolean-union the new mass into."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.AddMass(
                ToolInput.RequireString(input, "shape"),
                ToolInput.DoubleList(input, "location").ToArray(),
                ToolInput.DoubleList(input, "dimensions").ToArray(),
                ToolInput.String(input, "footprintCurveId"),
                ToolInput.NullableDouble(input, "height"),
                ToolInput.RequireString(input, "function"),
                ToolInput.String(input, "name"),
                ToolInput.StringList(input, "unionWithExisting")))
        };

        private static ToolDefinition SubtractMass(SemanticMutationService mutation) => new ToolDefinition
        {
            Name = "subtract_mass",
            Description =
                "Boolean-difference a cutter solid out of a base mass — a light well, an atrium, a notched " +
                "corner. The response says whether the result reads as a Cut (room-sized or through-going) or " +
                "as an Opening or Recess, by volume. The cutter can be any solid, including one you just made " +
                "with add_mass or create_box; it is deleted by default.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""baseMassId"", ""cutterMassId""],
  ""properties"": {
    ""baseMassId"": { ""type"": ""string"" },
    ""cutterMassId"": { ""type"": ""string"", ""description"": ""A Mass id, or the raw object id of any closed solid."" },
    ""deleteCutter"": { ""type"": ""boolean"", ""default"": true }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.SubtractMass(
                ToolInput.RequireString(input, "baseMassId"),
                ToolInput.RequireString(input, "cutterMassId"),
                ToolInput.Bool(input, "deleteCutter", true)))
        };

        private static ToolDefinition CutOpening(SemanticMutationService mutation) => new ToolDefinition
        {
            Name = "cut_opening",
            Description =
                "Cut a rectangular opening — window, door, storefront, curtain wall, louver — through a face " +
                "of a mass. This is what 'add a window' actually is in Rhino: a hole subtracted from the " +
                "solid, which the classifier then reads back as an Opening. Position it either by " +
                "distanceFromLeftEdge plus sillHeight, or by centroidOnFace. Positions that would put the " +
                "opening off the face are clamped, and the response says where it actually landed.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""massId"", ""faceSelector"", ""openingType"", ""width"", ""height""],
  ""properties"": {
    ""massId"": { ""type"": ""string"" },
    ""faceSelector"": " + SemanticReadTools.FaceSelectorSchema + @",
    ""openingType"": { ""type"": ""string"", ""enum"": [""Window"",""Door"",""Storefront"",""CurtainWall"",""Louver""] },
    ""width"": { ""type"": ""number"" },
    ""height"": { ""type"": ""number"" },
    ""sillHeight"": { ""type"": ""number"", ""default"": 0, ""description"": ""Above the bottom of the face, in model units."" },
    ""positionOnFace"": {
      ""type"": ""object"",
      ""description"": ""Either {distanceFromLeftEdge} or {centroidOnFace: [u, v]}. Omit to centre the opening horizontally."",
      ""properties"": {
        ""distanceFromLeftEdge"": { ""type"": ""number"" },
        ""centroidOnFace"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 2 }
      },
      ""additionalProperties"": false
    },
    ""depth"": { ""type"": ""number"", ""description"": ""How far the cut goes into the mass. Omit to cut all the way through."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) =>
            {
                double? distanceFromLeftEdge = null;
                double[] centroidOnFace = null;

                if (ToolInput.TryGet(input, "positionOnFace", out var position)
                    && position.ValueKind == JsonValueKind.Object)
                {
                    distanceFromLeftEdge = ToolInput.NullableDouble(position, "distanceFromLeftEdge");
                    var centroid = ToolInput.DoubleList(position, "centroidOnFace");
                    if (centroid.Count >= 2) centroidOnFace = centroid.ToArray();
                }

                return ToolResult.Ok(mutation.CutOpening(
                    ToolInput.RequireString(input, "massId"),
                    FaceSelector.Parse(ToolInput.Require(input, "faceSelector")),
                    ToolInput.RequireString(input, "openingType"),
                    ToolInput.RequireDouble(input, "width"),
                    ToolInput.RequireDouble(input, "height"),
                    ToolInput.Double(input, "sillHeight", 0),
                    distanceFromLeftEdge,
                    centroidOnFace,
                    ToolInput.NullableDouble(input, "depth")));
            }
        };

        private static ToolDefinition SliceMassAtElevation(SemanticMutationService mutation) => new ToolDefinition
        {
            Name = "slice_mass_at_elevation",
            Description =
                "Slice a mass horizontally. 'generate-floorplate' leaves the mass alone and adds the section " +
                "as a planar plate on a FLOOR_* layer — the way to get a real floor-plate area on a stepped " +
                "mass. 'split-mass' cuts the mass into two stacked masses, both inheriting the original's " +
                "layer and tags.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""massId"", ""elevation"", ""mode""],
  ""properties"": {
    ""massId"": { ""type"": ""string"" },
    ""elevation"": { ""type"": ""number"", ""description"": ""World Z, in model units."" },
    ""mode"": { ""type"": ""string"", ""enum"": [""generate-floorplate"", ""split-mass""] }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.SliceMassAtElevation(
                ToolInput.RequireString(input, "massId"),
                ToolInput.RequireDouble(input, "elevation"),
                ToolInput.RequireString(input, "mode")))
        };

        private static ToolDefinition ExtrudeFaceOutward(SemanticMutationService mutation) => new ToolDefinition
        {
            Name = "extrude_face_outward",
            Description =
                "Project a new solid out from a face, leaving the face where it is — a canopy, a brise-soleil, " +
                "a bump-out. Distinct from push_pull_face, which moves the existing face plane instead. With " +
                "asOverhang true the result is tagged as an Overhang and stays out of program area; with it " +
                "false the result is a Mass carrying the parent's function.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""massId"", ""faceSelector"", ""distance""],
  ""properties"": {
    ""massId"": { ""type"": ""string"" },
    ""faceSelector"": " + SemanticReadTools.FaceSelectorSchema + @",
    ""distance"": { ""type"": ""number"", ""description"": ""How far it projects, in model units. Must be positive."" },
    ""asOverhang"": { ""type"": ""boolean"", ""default"": true }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.ExtrudeFaceOutward(
                ToolInput.RequireString(input, "massId"),
                FaceSelector.Parse(ToolInput.Require(input, "faceSelector")),
                ToolInput.RequireDouble(input, "distance"),
                ToolInput.Bool(input, "asOverhang", true)))
        };

        private static ToolDefinition FilletEdges(SemanticMutationService mutation) => new ToolDefinition
        {
            Name = "fillet_edges",
            Description =
                "Round the edges of a mass. Corner treatment is an SD-level move, and {role: 'outside-corner'} " +
                "is one selector that covers every outside corner on the mass — you do not have to enumerate " +
                "edge ids. A radius larger than the adjacent faces can absorb will fail; the error says so.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""massId"", ""edgeSelectors"", ""radius""],
  ""properties"": {
    ""massId"": { ""type"": ""string"" },
    ""edgeSelectors"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""edgeId"": { ""type"": ""string"" },
          ""edgeIndex"": { ""type"": ""integer"" },
          ""role"": { ""type"": ""string"", ""enum"": [""parapet"",""outside-corner"",""inside-corner"",""roof-ridge"",""eave"",""other""] }
        },
        ""additionalProperties"": false
      }
    },
    ""radius"": { ""type"": ""number"" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) =>
            {
                var selectors = ToolInput.Require(input, "edgeSelectors")
                    .EnumerateArray()
                    .Select(EdgeSelector.Parse)
                    .ToList();

                return ToolResult.Ok(mutation.FilletEdges(
                    ToolInput.RequireString(input, "massId"),
                    selectors,
                    ToolInput.RequireDouble(input, "radius")));
            }
        };

        private static ToolDefinition PromoteOpeningToEntry(SemanticMutationService mutation) => new ToolDefinition
        {
            Name = "promote_opening_to_entry",
            Description =
                "Mark an existing opening as the building's entry. Entry is a property on an Opening, not a " +
                "separate element — nothing new is created, and find_element('the main entry') will resolve " +
                "to it afterwards.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""openingId"", ""entryType""],
  ""properties"": {
    ""openingId"": { ""type"": ""string"" },
    ""entryType"": { ""type"": ""string"", ""enum"": [""Main"", ""Secondary"", ""Service"", ""Emergency""] }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.PromoteOpeningToEntry(
                ToolInput.RequireString(input, "openingId"),
                ToolInput.RequireString(input, "entryType")))
        };
    }
}
