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
    public class GeminiClient : ILlmClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly int _maxTokens;
        private int _toolCallCounter;

        public LlmProvider Provider => LlmProvider.Google;

        public GeminiClient(string apiKey, string model, int maxTokens)
        {
            _apiKey = apiKey;
            _model = model;
            _maxTokens = maxTokens;
            _toolCallCounter = 0;

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);
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
                // Convert messages to Gemini format
                var contents = new List<object>();
                foreach (var msg in messages)
                {
                    contents.Add(ConvertMessageToGeminiFormat(msg));
                }

                var requestBody = new Dictionary<string, object>
                {
                    ["contents"] = contents,
                    ["generationConfig"] = new Dictionary<string, object>
                    {
                        ["maxOutputTokens"] = _maxTokens
                    }
                };

                if (!string.IsNullOrEmpty(systemPrompt))
                {
                    requestBody["systemInstruction"] = new Dictionary<string, object>
                    {
                        ["parts"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["text"] = systemPrompt
                            }
                        }
                    };
                }

                if (tools != null && tools.Count > 0)
                {
                    requestBody["tools"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["functionDeclarations"] = FormatToolDefinitions(tools)
                        }
                    };
                }

                var apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:streamGenerateContent?alt=sse";

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var request = new HttpRequestMessage(HttpMethod.Post, apiUrl) { Content = content })
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

                                    // Navigate to candidates[0]
                                    JsonElement candidatesEl;
                                    if (!root.TryGetProperty("candidates", out candidatesEl))
                                        continue;
                                    if (candidatesEl.GetArrayLength() == 0)
                                        continue;

                                    var candidate = candidatesEl[0];

                                    // Check finishReason
                                    if (candidate.TryGetProperty("finishReason", out var finishReasonEl) &&
                                        finishReasonEl.ValueKind == JsonValueKind.String)
                                    {
                                        var finishReason = finishReasonEl.GetString();
                                        switch (finishReason)
                                        {
                                            case "STOP":
                                                // Only set end_turn if no tool calls have been collected
                                                if (response.ToolCalls.Count == 0)
                                                {
                                                    response.StopReason = "end_turn";
                                                }
                                                else
                                                {
                                                    response.StopReason = "tool_use";
                                                }
                                                break;
                                            default:
                                                response.StopReason = finishReason;
                                                break;
                                        }
                                    }

                                    // Parse content.parts
                                    JsonElement contentEl;
                                    if (!candidate.TryGetProperty("content", out contentEl))
                                        continue;

                                    JsonElement partsEl;
                                    if (!contentEl.TryGetProperty("parts", out partsEl))
                                        continue;

                                    foreach (var part in partsEl.EnumerateArray())
                                    {
                                        // Text part
                                        if (part.TryGetProperty("text", out var textEl) &&
                                            textEl.ValueKind == JsonValueKind.String)
                                        {
                                            var text = textEl.GetString();
                                            if (!string.IsNullOrEmpty(text))
                                            {
                                                textBuilder.Append(text);
                                                onTextDelta?.Invoke(text);
                                            }
                                        }

                                        // Function call part
                                        if (part.TryGetProperty("functionCall", out var funcCallEl))
                                        {
                                            var toolUse = new ToolUse();

                                            // Gemini doesn't provide tool call IDs, generate one
                                            toolUse.Id = $"gemini_call_{_toolCallCounter++}";

                                            if (funcCallEl.TryGetProperty("name", out var nameEl))
                                            {
                                                toolUse.Name = nameEl.GetString();
                                            }

                                            if (funcCallEl.TryGetProperty("args", out var argsEl) &&
                                                argsEl.ValueKind == JsonValueKind.Object)
                                            {
                                                var rawDict = new Dictionary<string, JsonElement>();
                                                foreach (var prop in argsEl.EnumerateObject())
                                                {
                                                    rawDict[prop.Name] = prop.Value.Clone();
                                                }
                                                toolUse.Input = ConvertJsonElementDict(rawDict);
                                            }
                                            else
                                            {
                                                toolUse.Input = new Dictionary<string, object>();
                                            }

                                            // Gemini 3.x: capture thoughtSignature (required for function call echo-back)
                                            if (part.TryGetProperty("thoughtSignature", out var sigEl) &&
                                                sigEl.ValueKind == JsonValueKind.String)
                                            {
                                                toolUse.ThoughtSignature = sigEl.GetString();
                                            }

                                            response.ToolCalls.Add(toolUse);

                                            // If we have tool calls, ensure stop reason reflects it
                                            response.StopReason = "tool_use";
                                        }
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
        /// Format assistant response into Gemini's model message format.
        /// Gemini uses {role: "model", parts: [{text: "..."}, {functionCall: {name, args}}]}
        /// </summary>
        public Dictionary<string, object> FormatAssistantMessage(string text, List<ToolUse> toolCalls)
        {
            var parts = new List<object>();

            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(new Dictionary<string, object>
                {
                    ["text"] = text
                });
            }

            if (toolCalls != null)
            {
                foreach (var tc in toolCalls)
                {
                    var part = new Dictionary<string, object>
                    {
                        ["functionCall"] = new Dictionary<string, object>
                        {
                            ["name"] = tc.Name,
                            ["args"] = tc.Input
                        }
                    };

                    // Gemini 3.x: echo back thoughtSignature (mandatory for function call parts)
                    if (!string.IsNullOrEmpty(tc.ThoughtSignature))
                    {
                        part["thoughtSignature"] = tc.ThoughtSignature;
                    }

                    parts.Add(part);
                }
            }

            return new Dictionary<string, object>
            {
                ["role"] = "model",
                ["parts"] = parts
            };
        }

        /// <summary>
        /// Format tool results for Gemini. All results go in a single "function" role message
        /// with functionResponse parts.
        /// </summary>
        public List<Dictionary<string, object>> FormatToolResultMessages(List<ToolCallResult> results)
        {
            var parts = new List<object>();

            foreach (var r in results)
            {
                // Parse the result JSON into an object for proper nesting
                object resultContent;
                try
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.ResultJson);
                    resultContent = ConvertJsonElementDict(parsed);
                }
                catch
                {
                    resultContent = new Dictionary<string, object>
                    {
                        ["result"] = r.ResultJson
                    };
                }

                parts.Add(new Dictionary<string, object>
                {
                    ["functionResponse"] = new Dictionary<string, object>
                    {
                        ["name"] = r.ToolName,
                        ["response"] = new Dictionary<string, object>
                        {
                            ["content"] = resultContent
                        }
                    }
                });
            }

            return new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["role"] = "function",
                    ["parts"] = parts
                }
            };
        }

        /// <summary>
        /// Convert a conversation message from the generic format to Gemini's format.
        /// Maps "assistant" -> "model", "user" -> "user", and restructures content.
        /// </summary>
        private Dictionary<string, object> ConvertMessageToGeminiFormat(Dictionary<string, object> message)
        {
            var role = "user";
            if (message.ContainsKey("role"))
            {
                var rawRole = message["role"]?.ToString();
                switch (rawRole)
                {
                    case "assistant":
                        role = "model";
                        break;
                    case "function":
                        role = "function";
                        break;
                    default:
                        role = rawRole;
                        break;
                }
            }

            // If the message already has "parts", pass through (already Gemini format)
            if (message.ContainsKey("parts"))
            {
                return new Dictionary<string, object>
                {
                    ["role"] = role,
                    ["parts"] = message["parts"]
                };
            }

            // Convert content field to parts
            var parts = new List<object>();

            if (message.ContainsKey("content"))
            {
                var contentValue = message["content"];

                if (contentValue is string strContent)
                {
                    // Simple text content
                    parts.Add(new Dictionary<string, object>
                    {
                        ["text"] = strContent
                    });
                }
                else if (contentValue is List<object> contentList)
                {
                    // Content is a list of blocks (e.g., Anthropic-style content blocks)
                    foreach (var block in contentList)
                    {
                        if (block is Dictionary<string, object> blockDict)
                        {
                            if (blockDict.ContainsKey("type"))
                            {
                                var blockType = blockDict["type"]?.ToString();
                                switch (blockType)
                                {
                                    case "text":
                                        if (blockDict.ContainsKey("text"))
                                        {
                                            parts.Add(new Dictionary<string, object>
                                            {
                                                ["text"] = blockDict["text"]?.ToString()
                                            });
                                        }
                                        break;
                                    case "image":
                                        // Internal {type:"image", mime_type, data} → Gemini's
                                        // inlineData part {inlineData:{mimeType, data}}.
                                        var imgMime = blockDict.ContainsKey("mime_type") && blockDict["mime_type"] != null
                                            ? blockDict["mime_type"].ToString()
                                            : "image/png";
                                        var imgData = blockDict.ContainsKey("data") ? blockDict["data"]?.ToString() : null;
                                        parts.Add(new Dictionary<string, object>
                                        {
                                            ["inlineData"] = new Dictionary<string, object>
                                            {
                                                ["mimeType"] = imgMime,
                                                ["data"] = imgData
                                            }
                                        });
                                        break;
                                    case "tool_use":
                                        // Convert to functionCall part
                                        var funcCall = new Dictionary<string, object>
                                        {
                                            ["name"] = blockDict.ContainsKey("name") ? blockDict["name"]?.ToString() : ""
                                        };
                                        if (blockDict.ContainsKey("input"))
                                        {
                                            funcCall["args"] = blockDict["input"];
                                        }
                                        parts.Add(new Dictionary<string, object>
                                        {
                                            ["functionCall"] = funcCall
                                        });
                                        break;
                                    case "tool_result":
                                        // Convert to functionResponse part
                                        var funcResp = new Dictionary<string, object>
                                        {
                                            ["name"] = blockDict.ContainsKey("tool_name") ? blockDict["tool_name"]?.ToString() : ""
                                        };
                                        if (blockDict.ContainsKey("content"))
                                        {
                                            funcResp["response"] = new Dictionary<string, object>
                                            {
                                                ["content"] = blockDict["content"]
                                            };
                                        }
                                        parts.Add(new Dictionary<string, object>
                                        {
                                            ["functionResponse"] = funcResp
                                        });
                                        break;
                                    default:
                                        // Unknown block type, try to extract text
                                        if (blockDict.ContainsKey("text"))
                                        {
                                            parts.Add(new Dictionary<string, object>
                                            {
                                                ["text"] = blockDict["text"]?.ToString()
                                            });
                                        }
                                        break;
                                }
                            }
                            else if (blockDict.ContainsKey("functionCall"))
                            {
                                parts.Add(new Dictionary<string, object>
                                {
                                    ["functionCall"] = blockDict["functionCall"]
                                });
                            }
                            else if (blockDict.ContainsKey("functionResponse"))
                            {
                                parts.Add(new Dictionary<string, object>
                                {
                                    ["functionResponse"] = blockDict["functionResponse"]
                                });
                            }
                            else if (blockDict.ContainsKey("text"))
                            {
                                parts.Add(new Dictionary<string, object>
                                {
                                    ["text"] = blockDict["text"]?.ToString()
                                });
                            }
                        }
                    }
                }
                else if (contentValue is JsonElement jsonEl)
                {
                    // Handle JsonElement content
                    if (jsonEl.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(new Dictionary<string, object>
                        {
                            ["text"] = jsonEl.GetString()
                        });
                    }
                    else if (jsonEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in jsonEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object &&
                                item.TryGetProperty("text", out var textProp))
                            {
                                parts.Add(new Dictionary<string, object>
                                {
                                    ["text"] = textProp.GetString()
                                });
                            }
                        }
                    }
                }
            }

            // Ensure at least one part exists
            if (parts.Count == 0)
            {
                parts.Add(new Dictionary<string, object>
                {
                    ["text"] = ""
                });
            }

            return new Dictionary<string, object>
            {
                ["role"] = role,
                ["parts"] = parts
            };
        }

        /// <summary>
        /// Convert ToolDefinition list into Gemini's functionDeclarations format.
        /// Gemini uses {name, description, parameters: {type, properties, required}}
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
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = parameters
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

                // Gemini API requires "items" for array-type properties
                if (kvp.Value.Type == "array")
                {
                    // Infer element type from parameter name conventions
                    var name = kvp.Key.ToLower();
                    var itemType = (name.Contains("_ids") || name.Contains("_id") ||
                                   name == "element_ids" || name == "view_ids" ||
                                   name == "system_ids" || name == "link_ids")
                        ? "integer"
                        : "string";
                    prop["items"] = new Dictionary<string, object> { ["type"] = itemType };
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
