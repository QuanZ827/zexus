using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Zexus.Models;
using Zexus.Tools;

namespace Zexus.Services
{
    public class AnthropicClient : ILlmClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _model;
        private readonly int _maxTokens;
        private const string API_URL = "https://api.anthropic.com/v1/messages";

        public LlmProvider Provider => LlmProvider.Anthropic;

        public AnthropicClient(string apiKey, string model, int maxTokens)
        {
            _model = model;
            _maxTokens = maxTokens;

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
        }

        public async Task<ApiResponse> SendMessageStreamingAsync(
            List<Dictionary<string, object>> messages,
            string systemPrompt,
            List<ToolDefinition> tools,
            Action<string> onTextDelta,
            CancellationToken cancellationToken = default)
        {
            var response = new ApiResponse { Success = true };
            var textBuilder = new StringBuilder();

            try
            {
                var requestBody = new Dictionary<string, object>
                {
                    ["model"] = _model,
                    ["max_tokens"] = _maxTokens,
                    ["messages"] = messages,
                    ["stream"] = true
                };

                if (!string.IsNullOrEmpty(systemPrompt))
                {
                    requestBody["system"] = systemPrompt;
                }

                if (tools != null && tools.Count > 0)
                {
                    requestBody["tools"] = FormatToolDefinitions(tools);
                }

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var request = new HttpRequestMessage(HttpMethod.Post, API_URL) { Content = content })
                using (var httpResponse = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        var errorBody = await httpResponse.Content.ReadAsStringAsync();
                        response.Success = false;
                        response.Error = $"API Error {(int)httpResponse.StatusCode}: {errorBody}";
                        return response;
                    }

                    using (var stream = await httpResponse.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        ToolUse currentToolUse = null;
                        var toolInputBuilder = new StringBuilder();

                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                                continue;

                            var data = line.Substring(6);
                            if (data == "[DONE]") break;

                            try
                            {
                                using (var doc = JsonDocument.Parse(data))
                                {
                                    var root = doc.RootElement;
                                    var eventType = root.GetProperty("type").GetString();

                                    switch (eventType)
                                    {
                                        case "content_block_start":
                                            var blockType = root.GetProperty("content_block").GetProperty("type").GetString();
                                            if (blockType == "tool_use")
                                            {
                                                currentToolUse = new ToolUse
                                                {
                                                    Id = root.GetProperty("content_block").GetProperty("id").GetString(),
                                                    Name = root.GetProperty("content_block").GetProperty("name").GetString()
                                                };
                                                toolInputBuilder.Clear();
                                            }
                                            break;

                                        case "content_block_delta":
                                            var deltaType = root.GetProperty("delta").GetProperty("type").GetString();
                                            if (deltaType == "text_delta")
                                            {
                                                var text = root.GetProperty("delta").GetProperty("text").GetString();
                                                textBuilder.Append(text);
                                                onTextDelta?.Invoke(text);
                                            }
                                            else if (deltaType == "input_json_delta")
                                            {
                                                var partialJson = root.GetProperty("delta").GetProperty("partial_json").GetString();
                                                toolInputBuilder.Append(partialJson);
                                            }
                                            break;

                                        case "content_block_stop":
                                            if (currentToolUse != null)
                                            {
                                                try
                                                {
                                                    var inputJson = toolInputBuilder.ToString();
                                                    if (!string.IsNullOrEmpty(inputJson))
                                                    {
                                                        var rawDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inputJson);
                                                        currentToolUse.Input = ConvertJsonElementDict(rawDict);
                                                    }
                                                    else
                                                    {
                                                        currentToolUse.Input = new Dictionary<string, object>();
                                                    }
                                                }
                                                catch
                                                {
                                                    currentToolUse.Input = new Dictionary<string, object>();
                                                }
                                                response.ToolCalls.Add(currentToolUse);
                                                currentToolUse = null;
                                            }
                                            break;

                                        case "message_delta":
                                            if (root.TryGetProperty("delta", out var delta) &&
                                                delta.TryGetProperty("stop_reason", out var stopReason))
                                            {
                                                response.StopReason = stopReason.GetString();
                                            }
                                            break;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Zexus] SSE parse error: {data?.Substring(0, Math.Min(200, data?.Length ?? 0))}... {ex.Message}");
                            }
                        }
                    }
                }

                response.Text = textBuilder.ToString();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Error = ex.Message;
            }

            return response;
        }

        /// <summary>
        /// Format assistant response into Anthropic's content block structure.
        /// Anthropic uses a list of typed blocks: [{type:"text", text:"..."}, {type:"tool_use", id:..., name:..., input:...}]
        /// </summary>
        public Dictionary<string, object> FormatAssistantMessage(string text, List<ToolUse> toolCalls)
        {
            var contentBlocks = new List<object>();

            if (!string.IsNullOrEmpty(text))
            {
                contentBlocks.Add(new Dictionary<string, object>
                {
                    ["type"] = "text",
                    ["text"] = text
                });
            }

            if (toolCalls != null)
            {
                foreach (var tc in toolCalls)
                {
                    contentBlocks.Add(new Dictionary<string, object>
                    {
                        ["type"] = "tool_use",
                        ["id"] = tc.Id,
                        ["name"] = tc.Name,
                        ["input"] = tc.Input
                    });
                }
            }

            return new Dictionary<string, object>
            {
                ["role"] = "assistant",
                ["content"] = contentBlocks
            };
        }

        /// <summary>
        /// Format tool results for Anthropic. All results go in a single "user" message
        /// with content blocks of type "tool_result".
        /// </summary>
        public List<Dictionary<string, object>> FormatToolResultMessages(List<ToolCallResult> results)
        {
            var toolResultBlocks = new List<object>();

            foreach (var r in results)
            {
                toolResultBlocks.Add(new Dictionary<string, object>
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = r.ToolCallId,
                    ["content"] = r.ResultJson
                });
            }

            // Anthropic packs all tool results into one "user" message
            return new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = toolResultBlocks
                }
            };
        }

        /// <summary>
        /// Convert ToolDefinition list into Anthropic's tool schema format.
        /// Anthropic uses "input_schema" as the key.
        /// </summary>
        private List<Dictionary<string, object>> FormatToolDefinitions(List<ToolDefinition> tools)
        {
            return tools.Select(t =>
            {
                var schema = new Dictionary<string, object>
                {
                    ["type"] = t.InputSchema.Type,
                    ["properties"] = ConvertPropertySchemas(t.InputSchema.Properties),
                    ["required"] = t.InputSchema.Required
                };

                return new Dictionary<string, object>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = schema
                };
            }).ToList();
        }

        private Dictionary<string, object> ConvertPropertySchemas(Dictionary<string, PropertySchema> props)
        {
            var result = new Dictionary<string, object>();
            if (props == null) return result;

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

        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        private Dictionary<string, object> ConvertJsonElementDict(Dictionary<string, JsonElement> rawDict)
        {
            var result = new Dictionary<string, object>();
            if (rawDict == null) return result;

            foreach (var kvp in rawDict)
            {
                result[kvp.Key] = ConvertJsonElement(kvp.Value);
            }
            return result;
        }

        private object ConvertJsonElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    if (element.TryGetInt32(out int intVal))
                        return intVal;
                    if (element.TryGetInt64(out long longVal))
                        return longVal;
                    return element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                    return null;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var item in element.EnumerateArray())
                    {
                        list.Add(ConvertJsonElement(item));
                    }
                    return list;
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in element.EnumerateObject())
                    {
                        dict[prop.Name] = ConvertJsonElement(prop.Value);
                    }
                    return dict;
                default:
                    return element.ToString();
            }
        }
    }
}
