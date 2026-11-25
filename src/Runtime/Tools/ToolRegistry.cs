using System;
using System.Collections.Generic;
using System.Linq;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Runtime.Tools
{
    // Default in-memory registry. Thread-safe for the read-mostly access pattern of the agent loop.
    public sealed class ToolRegistry : IToolRegistry
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

        public ToolRegistry() { }

        public ToolRegistry(IEnumerable<ITool> tools)
        {
            if (tools == null) return;
            foreach (var t in tools) Register(t);
        }

        public void Register(ITool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            var name = tool.Definition?.Name;
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Tool definition must have a name.", nameof(tool));
            lock (_gate) { _tools[name!] = tool; }
        }

        public bool TryGet(string name, out ITool tool)
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(name) && _tools.TryGetValue(name, out var t))
                {
                    tool = t;
                    return true;
                }
            }
            tool = null!;
            return false;
        }

        public IReadOnlyCollection<ITool> All
        {
            get { lock (_gate) { return _tools.Values.ToArray(); } }
        }

        public IReadOnlyList<AiToolDefinition> Definitions()
        {
            lock (_gate) { return _tools.Values.Select(t => t.Definition).ToArray(); }
        }
    }
}
