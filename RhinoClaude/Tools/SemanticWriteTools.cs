using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RhinoClaude.Agent;
using RhinoClaude.Semantic;
using RhinoClaude.Services.Semantic;

namespace RhinoClaude.Tools
{
    /// <summary>
    /// The massing operations (semantic plan §4.6) — seven writes plus the Entry promotion,
    /// and the four solid-preserving moves added after the gable test: subdivide_face,
    /// move_face, move_edge and the create_gable_roof composite.
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
                PromoteOpeningToEntry(mutation),

                // The solid-preserving set. move_face and move_edge deliberately keep the raw
                // tools' names: they take the semantic {massId, selector} shape *and* the raw
                // {brepId, index} one, so registering them last replaces the Tier 1 pair with a
                // superset rather than leaving the agent two tools with the same job.
                SubdivideFace(mutation),
                MoveFace(mutation),
                MoveEdge(mutation),
                CreateGableRoof(mutation)
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

        // ── Solid-preserving moves ────────────────────────────────────

        /// <summary>Named axes plus the two face-relative words, shared by move_face and move_edge.</summary>
        private const string DirectionSchema = @"{
      ""type"": ""string"",
      ""description"": ""A named direction: +x, -x, +y, -y, +z, -z, up, down, north, south, east, west — or, on move_face only, outward/inward along the face's own normal. Use directionVector instead for anything off-axis."",
      ""enum"": [""+x"",""-x"",""+y"",""-y"",""+z"",""-z"",""up"",""down"",""north"",""south"",""east"",""west"",""outward"",""inward""]
    }";

        private const string DirectionVectorSchema = @"{
      ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3,
      ""description"": ""[x, y, z]. Normalised before use, so only its direction matters.""
    }";

        private static ToolDefinition SubdivideFace(SemanticMutationService mutation) => new ToolDefinition
        {
            Name = "subdivide_face",
            Description =
                "Divide one face of a solid, keeping the solid closed. This is the move behind every feature " +
                "that reshapes a mass rather than sitting on top of it: a gable ridge, a dormer, a stepped " +
                "setback, a sloped roof. Split the face, then move_edge the edges the split created — the " +
                "response returns their ids ready to pass straight through. Cut it five ways: one line " +
                "between two points, several lines applied together, an open polyline, existing curves in " +
                "the document, or a proportional split along the face's own axes (0.5 is the midline). " +
                "A single line is extended to the face's edges automatically and has to cross it. A set of " +
                "cuts is used exactly as given and the rule applies to the set, not its members: no one cut " +
                "has to reach the boundary, but together they have to divide the face. That is what an " +
                "L-shaped roof needs — two ridge segments plus a cut out to the outside corner and one in " +
                "to the inside corner, four cuts, none of which crosses the face alone.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""massId"", ""faceSelector"", ""cut""],
  ""properties"": {
    ""massId"": { ""type"": ""string"" },
    ""faceSelector"": " + SemanticReadTools.FaceSelectorSchema + @",
    ""cut"": {
      ""type"": ""object"",
      ""description"": ""How to divide the face. One of: {line}, {lines}, {polyline}, {cuttingCurveId} or {cuttingCurveIds}, or {splitRatio, direction}."",
      ""properties"": {
        ""line"": {
          ""type"": ""object"",
          ""description"": ""One cut between two points. Extended to the face's edges automatically, so it must cross the face. Projected onto the face along its normal, so on a roof the z is optional."",
          ""properties"": {
            ""startPoint"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
            ""endPoint"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 }
          },
          ""additionalProperties"": false
        },
        ""lines"": {
          ""type"": ""array"", ""minItems"": 1,
          ""description"": ""Several cuts applied in one split, used as given rather than extended. Together they must divide the face; individually they need not reach its boundary. This is the form an L-shaped or T-shaped roof needs."",
          ""items"": {
            ""type"": ""object"",
            ""properties"": {
              ""startPoint"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
              ""endPoint"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 }
            },
            ""required"": [""startPoint"", ""endPoint""],
            ""additionalProperties"": false
          }
        },
        ""polyline"": {
          ""type"": ""array"", ""minItems"": 2,
          ""description"": ""An open polyline, cut as its segments. A bent ridge is two points plus the turning point."",
          ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 }
        },
        ""cuttingCurveId"": { ""type"": ""string"", ""description"": ""An existing curve, projected onto the face before it cuts."" },
        ""cuttingCurveIds"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Several existing curves, applied together in one split."" },
        ""splitRatio"": { ""type"": ""number"", ""description"": ""Strictly between 0 and 1. 0.5 is the midline."" },
        ""direction"": { ""type"": ""string"", ""enum"": [""u"", ""v""], ""default"": ""u"", ""description"": ""Which of the face's own axes splitRatio runs along. On a roof, u is east-west and v is north-south."" }
      },
      ""additionalProperties"": false
    }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.SubdivideFace(
                ToolInput.RequireString(input, "massId"),
                FaceSelector.Parse(ToolInput.Require(input, "faceSelector")),
                FaceCut.Parse(ToolInput.Require(input, "cut"))))
        };

        private static ToolDefinition MoveFace(SemanticMutationService mutation) => new ToolDefinition
        {
            Name = "move_face",
            Description =
                "Translate a whole face of a solid and let the faces around it follow — how an existing mass " +
                "gets taller, wider or set back without being rebuilt, and without ever ceasing to be one " +
                "closed solid. Pick the face by role or orientation with faceSelector on a mass; the raw " +
                "form, brepId plus faceIndex, still works on any Brep in the document. Moving along the " +
                "face's own normal is push_pull_face's job, but 'outward' and 'inward' do the same thing here.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""distance""],
  ""properties"": {
    ""massId"": { ""type"": ""string"", ""description"": ""The mass to edit. Use with faceSelector."" },
    ""faceSelector"": " + SemanticReadTools.FaceSelectorSchema + @",
    ""brepId"": { ""type"": ""string"", ""description"": ""Raw form: any Brep in the document. Use with faceIndex."" },
    ""faceIndex"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""Raw form: face index from get_object with includeSubobjects."" },
    ""direction"": " + DirectionSchema + @",
    ""directionVector"": " + DirectionVectorSchema + @",
    ""distance"": { ""type"": ""number"", ""description"": ""Model units along the direction. Negative reverses it."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) =>
            {
                string massId = ToolInput.String(input, "massId");
                string brepId = ToolInput.String(input, "brepId");

                if (string.IsNullOrWhiteSpace(massId) && !string.IsNullOrWhiteSpace(brepId))
                    return ToolResult.Ok(mutation.MoveSubObjectRaw(
                        true, brepId, ToolInput.Int(input, "faceIndex", -1),
                        ToolInput.String(input, "direction"),
                        ToolInput.DoubleList(input, "directionVector").ToArray(),
                        ToolInput.RequireDouble(input, "distance")));

                return ToolResult.Ok(mutation.MoveFace(
                    ToolInput.RequireString(input, "massId"),
                    FaceSelector.Parse(ToolInput.Require(input, "faceSelector")),
                    ToolInput.String(input, "direction"),
                    ToolInput.DoubleList(input, "directionVector").ToArray(),
                    ToolInput.RequireDouble(input, "distance")));
            }
        };

        private static ToolDefinition MoveEdge(SemanticMutationService mutation) => new ToolDefinition
        {
            Name = "move_edge",
            Description =
                "Translate one or more edges of a solid, letting the faces that meet them stretch to follow. " +
                "This is the second half of a gable: subdivide_face the roof along the ridge, then lift the " +
                "edges that created. Pass edgeIds straight from subdivide_face's newEdgeIds, or pick by role " +
                "— 'roof-ridge', 'parapet', 'eave'. Edges that belong to the same feature must go in ONE " +
                "call: an L-shaped ridge is two edges, and lifting them one at a time warps the roof faces " +
                "between them instead of raising them. Use edgeSelectors for that; the response reports " +
                "allFacesPlanar so you can tell. The raw form, brepId plus edgeIndex, still works on any Brep.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""distance""],
  ""properties"": {
    ""massId"": { ""type"": ""string"", ""description"": ""The mass to edit. Use with edgeSelector or edgeSelectors."" },
    ""edgeSelector"": " + EdgeSelectorSchema + @",
    ""edgeSelectors"": {
      ""type"": ""array"", ""minItems"": 1,
      ""description"": ""Several edges moved together in one transform. Required whenever the edges form one feature — a bent ridge, a stepped eave — because moving them separately warps the faces spanning them."",
      ""items"": " + EdgeSelectorSchema + @"
    },
    ""brepId"": { ""type"": ""string"", ""description"": ""Raw form: any Brep in the document. Use with edgeIndex."" },
    ""edgeIndex"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""Raw form: edge index from get_object with includeSubobjects."" },
    ""direction"": " + DirectionSchema + @",
    ""directionVector"": " + DirectionVectorSchema + @",
    ""distance"": { ""type"": ""number"" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) =>
            {
                string massId = ToolInput.String(input, "massId");
                string brepId = ToolInput.String(input, "brepId");

                if (string.IsNullOrWhiteSpace(massId) && !string.IsNullOrWhiteSpace(brepId))
                    return ToolResult.Ok(mutation.MoveSubObjectRaw(
                        false, brepId, ToolInput.Int(input, "edgeIndex", -1),
                        ToolInput.String(input, "direction"),
                        ToolInput.DoubleList(input, "directionVector").ToArray(),
                        ToolInput.RequireDouble(input, "distance")));

                var selectors = new List<EdgeSelector>();
                if (ToolInput.TryGet(input, "edgeSelectors", out var many)
                    && many.ValueKind == JsonValueKind.Array)
                    selectors.AddRange(many.EnumerateArray().Select(EdgeSelector.Parse));
                if (ToolInput.TryGet(input, "edgeSelector", out var one))
                    selectors.Add(EdgeSelector.Parse(one));

                if (selectors.Count == 0)
                    throw new ArgumentException(
                        "Give edgeSelector for one edge, or edgeSelectors for several that move together.");

                return ToolResult.Ok(mutation.MoveEdge(
                    ToolInput.RequireString(input, "massId"),
                    selectors,
                    ToolInput.String(input, "direction"),
                    ToolInput.DoubleList(input, "directionVector").ToArray(),
                    ToolInput.RequireDouble(input, "distance")));
            }
        };

        /// <summary>Shared by move_edge's single and list forms so the two cannot drift apart.</summary>
        private const string EdgeSelectorSchema = @"{
      ""type"": ""object"",
      ""description"": ""How to pick an edge. One of {edgeId} — including one returned by subdivide_face — {edgeIndex}, or {role}."",
      ""properties"": {
        ""edgeId"": { ""type"": ""string"" },
        ""edgeIndex"": { ""type"": ""integer"" },
        ""role"": { ""type"": ""string"", ""enum"": [""parapet"",""outside-corner"",""inside-corner"",""roof-ridge"",""eave"",""other""] }
      },
      ""additionalProperties"": false
    }";

        private static ToolDefinition CreateGableRoof(SemanticMutationService mutation) => new ToolDefinition
        {
            Name = "create_gable_roof",
            Description =
                "Turn a flat-topped mass into a gable in one move and one undo record: the top face is split " +
                "along the ridge and the new edges are raised together by pitchHeight, leaving one closed " +
                "solid rather than planes resting on a box. On a rectangular footprint give the ridge as two " +
                "points, usually the midline of the long direction running the full length. On an L or a T " +
                "the ridge bends: give ridgePoints through the turning point, and pass additionalCuts from " +
                "that turning point out to the outside corner and in to the inside corner. Those two extra " +
                "cuts are not decoration — without them the roof planes have no straight boundary to sit on " +
                "and come back warped. The response reports allFacesPlanar, which is the test of whether the " +
                "roof is right.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""massId"", ""pitchHeight""],
  ""properties"": {
    ""massId"": { ""type"": ""string"" },
    ""ridgeLineStart"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""Straight ridge, first point. [x, y] in plan; z is ignored — the ridge is projected onto the top face."" },
    ""ridgeLineEnd"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""Straight ridge, second point."" },
    ""ridgePoints"": {
      ""type"": ""array"", ""minItems"": 2,
      ""description"": ""A bent ridge, in order. Use instead of ridgeLineStart/End on an L or T plan: the ends run to the middle of each gable wall and the middle point is where the wings meet."",
      ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 }
    },
    ""additionalCuts"": {
      ""type"": ""array"",
      ""description"": ""Extra cuts made with the ridge but NOT raised — the hip line from the ridge turning point out to the outside corner, and the valley line in to the inside corner. Required for a bent ridge."",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""startPoint"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""endPoint"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 }
        },
        ""required"": [""startPoint"", ""endPoint""],
        ""additionalProperties"": false
      }
    },
    ""pitchHeight"": { ""type"": ""number"", ""description"": ""How far the ridge rises above the existing top face, in model units."" },
    ""faceSelector"": " + SemanticReadTools.FaceSelectorSchema + @"
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) =>
            {
                var selector = ToolInput.TryGet(input, "faceSelector", out var face)
                    ? FaceSelector.Parse(face)
                    : null;

                var ridge = new List<double[]>();
                if (ToolInput.TryGet(input, "ridgePoints", out var points)
                    && points.ValueKind == JsonValueKind.Array)
                {
                    ridge.AddRange(points.EnumerateArray()
                                         .Select(PointArray)
                                         .Where(p => p != null));
                }
                else
                {
                    ridge.Add(ToolInput.DoubleList(input, "ridgeLineStart").ToArray());
                    ridge.Add(ToolInput.DoubleList(input, "ridgeLineEnd").ToArray());
                }

                List<double[][]> extra = null;
                if (ToolInput.TryGet(input, "additionalCuts", out var cuts)
                    && cuts.ValueKind == JsonValueKind.Array)
                {
                    extra = cuts.EnumerateArray()
                                .Select(c => new[] { PointArray(c, "startPoint"), PointArray(c, "endPoint") })
                                .Where(pair => pair[0] != null && pair[1] != null)
                                .ToList();
                }

                return ToolResult.Ok(mutation.CreateGableRoof(
                    ToolInput.RequireString(input, "massId"),
                    ridge,
                    ToolInput.RequireDouble(input, "pitchHeight"),
                    selector,
                    extra));
            }
        };

        private static double[] PointArray(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array) return null;
            var numbers = element.EnumerateArray()
                                 .Where(e => e.ValueKind == JsonValueKind.Number)
                                 .Select(e => e.GetDouble())
                                 .ToArray();
            return numbers.Length >= 2 ? numbers : null;
        }

        private static double[] PointArray(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
                ? PointArray(value)
                : null;

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
