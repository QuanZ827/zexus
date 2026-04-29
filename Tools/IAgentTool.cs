using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Models;

namespace Zexus.Tools
{
    public interface IAgentTool
    {
        string Name { get; }
        string Description { get; }
        ToolSchema GetInputSchema();
        ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters);
    }

    /// <summary>
    /// Implement on tools that need UIApplication access (e.g., PostCommand).
    /// RevitEventHandler calls SetUIApplication() before Execute() for tools implementing this.
    /// </summary>
    public interface IAppAwareTool
    {
        void SetUIApplication(UIApplication uiApp);
    }

    public class ToolSchema
    {
        public string Type { get; set; } = "object";
        public Dictionary<string, PropertySchema> Properties { get; set; } = new Dictionary<string, PropertySchema>();
        public List<string> Required { get; set; } = new List<string>();
    }

    public class PropertySchema
    {
        public string Type { get; set; }
        public string Description { get; set; }
        public List<string> Enum { get; set; }
        public object Default { get; set; }
    }

    public class ToolDefinition
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public ToolSchema InputSchema { get; set; }

        public Dictionary<string, object> ToDictionary()
        {
            var inputSchema = new Dictionary<string, object>
            {
                ["type"] = InputSchema.Type,
                ["properties"] = ConvertProperties(InputSchema.Properties),
                ["required"] = InputSchema.Required
            };

            return new Dictionary<string, object>
            {
                ["name"] = Name,
                ["description"] = Description,
                ["input_schema"] = inputSchema
            };
        }

        private Dictionary<string, object> ConvertProperties(Dictionary<string, PropertySchema> props)
        {
            var result = new Dictionary<string, object>();
            foreach (var kvp in props)
            {
                var prop = new Dictionary<string, object>
                {
                    ["type"] = kvp.Value.Type,
                    ["description"] = kvp.Value.Description
                };

                if (kvp.Value.Enum != null && kvp.Value.Enum.Count > 0)
                {
                    prop["enum"] = kvp.Value.Enum;
                }

                result[kvp.Key] = prop;
            }
            return result;
        }
    }
}
