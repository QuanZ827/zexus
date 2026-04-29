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
    public class OpenAiClient : ILlmClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _model;
        private readonly int _maxTokens;
        private const string API_URL = "https://api.openai.com/v1/chat/completions";

        public LlmProvider Provider => LlmProvider.OpenAI;

        public OpenAiClient(string apiKey, string model, int maxTokens)
        {
            _model = model;
            _maxTokens = maxTokens;

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
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
                // Build messages list with system prompt prepended
                var apiMessages = new List<object>();

                if (!string.IsNullOrEmpty(systemPrompt))
                {
                    apiMessages.Add(new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] = systemPrompt
                    });
                }

                foreach (var msg in messages)
                {
                    apiMessages.Add(msg);
                }

                var requestBody = new Dictionary<string, object>
                {
                    ["model"] = _model,
                    ["max_tokens"] = _maxTokens,
                    ["messages"] = apiMessages,
                    ["stream"] = true
                };

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

                        // Accumulators for streaming tool calls, keyed by index
                        var toolCallIds = new Dictionary<int, string>();
                        var toolCallNames = new Dictionary<int, string>();
                        var toolCallArgs = new Dictionary<int, StringBuilder>();

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

                                    // Navigate to choices[0]
                                    JsonElement choicesElement;
                                    if (!root.TryGetProperty("choices", out choicesElement))
                                        continue;
                                    if (choicesElement.GetArrayLength() == 0)
                                        continue;

                                    var choice = choicesElement[0];

                                    // Check finish_reason
                                    if (choice.TryGetProperty("finish_reason", out var finishReasonEl) &&
                                        finishReasonEl.ValueKind != JsonValueKind.Null)
                                    {
                                        var finishReason = finishReasonEl.GetString();
                                        switch (finishReason)
                                        {
                                            case "stop":
                                                response.StopReason = "end_turn";
                                                break;
                                            case "tool_calls":
                                                response.StopReason = "tool_use";
                                                break;
                                            default:
                                                response.StopReason = finishReason;
                                                break;
                                        }
                                    }

                                    // Parse delta
                                    JsonElement deltaEl;
                                    if (!choice.TryGetProperty("delta", out deltaEl))
                                        continue;

                                    // Text content
                                    if (deltaEl.TryGetProperty("content", out var contentEl) &&
                                        contentEl.ValueKind == JsonValueKind.String)
                                    {
                                        var text = contentEl.GetString();
                                        if (!string.IsNullOrEmpty(text))
                                        {
                                            textBuilder.Append(text);
                                            onTextDelta?.Invoke(text);
                                        }
                                    }

                                    // Tool calls (streamed incrementally by index)
                                    if (deltaEl.TryGetProperty("tool_calls", out var toolCallsEl) &&
                                        toolCallsEl.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var tc in toolCallsEl.EnumerateArray())
                                        {
                                            int idx = 0;
                                            if (tc.TryGetProperty("index", out var idxEl))
                                            {
                                                idx = idxEl.GetInt32();
                                            }

                                            // First chunk for this index has id, type, function.name
                                            if (tc.TryGetProperty("id", out var idEl) &&
                                                idEl.ValueKind == JsonValueKind.String)
                                            {
                                                toolCallIds[idx] = idEl.GetString();
                                            }

                                            if (tc.TryGetProperty("function", out var funcEl))
                                            {
                                                if (funcEl.TryGetProperty("name", out var nameEl) &&
                                                    nameEl.ValueKind == JsonValueKind.String)
                                                {
                                                    toolCallNames[idx] = nameEl.GetString();
                                                }

                                                if (funcEl.TryGetProperty("arguments", out var argsEl) &&
                                                    argsEl.ValueKind == JsonValueKind.String)
                                                {
                                                    if (!toolCallArgs.ContainsKey(idx))
                                                    {
                                                        toolCallArgs[idx] = new StringBuilder();
                                                    }
                                                    toolCallArgs[idx].Append(argsEl.GetString());
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Zexus] SSE parse error: {data?.Substring(0, Math.Min(200, data?.Length ?? 0))}... {ex.Message}");
                            }
                        }

                        // Assemble completed tool calls from accumulated chunks
                        var allIndices = new HashSet<int>();
                        foreach (var k in toolCallIds.Keys) allIndices.Add(k);
                        foreach (var k in toolCallNames.Keys) allIndices.Add(k);
                        foreach (var k in toolCallArgs.Keys) allIndices.Add(k);

                        var sortedIndices = allIndices.OrderBy(i => i).ToList();
                        foreach (var idx in sortedIndices)
                        {
                            var toolUse = new ToolUse();

                            if (toolCallIds.ContainsKey(idx))
                                toolUse.Id = toolCallIds[idx];
                            if (toolCallNames.ContainsKey(idx))
                                toolUse.Name = toolCallNames[idx];

                            if (toolCallArgs.ContainsKey(idx))
                            {
                                try
                                {
                                    var argsJson = toolCallArgs[idx].ToString();
                                    if (!string.IsNullOrEmpty(argsJson))
                                    {
                                        var rawDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                                        toolUse.Input = ConvertJsonElementDict(rawDict);
                                    }
                                    else
                                    {
                                        toolUse.Input = new Dictionary<string, object>();
                                    }
                                }
                                catch
                                {
                                    toolUse.Input = new Dictionary<string, object>();
                                }
                            }
                            else
                            {
                                toolUse.Input = new Dictionary<string, object>();
                            }

                            response.ToolCalls.Add(toolUse);
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
        /// Format assistant response into OpenAI's message format.
        /// OpenAI uses {role: "assistant", content: text, tool_calls: [{id, type: "function", function: {name, arguments}}]}
        /// </summary>
        public Dictionary<string, object> FormatAssistantMessage(string text, List<ToolUse> toolCalls)
        {
            var message = new Dictionary<string, object>
            {
                ["role"] = "assistant",
                ["content"] = text ?? ""
            };

            if (toolCalls != null && toolCalls.Count > 0)
            {
                var formattedToolCalls = new List<object>();
                foreach (var tc in toolCalls)
                {
                    formattedToolCalls.Add(new Dictionary<string, object>
                    {
                        ["id"] = tc.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object>
                        {
                            ["name"] = tc.Name,
                            ["arguments"] = JsonSerializer.Serialize(tc.Input)
                        }
                    });
                }
                message["tool_calls"] = formattedToolCalls;
            }

            return message;
        }

        /// <summary>
        /// Format tool results for OpenAI. Each result is a SEPARATE message with role "tool".
        /// </summary>
        public List<Dictionary<string, object>> FormatToolResultMessages(List<ToolCallResult> results)
        {
            var messages = new List<Dictionary<string, object>>();

            foreach (var r in results)
            {
                messages.Add(new Dictionary<string, object>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = r.ToolCallId,
                    ["content"] = r.ResultJson
                });
            }

            return messages;
        }

        /// <summary>
        /// Convert ToolDefinition list into OpenAI's tool schema format.
        /// OpenAI uses {type: "function", function: {name, description, parameters: {type, properties, required}}}
        /// </summary>
        private List<Dictionary<string, object>> FormatToolDefinitions(List<ToolDefinition> tools)
        {
            return tools.Select(t =>
            {
                var parameters = new Dictionary<string, object>
                {
                    ["type"] = t.InputSchema.Type,
                    ["properties"] = ConvertPropertySchemas(t.InputSchema.Properties),
                    ["required"] = t.InputSchema.Required
                };

                return new Dictionary<string, object>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object>
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = parameters
                    }
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
