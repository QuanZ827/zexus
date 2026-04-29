using System;
using System.Collections.Generic;

namespace Zexus.Models
{
    public enum MessageRole
    {
        User,
        Assistant,
        System
    }

    public enum ToolCallStatus
    {
        Pending,
        Executing,
        Completed,
        Failed
    }

    public class ChatMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public MessageRole Role { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public List<ToolCall> ToolCalls { get; set; }
    }

    public class ToolCall
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Dictionary<string, object> Input { get; set; }
        public ToolResult Result { get; set; }
        public ToolCallStatus Status { get; set; } = ToolCallStatus.Pending;
    }

    public class Session
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string DocumentName { get; set; }
        public DateTime StartTime { get; set; } = DateTime.Now;
        public List<ChatMessage> Messages { get; } = new List<ChatMessage>();

        public void AddMessage(ChatMessage message)
        {
            Messages.Add(message);
        }
    }
}
