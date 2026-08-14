using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoClaude.Agent;
using RhinoClaude.Semantic;
using RhinoClaude.Services.Semantic;

namespace RhinoClaude.Commands
{
    /// <summary>
    /// Command: ClaudeLearnNamingConvention
    ///
    /// Semantic plan §5.6 — the "our firm has its own layers" escape hatch. Inventories the
    /// document's layers, asks Claude once to map them onto the element vocabulary, shows the
    /// proposal, and saves what the user confirms.
    ///
    /// This is what makes the shipped convention a default rather than a requirement: a firm
    /// with <c>BLDG-MASSING-OFFICE</c> layers gets the same semantic layer as one that adopted
    /// <c>MASS_Office</c>, without renaming anything.
    /// </summary>
    public class ClaudeLearnNamingConventionCommand : Command
    {
        public override string EnglishName => "ClaudeLearnNamingConvention";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var host = AgentHost.For(doc);
            if (host.Client == null)
            {
                RhinoApp.WriteLine("RhinoClaude: the plugin is not initialised.");
                return Result.Failure;
            }

            // Runs on whichever provider the panel is set to, so a user who has moved off
            // Anthropic is not asked for a Claude key by a side command.
            if (!host.Client.IsConfigured)
            {
                RhinoApp.WriteLine("RhinoClaude: no API key configured for " + host.Client.ProviderName + ".");
                return Result.Failure;
            }

            var layers = doc.Layers.Where(l => !l.IsDeleted)
                                   .Select(l => l.FullPath)
                                   .Where(p => !string.IsNullOrWhiteSpace(p))
                                   .Distinct(StringComparer.Ordinal)
                                   .OrderBy(p => p, StringComparer.Ordinal)
                                   .ToList();

            if (layers.Count == 0)
            {
                RhinoApp.WriteLine("RhinoClaude: this document has no layers to learn from.");
                return Result.Nothing;
            }

            RhinoApp.WriteLine("RhinoClaude: asking Claude to map " + layers.Count + " layer(s) onto the " +
                               "element vocabulary. This is one API call.");

            LayerConventionMap proposed;
            try
            {
                proposed = Ask(host.Client, host.Settings, layers);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("RhinoClaude: the mapping call failed — " + ex.Message);
                return Result.Failure;
            }

            if (proposed == null || proposed.IsEmpty)
            {
                RhinoApp.WriteLine("RhinoClaude: no mapping came back. Nothing was saved.");
                return Result.Nothing;
            }

            // The confirm step. A Rhino command line is a poor dialog, so the proposal is
            // printed in full and the user accepts, edits one entry at a time, or cancels —
            // which is the same decision the plan's dialog offers, without a modal form the
            // rest of the plugin does not have.
            RhinoApp.WriteLine("");
            RhinoApp.WriteLine("Proposed mapping:");
            RhinoApp.WriteLine(SemanticClassifierPrompt.Describe(proposed));
            RhinoApp.WriteLine("");

            int mapped = proposed.Entries.Count(e => e.ElementType != null);
            int masses = proposed.Entries.Count(e => e.ElementType == SemanticVocabulary.Mass);
            RhinoApp.WriteLine(mapped + " of " + proposed.Entries.Count + " layer(s) mapped, " +
                               masses + " of them as Masses.");

            if (masses == 0)
            {
                RhinoApp.WriteLine("RhinoClaude: warning — nothing was mapped to Mass. Without masses the " +
                                   "semantic tools have nothing to describe. Edit the mapping or cancel.");
            }

            while (true)
            {
                var decision = new GetOption();
                decision.SetCommandPrompt("Accept this mapping?");
                int acceptIndex = decision.AddOption("Accept");
                int editIndex = decision.AddOption("EditOne");
                int cancelIndex = decision.AddOption("Cancel");

                if (decision.Get() != GetResult.Option) return Result.Cancel;

                int chosen = decision.OptionIndex();
                if (chosen == cancelIndex) return Result.Cancel;
                if (chosen == acceptIndex) break;
                if (chosen == editIndex) EditOne(proposed);
            }

            // Rev 2 addition: one number, for the levels nobody draws.
            double floorToFloor = PromptFloorToFloor(host.Settings.FloorToFloorDefault);
            if (floorToFloor > 0) proposed.FloorToFloorDefault = floorToFloor;

            var scope = new GetOption();
            scope.SetCommandPrompt("Save the mapping where?");
            int docIndex = scope.AddOption("ThisDocument");
            int firmIndex = scope.AddOption("EveryDocument");
            int bothIndex = scope.AddOption("Both");

            if (scope.Get() != GetResult.Option) return Result.Cancel;
            int scopeChoice = scope.OptionIndex();

            if (scopeChoice == docIndex || scopeChoice == bothIndex)
                host.LayerConventions.SaveDocumentMap(proposed);

            if (scopeChoice == firmIndex || scopeChoice == bothIndex)
                host.LayerConventions.SaveFirmMap(proposed);

            if (floorToFloor > 0) host.Settings.FloorToFloorDefault = floorToFloor;

            // The classifier's cache predates the mapping, so it has to go.
            host.Elements.InvalidateAll();

            RhinoApp.WriteLine("RhinoClaude: convention saved. Run describe_massing in ClaudeChat to see " +
                               "the classifier using it.");
            return Result.Success;
        }

        // ── The one-shot call ─────────────────────────────────────────

        private static LayerConventionMap Ask(
            ILlmClient client, AgentSettings settings, List<string> layers)
        {
            var request = new MessagesRequest
            {
                // The loop model, not the reviewer: this is a mapping task, not a judgment call,
                // and it runs while the user waits at the command line.
                Model = settings.LoopModel,
                MaxTokens = 8000,
                System = SemanticClassifierPrompt.System,
                Messages =
                {
                    new AgentMessage("user", new ContentBlock[]
                    {
                        new TextBlock(SemanticClassifierPrompt.BuildUserText(layers))
                    })
                }
            };

            request.OutputConfig = new OutputConfig
            {
                Format = new OutputFormat { SchemaJson = SemanticClassifierPrompt.OutputSchema }
            };

            var usage = new TokenUsage();

            // The command line is synchronous and the user is standing at it; a short block is
            // the honest UX here rather than an async command that returns before it is done.
            var message = client.SendAsync(request, CancellationToken.None, usage)
                                .GetAwaiter().GetResult();

            var map = SemanticClassifierPrompt.Parse(message.TextContent(), out string error);
            if (error != null) RhinoApp.WriteLine("RhinoClaude: " + error);

            RhinoApp.WriteLine("RhinoClaude: mapping cost " + usage.InputTokens + " in / " +
                               usage.OutputTokens + " out tokens.");
            return map;
        }

        // ── Editing ───────────────────────────────────────────────────

        private static void EditOne(LayerConventionMap map)
        {
            var getLayer = new GetString();
            getLayer.SetCommandPrompt("Layer to change (exact name from the list above)");
            if (getLayer.Get() != GetResult.String) return;

            string layer = getLayer.StringResult()?.Trim();
            var entry = map.Entries.FirstOrDefault(
                e => string.Equals(e.Layer, layer, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
            {
                RhinoApp.WriteLine("RhinoClaude: '" + layer + "' is not in the mapping.");
                return;
            }

            var getType = new GetOption();
            getType.SetCommandPrompt("New element type for " + entry.Layer);

            var options = new Dictionary<int, string>();
            foreach (var type in SemanticVocabulary.TaggableTypes)
                options[getType.AddOption(type)] = type;
            int noneIndex = getType.AddOption("NotArchitectural");

            if (getType.Get() != GetResult.Option) return;

            int chosen = getType.OptionIndex();
            if (chosen == noneIndex)
            {
                entry.ElementType = null;
                entry.Subtype = null;
                entry.Note = "set by hand";
                RhinoApp.WriteLine("RhinoClaude: " + entry.Layer + " → (not architectural).");
                return;
            }

            if (!options.TryGetValue(chosen, out string elementType)) return;

            entry.ElementType = elementType;
            entry.Note = "set by hand";

            if (elementType == SemanticVocabulary.Mass)
            {
                var getFunction = new GetOption();
                getFunction.SetCommandPrompt("Function");
                var functions = new Dictionary<int, string>();
                foreach (var function in SemanticVocabulary.MassFunctions)
                    functions[getFunction.AddOption(function)] = function;

                if (getFunction.Get() == GetResult.Option
                    && functions.TryGetValue(getFunction.OptionIndex(), out string chosenFunction))
                {
                    entry.Subtype = chosenFunction;
                }
            }

            RhinoApp.WriteLine("RhinoClaude: " + entry.Layer + " → " + entry.ElementType +
                               (string.IsNullOrEmpty(entry.Subtype) ? "" : " / " + entry.Subtype) + ".");
        }

        private static double PromptFloorToFloor(double current)
        {
            var get = new GetNumber();
            get.SetCommandPrompt("Firm-standard floor-to-floor, in model units (0 to skip)");
            get.SetDefaultNumber(current > 0 ? current : 0);
            get.SetLowerLimit(0, false);

            return get.Get() != GetResult.Number ? 0 : get.Number();
        }
    }
}
