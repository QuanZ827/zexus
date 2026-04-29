using System;

namespace Zexus.Services
{
    /// <summary>
    /// Factory for creating LLM client instances based on provider selection.
    /// </summary>
    public static class LlmClientFactory
    {
        public static ILlmClient Create(LlmProvider provider, string apiKey, string model, int maxTokens)
        {
            switch (provider)
            {
                case LlmProvider.Anthropic:
                    return new AnthropicClient(apiKey, model, maxTokens);
                case LlmProvider.OpenAI:
                    return new OpenAiClient(apiKey, model, maxTokens);
                case LlmProvider.Google:
                    return new GeminiClient(apiKey, model, maxTokens);
                default:
                    throw new ArgumentException($"Unknown LLM provider: {provider}");
            }
        }
    }
}
