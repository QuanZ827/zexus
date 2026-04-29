using System;
using System.Collections.Generic;
using System.Linq;

namespace Zexus.Tools
{
    public class ToolRegistry
    {
        private readonly Dictionary<string, IAgentTool> _tools =
            new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);

        public void Register(IAgentTool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            _tools[tool.Name] = tool;
        }

        public void RegisterAll(params IAgentTool[] tools)
        {
            foreach (var tool in tools)
            {
                Register(tool);
            }
        }

        public IAgentTool GetTool(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            _tools.TryGetValue(name, out var tool);
            return tool;
        }

        public IEnumerable<string> GetToolNames() => _tools.Keys.ToList();

        public List<ToolDefinition> GetToolDefinitions()
        {
            return _tools.Values.Select(tool => new ToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.GetInputSchema()
            }).ToList();
        }

        public List<Dictionary<string, object>> GetToolDefinitionsAsDictionaries()
        {
            return GetToolDefinitions().Select(t => t.ToDictionary()).ToList();
        }

        public bool HasTool(string name) =>
            !string.IsNullOrEmpty(name) && _tools.ContainsKey(name);

        public int Count => _tools.Count;

        public static ToolRegistry CreateDefault()
        {
            var registry = new ToolRegistry();
            registry.Register(new ExecuteCodeTool());
            return registry;
        }
    }
}
