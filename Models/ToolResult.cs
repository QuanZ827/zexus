using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Zexus.Models
{
    public class ToolResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("data")]
        public Dictionary<string, object> Data { get; set; }

        [JsonPropertyName("warning")]
        public string Warning { get; set; }

        public static ToolResult Ok(string message, Dictionary<string, object> data = null)
        {
            return new ToolResult
            {
                Success = true,
                Message = message,
                Data = data ?? new Dictionary<string, object>()
            };
        }

        public static ToolResult Fail(string message)
        {
            return new ToolResult
            {
                Success = false,
                Message = message,
                Data = new Dictionary<string, object>()
            };
        }

        public static ToolResult WithWarning(string message, Dictionary<string, object> data)
        {
            return new ToolResult
            {
                Success = true,
                Message = message,
                Data = data ?? new Dictionary<string, object>(),
                Warning = message
            };
        }
    }
}
