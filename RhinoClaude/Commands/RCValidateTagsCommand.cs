using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoClaude.Services;

namespace RhinoClaude.Commands
{
    /// <summary>
    /// Command: RCValidateTags
    /// Audit the document for tagging completeness and consistency.
    /// Reports untagged objects, missing required tags, and value distribution.
    /// Optionally selects untagged objects for easy tagging.
    /// </summary>
    public class RCValidateTagsCommand : Command
    {
        public override string EnglishName => "RCValidateTags";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = RhinoClaudePlugin.Instance;
            var tagService = plugin.TagService;

            RhinoApp.WriteLine("RhinoClaude: Auditing document tags...");

            var report = tagService.AuditDocument(doc);

            // Print the report
            RhinoApp.WriteLine(report.ToReport());

            // If there are untagged objects, offer to select them
            if (report.UntaggedObjects.Count > 0)
            {
                var getAction = new GetOption();
                getAction.SetCommandPrompt(string.Format(
                    "{0} untagged objects found. Select them?", report.UntaggedObjects.Count));
                getAction.AddOption("SelectUntagged");
                getAction.AddOption("SelectMissingType");
                getAction.AddOption("Done");

                var result = getAction.Get();
                if (result == GetResult.Option)
                {
                    if (getAction.OptionIndex() == 1) // SelectUntagged
                    {
                        doc.Objects.UnselectAll();
                        foreach (var obj in report.UntaggedObjects)
                            obj.Select(true);
                        doc.Views.Redraw();
                        RhinoApp.WriteLine(string.Format(
                            "RhinoClaude: Selected {0} untagged object(s).", report.UntaggedObjects.Count));
                    }
                    else if (getAction.OptionIndex() == 2) // SelectMissingType
                    {
                        doc.Objects.UnselectAll();
                        foreach (var obj in report.MissingElementType)
                            obj.Select(true);
                        doc.Views.Redraw();
                        RhinoApp.WriteLine(string.Format(
                            "RhinoClaude: Selected {0} object(s) missing ElementType.", report.MissingElementType.Count));
                    }
                }
            }
            else if (report.TaggedObjects == report.TotalObjects && report.MissingElementType.Count == 0)
            {
                RhinoApp.WriteLine("RhinoClaude: All objects are fully tagged. Nice work!");
            }

            return Result.Success;
        }
    }
}
