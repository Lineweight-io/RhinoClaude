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
    /// Command: RCQuery
    /// Find and select objects by their RC: tag values.
    /// Supports querying by key, by key+value, listing distinct values,
    /// and selecting all tagged/untagged objects.
    /// </summary>
    public class RCQueryCommand : Command
    {
        public override string EnglishName => "RCQuery";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = RhinoClaudePlugin.Instance;
            var tagService = plugin.TagService;

            // ── Choose query mode ────────────────────────────────────
            var getMode = new GetOption();
            getMode.SetCommandPrompt("Query mode");
            int byTagIdx     = getMode.AddOption("ByTag");         // key + value
            int byKeyIdx     = getMode.AddOption("ByKey");         // all objects with any value for a key
            int listIdx      = getMode.AddOption("ListValues");    // show distinct values for a key
            int taggedIdx    = getMode.AddOption("AllTagged");     // select all objects with any RC: tag
            int untaggedIdx  = getMode.AddOption("AllUntagged");   // select all objects with no RC: tags

            if (getMode.Get() != GetResult.Option)
                return Result.Cancel;

            int modeIdx = getMode.OptionIndex();

            // ── AllTagged ────────────────────────────────────────────
            if (modeIdx == taggedIdx)
            {
                return SelectByPredicate(doc, tagService,
                    obj => tagService.HasAnyTags(obj),
                    "tagged");
            }

            // ── AllUntagged ──────────────────────────────────────────
            if (modeIdx == untaggedIdx)
            {
                return SelectByPredicate(doc, tagService,
                    obj => !tagService.HasAnyTags(obj),
                    "untagged");
            }

            // ── Pick a key (shared by ByTag, ByKey, ListValues) ─────
            string tagKey = PickTagKey("Select tag key");
            if (tagKey == null)
                return Result.Cancel;

            string displayName = TagSchema.DisplayNames.GetValueOrDefault(tagKey, tagKey);

            // ── ListValues ───────────────────────────────────────────
            if (modeIdx == listIdx)
            {
                var values = tagService.GetDistinctValues(doc, tagKey);
                if (values.Count == 0)
                {
                    RhinoApp.WriteLine(string.Format("RhinoClaude: No objects have {0} set.", displayName));
                    return Result.Success;
                }

                RhinoApp.WriteLine(string.Format("─── Distinct values for {0} ───", displayName));
                foreach (var val in values)
                {
                    var objs = tagService.FindByTag(doc, tagKey, val);
                    RhinoApp.WriteLine(string.Format("  {0}  ({1} objects)", val, objs.Count));
                }
                RhinoApp.WriteLine("─────────────────────────────────────────");
                return Result.Success;
            }

            // ── ByKey (select all objects with any value for this key) ─
            if (modeIdx == byKeyIdx)
            {
                var found = tagService.FindByKey(doc, tagKey);
                return SelectObjects(doc, found, string.Format("with {0} set", displayName));
            }

            // ── ByTag (key + value) ──────────────────────────────────
            if (modeIdx == byTagIdx)
            {
                string tagValue = PickTagValue(doc, tagService, tagKey, displayName);
                if (tagValue == null)
                    return Result.Cancel;

                var found = tagService.FindByTag(doc, tagKey, tagValue);
                return SelectObjects(doc, found, string.Format("{0} = '{1}'", displayName, tagValue));
            }

            return Result.Cancel;
        }

        /// <summary>
        /// Present the schema keys as options and let the user pick one.
        /// </summary>
        private string PickTagKey(string prompt)
        {
            var getKey = new GetOption();
            getKey.SetCommandPrompt(prompt);

            var keyOptions = new Dictionary<int, string>();
            foreach (var key in TagSchema.AllKeys)
            {
                string shortName = key.Replace(TagSchema.Prefix, "");
                int idx = getKey.AddOption(shortName);
                keyOptions[idx] = key;
            }

            if (getKey.Get() != GetResult.Option)
                return null;

            keyOptions.TryGetValue(getKey.OptionIndex(), out string selected);
            return selected;
        }

        /// <summary>
        /// Let the user pick a value — either from constrained values or from
        /// distinct values already in use, or by typing a custom value.
        /// </summary>
        private string PickTagValue(RhinoDoc doc, TagService tagService, string tagKey, string displayName)
        {
            // If constrained, show allowed values
            if (TagSchema.ConstrainedValues.TryGetValue(tagKey, out string[] allowedValues))
            {
                var getVal = new GetOption();
                getVal.SetCommandPrompt(string.Format("Select {0} value", displayName));

                var valOptions = new Dictionary<int, string>();
                foreach (var val in allowedValues)
                {
                    string optName = val.Replace(" ", "_").Replace(".", "_").Replace("-", "_");
                    int idx = getVal.AddOption(optName);
                    valOptions[idx] = val;
                }

                if (getVal.Get() != GetResult.Option)
                    return null;

                valOptions.TryGetValue(getVal.OptionIndex(), out string selected);
                return selected;
            }

            // For freeform keys, show existing distinct values plus a Custom option
            var existing = tagService.GetDistinctValues(doc, tagKey);

            if (existing.Count > 0 && existing.Count <= 15)
            {
                var getVal = new GetOption();
                getVal.SetCommandPrompt(string.Format("Select {0} value (or Custom)", displayName));

                var valOptions = new Dictionary<int, string>();
                foreach (var val in existing)
                {
                    string optName = val.Replace(" ", "_").Replace(".", "_").Replace("-", "_");
                    // Ensure option name is valid (alphanumeric + underscore)
                    optName = System.Text.RegularExpressions.Regex.Replace(optName, @"[^a-zA-Z0-9_]", "_");
                    if (string.IsNullOrEmpty(optName) || char.IsDigit(optName[0]))
                        optName = "v_" + optName;
                    int idx = getVal.AddOption(optName);
                    valOptions[idx] = val;
                }

                int customIdx = getVal.AddOption("Custom");

                if (getVal.Get() != GetResult.Option)
                    return null;

                if (getVal.OptionIndex() == customIdx)
                {
                    // Fall through to freeform input below
                }
                else
                {
                    valOptions.TryGetValue(getVal.OptionIndex(), out string selected);
                    return selected;
                }
            }

            // Freeform text input
            var getString = new GetString();
            getString.SetCommandPrompt(string.Format("Enter {0} value", displayName));

            if (getString.Get() != GetResult.String)
                return null;

            string result = getString.StringResult().Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }

        /// <summary>
        /// Select objects matching a predicate and report results.
        /// </summary>
        private Result SelectByPredicate(RhinoDoc doc, TagService tagService,
            Func<RhinoObject, bool> predicate, string label)
        {
            // Deselect all first
            doc.Objects.UnselectAll();

            int count = 0;
            foreach (var obj in doc.Objects.Where(o => !o.IsDeleted))
            {
                if (predicate(obj))
                {
                    obj.Select(true);
                    count++;
                }
            }

            doc.Views.Redraw();
            RhinoApp.WriteLine(string.Format("RhinoClaude: Selected {0} {1} object(s).", count, label));
            return Result.Success;
        }

        /// <summary>
        /// Select a list of objects and report results.
        /// </summary>
        private Result SelectObjects(RhinoDoc doc, List<RhinoObject> objects, string description)
        {
            doc.Objects.UnselectAll();

            foreach (var obj in objects)
                obj.Select(true);

            doc.Views.Redraw();
            RhinoApp.WriteLine(string.Format(
                "RhinoClaude: Selected {0} object(s) matching {1}.", objects.Count, description));
            return Result.Success;
        }
    }
}
