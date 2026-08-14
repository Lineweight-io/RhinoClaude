using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Geometry;
using RhinoClaude.Agent;

namespace RhinoClaude.Services.Agent
{
    /// <summary>
    /// Selection and viewport navigation.
    ///
    /// These sit apart from <see cref="RhinoMutationService"/> deliberately: they change what
    /// the user is looking at, not what the document contains. Rhino does not put selection or
    /// camera changes on the undo stack, so wrapping them in an undo record would add entries
    /// that "Revert session" then tries to pop — inflating the count and undoing real geometry
    /// edits instead.
    ///
    /// UI-thread only.
    /// </summary>
    public sealed class RhinoInteractionService
    {
        private readonly RhinoQueryService _query;

        public RhinoInteractionService(RhinoQueryService query)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
        }

        public object SelectObjects(List<string> ids, bool replace)
        {
            if (ids == null || ids.Count == 0)
                throw new ArgumentException("At least one object id is required.");

            var objects = ids.Select(_query.RequireObject).ToList();
            var doc = _query.Doc;

            if (replace) doc.Objects.UnselectAll();

            int selected = 0;
            var failed = new List<string>();
            foreach (var obj in objects)
            {
                if (doc.Objects.Select(obj.Id)) selected++;
                else failed.Add(obj.Id.ToString());
            }

            doc.Views.Redraw();

            return new Dictionary<string, object>
            {
                { "selectedCount", selected },
                { "failedIds", failed },
                { "notes", failed.Count == 0
                    ? "Selection updated."
                    : "Some objects could not be selected — they may be hidden or on a locked layer." }
            };
        }

        public object DeselectAll()
        {
            var doc = _query.Doc;
            int cleared = doc.Objects.UnselectAll();
            doc.Views.Redraw();

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "deselectedCount", cleared }
            };
        }

        /// <summary>
        /// Zoom a viewport to the whole document or to a set of objects. This is the
        /// user-facing camera; capture_views does not disturb it.
        /// </summary>
        public object ZoomExtents(string viewName, List<string> ids)
        {
            var doc = _query.Doc;

            var view = string.IsNullOrWhiteSpace(viewName)
                ? doc.Views.ActiveView
                : doc.Views.FirstOrDefault(v =>
                      string.Equals(v.ActiveViewport.Name, viewName, StringComparison.OrdinalIgnoreCase));

            if (view == null)
            {
                var available = doc.Views.Select(v => v.ActiveViewport.Name).ToList();
                throw new ArgumentException("No view named '" + viewName + "'. Available: " +
                    (available.Count == 0 ? "(none)" : string.Join(", ", available)));
            }

            var viewport = view.ActiveViewport;

            if (ids != null && ids.Count > 0)
            {
                var guids = ids.Select(_query.RequireObject).Select(o => o.Id).ToList();
                var box = _query.BoundingBoxOf(guids);
                if (!box.IsValid)
                    throw new ArgumentException("Those objects have no valid bounding box to zoom to.");

                double pad = Math.Max(box.Diagonal.Length * 0.06, 0.1);
                box.Inflate(pad);
                viewport.ZoomBoundingBox(box);
            }
            else
            {
                viewport.ZoomExtents();
            }

            view.Redraw();

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "viewName", ToolJson.Safe(viewport.Name) },
                { "resultCameraLocation", RhinoQueryService.Pt(viewport.CameraLocation) },
                { "resultCameraTarget", RhinoQueryService.Pt(viewport.CameraTarget) }
            };
        }
    }
}
