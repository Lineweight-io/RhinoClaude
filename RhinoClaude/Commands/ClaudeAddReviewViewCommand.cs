using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;
using RhinoClaude.Services.Agent;

namespace RhinoClaude.Commands
{
    /// <summary>
    /// Command: ClaudeAddReviewView
    /// Stamps the current camera as the named view <c>Claude:Review</c>. Self-review prefers
    /// that view when it exists, so this is how you tell the reviewer "judge it from here"
    /// — useful when the interesting thing about a model is only visible from one angle.
    /// </summary>
    public class ClaudeAddReviewViewCommand : Command
    {
        public override string EnglishName => "ClaudeAddReviewView";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var view = doc.Views.ActiveView;
            if (view == null)
            {
                RhinoApp.WriteLine("RhinoClaude: there is no active view to stamp.");
                return Result.Failure;
            }

            int existing = doc.NamedViews.FindByName(SelfReviewService.ReviewViewName);
            if (existing >= 0)
            {
                var confirm = new GetOption();
                confirm.SetCommandPrompt(
                    "A '" + SelfReviewService.ReviewViewName + "' view already exists. Replace it?");
                confirm.AddOption("Replace");
                int cancelIndex = confirm.AddOption("Cancel");

                if (confirm.Get() != Rhino.Input.GetResult.Option || confirm.OptionIndex() == cancelIndex)
                {
                    RhinoApp.WriteLine("RhinoClaude: review view unchanged.");
                    return Result.Cancel;
                }

                doc.NamedViews.Delete(existing);
            }

            int index = doc.NamedViews.Add(SelfReviewService.ReviewViewName, view.ActiveViewport.Id);
            if (index < 0)
            {
                RhinoApp.WriteLine("RhinoClaude: Rhino refused to save the named view.");
                return Result.Failure;
            }

            RhinoApp.WriteLine(
                "RhinoClaude: saved '{0}'. Self-review will judge the model from this camera.",
                SelfReviewService.ReviewViewName);

            return Result.Success;
        }
    }
}
