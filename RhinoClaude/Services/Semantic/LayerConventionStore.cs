using System;
using Rhino;
using RhinoClaude.Agent;
using RhinoClaude.Semantic;

namespace RhinoClaude.Services.Semantic
{
    /// <summary>
    /// Loads and persists the learned layer convention at both scopes: per-document, baked
    /// into the <c>.3dm</c>, and firm-level, in plugin settings so it follows the user across
    /// documents (semantic plan §5.6 step 6, §6.1).
    ///
    /// The key namespace is distinct from <see cref="AgentConversationStore"/>'s, so the two
    /// stores can never collide inside <c>RhinoDoc.Strings</c> (plan §6.4).
    /// </summary>
    public sealed class LayerConventionStore
    {
        private readonly uint _docSerialNumber;
        private readonly AgentSettings _settings;

        public LayerConventionStore(RhinoDoc doc, AgentSettings settings)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _docSerialNumber = doc.RuntimeSerialNumber;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        private RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(_docSerialNumber);

        // ── Document scope ────────────────────────────────────────────

        public LayerConventionMap LoadDocumentMap()
        {
            var doc = Doc;
            if (doc == null) return null;

            try
            {
                return LayerConventionMap.FromJson(
                    doc.Strings.GetValue(SemanticVocabulary.DocKeyLayerConvention));
            }
            catch (Exception)
            {
                // A corrupt string must not stop the classifier — the canonical convention
                // is always there to fall back on.
                return null;
            }
        }

        public void SaveDocumentMap(LayerConventionMap map)
        {
            var doc = Doc;
            if (doc == null || map == null) return;
            doc.Strings.SetString(SemanticVocabulary.DocKeyLayerConvention, map.ToJson());
        }

        public void ClearDocumentMap()
        {
            var doc = Doc;
            if (doc == null) return;
            try { doc.Strings.Delete(SemanticVocabulary.DocKeyLayerConvention); }
            catch (Exception) { /* already absent */ }
        }

        // ── Firm scope ────────────────────────────────────────────────

        public LayerConventionMap LoadFirmMap()
        {
            var map = LayerConventionMap.FromJson(_settings.FirmLayerConventionJson);

            // A firm floor-to-floor set through the settings dialog rather than the learn
            // dialog still has to reach the classifier.
            if (_settings.FloorToFloorDefault > 0)
            {
                map = map ?? new LayerConventionMap { Source = "settings" };
                if (map.FloorToFloorDefault <= 0) map.FloorToFloorDefault = _settings.FloorToFloorDefault;
            }

            return map;
        }

        public void SaveFirmMap(LayerConventionMap map)
        {
            if (map == null)
            {
                _settings.FirmLayerConventionJson = null;
                return;
            }

            _settings.FirmLayerConventionJson = map.ToJson();
            if (map.FloorToFloorDefault > 0) _settings.FloorToFloorDefault = map.FloorToFloorDefault;
            Persist();
        }

        /// <summary>Push the firm-scope settings through to the plugin's persisted settings.</summary>
        public void Persist()
        {
            var plugin = RhinoClaudePlugin.Instance;
            if (plugin == null) return;

            try
            {
                // PersistentSettings writes through on set — phase 1's ClaudeSetKey relies on
                // the same behaviour for the API key.
                plugin.Settings.SetString("SemanticFirmLayerConvention", _settings.FirmLayerConventionJson ?? string.Empty);
                plugin.Settings.SetDouble("SemanticFloorToFloorDefault", _settings.FloorToFloorDefault);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("RhinoClaude: could not persist the layer convention — " + ex.Message);
            }
        }

        /// <summary>Read firm-scope settings back at plugin load, into a fresh AgentSettings.</summary>
        public static void Restore(AgentSettings settings)
        {
            var plugin = RhinoClaudePlugin.Instance;
            if (plugin == null || settings == null) return;

            try
            {
                string json = plugin.Settings.GetString("SemanticFirmLayerConvention", string.Empty);
                if (!string.IsNullOrWhiteSpace(json)) settings.FirmLayerConventionJson = json;

                double floorToFloor = plugin.Settings.GetDouble("SemanticFloorToFloorDefault", 0);
                if (floorToFloor > 0) settings.FloorToFloorDefault = floorToFloor;
            }
            catch (Exception)
            {
                // No saved settings yet: defaults stand.
            }
        }

        /// <summary>Document map first, then firm map, then the shipped canonical convention.</summary>
        public ConventionResolver BuildResolver() => new ConventionResolver(LoadDocumentMap(), LoadFirmMap());
    }
}
