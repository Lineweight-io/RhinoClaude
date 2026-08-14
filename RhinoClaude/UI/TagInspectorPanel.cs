using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.UI;
using RhinoClaude.Schema;
using RhinoClaude.Services;

namespace RhinoClaude.UI
{
    [System.Runtime.InteropServices.Guid("9A1F5C7E-3B2D-4E8A-B6F0-D2C4A8E5F190")]
    public class TagInspectorPanel : Panel, IPanel
    {
        public static Guid PanelId => typeof(TagInspectorPanel).GUID;

        private readonly uint _docSN;
        private bool _updating;
        private bool _updatePending;
        private bool _eventsHooked;

        // Top-level views
        private Panel _selectionView;
        private Panel _summaryView;

        // Selection view controls
        private Label _objectInfoLabel;
        private readonly Dictionary<string, Control> _tagEditors = new Dictionary<string, Control>();
        private readonly Dictionary<string, Label> _tagLabels = new Dictionary<string, Label>();
        private Button _clearAllButton;

        // Summary view
        private Label _summaryTextLabel;

        public TagInspectorPanel(uint documentSerialNumber)
        {
            _docSN = documentSerialNumber;
            BuildUI();
        }

        // ── UI Construction ─────────────────────────────────────────

        private void BuildUI()
        {
            _selectionView = BuildSelectionView();
            _summaryView = BuildSummaryView();

            var mainLayout = new StackLayout
            {
                Orientation = Orientation.Vertical,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new StackLayoutItem(_selectionView, true),
                    new StackLayoutItem(_summaryView, true)
                }
            };

            Content = new Scrollable
            {
                Content = mainLayout,
                ExpandContentWidth = true,
                Border = BorderType.None
            };
        }

        private Panel BuildSelectionView()
        {
            var layout = new DynamicLayout();
            layout.DefaultSpacing = new Size(5, 5);
            layout.DefaultPadding = new Padding(10);

            // Object info header
            _objectInfoLabel = new Label
            {
                Text = "No selection",
                Wrap = WrapMode.Word
            };
            layout.Add(_objectInfoLabel);
            layout.Add(CreateDivider());

            // Tag editor rows
            layout.BeginVertical(new Padding(0, 4), new Size(6, 6));
            foreach (var key in TagSchema.AllKeys)
            {
                string displayName = TagSchema.DisplayNames.GetValueOrDefault(key, key);
                var label = new Label
                {
                    Text = displayName,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 110
                };
                _tagLabels[key] = label;

                Control editor;

                if (TagSchema.ConstrainedValues.TryGetValue(key, out string[] allowed))
                {
                    var dd = new DropDown();
                    dd.Items.Add(new ListItem { Text = "(none)", Key = "" });
                    foreach (var val in allowed)
                        dd.Items.Add(new ListItem { Text = val, Key = val });

                    string capturedKey = key;
                    dd.SelectedKeyChanged += (s, e) =>
                    {
                        if (_updating) return;
                        ApplyTagValue(capturedKey, dd.SelectedKey);
                    };
                    editor = dd;
                }
                else
                {
                    var tb = new TextBox();
                    string capturedKey = key;

                    tb.KeyDown += (s, e) =>
                    {
                        if (e.Key == Keys.Enter)
                        {
                            if (!_updating)
                                ApplyTagValue(capturedKey, tb.Text?.Trim());
                            e.Handled = true;
                        }
                    };
                    tb.LostFocus += (s, e) =>
                    {
                        if (!_updating)
                            ApplyTagValue(capturedKey, tb.Text?.Trim());
                    };
                    editor = tb;
                }

                _tagEditors[key] = editor;
                layout.AddRow(label, editor);
            }
            layout.EndVertical();

            // Action buttons
            layout.Add(CreateDivider());

            _clearAllButton = new Button { Text = "Clear All Tags" };
            _clearAllButton.Click += (s, e) => ClearAllTags();
            layout.AddCentered(_clearAllButton);

            layout.Add(null); // spacer

            return new Panel { Content = layout };
        }

        private Panel BuildSummaryView()
        {
            var layout = new DynamicLayout();
            layout.DefaultSpacing = new Size(5, 5);
            layout.DefaultPadding = new Padding(10);

            var headerLabel = new Label { Text = "Scene Summary" };
            layout.Add(headerLabel);
            layout.Add(CreateDivider());

            _summaryTextLabel = new Label
            {
                Wrap = WrapMode.Word,
                Text = "Loading..."
            };
            layout.Add(_summaryTextLabel);
            layout.Add(null);

            return new Panel { Content = layout };
        }

        private static Panel CreateDivider()
        {
            return new Panel
            {
                Height = 1,
                BackgroundColor = Colors.DarkGray
            };
        }

        // ── Event Wiring ────────────────────────────────────────────

        private void HookEvents()
        {
            if (_eventsHooked) return;
            _eventsHooked = true;

            RhinoDoc.SelectObjects += OnSelectionChanged;
            RhinoDoc.DeselectObjects += OnSelectionChanged;
            RhinoDoc.DeselectAllObjects += OnDeselectAll;
            RhinoDoc.ModifyObjectAttributes += OnAttributesModified;
        }

        private void UnhookEvents()
        {
            if (!_eventsHooked) return;
            _eventsHooked = false;

            RhinoDoc.SelectObjects -= OnSelectionChanged;
            RhinoDoc.DeselectObjects -= OnSelectionChanged;
            RhinoDoc.DeselectAllObjects -= OnDeselectAll;
            RhinoDoc.ModifyObjectAttributes -= OnAttributesModified;
        }

        private void OnSelectionChanged(object sender, RhinoObjectSelectionEventArgs e)
        {
            if (e.Document.RuntimeSerialNumber != _docSN) return;
            ScheduleRefresh();
        }

        private void OnDeselectAll(object sender, RhinoDeselectAllObjectsEventArgs e)
        {
            if (e.Document.RuntimeSerialNumber != _docSN) return;
            ScheduleRefresh();
        }

        private void OnAttributesModified(object sender, RhinoModifyObjectAttributesEventArgs e)
        {
            if (e.Document.RuntimeSerialNumber != _docSN) return;
            // Only refresh if a selected object was modified
            if (e.RhinoObject.IsSelected(false) > 0)
                ScheduleRefresh();
        }

        /// <summary>
        /// Coalesce rapid selection events into a single panel refresh.
        /// </summary>
        private void ScheduleRefresh()
        {
            if (_updatePending) return;
            _updatePending = true;
            Application.Instance.AsyncInvoke(() =>
            {
                _updatePending = false;
                RefreshPanel();
            });
        }

        // ── Panel Refresh ───────────────────────────────────────────

        private void RefreshPanel()
        {
            var doc = RhinoDoc.FromRuntimeSerialNumber(_docSN);
            if (doc == null) return;

            var selected = doc.Objects.GetSelectedObjects(false, false).ToList();

            if (selected.Count > 0)
            {
                _selectionView.Visible = true;
                _summaryView.Visible = false;
                UpdateSelectionView(doc, selected);
            }
            else
            {
                _selectionView.Visible = false;
                _summaryView.Visible = true;
                UpdateSummaryView(doc);
            }
        }

        private void UpdateSelectionView(RhinoDoc doc, List<RhinoObject> selected)
        {
            _updating = true;
            try
            {
                // Object info header
                if (selected.Count == 1)
                {
                    var obj = selected[0];
                    string name = string.IsNullOrEmpty(obj.Name) ? "(unnamed)" : obj.Name;
                    string layer = doc.Layers[obj.Attributes.LayerIndex]?.FullPath ?? "?";
                    string id = obj.Id.ToString().Substring(0, 8);
                    _objectInfoLabel.Text = string.Format(
                        "{0}  \"{1}\"\nLayer: {2}\nID: {3}...",
                        obj.ObjectType, name, layer, id);
                }
                else
                {
                    var types = selected
                        .GroupBy(o => o.ObjectType)
                        .Select(g => string.Format("{0} {1}", g.Count(), g.Key))
                        .ToList();
                    _objectInfoLabel.Text = string.Format(
                        "{0} objects selected\n{1}",
                        selected.Count, string.Join(", ", types));
                }

                // Update each tag editor
                var tagService = RhinoClaudePlugin.Instance.TagService;

                foreach (var key in TagSchema.AllKeys)
                {
                    // Gather values across all selected objects
                    var values = selected
                        .Select(o => tagService.GetTag(o, key))
                        .ToList();

                    bool allEmpty = values.All(v => v == null);
                    string sharedValue = null;
                    bool isMixed = false;

                    if (!allEmpty)
                    {
                        var distinct = values
                            .Where(v => v != null)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (distinct.Count == 1 && values.All(v => v != null))
                            sharedValue = distinct[0];
                        else
                            isMixed = true;
                    }

                    // Update label (add * for mixed values)
                    string displayName = TagSchema.DisplayNames.GetValueOrDefault(key, key);
                    _tagLabels[key].Text = isMixed
                        ? displayName + " *"
                        : displayName;

                    // Update editor control
                    var editor = _tagEditors[key];
                    if (editor is DropDown dd)
                    {
                        if (isMixed)
                            dd.SelectedIndex = -1;
                        else
                            dd.SelectedKey = sharedValue ?? "";
                    }
                    else if (editor is TextBox tb)
                    {
                        if (isMixed)
                        {
                            tb.PlaceholderText = "(mixed)";
                            tb.Text = "";
                        }
                        else
                        {
                            tb.PlaceholderText = "";
                            tb.Text = sharedValue ?? "";
                        }
                    }
                }
            }
            finally
            {
                _updating = false;
            }
        }

        private void UpdateSummaryView(RhinoDoc doc)
        {
            var tagService = RhinoClaudePlugin.Instance.TagService;
            var report = tagService.AuditDocument(doc);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Format("{0} objects total", report.TotalObjects));
            sb.AppendLine(string.Format("{0} tagged, {1} untagged",
                report.TaggedObjects, report.UntaggedObjects.Count));
            sb.AppendLine();

            // Element type breakdown
            var elementTypes = report.ValueDistribution
                .Where(kvp => kvp.Key.StartsWith(TagSchema.ElementType + "="))
                .OrderByDescending(kvp => kvp.Value)
                .ToList();

            if (elementTypes.Count > 0)
            {
                sb.AppendLine("By Element Type:");
                foreach (var kvp in elementTypes)
                {
                    string val = kvp.Key.Substring(TagSchema.ElementType.Length + 1);
                    sb.AppendLine(string.Format("  {0}: {1}", val, kvp.Value));
                }
                sb.AppendLine();
            }

            // Levels
            var levels = report.ValueDistribution
                .Where(kvp => kvp.Key.StartsWith(TagSchema.Level + "="))
                .Select(kvp => kvp.Key.Substring(TagSchema.Level.Length + 1))
                .OrderBy(v => v)
                .ToList();

            if (levels.Count > 0)
                sb.AppendLine(string.Format("Levels: {0}", string.Join(", ", levels)));

            // Fire ratings
            var ratings = report.ValueDistribution
                .Where(kvp => kvp.Key.StartsWith(TagSchema.FireRating + "="))
                .Where(kvp => !kvp.Key.EndsWith("=None"))
                .OrderByDescending(kvp => kvp.Value)
                .ToList();

            if (ratings.Count > 0)
            {
                var parts = ratings.Select(kvp =>
                {
                    string val = kvp.Key.Substring(TagSchema.FireRating.Length + 1);
                    return string.Format("{0} ({1})", val, kvp.Value);
                });
                sb.AppendLine(string.Format("Fire Ratings: {0}", string.Join(", ", parts)));
            }

            sb.AppendLine();
            sb.Append("Select objects to view and edit tags.");

            _summaryTextLabel.Text = sb.ToString();
        }

        // ── Tag Actions ─────────────────────────────────────────────

        private void ApplyTagValue(string key, string value)
        {
            var doc = RhinoDoc.FromRuntimeSerialNumber(_docSN);
            if (doc == null) return;

            var selected = doc.Objects.GetSelectedObjects(false, false).ToList();
            if (selected.Count == 0) return;

            var tagService = RhinoClaudePlugin.Instance.TagService;
            bool anyChanged = false;

            // Temporarily unhook to prevent N refreshes during batch update
            RhinoDoc.ModifyObjectAttributes -= OnAttributesModified;
            try
            {
                foreach (var obj in selected)
                {
                    string current = tagService.GetTag(obj, key);
                    bool isEmpty = string.IsNullOrEmpty(value);

                    if (isEmpty && current == null)
                        continue;
                    if (!isEmpty && string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (isEmpty)
                        tagService.RemoveTag(doc, obj, key);
                    else
                        tagService.SetTag(doc, obj, key, value);

                    anyChanged = true;
                }
            }
            finally
            {
                RhinoDoc.ModifyObjectAttributes += OnAttributesModified;
            }

            if (anyChanged)
            {
                doc.Views.Redraw();
                RefreshPanel();
            }
        }

        private void ClearAllTags()
        {
            var doc = RhinoDoc.FromRuntimeSerialNumber(_docSN);
            if (doc == null) return;

            var selected = doc.Objects.GetSelectedObjects(false, false).ToList();
            if (selected.Count == 0) return;

            var tagService = RhinoClaudePlugin.Instance.TagService;
            int totalCleared = 0;

            RhinoDoc.ModifyObjectAttributes -= OnAttributesModified;
            try
            {
                foreach (var obj in selected)
                    totalCleared += tagService.ClearAllTags(doc, obj);
            }
            finally
            {
                RhinoDoc.ModifyObjectAttributes += OnAttributesModified;
            }

            RhinoApp.WriteLine(string.Format(
                "RhinoClaude: Cleared {0} tag(s) from {1} object(s).",
                totalCleared, selected.Count));

            doc.Views.Redraw();
            RefreshPanel();
        }

        // ── IPanel ──────────────────────────────────────────────────

        public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
        {
            HookEvents();
            RefreshPanel();
        }

        public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason)
        {
            UnhookEvents();
        }

        public void PanelClosing(uint documentSerialNumber, bool onCloseDocument)
        {
            UnhookEvents();
        }

        // ── Cleanup ─────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                UnhookEvents();
            base.Dispose(disposing);
        }
    }
}
