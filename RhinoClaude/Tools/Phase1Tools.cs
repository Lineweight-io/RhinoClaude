using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Rhino.Geometry;
using RhinoClaude.Agent;
using RhinoClaude.Services.Agent;

namespace RhinoClaude.Tools
{
    /// <summary>
    /// The Tier 1 tool set registered in phase 1: enough query / create / transform /
    /// vision coverage to prove the loop end-to-end, plus the Tier 2 script escape hatch
    /// and signal_done.
    ///
    /// Names follow the plan's §3 inventory so later phases add tools rather than rename
    /// these. The one addition beyond the plan's 34 is <c>delete_objects</c> — the plan has
    /// no delete tool at all, which makes an agent that mis-creates geometry unable to
    /// clean up after itself.
    /// </summary>
    public static class Phase1Tools
    {
        public static List<ToolDefinition> Build(
            RhinoQueryService query,
            RhinoMutationService mutation,
            ViewCaptureService capture,
            ScriptExecutorService script,
            AgentSettings settings)
        {
            var tools = new List<ToolDefinition>
            {
                DescribeDocument(query),
                ListLayers(query),
                ListObjects(query),
                GetObject(query),
                GetSelection(query),
                EnsureLayer(mutation),
                CreateBox(mutation),
                CreateLineCurve(mutation),
                TranslateObjects(mutation),
                AssignObjectsToLayer(mutation),
                DeleteObjects(mutation),
                CaptureViews(capture),
            };

            if (settings.EnableScriptTool)
                tools.Add(RunScript(script, settings));

            tools.Add(SignalDone());
            return tools;
        }

        // ── Query ─────────────────────────────────────────────────────

        private static ToolDefinition DescribeDocument(RhinoQueryService query) => new ToolDefinition
        {
            Name = "describe_document",
            IsReadOnly = true,
            Description =
                "High-level information about the open Rhino document: name, model units, tolerances, " +
                "layer and object counts, current selection size, and the active viewport camera. " +
                "Call this first in any turn that will create or measure geometry — every length you " +
                "supply to other tools is in the document's model units, and you cannot know those " +
                "without asking.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{},""additionalProperties"":false}",
            Handler = (input, ct) => ToolResult.Ok(query.DescribeDocument())
        };

        private static ToolDefinition ListLayers(RhinoQueryService query) => new ToolDefinition
        {
            Name = "list_layers",
            IsReadOnly = true,
            Description =
                "Enumerate the document's layers with their full paths, visibility, lock state, object " +
                "counts and colours. Layer full paths use '::' between parent and child (e.g. " +
                "'Walls::Interior'); other tools that take a layer expect that exact full path.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""properties"": {
    ""includeHidden"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Include layers that are currently switched off."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.ListLayers(GetBool(input, "includeHidden", true)))
        };

        private static ToolDefinition ListObjects(RhinoQueryService query) => new ToolDefinition
        {
            Name = "list_objects",
            IsReadOnly = true,
            Description =
                "Enumerate objects with optional filters. Returns id, type, layer, name, bounding box and " +
                "RC: tags for each. Use layerFullPath to list one layer's contents. Results are capped by " +
                "'limit'; the response reports totalMatched and whether it was truncated.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""properties"": {
    ""layerFullPath"": { ""type"": ""string"", ""description"": ""Only objects on this layer, e.g. 'Walls::Interior'."" },
    ""objectType"": { ""type"": ""string"", ""description"": ""Rhino object type name, e.g. 'Brep', 'Curve', 'Point', 'Mesh'."" },
    ""hasTagKey"": { ""type"": ""string"", ""description"": ""Only objects that have any value for this RC: tag key, e.g. 'RC:ElementType'."" },
    ""limit"": { ""type"": ""integer"", ""default"": 200, ""minimum"": 1, ""maximum"": 1000 }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.ListObjects(
                GetString(input, "layerFullPath"),
                GetString(input, "objectType"),
                GetString(input, "hasTagKey"),
                GetInt(input, "limit", 200)))
        };

        private static ToolDefinition GetObject(RhinoQueryService query) => new ToolDefinition
        {
            Name = "get_object",
            IsReadOnly = true,
            Description =
                "Full detail for one object: bounding box, tags, and a geometry summary (curve length, " +
                "Brep face/edge counts, volume, validity). Set includeSubobjects to also get per-face " +
                "and per-edge indexing with areas, centroids, normals and planarity — those indices are " +
                "what later face/edge editing tools will refer to.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""id""],
  ""properties"": {
    ""id"": { ""type"": ""string"", ""description"": ""Object GUID."" },
    ""includeSubobjects"": { ""type"": ""boolean"", ""default"": false }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(query.GetObject(
                RequireString(input, "id"),
                GetBool(input, "includeSubobjects", false)))
        };

        private static ToolDefinition GetSelection(RhinoQueryService query) => new ToolDefinition
        {
            Name = "get_selection",
            IsReadOnly = true,
            Description =
                "The ids of the objects the user currently has selected in Rhino. When the user's request " +
                "says 'this', 'these', or 'the selected ones', resolve it with this rather than guessing.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{},""additionalProperties"":false}",
            Handler = (input, ct) => ToolResult.Ok(query.GetSelection())
        };

        // ── Layer ─────────────────────────────────────────────────────

        private static ToolDefinition EnsureLayer(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "ensure_layer",
            Description =
                "Create a layer if it does not already exist, and return it either way — safe to call " +
                "repeatedly. Parent layers in a '::' path are created as needed. The response's 'created' " +
                "field tells you whether anything was actually added.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""fullPath""],
  ""properties"": {
    ""fullPath"": { ""type"": ""string"", ""description"": ""Full layer path, e.g. 'Walls' or 'Walls::Interior'."" },
    ""colorHex"": { ""type"": ""string"", ""description"": ""Optional layer colour as 6-digit hex, e.g. '#8B4513'. Applied to the leaf layer only."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.EnsureLayer(
                RequireString(input, "fullPath"),
                GetString(input, "colorHex")))
        };

        private static ToolDefinition AssignObjectsToLayer(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "assign_objects_to_layer",
            Description =
                "Move existing objects onto a layer. The layer must already exist — call ensure_layer first.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids"", ""layerFullPath""],
  ""properties"": {
    ""ids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1 },
    ""layerFullPath"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.AssignObjectsToLayer(
                RequireStringList(input, "ids"),
                RequireString(input, "layerFullPath")))
        };

        // ── Create geometry ───────────────────────────────────────────

        private static ToolDefinition CreateBox(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "create_box",
            Description =
                "Create an axis-aligned solid box (a closed Brep) from two opposite corners. Coordinates " +
                "are [x, y, z] in the document's model units. The corners may be given in any order.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""corner1"", ""corner2""],
  ""properties"": {
    ""corner1"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3 },
    ""corner2"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3 },
    ""layer"": { ""type"": ""string"", ""description"": ""Full path of an existing layer."" },
    ""name"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.CreateBox(
                RhinoQueryService.ReadPoint(RequireProperty(input, "corner1")),
                RhinoQueryService.ReadPoint(RequireProperty(input, "corner2")),
                GetString(input, "layer"),
                GetString(input, "name")))
        };

        private static ToolDefinition CreateLineCurve(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "create_line_curve",
            Description =
                "Create a curve through a list of points. degree 1 (the default) makes a straight line or " +
                "polyline through the points exactly; degree 3 interpolates a smooth NURBS curve through " +
                "them. Two points and degree 1 is a single line segment. Set closed to join the last point " +
                "back to the first.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""points""],
  ""properties"": {
    ""points"": {
      ""type"": ""array"",
      ""minItems"": 2,
      ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3 }
    },
    ""degree"": { ""type"": ""integer"", ""default"": 1, ""enum"": [1, 3], ""description"": ""1 = polyline through the points; 3 = smooth interpolated curve."" },
    ""closed"": { ""type"": ""boolean"", ""default"": false },
    ""layer"": { ""type"": ""string"" },
    ""name"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) =>
            {
                var pointsElement = RequireProperty(input, "points");
                var points = pointsElement.EnumerateArray()
                                          .Select(RhinoQueryService.ReadPoint)
                                          .ToList();
                return ToolResult.Ok(mutation.CreateLineCurve(
                    points,
                    GetInt(input, "degree", 1),
                    GetBool(input, "closed", false),
                    GetString(input, "layer"),
                    GetString(input, "name")));
            }
        };

        // ── Transform / delete ────────────────────────────────────────

        private static ToolDefinition TranslateObjects(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "translate_objects",
            Description =
                "Move objects by a vector. Set copy to true to leave the originals in place and move " +
                "duplicates instead. The response's updatedIds are the ids after the move — note that " +
                "Rhino assigns a new id when copy is true.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids"", ""vector""],
  ""properties"": {
    ""ids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1 },
    ""vector"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3 },
    ""copy"": { ""type"": ""boolean"", ""default"": false }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.TranslateObjects(
                RequireStringList(input, "ids"),
                RhinoQueryService.ReadVector(RequireProperty(input, "vector")),
                GetBool(input, "copy", false)))
        };

        private static ToolDefinition DeleteObjects(RhinoMutationService mutation) => new ToolDefinition
        {
            Name = "delete_objects",
            Description =
                "Delete objects from the document. Use this to clean up geometry you created in error. " +
                "Deletions are inside the session's undo group, so 'Revert session' brings them back — " +
                "but be sure before deleting anything you did not create yourself.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""ids""],
  ""properties"": {
    ""ids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1 }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(mutation.DeleteObjects(RequireStringList(input, "ids")))
        };

        // ── View ──────────────────────────────────────────────────────

        private static ToolDefinition CaptureViews(ViewCaptureService capture) => new ToolDefinition
        {
            Name = "capture_views",
            IsReadOnly = true,
            Description =
                "Look at the model. Supply one or more camera specs and get back one image per spec in a " +
                "single call — a plan, a front elevation and an iso costs one round trip, not three. " +
                "Each shot is exactly one of: {namedView}, {cameraLocation + cameraTarget}, " +
                "{preset + optional framingIds}, or {orbitFromCurrent}. Presets and orbits frame the whole " +
                "document unless framingIds names specific objects. Maximum 6 shots per call. " +
                "Use this when seeing the geometry would tell you something you cannot get from " +
                "bounding boxes — checking alignment, proportion, or whether something looks wrong. " +
                "Do not capture after every edit; images are expensive.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""shots""],
  ""properties"": {
    ""shots"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""maxItems"": 6,
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""namedView"": { ""type"": ""string"", ""description"": ""Use a saved named view by name."" },
          ""cameraLocation"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3 },
          ""cameraTarget"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3 },
          ""cameraUp"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3, ""description"": ""Defaults to world Z."" },
          ""preset"": {
            ""type"": ""string"",
            ""enum"": [""plan"", ""front"", ""back"", ""left"", ""right"", ""iso_ne"", ""iso_nw"", ""iso_se"", ""iso_sw""]
          },
          ""orbitFromCurrent"": {
            ""type"": ""object"",
            ""properties"": {
              ""yawDegrees"": { ""type"": ""number"" },
              ""pitchDegrees"": { ""type"": ""number"" }
            },
            ""additionalProperties"": false
          },
          ""framingIds"": {
            ""type"": ""array"",
            ""items"": { ""type"": ""string"" },
            ""description"": ""Frame these objects instead of the whole document.""
          }
        },
        ""additionalProperties"": false
      }
    },
    ""width"": { ""type"": ""integer"", ""default"": 1280, ""maximum"": 1600 },
    ""height"": { ""type"": ""integer"", ""default"": 800, ""maximum"": 1200 },
    ""displayMode"": { ""type"": ""string"", ""enum"": [""wireframe"", ""shaded"", ""rendered""], ""default"": ""shaded"" },
    ""showGrid"": { ""type"": ""boolean"", ""default"": false }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) =>
            {
                var request = ViewCaptureService.ParseRequest(input);
                var captures = capture.Capture(request);

                var result = ToolResult.Ok(new Dictionary<string, object>
                {
                    { "shots", captures.Select(c => (object)new Dictionary<string, object>
                        {
                            { "index", c.Index },
                            { "shot", c.Shot },
                            { "width", c.Width },
                            { "height", c.Height },
                            { "bytes", c.Bytes },
                            { "cameraLocation", c.CameraLocation },
                            { "cameraTarget", c.CameraTarget },
                            { "notes", c.Notes }
                        }).ToList() }
                });

                foreach (var c in captures)
                {
                    result.Images.Add(new ToolImage
                    {
                        MediaType = c.MediaType,
                        Base64 = c.Base64,
                        Bytes = c.Data
                    });
                }

                return result;
            }
        };

        // ── Tier 2 escape hatch ───────────────────────────────────────

        private static ToolDefinition RunScript(ScriptExecutorService script, AgentSettings settings) => new ToolDefinition
        {
            Name = "run_rhinocommon_script",
            Description =
                "Escape hatch for anything the curated tools do not cover. Runs a C# script with full " +
                "RhinoCommon access inside an isolated undo record. Prefer the curated tools when one " +
                "exists — use this for one-offs, unusual geometry, or when a curated tool is missing a " +
                "parameter you need. Globals available: Doc (RhinoDoc), Tags (TagService), " +
                "Log(string) for output, Result for the return value, and Cancellation. " +
                "System, System.Collections.Generic, System.Linq, System.IO, Rhino, Rhino.Geometry, " +
                "Rhino.DocObjects and Rhino.Commands are pre-imported — no using directives needed. " +
                "Scripts must not do network or file I/O, start processes, or load assemblies. " +
                "Every call is logged.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""code"", ""purpose""],
  ""properties"": {
    ""code"": { ""type"": ""string"", ""description"": ""C# script body. Assign to Result to return a value. Use Log(...) rather than Console.WriteLine."" },
    ""purpose"": { ""type"": ""string"", ""description"": ""One sentence describing what this script is meant to do. Logged for later analysis."" },
    ""expectedResultShape"": { ""type"": ""string"", ""description"": ""Optional. What Result should hold, e.g. 'list of GUIDs of created objects'."" },
    ""timeoutSeconds"": { ""type"": ""integer"", ""default"": 15, ""maximum"": 60 }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => script.Run(
                RequireString(input, "code"),
                RequireString(input, "purpose"),
                GetString(input, "expectedResultShape"),
                GetInt(input, "timeoutSeconds", settings.ScriptTimeoutSeconds),
                ct)
        };

        // ── Meta ──────────────────────────────────────────────────────

        private static ToolDefinition SignalDone() => new ToolDefinition
        {
            Name = "signal_done",
            IsReadOnly = true,
            TerminatesTurn = true,
            Description =
                "Call this when you believe the user's request is complete. Summarize what you did and " +
                "what the user should now see in the model. This ends the turn.",
            InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""summary"", ""expectedOutcome""],
  ""properties"": {
    ""summary"": { ""type"": ""string"", ""description"": ""What you did, in the user's terms."" },
    ""expectedOutcome"": { ""type"": ""string"", ""description"": ""What the user should now see in the model."" }
  },
  ""additionalProperties"": false
}",
            Handler = (input, ct) => ToolResult.Ok(new Dictionary<string, object>
            {
                { "acknowledged", true },
                // Self-review (plan §5) lands in a later phase. Returning a fixed verdict
                // keeps the tool's contract stable so wiring the reviewer in later is additive.
                { "reviewVerdict", "ship" },
                { "notes", "Automated self-review is not enabled in this build; the turn ended on your signal." }
            })
        };

        // ── JSON input helpers ────────────────────────────────────────

        private static JsonElement RequireProperty(JsonElement input, string name)
        {
            if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                throw new ArgumentException("Required parameter '" + name + "' is missing.");
            return value;
        }

        private static string RequireString(JsonElement input, string name)
        {
            var value = RequireProperty(input, name);
            if (value.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Parameter '" + name + "' must be a string.");
            return value.GetString();
        }

        private static string GetString(JsonElement input, string name)
        {
            return input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static bool GetBool(JsonElement input, string name, bool fallback)
        {
            if (!input.TryGetProperty(name, out var value)) return fallback;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            return fallback;
        }

        private static int GetInt(JsonElement input, string name, int fallback)
        {
            if (input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out int parsed))
            {
                return parsed;
            }
            return fallback;
        }

        private static List<string> RequireStringList(JsonElement input, string name)
        {
            var value = RequireProperty(input, name);
            if (value.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Parameter '" + name + "' must be an array of strings.");

            var list = value.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString())
                            .ToList();

            if (list.Count == 0)
                throw new ArgumentException("Parameter '" + name + "' must contain at least one value.");

            return list;
        }
    }
}
