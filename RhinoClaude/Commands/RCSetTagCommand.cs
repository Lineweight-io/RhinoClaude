using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoClaude.Schema;
using RhinoClaude.Services;

namespace RhinoClaude.Commands
{
    /// <summary>
    /// Command: RCSetTag
    /// Directly set RC: tags on selected objects without calling the API.
    /// The user picks a key and enters a value.
    /// </summary>
    public class RCSetTagCommand : Command
    {
        public override string EnglishName => "RCSetTag";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = RhinoClaudePlugin.Instance;
            var tagService = plugin.TagService;

            // ── Step 1: Get selected objects ─────────────────────────
            var selectedObjects = new List<RhinoObject>();
            foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
            {
                selectedObjects.Add(obj);
            }

            if (selectedObjects.Count == 0)
            {
                var go = new GetObject();
                go.SetCommandPrompt("Select objects to tag");
                go.GroupSelect = true;
                go.GetMultiple(1, 0);
                if (go.CommandResult() != Result.Success)
                    return go.CommandResult();

                for (int i = 0; i < go.ObjectCount; i++)
                    selectedObjects.Add(go.Object(i).Object());
            }

            // ── Step 2: Let user pick a tag key ──────────────────────
            var getKey = new GetOption();
            getKey.SetCommandPrompt("Select tag to set");

            // Add each schema key as an option (without the RC: prefix for cleaner display)
            var keyOptions = new Dictionary<int, string>();
            foreach (var key in TagSchema.AllKeys)
            {
                string shortName = key.Replace(TagSchema.Prefix, "");
                int idx = getKey.AddOption(shortName);
                keyOptions[idx] = key;
            }

            // Also add Clear and ClearAll options
            int clearIdx = getKey.AddOption("RemoveTag");
            int clearAllIdx = getKey.AddOption("ClearAll");

            if (getKey.Get() != GetResult.Option)
                return Result.Cancel;

            int selectedIdx = getKey.OptionIndex();

            // ── Handle ClearAll ──────────────────────────────────────
            if (selectedIdx == clearAllIdx)
            {
                int totalCleared = 0;
                foreach (var obj in selectedObjects)
                    totalCleared += tagService.ClearAllTags(doc, obj);

                RhinoApp.WriteLine(string.Format(
                    "RhinoClaude: Cleared {0} tag(s) from {1} object(s).",
                    totalCleared, selectedObjects.Count));
                return Result.Success;
            }

            // ── Handle RemoveTag ─────────────────────────────────────
            if (selectedIdx == clearIdx)
            {
                var getRemoveKey = new GetOption();
                getRemoveKey.SetCommandPrompt("Select tag to remove");
                var removeKeyOptions = new Dictionary<int, string>();
                foreach (var key in TagSchema.AllKeys)
                {
                    string shortName = key.Replace(TagSchema.Prefix, "");
                    int idx = getRemoveKey.AddOption(shortName);
                    removeKeyOptions[idx] = key;
                }

                if (getRemoveKey.Get() != GetResult.Option)
                    return Result.Cancel;

                if (!removeKeyOptions.TryGetValue(getRemoveKey.OptionIndex(), out string removeKey))
                    return Result.Cancel;

                int removed = 0;
                foreach (var obj in selectedObjects)
                {
                    if (tagService.RemoveTag(doc, obj, removeKey))
                        removed++;
                }

                string display = TagSchema.DisplayNames.GetValueOrDefault(removeKey, removeKey);
                RhinoApp.WriteLine(string.Format(
                    "RhinoClaude: Removed {0} from {1} object(s).", display, removed));
                return Result.Success;
            }

            // ── Step 3: Get the selected key ─────────────────────────
            if (!keyOptions.TryGetValue(selectedIdx, out string tagKey))
                return Result.Cancel;

            // ── Step 4: Get the value ────────────────────────────────
            string tagValue;
            string displayName = TagSchema.DisplayNames.GetValueOrDefault(tagKey, tagKey);

            // If the key has constrained values, show them as options
            if (TagSchema.ConstrainedValues.TryGetValue(tagKey, out string[] allowedValues))
            {
                var getVal = new GetOption();
                getVal.SetCommandPrompt(string.Format("Set {0}", displayName));

                var valOptions = new Dictionary<int, string>();
                foreach (var val in allowedValues)
                {
                    // Replace spaces/dots/hyphens for valid option names
                    string optName = val.Replace(" ", "_").Replace(".", "_").Replace("-", "_");
                    int idx = getVal.AddOption(optName);
                    valOptions[idx] = val;
                }

                if (getVal.Get() != GetResult.Option)
                    return Result.Cancel;

                if (!valOptions.TryGetValue(getVal.OptionIndex(), out tagValue))
                    return Result.Cancel;
            }
            else
            {
                // Freeform text input
                var getString = new GetString();
                getString.SetCommandPrompt(string.Format("Enter value for {0}", displayName));

                if (getString.Get() != GetResult.String)
                    return Result.Cancel;

                tagValue = getString.StringResult().Trim();
                if (string.IsNullOrEmpty(tagValue))
                    return Result.Cancel;
            }

            // ── Step 5: Write the tag to all selected objects ────────
            int successCount = 0;
            var errors = new List<string>();

            foreach (var obj in selectedObjects)
            {
                string error = tagService.SetTag(doc, obj, tagKey, tagValue);
                if (error == null)
                    successCount++;
                else
                    errors.Add(error);
            }

            RhinoApp.WriteLine(string.Format(
                "RhinoClaude: Set {0} = '{1}' on {2} of {3} object(s).",
                displayName, tagValue, successCount, selectedObjects.Count));

            if (errors.Count > 0)
            {
                foreach (var err in errors)
                    RhinoApp.WriteLine(string.Format("  Warning: {0}", err));
            }

            return Result.Success;
        }
    }
}
