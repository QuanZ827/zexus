using System.Collections.Generic;

namespace Zexus.Models
{
    public class ApiResponse
    {
        public bool Success { get; set; }
        public string Text { get; set; }
        public string Error { get; set; }
        public string StopReason { get; set; }
        public List<ToolUse> ToolCalls { get; set; } = new List<ToolUse>();
    }

    public class ToolUse
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Dictionary<string, object> Input { get; set; }

        /// <summary>
        /// Gemini 3.x thought signature. Must be echoed back in conversation history
        /// for function call parts, otherwise the API returns INVALID_ARGUMENT.
        /// See: https://ai.google.dev/gemini-api/docs/thought-signatures
        /// </summary>
        public string ThoughtSignature { get; set; }
    }
}
