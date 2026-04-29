using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Zexus.Models;
using Zexus.Tools;

namespace Zexus.Services
{
    /// <summary>
    /// Supported LLM providers
    /// </summary>
    public enum LlmProvider
    {
        Anthropic,
        OpenAI,
        Google
    }

    /// <summary>
    /// Abstraction for LLM API clients. Each provider (Anthropic, OpenAI, Google)
    /// implements this interface to encapsulate its own API format, streaming protocol,
    /// and tool call/result message structures.
    /// </summary>
    public interface ILlmClient : IDisposable
    {
        /// <summary>Which provider this client connects to</summary>
        LlmProvider Provider { get; }

        /// <summary>
        /// Send a streaming message to the LLM with tool definitions.
        /// Returns a provider-neutral ApiResponse with text and tool calls.
        /// </summary>
        Task<ApiResponse> SendMessageStreamingAsync(
            List<Dictionary<string, object>> messages,
            string systemPrompt,
            List<ToolDefinition> tools,
            Action<string> onTextDelta,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Format an assistant response (text + tool calls) into the provider's wire format
        /// for inclusion in the conversation history.
        /// </summary>
        Dictionary<string, object> FormatAssistantMessage(string text, List<ToolUse> toolCalls);

        /// <summary>
        /// Format executed tool results into the provider's feedback message format
        /// for sending back to the API.
        /// </summary>
        List<Dictionary<string, object>> FormatToolResultMessages(List<ToolCallResult> results);
    }

    /// <summary>
    /// Bridge between a ToolUse (from API response) and a ToolResult (from execution).
    /// Used by FormatToolResultMessages to package results in provider-specific format.
    /// </summary>
    public class ToolCallResult
    {
        public string ToolCallId { get; set; }
        public string ToolName { get; set; }
        public Dictionary<string, object> Input { get; set; }
        public string ResultJson { get; set; }
    }

    /// <summary>
    /// Provider metadata and validation utilities. UI and ConfigManager use this
    /// instead of hardcoded provider-specific strings.
    /// </summary>
    public static class LlmProviderInfo
    {
        public static bool ValidateApiKey(LlmProvider provider, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            switch (provider)
            {
                case LlmProvider.Anthropic:
                    return key.StartsWith("sk-ant-");
                case LlmProvider.OpenAI:
                    return key.StartsWith("sk-");
                case LlmProvider.Google:
                    // Google API keys typically start with "AIza"
                    return key.Length >= 20;
                default:
                    return false;
            }
        }

        public static string GetDisplayName(LlmProvider provider)
        {
            switch (provider)
            {
                case LlmProvider.Anthropic: return "Anthropic (Claude)";
                case LlmProvider.OpenAI: return "OpenAI (GPT)";
                case LlmProvider.Google: return "Google (Gemini)";
                default: return provider.ToString();
            }
        }

        public static string GetApiKeyLabel(LlmProvider provider)
        {
            switch (provider)
            {
                case LlmProvider.Anthropic: return "Enter your Anthropic API Key";
                case LlmProvider.OpenAI: return "Enter your OpenAI API Key";
                case LlmProvider.Google: return "Enter your Google AI API Key";
                default: return "Enter your API Key";
            }
        }

        public static string GetApiKeyHint(LlmProvider provider)
        {
            switch (provider)
            {
                case LlmProvider.Anthropic: return "Get one at console.anthropic.com";
                case LlmProvider.OpenAI: return "Get one at platform.openai.com/api-keys";
                case LlmProvider.Google: return "Get one at aistudio.google.com/apikey";
                default: return "";
            }
        }

        public static string GetDefaultModel(LlmProvider provider)
        {
            switch (provider)
            {
                case LlmProvider.Anthropic: return "claude-sonnet-4-6";
                case LlmProvider.OpenAI: return "gpt-4o";
                case LlmProvider.Google: return "gemini-3.1-pro-preview";
                default: return "";
            }
        }

        /// <summary>
        /// Available models per provider. Returns list of (modelId, displayName) pairs.
        /// Default model for each provider is listed first.
        /// </summary>
        public static List<KeyValuePair<string, string>> GetAvailableModels(LlmProvider provider)
        {
            switch (provider)
            {
                case LlmProvider.Anthropic:
                    return new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("claude-sonnet-4-6", "Claude Sonnet 4.6"),
                        new KeyValuePair<string, string>("claude-opus-4-7", "Claude Opus 4.7"),
                        new KeyValuePair<string, string>("claude-opus-4-6", "Claude Opus 4.6"),
                        new KeyValuePair<string, string>("claude-haiku-4-5", "Claude Haiku 4.5")
                    };
                case LlmProvider.OpenAI:
                    return new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("gpt-4o", "GPT-4o"),
                        new KeyValuePair<string, string>("gpt-4o-mini", "GPT-4o Mini"),
                        new KeyValuePair<string, string>("o3-mini", "o3-mini")
                    };
                case LlmProvider.Google:
                    return new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("gemini-3.1-pro-preview", "Gemini 3.1 Pro"),
                        new KeyValuePair<string, string>("gemini-3-pro-preview", "Gemini 3.0 Pro"),
                        new KeyValuePair<string, string>("gemini-3-flash-preview", "Gemini 3.0 Flash"),
                        new KeyValuePair<string, string>("gemini-2.5-pro", "Gemini 2.5 Pro"),
                        new KeyValuePair<string, string>("gemini-2.5-flash", "Gemini 2.5 Flash")
                    };
                default:
                    return new List<KeyValuePair<string, string>>();
            }
        }

        /// <summary>
        /// Get human-readable display name for a model ID. Falls back to the raw model ID.
        /// </summary>
        public static string GetModelDisplayName(LlmProvider provider, string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return "";
            var models = GetAvailableModels(provider);
            foreach (var m in models)
            {
                if (m.Key.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                    return m.Value;
            }
            return modelId; // fallback: show raw model ID
        }

        public static string GetApiKeyValidationError(LlmProvider provider)
        {
            switch (provider)
            {
                case LlmProvider.Anthropic: return "Invalid API key. Anthropic keys start with 'sk-ant-'";
                case LlmProvider.OpenAI: return "Invalid API key. OpenAI keys start with 'sk-'";
                case LlmProvider.Google: return "Invalid API key. Please check your Google AI API key.";
                default: return "Invalid API key.";
            }
        }

        /// <summary>Parse provider string from config (case-insensitive, backward-compatible)</summary>
        public static LlmProvider Parse(string providerString)
        {
            if (string.IsNullOrWhiteSpace(providerString))
                return LlmProvider.Anthropic;

            var lower = providerString.Trim().ToLower();
            if (lower == "openai" || lower == "gpt") return LlmProvider.OpenAI;
            if (lower == "google" || lower == "gemini") return LlmProvider.Google;
            return LlmProvider.Anthropic; // default
        }
    }
}
