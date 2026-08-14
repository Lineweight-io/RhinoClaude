using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using RhinoClaude.Schema;
using RhinoClaude.Services;

namespace RhinoClaude.Commands
{
    /// <summary>
    /// Command: RCInspectTags
    /// Display all RC: tags on the currently selected objects.
    /// Provides a formatted view in the Rhino command line.
    /// </summary>
    public class RCInspectTagsCommand : Command
    {
        public override string EnglishName => "RCInspectTags";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = RhinoClaudePlugin.Instance;
            var tagService = plugin.TagService;

            // Get selected objects
            var selectedObjects = doc.Objects.GetSelectedObjects(false, false).ToList();

            if (selectedObjects.Count == 0)
            {
                RhinoApp.WriteLine("RhinoClaude: No objects selected. Select objects to inspect their tags.");
                return Result.Cancel;
            }

            RhinoApp.WriteLine("═══════════════════════════════════════════");
            RhinoApp.WriteLine(string.Format("  Tag Inspector — {0} object(s) selected", selectedObjects.Count));
            RhinoApp.WriteLine("═══════════════════════════════════════════");

            int taggedCount = 0;
            int untaggedCount = 0;

            // Limit display to avoid flooding the console
            int displayLimit = Math.Min(selectedObjects.Count, 30);

            for (int i = 0; i < displayLimit; i++)
            {
                var obj = selectedObjects[i];
                var tags = tagService.GetAllTags(obj);

                // Object header
                string name = obj.Name ?? "(unnamed)";
                string layer = obj.Document?.Layers[obj.Attributes.LayerIndex]?.FullPath ?? "?";
                string objId = obj.Id.ToString().Substring(0, 8);

                RhinoApp.WriteLine("─────────────────────────────────────────");
                RhinoApp.WriteLine(string.Format("  [{0}] {1}  (layer: {2}, id: {3}...)",
                    obj.ObjectType, name, layer, objId));

                if (tags.Count == 0)
                {
                    RhinoApp.WriteLine("    (no RC: tags)");
                    untaggedCount++;
                }
                else
                {
                    taggedCount++;
                    foreach (var kvp in tags)
                    {
                        string displayName = TagSchema.DisplayNames.GetValueOrDefault(kvp.Key, kvp.Key);
                        RhinoApp.WriteLine(string.Format("    {0}: {1}", displayName, kvp.Value));
                    }
                }
            }

            if (selectedObjects.Count > displayLimit)
            {
                RhinoApp.WriteLine("─────────────────────────────────────────");
                RhinoApp.WriteLine(string.Format("  ... and {0} more object(s) not shown.",
                    selectedObjects.Count - displayLimit));
            }

            // Summary
            RhinoApp.WriteLine("═══════════════════════════════════════════");
            RhinoApp.WriteLine(string.Format("  Summary: {0} tagged, {1} untagged (of {2} shown)",
                taggedCount, untaggedCount, displayLimit));
            RhinoApp.WriteLine("═══════════════════════════════════════════");

            return Result.Success;
        }
    }
}
