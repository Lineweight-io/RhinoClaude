using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoClaude.Semantic;

namespace RhinoClaude.Commands
{
    /// <summary>
    /// Command: ClaudeSetElement
    ///
    /// The override path in semantic plan §5.4 — step 1 of the classifier's resolution rule,
    /// and the one that trumps every convention. Tag a selection as a Mass, an Opening, an
    /// Overhang, a MassGroup, a Level or a Site element, with the subtype the type needs.
    ///
    /// The Rev 2 addition is the face-role sub-flow: pick a face of a mass by clicking it and
    /// label it directly. That is the fix for a curved facade the classifier reads as one
    /// orientation-less face (plan risk #3), and there is no other way to correct it.
    /// </summary>
    public class ClaudeSetElementCommand : Command
    {
        public override string EnglishName => "ClaudeSetElement";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var selected = doc.Objects.GetSelectedObjects(false, false).ToList();

            if (selected.Count == 0)
            {
                var picker = new GetObject();
                picker.SetCommandPrompt("Select objects to classify");
                picker.GroupSelect = true;
                picker.GetMultiple(1, 0);
                if (picker.CommandResult() != Result.Success) return picker.CommandResult();

                for (int i = 0; i < picker.ObjectCount; i++)
                    selected.Add(picker.Object(i).Object());
            }

            var getType = new GetOption();
            getType.SetCommandPrompt("Element type");

            var typeOptions = new Dictionary<int, string>();
            foreach (var type in SemanticVocabulary.TaggableTypes)
                typeOptions[getType.AddOption(type)] = type;

            int faceRoleIndex = getType.AddOption("SetFaceRole");

            if (getType.Get() != GetResult.Option) return Result.Cancel;

            int chosen = getType.OptionIndex();
            if (chosen == faceRoleIndex) return SetFaceRole(doc);

            if (!typeOptions.TryGetValue(chosen, out string elementType)) return Result.Cancel;

            string subtype = PromptSubtype(elementType);
            if (subtype == null && NeedsSubtype(elementType)) return Result.Cancel;

            int tagged = 0;
            foreach (var obj in selected)
            {
                var attributes = obj.Attributes.Duplicate();

                // One element type per object: clear any previous one so the classifier never
                // sees an object claiming to be two things.
                foreach (var key in ExistingElementKeys(obj))
                    attributes.DeleteUserString(key);

                attributes.SetUserString(SemanticVocabulary.KeyElementPrefix + elementType, "1");

                switch (elementType)
                {
                    case SemanticVocabulary.Mass:
                        attributes.SetUserString(SemanticVocabulary.KeyMassFunction, subtype);
                        break;
                    case SemanticVocabulary.Opening:
                        attributes.SetUserString(SemanticVocabulary.KeyOpeningType, subtype);
                        break;
                    case SemanticVocabulary.Site:
                        attributes.SetUserString(SemanticVocabulary.KeySiteType, subtype);
                        break;
                    case SemanticVocabulary.MassGroup:
                        attributes.SetUserString(SemanticVocabulary.KeyMassGroup, subtype);
                        break;
                }

                if (doc.Objects.ModifyAttributes(obj, attributes, true)) tagged++;
            }

            doc.Views.Redraw();
            RhinoApp.WriteLine("RhinoClaude: tagged " + tagged + " object(s) as " + elementType +
                               (string.IsNullOrEmpty(subtype) ? "" : " / " + subtype) +
                               ". This beats any layer convention.");
            return Result.Success;
        }

        // ── Face-role sub-flow (Rev 2) ────────────────────────────────

        private static Result SetFaceRole(RhinoDoc doc)
        {
            var picker = new GetObject();
            picker.SetCommandPrompt("Select the face to label");
            picker.GeometryFilter = ObjectType.Surface;
            picker.SubObjectSelect = true;
            picker.EnablePreSelect(false, true);

            if (picker.Get() != GetResult.Object) return picker.CommandResult();

            var reference = picker.Object(0);
            var brepObject = reference.Object();
            if (brepObject == null) return Result.Cancel;

            var componentIndex = reference.GeometryComponentIndex;
            if (componentIndex.ComponentIndexType != ComponentIndexType.BrepFace)
            {
                RhinoApp.WriteLine("RhinoClaude: that is not a face of a solid. " +
                                   "Hold Ctrl+Shift and click a face of the mass.");
                return Result.Nothing;
            }

            int faceIndex = componentIndex.Index;

            var getRole = new GetOption();
            getRole.SetCommandPrompt("Role for face " + faceIndex);

            var roleOptions = new Dictionary<int, string>();
            foreach (var role in SemanticVocabulary.FaceRoles)
            {
                // Rhino command options cannot contain a hyphen.
                roleOptions[getRole.AddOption(role.Replace("-", string.Empty))] = role;
            }

            if (getRole.Get() != GetResult.Option) return Result.Cancel;
            if (!roleOptions.TryGetValue(getRole.OptionIndex(), out string chosenRole)) return Result.Cancel;

            var attributes = brepObject.Attributes.Duplicate();
            attributes.SetUserString(SemanticVocabulary.FaceRoleKey(faceIndex), chosenRole);

            if (!doc.Objects.ModifyAttributes(brepObject, attributes, true))
            {
                RhinoApp.WriteLine("RhinoClaude: Rhino refused to write the face role.");
                return Result.Failure;
            }

            doc.Views.Redraw();
            RhinoApp.WriteLine("RhinoClaude: face " + faceIndex + " labelled '" + chosenRole + "'. " +
                               "Note that face indices change whenever the solid is edited — " +
                               "re-run this after a boolean.");
            return Result.Success;
        }

        // ── Prompts ───────────────────────────────────────────────────

        private static bool NeedsSubtype(string elementType) =>
            elementType == SemanticVocabulary.Mass
            || elementType == SemanticVocabulary.Opening
            || elementType == SemanticVocabulary.Site
            || elementType == SemanticVocabulary.MassGroup;

        private static string PromptSubtype(string elementType)
        {
            switch (elementType)
            {
                case SemanticVocabulary.Mass:
                    return PromptFromList("Function", SemanticVocabulary.MassFunctions);
                case SemanticVocabulary.Opening:
                    return PromptFromList("Opening type", SemanticVocabulary.OpeningTypes);
                case SemanticVocabulary.Site:
                    return PromptFromList("Site type", SemanticVocabulary.SiteTypes);
                case SemanticVocabulary.MassGroup:
                    return PromptForText("Group name");
                default:
                    return null;
            }
        }

        private static string PromptFromList(string prompt, string[] values)
        {
            var get = new GetOption();
            get.SetCommandPrompt(prompt);

            var options = new Dictionary<int, string>();
            foreach (var value in values)
                options[get.AddOption(value.Replace("-", string.Empty))] = value;

            if (get.Get() != GetResult.Option) return null;
            return options.TryGetValue(get.OptionIndex(), out string chosen) ? chosen : null;
        }

        private static string PromptForText(string prompt)
        {
            var get = new GetString();
            get.SetCommandPrompt(prompt);
            if (get.Get() != GetResult.String) return null;

            string value = get.StringResult()?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        internal static IEnumerable<string> ExistingElementKeys(RhinoObject obj)
        {
            System.Collections.Specialized.NameValueCollection strings;
            try
            {
                strings = obj.Attributes.GetUserStrings();
            }
            catch (Exception)
            {
                yield break;
            }

            if (strings == null) yield break;

            foreach (string key in strings.AllKeys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (key.StartsWith(SemanticVocabulary.KeyElementPrefix, StringComparison.OrdinalIgnoreCase))
                    yield return key;
            }
        }
    }

    /// <summary>
    /// Command: ClaudeClearElement
    ///
    /// Removes every RhinoClaude:* semantic tag from the selection, including face-role
    /// overrides and the position-keyed opening tags the write tools leave behind. Phase 1's
    /// <c>RC:</c> tags are a separate namespace and are left alone.
    /// </summary>
    public class ClaudeClearElementCommand : Command
    {
        public override string EnglishName => "ClaudeClearElement";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var selected = doc.Objects.GetSelectedObjects(false, false).ToList();

            if (selected.Count == 0)
            {
                var picker = new GetObject();
                picker.SetCommandPrompt("Select objects to clear semantic tags from");
                picker.GroupSelect = true;
                picker.GetMultiple(1, 0);
                if (picker.CommandResult() != Result.Success) return picker.CommandResult();

                for (int i = 0; i < picker.ObjectCount; i++)
                    selected.Add(picker.Object(i).Object());
            }

            int cleared = 0, keysRemoved = 0;

            foreach (var obj in selected)
            {
                var keys = SemanticKeys(obj).ToList();
                if (keys.Count == 0) continue;

                var attributes = obj.Attributes.Duplicate();
                foreach (var key in keys)
                {
                    attributes.DeleteUserString(key);
                    keysRemoved++;
                }

                if (doc.Objects.ModifyAttributes(obj, attributes, true)) cleared++;
            }

            doc.Views.Redraw();
            RhinoApp.WriteLine("RhinoClaude: cleared " + keysRemoved + " semantic tag(s) from " + cleared +
                               " object(s). Classification now falls back to the layer convention, then " +
                               "to geometry. Phase 1's RC: tags were not touched.");
            return Result.Success;
        }

        private static IEnumerable<string> SemanticKeys(RhinoObject obj)
        {
            System.Collections.Specialized.NameValueCollection strings;
            try
            {
                strings = obj.Attributes.GetUserStrings();
            }
            catch (Exception)
            {
                yield break;
            }

            if (strings == null) yield break;

            foreach (string key in strings.AllKeys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (key.StartsWith("RhinoClaude:", StringComparison.OrdinalIgnoreCase)) yield return key;
            }
        }
    }
}
