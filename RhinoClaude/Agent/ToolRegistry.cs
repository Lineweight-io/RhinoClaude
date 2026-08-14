using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Agent
{
    /// <summary>
    /// The tools Claude can see, and the delegates that run them. Serves both the
    /// <c>tools</c> array on the request and the dispatcher's lookup.
    /// </summary>
    public sealed class ToolRegistry
    {
        private readonly Dictionary<string, ToolDefinition> _tools =
            new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);

        private readonly List<string> _order = new List<string>();

        public void Register(ToolDefinition tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            if (string.IsNullOrWhiteSpace(tool.Name))
                throw new ArgumentException("Tool name is required.", nameof(tool));
            if (tool.Handler == null)
                throw new ArgumentException("Tool '" + tool.Name + "' has no handler.", nameof(tool));

            if (!_tools.ContainsKey(tool.Name))
                _order.Add(tool.Name);
            _tools[tool.Name] = tool;
        }

        public void RegisterAll(IEnumerable<ToolDefinition> tools)
        {
            if (tools == null) return;
            foreach (var tool in tools) Register(tool);
        }

        public bool TryGet(string name, out ToolDefinition tool) => _tools.TryGetValue(name ?? string.Empty, out tool);

        public int Count => _tools.Count;

        public IReadOnlyList<string> Names => _order;

        /// <summary>
        /// Order is stable (registration order), which matters: the tools array renders
        /// first in the prompt, so a non-deterministic order would break prompt caching.
        /// </summary>
        public List<ToolSpec> ToSpecs() => _order.Select(n => _tools[n].ToSpec()).ToList();

        public IEnumerable<ToolDefinition> All() => _order.Select(n => _tools[n]);
    }
}
