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

        /// <summary>
        /// Optional image attachments for multimodal messages.
        /// Stored as encoded byte arrays (PNG for clipboard captures).
        /// Cleared after the first API round-trip (see ToolLoopController).
        /// </summary>
        public List<ImageAttachment> Images { get; set; }

        /// <summary>True after images have been degraded to placeholder text in history.</summary>
        public bool ImagesStripped { get; set; }
    }

    /// <summary>
    /// A single image attached to a user message. Stored as raw encoded bytes
    /// (no WPF UI types — those have thread affinity and shouldn't live in the model).
    /// </summary>
    public class ImageAttachment
    {
        /// <summary>Encoded image bytes. Always PNG for clipboard captures.</summary>
        public byte[] Data { get; set; }

        /// <summary>MIME type — "image/png" for clipboard captures.</summary>
        public string MimeType { get; set; } = "image/png";

        /// <summary>Display label for the history placeholder ("[1 image(s) attached]").</summary>
        public string Label { get; set; } = "Screenshot";
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
