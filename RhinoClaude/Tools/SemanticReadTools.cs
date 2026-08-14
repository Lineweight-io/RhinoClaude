using System.Collections.Generic;
using RhinoClaude.Agent;
using RhinoClaude.Semantic;
using RhinoClaude.Services.Semantic;

namespace RhinoClaude.Tools
{
    /// <summary>
    /// The seventeen semantic read tools (semantic plan §4.1–§4.5). They live alongside phase
    /// 1's raw geometry tools in the same registry — the agent does not know or care which tier
    /// a tool belongs to, it just picks the one that answers the question.
    ///
    /// Registration order is stable, as in phase 1: the tools array renders first in the prompt,
    /// so shuffling it would break prompt caching every turn.
    /// </summary>
    public static class SemanticReadTools
    {
        /// <summary>
        /// The FaceSelector union, shared by every tool that operates on a face. One schema
        /// string so the model sees exactly the same shape everywhere — the same trick phase 1
        /// uses for capture_views' CameraShot union.
        /// </summary>
        public const string FaceSelectorSchema = @"{
      ""type"": ""object"",
      ""description"": ""How to pick the face. Exactly one of: {faceId}, {faceIndex}, {orientation}, {role}, or {role + orientation}. Add elevationRange to narrow to a band of a stepped facade. When several faces match, the largest wins and the response says so."",
      ""properties"": {
        ""faceId"": { ""type"": ""string"", ""description"": ""Face id from get_mass_faces. Positional — it changes when the Brep is edited."" },
        ""faceIndex"": { ""type"": ""integer"", ""description"": ""Brep face index on the target mass."" },
        ""orientation"": { ""type"": ""string"", ""enum"": [""N"",""NE"",""E"",""SE"",""S"",""SW"",""W"",""NW"",""up"",""down"",""other""] },
        ""role"": { ""type"": ""string"", ""enum"": [""facade"",""roof"",""floor"",""party-wall"",""interior"",""unclassified""] },
        ""elevationRange"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 2, ""description"": ""[zMin, zMax] in model units. A face qualifies when it overlaps the band."" }
      },
      ""additionalProperties"": false
    }";

        public static List<ToolDefinition> Build(SemanticQueryService query)
        {
            return new List<ToolDefinition>
            {
                // §4.1 Descriptive
                DescribeMassing(query),
                DescribeContext(query),
                FindElement(query),

                // §4.2 Mass catalog
                ListMasses(query),
                ListMassGroups(query),
                AnalyzeBooleanHistory(query),

                // §4.3 Face and edge analysis
                GetMassFaces(query),
                GetFace(query),
                GetMassEdges(query),
                CheckFaceRelationships(query),
                FindOpeningsInFace(query),

                // §4.4 Envelope, program, composition
                CheckWallWindowRatio(query),
                GetRoofAnalysis(query),
                GetProgramAllocation(query),
                CheckMassingComposition(query),
                GetLevelInfo(query),

                // §4.5 Constraints
                GetZoningEnvelope(query)
            };
        }

        // ── §4.1 Descriptive ──────────────────────────────────────────

        private static ToolDefinition DescribeMassing(SemanticQueryService query) => new ToolDefinition
        {
            Name = "describe_massing",
            IsReadOnly = true,
            Description =
                "Orient yourself. A narrative plus structured summary of the whole massing: every Mass with " +
                "its function, volume, footprint, height and face-role counts; how the masses relate (sits-on, " +
                "abuts, reads-as-one-form); site context; and totals. Call this first in any turn about the " +
                "design as a building rather than as objects. It also reports how many objects the classifier " +
                "could not read, which tells you how much of the file you are not seeing.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""properties"": {
    ""levelOfDetail"": { ""type"": ""string"", ""enum"": [""brief"", ""standard"", ""detailed""], ""default"": ""standard"" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(
                query.DescribeMassing(ToolInput.String(input, "levelOfDetail", MassingNarrator.Standard)))
        };

        private static ToolDefinition DescribeContext(SemanticQueryService query) => new ToolDefinition
        {
            Name = "describe_context",
            IsReadOnly = true,
            Description =
                "Site elements near the building: context buildings, streets, curbs, topography, utilities, " +
                "and the property line with the building's setbacks from it. Context is what the design " +
                "responds to — use this before any move that depends on orientation, approach, or frontage.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""properties"": {
    ""distance"": { ""type"": ""number"", ""default"": 200, ""description"": ""Only elements within this distance of the building envelope, in model units."" },
    ""includeTopography"": { ""type"": ""boolean"", ""default"": true },
    ""includeContextBuildings"": { ""type"": ""boolean"", ""default"": true },
    ""includeStreets"": { ""type"": ""boolean"", ""default"": true }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.DescribeContext(
                ToolInput.Double(input, "distance", 200),
                ToolInput.Bool(input, "includeTopography", true),
                ToolInput.Bool(input, "includeContextBuildings", true),
                ToolInput.Bool(input, "includeStreets", true)))
        };

        private static ToolDefinition FindElement(SemanticQueryService query) => new ToolDefinition
        {
            Name = "find_element",
            IsReadOnly = true,
            Description =
                "Look an element up the way the user described it: 'the north face of the office mass', " +
                "'the tallest mass', 'the main entry', 'all outside corners'. Returns matching element ids " +
                "with confidence, plus what the query was read as — check that reading before acting on the " +
                "result. Parsing is rules-based over the massing vocabulary, so plain architectural phrasing " +
                "works and prose does not need to be exact.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""query""],
  ""properties"": {
    ""query"": { ""type"": ""string"", ""description"": ""Natural-language description of the element."" },
    ""expect"": { ""type"": ""string"", ""enum"": [""one"", ""any""], ""default"": ""any"" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.FindElement(
                ToolInput.RequireString(input, "query"),
                ToolInput.String(input, "expect", "any")))
        };

        // ── §4.2 Mass catalog ─────────────────────────────────────────

        private static ToolDefinition ListMasses(SemanticQueryService query) => new ToolDefinition
        {
            Name = "list_masses",
            IsReadOnly = true,
            Description =
                "Enumerate the Masses — the solid Breps the semantic layer treats as building masses — with " +
                "function, volume, footprint, height, bounding box, adjacencies, and how each one was " +
                "classified. A mass with classifiedBy 'geometry-inference' was a guess from geometry alone: " +
                "hedge on it, and confirm with the user before any destructive move.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""properties"": {
    ""functionFilter"": { ""type"": ""string"", ""enum"": [""Office"",""Residential"",""Retail"",""Institutional"",""Common"",""Other""] },
    ""massGroupId"": { ""type"": ""string"", ""description"": ""Only masses in this MassGroup."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.ListMasses(
                ToolInput.String(input, "functionFilter"),
                ToolInput.String(input, "massGroupId")))
        };

        private static ToolDefinition ListMassGroups(SemanticQueryService query) => new ToolDefinition
        {
            Name = "list_mass_groups",
            IsReadOnly = true,
            Description =
                "Enumerate MassGroups — sets of Masses that read as one building or wing, from an explicit " +
                "tag, Rhino Group membership, or a shared parent layer. Use this when the user says 'the " +
                "office wing' and the wing is two masses that have not been boolean-unioned.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{},""additionalProperties"":false}",
            Handler = (input, ct) => ToolResult.Ok(query.ListMassGroups())
        };

        private static ToolDefinition AnalyzeBooleanHistory(SemanticQueryService query) => new ToolDefinition
        {
            Name = "analyze_boolean_history",
            IsReadOnly = true,
            Description =
                "How a Mass came to be its current shape, if Rhino recorded it. Most architects work with " +
                "history off, so historyAvailable is usually false — treat this as opportunistic. The " +
                "derivedFromTopology figures come from the Brep itself and are always there: closed voids, " +
                "openings in faces, recesses.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""massId""],
  ""properties"": {
    ""massId"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(
                query.AnalyzeBooleanHistory(ToolInput.RequireString(input, "massId")))
        };

        // ── §4.3 Face and edge analysis ───────────────────────────────

        private static ToolDefinition GetMassFaces(SemanticQueryService query) => new ToolDefinition
        {
            Name = "get_mass_faces",
            IsReadOnly = true,
            Description =
                "Every face of a Mass with its orientation, roles, area, elevation range and openings. This " +
                "is how you see a mass the way the architect does — face role and compass orientation are " +
                "the two axes nearly every massing question runs along. Roles are computed from geometry " +
                "each time you ask, not stored, so they are always current.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""massId""],
  ""properties"": {
    ""massId"": { ""type"": ""string"" },
    ""filterByRole"": { ""type"": ""string"", ""enum"": [""facade"",""roof"",""floor"",""party-wall"",""interior"",""unclassified""] },
    ""filterByOrientation"": { ""type"": ""string"", ""enum"": [""N"",""NE"",""E"",""SE"",""S"",""SW"",""W"",""NW"",""up"",""down"",""other""] }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.GetMassFaces(
                ToolInput.RequireString(input, "massId"),
                ToolInput.String(input, "filterByRole"),
                ToolInput.String(input, "filterByOrientation")))
        };

        private static ToolDefinition GetFace(SemanticQueryService query) => new ToolDefinition
        {
            Name = "get_face",
            IsReadOnly = true,
            Description =
                "Full detail for one face: orientation, roles, area, centroid, normal, elevation range, its " +
                "openings, any overhangs and recesses on it, and the edges bounding it.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""faceSelector""],
  ""properties"": {
    ""faceSelector"": " + FaceSelectorSchema + @",
    ""massId"": { ""type"": ""string"", ""description"": ""Required unless faceSelector is a faceId, or the document has exactly one mass."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.GetFace(
                FaceSelector.Parse(ToolInput.Require(input, "faceSelector")),
                ToolInput.String(input, "massId")))
        };

        private static ToolDefinition GetMassEdges(SemanticQueryService query) => new ToolDefinition
        {
            Name = "get_mass_edges",
            IsReadOnly = true,
            Description =
                "The significant edges of a Mass — parapets, outside and inside corners, roof ridges, eaves — " +
                "with their lengths and the faces they bound. Edges with role 'other' are omitted unless you " +
                "ask for them: a Brep has many edges and only some are ones an architect would name.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""massId""],
  ""properties"": {
    ""massId"": { ""type"": ""string"" },
    ""filterByRole"": { ""type"": ""string"", ""enum"": [""parapet"",""outside-corner"",""inside-corner"",""roof-ridge"",""eave"",""other""] }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.GetMassEdges(
                ToolInput.RequireString(input, "massId"),
                ToolInput.String(input, "filterByRole")))
        };

        private static ToolDefinition CheckFaceRelationships(SemanticQueryService query) => new ToolDefinition
        {
            Name = "check_face_relationships",
            IsReadOnly = true,
            Description =
                "Coplanar, parallel and perpendicular relationships between faces, and which faces are flush " +
                "across separate masses. Answers 'does the office mass's north face line up with the retail " +
                "mass's north face'. This is a geometric question, so it still works on curved or " +
                "mislabelled faces where orientation labels do not.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""properties"": {
    ""massIds"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Omit to compare every mass in the document."" },
    ""tolerance"": { ""type"": ""number"", ""description"": ""Coplanarity tolerance in model units. Defaults to the document's adjacency tolerance."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.CheckFaceRelationships(
                ToolInput.StringList(input, "massIds"),
                ToolInput.NullableDouble(input, "tolerance")))
        };

        private static ToolDefinition FindOpeningsInFace(SemanticQueryService query) => new ToolDefinition
        {
            Name = "find_openings_in_face",
            IsReadOnly = true,
            Description =
                "Every opening on one face, with type, width, height, sill height, area and position on the " +
                "face, plus that face's wall-window ratio. Openings are detected primarily as holes in the " +
                "mass's Brep face — which is how they get there when an architect boolean-cuts them — and " +
                "secondarily as objects drawn on an OPENING_* layer.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""faceSelector""],
  ""properties"": {
    ""faceSelector"": " + FaceSelectorSchema + @",
    ""massId"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.FindOpeningsInFace(
                FaceSelector.Parse(ToolInput.Require(input, "faceSelector")),
                ToolInput.String(input, "massId")))
        };

        // ── §4.4 Envelope, program, composition ───────────────────────

        private static ToolDefinition CheckWallWindowRatio(SemanticQueryService query) => new ToolDefinition
        {
            Name = "check_wall_window_ratio",
            IsReadOnly = true,
            Description =
                "Wall-window ratio, aggregated by compass orientation, per face, or over the whole envelope. " +
                "Only facade-role faces count — including roofs would give a smaller, wronger number. The " +
                "response reports how much face area could not be classified, so you can say what the ratio " +
                "excludes rather than implying full coverage.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""properties"": {
    ""scope"": { ""type"": ""string"", ""enum"": [""byOrientation"", ""byFace"", ""whole""], ""default"": ""byOrientation"" },
    ""massId"": { ""type"": ""string"", ""description"": ""Omit for the whole building."" },
    ""includeOverhangsAsShading"": { ""type"": ""boolean"", ""default"": false }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.CheckWallWindowRatio(
                ToolInput.String(input, "scope", WallWindowRatio.ScopeByOrientation),
                ToolInput.String(input, "massId"),
                ToolInput.Bool(input, "includeOverhangsAsShading", false)))
        };

        private static ToolDefinition GetRoofAnalysis(SemanticQueryService query) => new ToolDefinition
        {
            Name = "get_roof_analysis",
            IsReadOnly = true,
            Description =
                "Roof form: every roof-role face with its area, slope percentage, drainage direction and " +
                "bounding edges, plus total roof area, predominant form (flat / sloped / complex) and total " +
                "ridge, eave and parapet lengths. A flat roof reports null drainage by design — it drains " +
                "internally, not toward a compass point.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""properties"": {
    ""massId"": { ""type"": ""string"", ""description"": ""Omit for every mass."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.GetRoofAnalysis(ToolInput.String(input, "massId")))
        };

        private static ToolDefinition GetProgramAllocation(SemanticQueryService query) => new ToolDefinition
        {
            Name = "get_program_allocation",
            IsReadOnly = true,
            Description =
                "Volume and footprint by mass function — Office, Residential, Retail, Institutional, Common, " +
                "Other — with each function's share of the total. A large 'Other' share means masses are " +
                "untagged and not on MASS_* layers, not that the program is undecided.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{},""additionalProperties"":false}",
            Handler = (input, ct) => ToolResult.Ok(query.GetProgramAllocation())
        };

        private static ToolDefinition CheckMassingComposition(SemanticQueryService query) => new ToolDefinition
        {
            Name = "check_massing_composition",
            IsReadOnly = true,
            Description =
                "Deterministic composition facts: overall proportions and aspect ratios, symmetry about each " +
                "axis, the mass hierarchy ranked by volume with the primary-to-secondary ratio, the boolean " +
                "composition (unions, differences, cut volume), and vertical rhythm. Reach for this when the " +
                "user says something qualitative — 'too squat', 'the hierarchy is off' — and you need numbers " +
                "to discuss it against rather than an opinion.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{},""additionalProperties"":false}",
            Handler = (input, ct) => ToolResult.Ok(query.CheckMassingComposition())
        };

        private static ToolDefinition GetLevelInfo(SemanticQueryService query) => new ToolDefinition
        {
            Name = "get_level_info",
            IsReadOnly = true,
            Description =
                "Levels and their floor plates. Levels are usually inferred from a firm floor-to-floor " +
                "default rather than drawn, and inferred ones are flagged. Plate areas are each mass's " +
                "overall footprint — exact for a prismatic mass, approximate for a stepped one; use " +
                "slice_mass_at_elevation when you need the real plate.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""properties"": {
    ""levelName"": { ""type"": ""string"", ""description"": ""Substring match, e.g. 'Level 02' or 'Roof'."" },
    ""massId"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.GetLevelInfo(
                ToolInput.String(input, "levelName"),
                ToolInput.String(input, "massId")))
        };

        // ── §4.5 Constraints ──────────────────────────────────────────

        private static ToolDefinition GetZoningEnvelope(SemanticQueryService query) => new ToolDefinition
        {
            Name = "get_zoning_envelope",
            IsReadOnly = true,
            Description =
                "Compare the design to an allowable envelope: height limit, per-side setbacks from the " +
                "property line, and optionally FAR. Returns the allowed envelope, the current building's " +
                "numbers, and any violations with the masses responsible. Setbacks are measured against the " +
                "property line's bounding box, which is exact for a rectangular lot and flagged as " +
                "approximate otherwise. Height, setbacks and FAR are the whole scope — this does not check " +
                "egress, occupancy or energy code.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""maxHeight"", ""setbacks""],
  ""properties"": {
    ""maxHeight"": { ""type"": ""number"", ""description"": ""Height limit above the lowest mass base, in model units."" },
    ""setbacks"": {
      ""type"": ""object"",
      ""properties"": {
        ""N"": { ""type"": ""number"", ""default"": 0 },
        ""E"": { ""type"": ""number"", ""default"": 0 },
        ""S"": { ""type"": ""number"", ""default"": 0 },
        ""W"": { ""type"": ""number"", ""default"": 0 }
      },
      ""additionalProperties"": false
    },
    ""farMax"": { ""type"": ""number"", ""description"": ""Optional floor-area-ratio cap."" },
    ""propertyLineElementId"": { ""type"": ""string"", ""description"": ""Required when the document has more than one property line — the tool never picks one for you."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) =>
            {
                var setbacks = ToolInput.TryGet(input, "setbacks", out var element)
                    ? element
                    : default;

                var parameters = new ZoningParameters
                {
                    MaxHeight = ToolInput.RequireDouble(input, "maxHeight"),
                    FarMax = ToolInput.NullableDouble(input, "farMax"),
                    PropertyLineElementId = ToolInput.String(input, "propertyLineElementId")
                };

                if (setbacks.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    parameters.SetbackNorth = ToolInput.Double(setbacks, "N", 0);
                    parameters.SetbackEast = ToolInput.Double(setbacks, "E", 0);
                    parameters.SetbackSouth = ToolInput.Double(setbacks, "S", 0);
                    parameters.SetbackWest = ToolInput.Double(setbacks, "W", 0);
                }

                return ToolResult.Ok(query.GetZoningEnvelope(parameters));
            }
        };
    }
}
