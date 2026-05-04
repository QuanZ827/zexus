using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Zexus.Models;
using Zexus.Tools;

namespace Zexus.Services
{
    /// <summary>
    /// Owns the tool execution loop, all pre/post middleware, and loop budget enforcement.
    /// Extracted from AgentService to separate orchestration concerns.
    /// </summary>
    public class ToolLoopController
    {
        // Loop budget — hard limits to prevent infinite tool loops
        private const int MAX_TOOL_ITERATIONS = 30;
        private const int MAX_CONSECUTIVE_EXECUTECODE_FAILURES = 4;
        private const int MAX_ERROR_MEMORY = 5;

        // Compilation error memory — prevents LLM from repeating the same mistakes
        private readonly List<string> _compilationErrorMemory = new List<string>();

        // Pending policy confirmations — operations blocked by PolicyGate awaiting user approval
        private readonly List<string> _pendingPolicyConfirmations = new List<string>();
        private int _consecutiveDenials = 0;
        private const int DENIAL_THRESHOLD = 3;
        private const int MAX_CONTINUATIONS = 3;

        // Tracks whether the last iteration only produced read-only ExecuteCode (no Transaction)
        private bool _lastIterationWasReadOnlyEC = false;

        // Constructor-injected dependencies
        private readonly SessionContext _sessionContext;
        private readonly SessionReporter _reporter;
        private readonly PolicyGate _policyGate;

        // Events — wired by AgentService to forward to UI
        public event Action<string> OnStreamingText;
        public event Action<string, Dictionary<string, object>> OnToolExecuting;
        public event Action<string> OnStatusChanged;
        public event Action<string, ToolResult, long> OnToolCompleted;
        public event Action<string> OnReasoningForThinkingChain;

        public ToolLoopController(SessionContext sessionContext, SessionReporter reporter, PolicyGate policyGate)
        {
            _sessionContext = sessionContext;
            _reporter = reporter;
            _policyGate = policyGate;
        }

        // ───────────────────────────────────────────────────────
        // Public surface
        // ───────────────────────────────────────────────────────

        /// <summary>Read-only access to pending policy confirmation keys.</summary>
        public List<string> PendingPolicyConfirmations => _pendingPolicyConfirmations;

        /// <summary>Consecutive ExecuteCode failure count (delegates to PolicyGate).</summary>
        public int ConsecutiveFailures => _policyGate.ConsecutiveFailures;

        /// <summary>
        /// Reset per-task state: error memory, pending confirmations, PolicyGate counters.
        /// Called at the start of each new user task.
        /// </summary>
        public void ResetTask()
        {
            // If there were pending confirmations that were never resolved,
            // that counts as the user denying/ignoring the proposed operation
            if (_pendingPolicyConfirmations.Count > 0)
            {
                _consecutiveDenials++;
                ZexusLogger.Info($"[DENIAL] User started new task with {_pendingPolicyConfirmations.Count} unresolved confirmations. Consecutive denials: {_consecutiveDenials}");
            }

            _compilationErrorMemory.Clear();
            _pendingPolicyConfirmations.Clear();
            _lastIterationWasReadOnlyEC = false;
            _policyGate.ResetTask();
        }

        /// <summary>
        /// Mark pending policy operations as confirmed so they pass on re-call.
        /// </summary>
        public void HandleUserConfirmation(List<string> pendingKeys)
        {
            // User approved — reset denial counter
            _consecutiveDenials = 0;

            foreach (var key in pendingKeys)
            {
                var colonIdx = key.IndexOf(':');
                var desc = colonIdx >= 0 ? key.Substring(colonIdx + 1) : "";
                _policyGate.MarkConfirmed($"exec_write:{desc}");
                _policyGate.MarkConfirmed($"broad_scope:{desc}");
            }

            // Set blanket write confirmation — user said "yes" so all subsequent
            // write operations in this task are pre-approved (even with different descriptions).
            // Resets on ResetTask() when user starts a new task.
            _policyGate.SetWriteConfirmed(true);

            _pendingPolicyConfirmations.Clear();
        }

        public string GetDenialGuidance()
        {
            if (_consecutiveDenials >= DENIAL_THRESHOLD)
                return $"[SYSTEM] The user has rejected or ignored your proposed approach {_consecutiveDenials} times. " +
                       "Do NOT retry the same strategy. Ask the user what they want instead, or suggest a completely different approach.";
            return null;
        }

        // ───────────────────────────────────────────────────────
        // Core loop
        // ───────────────────────────────────────────────────────

        /// <summary>
        /// Run the tool loop: call LLM, execute tools, feed results back, repeat until
        /// the LLM returns a final text response with no tool calls.
        /// </summary>
        public async Task<ChatMessage> RunAsync(
            ILlmClient client,
            Session session,
            List<ToolDefinition> toolDefinitions,
            CancellationToken cancellationToken)
        {
            var conversationMessages = BuildApiMessages(session);
            var allToolCalls = new List<ToolCall>();
            var finalText = new System.Text.StringBuilder();
            int iteration = 0;

            while (true)
            {
                // Check cancellation before each iteration
                cancellationToken.ThrowIfCancellationRequested();

                iteration++;
                ZexusLogger.Info($"Tool loop iteration {iteration}");

                // ── Loop budget check ──
                if (iteration > MAX_TOOL_ITERATIONS)
                {
                    ZexusLogger.Warn($"[BUDGET] Loop budget exhausted after {MAX_TOOL_ITERATIONS} iterations");
                    var budgetMsg = new ChatMessage
                    {
                        Role = MessageRole.Assistant,
                        Content = $"I've reached the maximum of {MAX_TOOL_ITERATIONS} tool iterations for this request. " +
                                  "This usually means the task is more complex than expected. " +
                                  "Please review what's been done so far and send a follow-up message to continue."
                    };
                    session.AddMessage(budgetMsg);
                    OnStatusChanged?.Invoke("Complete (budget)");
                    return budgetMsg;
                }

                if (_policyGate.ConsecutiveFailures >= MAX_CONSECUTIVE_EXECUTECODE_FAILURES)
                {
                    ZexusLogger.Warn($"[BUDGET] {MAX_CONSECUTIVE_EXECUTECODE_FAILURES} consecutive ExecuteCode failures — stopping");
                    var failMsg = new ChatMessage
                    {
                        Role = MessageRole.Assistant,
                        Content = $"ExecuteCode has failed {MAX_CONSECUTIVE_EXECUTECODE_FAILURES} times in a row. " +
                                  "This usually indicates an API pattern that isn't working. " +
                                  "I'll stop retrying. Please describe what you need differently, or try a different approach."
                    };
                    session.AddMessage(failMsg);
                    OnStatusChanged?.Invoke("Complete (failures)");
                    return failMsg;
                }

                OnStatusChanged?.Invoke(iteration == 1 ? "Thinking..." : "Processing...");

                // ── API call with automatic retry on rate limit (exponential backoff) ──
                ApiResponse response = null;
                int maxRetries = 3;
                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    // Append working memory to system prompt for per-turn context
                    var systemPrompt = SystemPromptBuilder.Build();
                    var wmBlock = _sessionContext.Memory.ToCompactString();
                    if (!string.IsNullOrEmpty(wmBlock))
                        systemPrompt += "\n\n" + wmBlock;

                    // Append compilation error memory so LLM avoids repeating mistakes
                    if (_compilationErrorMemory.Count > 0)
                    {
                        systemPrompt += "\n\n[Known Failed Patterns — do NOT repeat these]\n" +
                            string.Join("\n", _compilationErrorMemory.Select((e, i) => $"{i + 1}. {e}"));
                    }

                    // Inject denial guidance if user has repeatedly rejected proposals
                    var denialGuidance = GetDenialGuidance();
                    if (denialGuidance != null)
                        systemPrompt += "\n\n" + denialGuidance;

                    response = await client.SendMessageStreamingAsync(
                        conversationMessages,
                        systemPrompt,
                        toolDefinitions,
                        delta => { finalText.Append(delta); OnStreamingText?.Invoke(delta); },
                        cancellationToken
                    );

                    // If success or non-rate-limit error, break out
                    bool isRateLimit = !response.Success && response.Error != null
                        && (response.Error.Contains("429") || response.Error.Contains("rate_limit")
                            || response.Error.Contains("overloaded") || response.Error.Contains("529"));

                    if (!isRateLimit || attempt == maxRetries)
                        break;

                    // Rate limited — wait with exponential backoff (10s, 30s, 60s)
                    int waitSeconds = attempt == 0 ? 10 : attempt == 1 ? 30 : 60;
                    ZexusLogger.Info($"Rate limited (attempt {attempt + 1}/{maxRetries}), waiting {waitSeconds}s before retry...");
                    OnStatusChanged?.Invoke($"Rate limited — retrying in {waitSeconds}s...");

                    await System.Threading.Tasks.Task.Delay(waitSeconds * 1000, cancellationToken);
                }

                ZexusLogger.Info($"API Response: Success={response.Success}, StopReason={response.StopReason}, ToolCalls={response.ToolCalls.Count}, Text={response.Text?.Length ?? 0} chars");

                if (!response.Success)
                {
                    // All retries exhausted for rate limit, or other API error
                    if (response.Error != null && (response.Error.Contains("429") || response.Error.Contains("rate_limit")
                        || response.Error.Contains("overloaded") || response.Error.Contains("529")))
                    {
                        _sessionContext.RecordError("rate_limit", response.Error, true);
                        return new ChatMessage { Role = MessageRole.System, Content = FormatRateLimitError() };
                    }
                    // Check for corporate proxy block in API response
                    var respErr = response.Error ?? "";
                    bool proxyBlock = respErr.Contains("Zscaler") || respErr.Contains("zscaler")
                        || respErr.Contains("Website blocked") || respErr.Contains("Not allowed to browse")
                        || respErr.Contains("Palo Alto") || respErr.Contains("Forcepoint")
                        || (respErr.Contains("403") && (respErr.Contains("<!DOCTYPE") || respErr.Contains("<html")));
                    if (proxyBlock)
                    {
                        _sessionContext.RecordError("proxy_block", "Corporate firewall blocked API request", false);
                        return new ChatMessage
                        {
                            Role = MessageRole.System,
                            Content = "**Connection blocked by corporate firewall**\n\n" +
                                "Your network security system (e.g. Zscaler) is blocking requests to the AI provider's API.\n\n" +
                                "**Solutions:**\n" +
                                "1. Switch to a personal network (mobile hotspot or home WiFi)\n" +
                                "2. Ask your IT department to whitelist the API endpoint\n" +
                                "3. Use a VPN that bypasses the corporate proxy\n\n" +
                                "This is a network restriction — not a Zexus or API key issue."
                        };
                    }
                    return new ChatMessage { Role = MessageRole.System, Content = $"Error: {response.Error}" };
                }

                // Auto-continue when text response is truncated by max_tokens
                if (response.ToolCalls.Count == 0 && IsMaxTokensTruncated(response.StopReason) && !cancellationToken.IsCancellationRequested)
                {
                    int continuationCount = 0;
                    while (IsMaxTokensTruncated(response.StopReason) && continuationCount < MAX_CONTINUATIONS)
                    {
                        continuationCount++;
                        ZexusLogger.Info($"[AUTO-CONTINUE] Response truncated by max_tokens, continuation {continuationCount}/{MAX_CONTINUATIONS}");
                        OnStatusChanged?.Invoke($"Continuing... ({continuationCount}/{MAX_CONTINUATIONS})");

                        // Add the partial response and a continuation prompt
                        conversationMessages.Add(client.FormatAssistantMessage(response.Text, new List<ToolUse>()));
                        conversationMessages.Add(new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = "Continue from where you left off."
                        });

                        var systemPrompt2 = SystemPromptBuilder.Build();
                        var wmBlock2 = _sessionContext.Memory.ToCompactString();
                        if (!string.IsNullOrEmpty(wmBlock2))
                            systemPrompt2 += "\n\n" + wmBlock2;

                        response = await client.SendMessageStreamingAsync(
                            conversationMessages,
                            systemPrompt2,
                            toolDefinitions,
                            delta => { finalText.Append(delta); OnStreamingText?.Invoke(delta); },
                            cancellationToken
                        );

                        if (!response.Success) break;
                        if (response.ToolCalls.Count > 0) break; // LLM decided to call a tool, exit continuation
                    }
                }

                // No tool calls — check for hallucination before exiting
                if (response.ToolCalls.Count == 0)
                {
                    // ── Hallucination guardrail ──
                    // If this is iteration 1 (first LLM response with no prior tool calls),
                    // and the text claims to have performed actions, the LLM fabricated results.
                    // Retry once with a correction prompt.
                    if (iteration == 1 && allToolCalls.Count == 0 && DetectFabricatedActions(finalText.ToString()))
                    {
                        ZexusLogger.Warn("[GUARDRAIL] Hallucination detected — LLM claimed tool execution without calling any tools. Retrying...");
                        _reporter.RecordFriction("hallucination_detected", "LLM claimed action results without tool calls");

                        // Inject the hallucinated response + correction as conversation context
                        conversationMessages.Add(client.FormatAssistantMessage(finalText.ToString(), new List<ToolUse>()));
                        conversationMessages.Add(new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = "[SYSTEM] You did NOT actually call any tools. Your previous response fabricated results. " +
                                          "You MUST call tools (SearchElements, ColorElements, ExecuteCode, etc.) to perform Revit operations. " +
                                          "Please try again and actually call the required tools."
                        });

                        // Clear text buffer and retry
                        finalText.Clear();
                        OnStreamingText?.Invoke("\n\n");
                        continue;
                    }

                    // ── Confirmation stall guardrail (escalating) ──
                    // If the user confirmed but LLM returned text instead of tool calls,
                    // retry up to 2 times with escalating correction messages.
                    if (iteration <= 2 && allToolCalls.Count == 0 && DetectConfirmationNudge(conversationMessages))
                    {
                        ZexusLogger.Warn($"[GUARDRAIL] Confirmation stall (attempt {iteration}) — user confirmed but LLM returned text instead of tool calls. Retrying...");
                        _reporter.RecordFriction("confirmation_stall", $"LLM did not call tools after user confirmation (attempt {iteration})");

                        string correction;
                        if (iteration == 1)
                        {
                            correction = "[SYSTEM] CRITICAL: The user already confirmed. You responded with text but did NOT call any tools. " +
                                          "This is WRONG. You MUST call ExecuteCode (or the appropriate tool) RIGHT NOW. " +
                                          "Do not explain. Do not ask. CALL A TOOL.";
                        }
                        else // iteration == 2, final attempt
                        {
                            correction = "[SYSTEM] FINAL WARNING: You have now failed TWICE to call a tool after user confirmation. " +
                                          "The user confirmed the plan. There is NO additional safety check needed. " +
                                          "There is NO 'system confirmation' or 'policy check' — those do not exist. " +
                                          "You are in Phase 2 (EXECUTE). The ONLY valid action is calling ExecuteCode with a Transaction. " +
                                          "Generate the C# code with Transaction and call ExecuteCode NOW.";
                        }

                        conversationMessages.Add(client.FormatAssistantMessage(finalText.ToString(), new List<ToolUse>()));
                        conversationMessages.Add(new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = correction
                        });

                        finalText.Clear();
                        OnStreamingText?.Invoke("\n\n");
                        continue;
                    }

                    // ── Read-only ExecuteCode after confirmation guardrail (Gemini variant) ──
                    // Gemini sometimes calls ExecuteCode with read-only code (no Transaction)
                    // after user confirmation, effectively previewing instead of writing.
                    if (_lastIterationWasReadOnlyEC && iteration <= 3)
                    {
                        ZexusLogger.Warn("[GUARDRAIL] Read-only ExecuteCode after confirmation — LLM is previewing instead of writing");
                        _reporter.RecordFriction("preview_after_confirm", "ExecuteCode had no Transaction after user confirmation");
                        _lastIterationWasReadOnlyEC = false;

                        conversationMessages.Add(new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = "[SYSTEM] Your ExecuteCode just ran READ-ONLY code (no Transaction). " +
                                          "The user already confirmed the write. You MUST now call ExecuteCode again " +
                                          "with a Transaction that COMMITS the changes. Do NOT preview again."
                        });
                    }

                    var assistantMsg = new ChatMessage
                    {
                        Role = MessageRole.Assistant,
                        Content = finalText.ToString(),
                        ToolCalls = allToolCalls.Count > 0 ? allToolCalls : null
                    };
                    session.AddMessage(assistantMsg);

                    // Mark task as completed
                    _sessionContext.CompleteTask(finalText.ToString());

                    // Record turn end for session report
                    _reporter.RecordTurnEnd(finalText.ToString());

                    OnStatusChanged?.Invoke("Complete");
                    return assistantMsg;
                }

                // Fire reasoning text for ThinkingChain (the LLM text before tool calls)
                var reasoningText = finalText.ToString().Trim();
                if (!string.IsNullOrEmpty(reasoningText))
                {
                    // Truncate to ~150 chars for display
                    var displayReasoning = reasoningText.Length > 150
                        ? reasoningText.Substring(0, 147) + "..."
                        : reasoningText;
                    OnReasoningForThinkingChain?.Invoke(displayReasoning);
                }

                // Execute tools and collect results
                var toolCallResults = new List<ToolCallResult>();

                foreach (var toolUse in response.ToolCalls)
                {
                    // Check cancellation before each tool execution
                    cancellationToken.ThrowIfCancellationRequested();

                    // ── Pre-execution: resolve *_ref parameters ──
                    Dictionary<string, object> resolvedInput;
                    try
                    {
                        resolvedInput = ResolveRefParameters(toolUse.Input);
                    }
                    catch (RefExpiredException refEx)
                    {
                        ZexusLogger.Warn($"RefResolver: expired ref — {refEx.Message}");
                        var failResult = ToolResult.Fail(
                            $"Reference '{refEx.RefKey}' has expired (FIFO limit reached). " +
                            "Please re-run the original search/query to get fresh element IDs, then retry.");
                        var failToolCall = new ToolCall
                        {
                            Id = toolUse.Id,
                            Name = toolUse.Name,
                            Input = toolUse.Input,
                            Result = failResult,
                            Status = ToolCallStatus.Failed
                        };
                        allToolCalls.Add(failToolCall);
                        OnToolCompleted?.Invoke(toolUse.Name, failResult, 0);
                        _sessionContext.RecordToolCall(toolUse.Name, toolUse.Input, null, false);
                        _sessionContext.AddStep(toolUse.Name, "failed");
                        _sessionContext.UpdateCurrentStep("failed", failResult.Message);

                        UsageTracker.RecordToolCall(toolUse.Name, toolUse.Input, false,
                            errorCategory: "ref_expired",
                            errorSnippet: refEx.Message);

                        toolCallResults.Add(new ToolCallResult
                        {
                            ToolCallId = toolUse.Id,
                            ToolName = toolUse.Name,
                            Input = toolUse.Input,
                            ResultJson = JsonSerializer.Serialize(new { success = false, message = failResult.Message })
                        });
                        continue; // skip to next tool call
                    }

                    ZexusLogger.Info($"Executing tool: {toolUse.Name} with input: {JsonSerializer.Serialize(resolvedInput)}");

                    // ── PolicyGate: check execution policy before running ──
                    var policyVerdict = _policyGate.Evaluate(toolUse.Name, resolvedInput);
                    if (policyVerdict.Action != PolicyAction.Allow)
                    {
                        ZexusLogger.Warn($"PolicyGate blocked {toolUse.Name}: {policyVerdict.Action}");
                        _reporter.RecordFriction("policy_block", $"PolicyGate blocked: {policyVerdict.Message}");
                        var policyResult = ToolResult.Fail(policyVerdict.Message);
                        policyResult.Data["policy_blocked"] = true;
                        policyResult.Data["policy_rule"] = policyVerdict.RuleName;
                        policyResult.Data["policy_action"] = policyVerdict.Action.ToString();

                        // Track the blocked operation so user confirmation can unblock it
                        var desc = resolvedInput != null && resolvedInput.ContainsKey("description")
                            ? resolvedInput["description"]?.ToString() ?? "" : "";
                        var pendingKey = $"{policyVerdict.RuleName}:{desc}";
                        if (!_pendingPolicyConfirmations.Contains(pendingKey))
                            _pendingPolicyConfirmations.Add(pendingKey);

                        var policyToolCall = new ToolCall
                        {
                            Id = toolUse.Id, Name = toolUse.Name,
                            Input = resolvedInput, Result = policyResult,
                            Status = ToolCallStatus.Failed
                        };
                        allToolCalls.Add(policyToolCall);
                        OnToolCompleted?.Invoke(toolUse.Name, policyResult, 0);

                        toolCallResults.Add(new ToolCallResult
                        {
                            ToolCallId = toolUse.Id, ToolName = toolUse.Name,
                            Input = toolUse.Input,
                            ResultJson = JsonSerializer.Serialize(new { success = false, message = policyVerdict.Message,
                                policy_action = policyVerdict.Action.ToString() })
                        });
                        continue; // skip actual execution — LLM will see the policy message
                    }

                    OnStatusChanged?.Invoke($"Running {toolUse.Name}...");
                    OnToolExecuting?.Invoke(toolUse.Name, resolvedInput);

                    var toolCall = new ToolCall
                    {
                        Id = toolUse.Id,
                        Name = toolUse.Name,
                        Input = resolvedInput,
                        Status = ToolCallStatus.Executing
                    };

                    ToolResult result;
                    var toolStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var resultObj = await App.RevitEventHandler.ExecuteToolAsync(toolUse.Name, resolvedInput);
                        result = resultObj as ToolResult ?? ToolResult.Fail("Tool returned invalid result");
                        toolStopwatch.Stop();
                        ZexusLogger.Info($"Tool {toolUse.Name} completed: Success={result.Success}, Message={result.Message}");

                        // Track usage data with error classification
                        string errCategory = null;
                        string errSnippet = null;
                        if (!result.Success)
                        {
                            errSnippet = result.Message;
                            if (result.Data != null && result.Data.ContainsKey("failure_type"))
                                errCategory = result.Data["failure_type"]?.ToString();
                            else if (result.Message?.StartsWith("Compilation failed") == true)
                                errCategory = "compilation";
                            else if (result.Message?.StartsWith("Runtime error") == true)
                                errCategory = "runtime";
                            else
                                errCategory = "tool_error";
                        }
                        UsageTracker.RecordToolCall(toolUse.Name, toolUse.Input, result.Success,
                            errorCategory: errCategory,
                            errorSnippet: errSnippet,
                            resultMessage: result.Message,
                            durationMs: toolStopwatch.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        toolStopwatch.Stop();
                        ZexusLogger.Error($"Tool execution exception: {ex.Message}");
                        result = ToolResult.Fail($"Tool execution error: {ex.Message}");

                        UsageTracker.RecordToolCall(toolUse.Name, toolUse.Input, false,
                            errorType: ex.GetType().Name,
                            errorCategory: "exception",
                            errorSnippet: ex.Message,
                            durationMs: toolStopwatch.ElapsedMilliseconds);
                    }

                    toolCall.Result = result;
                    toolCall.Status = result.Success ? ToolCallStatus.Completed : ToolCallStatus.Failed;
                    allToolCalls.Add(toolCall);

                    OnToolCompleted?.Invoke(toolUse.Name, result, toolStopwatch.ElapsedMilliseconds);

                    // ── PolicyGate: track ExecuteCode success/failure ──
                    if (toolUse.Name == "ExecuteCode")
                    {
                        _policyGate.TrackExecuteCode(result.Success);

                        // Accumulate compilation error patterns to prevent LLM from repeating them
                        if (!result.Success && result.Data != null &&
                            result.Data.ContainsKey("failure_type") &&
                            result.Data["failure_type"]?.ToString() == "compilation")
                        {
                            var errorSnippet = ExtractCompilationErrorPattern(result.Message);
                            if (errorSnippet != null && _compilationErrorMemory.Count < MAX_ERROR_MEMORY)
                            {
                                if (!_compilationErrorMemory.Contains(errorSnippet))
                                    _compilationErrorMemory.Add(errorSnippet);
                            }
                        }
                    }

                    _sessionContext.RecordToolCall(toolUse.Name, toolUse.Input, result.Data, result.Success);
                    _sessionContext.AddStep($"{toolUse.Name}", result.Success ? "completed" : "failed");
                    _sessionContext.UpdateCurrentStep(result.Success ? "completed" : "failed", result.Message);

                    if (result.Success && result.Data != null)
                    {
                        if (result.Data.ContainsKey("elements") || result.Data.ContainsKey("element_ids"))
                            _sessionContext.CacheData($"{toolUse.Name}_result", result.Data);
                        if (result.Data.ContainsKey("sheets"))
                            _sessionContext.CacheData("sheets_data", result.Data);
                        if (result.Data.ContainsKey("parameters"))
                        {
                            var elemId = resolvedInput != null && resolvedInput.ContainsKey("element_id")
                                ? resolvedInput["element_id"]?.ToString() ?? "unknown"
                                : "unknown";
                            _sessionContext.CacheData($"params_{elemId}", result.Data);
                        }
                    }

                    // ── Post-execution: Enrich result with result_ref, filters_echo, next_actions ──
                    if (result.Success && result.Data != null)
                    {
                        EnrichToolResult(toolUse.Name, resolvedInput, result);
                    }

                    // ── Record to SessionReporter (AI facts pack) ──
                    string reportRef = result.Data?.ContainsKey("result_ref") == true
                        ? result.Data["result_ref"]?.ToString() : null;
                    string reportErrType = null;
                    if (!result.Success)
                    {
                        reportErrType = result.Data?.ContainsKey("failure_type") == true
                            ? result.Data["failure_type"]?.ToString() : "tool_error";
                    }
                    _reporter.RecordToolCall(
                        toolUse.Name,
                        toolUse.Input,        // input_original (before ref resolution)
                        resolvedInput,         // input_resolved (after ref resolution)
                        result.Success,
                        result.Message,
                        result.Warning,
                        reportErrType,
                        result.Data,
                        reportRef,
                        toolStopwatch.ElapsedMilliseconds);

                    // Serialize result for provider message formatting
                    var resultJson = JsonSerializer.Serialize(result, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = false
                    });

                    toolCallResults.Add(new ToolCallResult
                    {
                        ToolCallId = toolUse.Id,
                        ToolName = toolUse.Name,
                        Input = toolUse.Input,
                        ResultJson = resultJson
                    });
                }

                // Track whether this iteration only produced read-only ExecuteCode calls (Gemini variant)
                bool allReadOnly = toolCallResults.Count > 0 && toolCallResults.All(r =>
                {
                    if (r.ToolName != "ExecuteCode") return false;
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.ResultJson);
                        if (parsed != null && parsed.TryGetValue("data", out var dataEl))
                        {
                            var dataDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dataEl.GetRawText());
                            if (dataDict != null && dataDict.TryGetValue("has_transaction", out var htVal))
                                return !htVal.GetBoolean();
                        }
                    }
                    catch { }
                    return false;
                });

                // RC-5: also trip the read-only guard when the user gave a
                // fresh execution instruction (not just a confirmation nudge).
                if (allReadOnly && (DetectConfirmationNudge(conversationMessages) || DetectExecutionIntent(conversationMessages)))
                {
                    _lastIterationWasReadOnlyEC = true;
                }

                // Provider formats assistant + tool result messages in its own wire format
                conversationMessages.Add(client.FormatAssistantMessage(response.Text, response.ToolCalls));
                var resultMessages = client.FormatToolResultMessages(toolCallResults);
                foreach (var msg in resultMessages)
                    conversationMessages.Add(msg);

                // Clear the text buffer for next response
                finalText.Clear();
            }
            // Loop only exits via return (no tool calls) or exception (cancellation/error)
        }

        // ───────────────────────────────────────────────────────
        // Message building
        // ───────────────────────────────────────────────────────

        /// <summary>
        /// Builds the API message list with a sliding window to prevent token exhaustion.
        /// Keeps the first user message (for context) + the most recent N message pairs.
        /// Estimates ~4 chars per token; reserves space for system prompt (~4K tokens)
        /// and output (max_tokens). The input budget is roughly 200K - system - output.
        /// </summary>
        private List<Dictionary<string, object>> BuildApiMessages(Session session)
        {
            var allMessages = new List<Dictionary<string, object>>();

            foreach (var msg in session.Messages)
            {
                if (msg.Role == MessageRole.System) continue;

                allMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = msg.Role == MessageRole.User ? "user" : "assistant",
                    ["content"] = msg.Content ?? ""
                });
            }

            // Sliding window: keep total estimated tokens under budget
            // Budget = 200K context - ~5K system prompt - 16K max_tokens = ~179K input tokens
            // Use conservative 3 chars/token estimate
            const int maxInputTokens = 150_000;
            const int charsPerToken = 3;
            const int maxInputChars = maxInputTokens * charsPerToken;

            if (allMessages.Count <= 2)
                return allMessages; // Nothing to trim

            // Calculate total character count
            int totalChars = 0;
            foreach (var m in allMessages)
                totalChars += ((string)m["content"]).Length;

            if (totalChars <= maxInputChars)
                return allMessages; // Fits within budget

            // Trim from the middle: keep first message + as many recent messages as fit
            var result = new List<Dictionary<string, object>>();

            // Always keep the first user message for original context
            var firstMsg = allMessages[0];
            int budget = maxInputChars - ((string)firstMsg["content"]).Length;
            result.Add(firstMsg);

            // Add a context-trimmed notice
            result.Add(new Dictionary<string, object>
            {
                ["role"] = "user",
                ["content"] = "[Earlier conversation history was trimmed to stay within context limits]"
            });
            result.Add(new Dictionary<string, object>
            {
                ["role"] = "assistant",
                ["content"] = "Understood. I'll continue based on the recent conversation context."
            });
            budget -= 200; // Approximate tokens for the notice pair

            // Walk backwards from most recent, collecting messages that fit
            var recentMessages = new List<Dictionary<string, object>>();
            for (int i = allMessages.Count - 1; i >= 1; i--)
            {
                int msgLen = ((string)allMessages[i]["content"]).Length;
                if (budget - msgLen < 0) break;
                budget -= msgLen;
                recentMessages.Insert(0, allMessages[i]);
            }

            result.AddRange(recentMessages);

            ZexusLogger.Info($"BuildApiMessages: trimmed {allMessages.Count} messages to {result.Count} (saved ~{(totalChars - maxInputChars) / charsPerToken} tokens)");
            return result;
        }

        // ═══════════════════════════════════════════════════════
        // Middleware: RefResolver (pre-execution)
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Detects whether the LLM's text response fabricates tool execution results
        /// without actually calling any tools. Checks for action-claiming patterns
        /// like "I've created", "successfully", combined with tool-specific keywords.
        /// </summary>
        private bool DetectFabricatedActions(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 30) return false;

            var lower = text.ToLowerInvariant();

            // Must contain a success/completion claim
            bool hasCompletionClaim =
                lower.Contains("已经") || lower.Contains("已成功") || lower.Contains("创建完成") ||
                lower.Contains("配置完毕") || lower.Contains("设置完成") || lower.Contains("执行完成") ||
                lower.Contains("successfully") || lower.Contains("i've created") ||
                lower.Contains("i have created") || lower.Contains("completed") ||
                lower.Contains("done!") || lower.Contains("finished");

            if (!hasCompletionClaim) return false;

            // Must also reference a concrete Revit action (tool-like operation)
            bool hasActionReference =
                lower.Contains("3d view") || lower.Contains("3d视图") ||
                lower.Contains("schedule") || lower.Contains("明细表") ||
                lower.Contains("视图") || lower.Contains("筛选器") ||
                lower.Contains("created a new") || lower.Contains("创建了") ||
                lower.Contains("activated") || lower.Contains("切换到") ||
                lower.Contains("category visibility") || lower.Contains("类别") ||
                lower.Contains("隐藏了") || lower.Contains("隔离") ||
                lower.Contains("isolated") || lower.Contains("hidden");

            if (!hasActionReference) return false;

            ZexusLogger.Warn($"[GUARDRAIL] Fabrication pattern matched in response ({text.Length} chars)");
            return true;
        }

        /// <summary>
        /// Check if the last user message in conversation history contains the
        /// confirmation execution nudge injected by AgentService.
        /// </summary>
        private bool DetectConfirmationNudge(List<Dictionary<string, object>> messages)
        {
            // Walk backwards to find the last user message
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].TryGetValue("role", out var role) && "user".Equals(role?.ToString()))
                {
                    var content = messages[i].TryGetValue("content", out var c) ? c?.ToString() : "";
                    return content != null && content.Contains("EXECUTE the plan NOW");
                }
            }
            return false;
        }

        /// <summary>
        /// RC-5 (phantom-success): broader trigger for the read-only-after-
        /// instruction guardrail. The original guard required an EXECUTE-the-plan
        /// nudge from AgentService (i.e., user message classified as confirmation),
        /// which missed cases where the user gave a fresh execution instruction
        /// like "添加 fields 到 schedule" — not a confirmation, but clearly a write.
        ///
        /// Detects whether the most recent real user message in the conversation
        /// (skipping pure [SYSTEM] correction injections from this loop) contains
        /// an execution verb. If so, and the iteration just produced only
        /// read-only ExecuteCode, the read-only guard fires and tells the LLM
        /// to retry with a Transaction.
        ///
        /// Walk direction: BACKWARD — mirrors DetectConfirmationNudge's pattern
        /// and ensures the *current turn's* request is checked rather than
        /// turn 1's request (which would happen if walking forward, since
        /// conversationMessages spans the whole session).
        /// </summary>
        private bool DetectExecutionIntent(List<Dictionary<string, object>> messages)
        {
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (!messages[i].TryGetValue("role", out var role)) continue;
                if (!"user".Equals(role?.ToString())) continue;

                var content = messages[i].TryGetValue("content", out var c) ? c?.ToString() : "";
                if (content == null) continue;

                // Skip pure [SYSTEM] correction injections this loop adds itself
                // (e.g. "[SYSTEM] You did NOT actually call any tools..."). Real
                // user messages with appended "[SYSTEM: ... has confirmed ...]"
                // nudges have the original user text earlier in the string and
                // do not match the "[SYSTEM]" closing-bracket prefix.
                if (content.Contains("[SYSTEM]")) continue;

                var lower = content.ToLowerInvariant();
                return lower.Contains("添加") || lower.Contains("修复") || lower.Contains("修改") ||
                       lower.Contains("写入") || lower.Contains("创建") || lower.Contains("删除") ||
                       lower.Contains("移动") || lower.Contains("设置") || lower.Contains("应用") ||
                       lower.Contains("fix") || lower.Contains("add") || lower.Contains("create") ||
                       lower.Contains("move") || lower.Contains("set") || lower.Contains("apply") ||
                       lower.Contains("write") || lower.Contains("update") || lower.Contains("remove");
            }
            return false;
        }

        /// <summary>
        /// Resolve *_ref parameters by expanding them from the ResultRefStore.
        /// e.g., element_ids_ref:"search_walls_42" → element_ids: [123, 456, ...]
        /// Also resolves schedule_id from schedule_id_ref.
        /// Returns a new dictionary with refs expanded (original dict is not modified).
        /// </summary>
        private Dictionary<string, object> ResolveRefParameters(Dictionary<string, object> input)
        {
            if (input == null) return input;

            var resolved = new Dictionary<string, object>(input);
            var refStore = _sessionContext.RefStore;

            // Map: *_ref param → target data key → target param name
            var refMappings = new[]
            {
                ("element_ids_ref", "element_ids", "element_ids"),
                ("sheet_numbers_ref", "sheets", "sheet_numbers"),  // sheets list → extract sheet_number
                ("view_ids_ref", "views", "view_ids"),
            };

            foreach (var (refParam, dataKey, targetParam) in refMappings)
            {
                if (!resolved.ContainsKey(refParam)) continue;
                var refKey = resolved[refParam]?.ToString();
                if (string.IsNullOrEmpty(refKey)) continue;

                var data = refStore.ResolveDataKey(refKey, dataKey);
                if (data != null)
                {
                    // For sheets, extract sheet_numbers from the sheets list
                    if (dataKey == "sheets" && data is System.Collections.IEnumerable sheets)
                    {
                        var numbers = new List<object>();
                        foreach (var sheet in sheets)
                        {
                            if (sheet is Dictionary<string, object> sheetDict &&
                                sheetDict.TryGetValue("sheet_number", out var num))
                                numbers.Add(num);
                        }
                        resolved[targetParam] = numbers;
                    }
                    else
                    {
                        resolved[targetParam] = data;
                    }
                    resolved.Remove(refParam);
                    ZexusLogger.Info($"RefResolver: expanded {refParam}={refKey} → {targetParam}");
                }
                else
                {
                    ZexusLogger.Warn($"RefResolver: ref not found for {refParam}={refKey}");
                    throw new RefExpiredException(refParam, refKey);
                }
            }

            // Special case: schedule_id_ref → resolve to schedule_id
            if (resolved.ContainsKey("schedule_id_ref"))
            {
                var refKey = resolved["schedule_id_ref"]?.ToString();
                if (!string.IsNullOrEmpty(refKey))
                {
                    var schedId = refStore.ResolveDataKey(refKey, "schedule_id");
                    if (schedId != null)
                    {
                        resolved["schedule_id"] = schedId;
                        resolved.Remove("schedule_id_ref");
                        ZexusLogger.Info($"RefResolver: expanded schedule_id_ref={refKey} → schedule_id={schedId}");
                    }
                }
            }

            return resolved;
        }

        // ═══════════════════════════════════════════════════════
        // Middleware: Enrich (post-execution)
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Enrich a successful tool result with result_ref, filters_echo, and next_actions.
        /// Stores large payloads in RefStore, updates WorkingMemory.
        /// Called as middleware — tools themselves don't need to know about this.
        /// </summary>
        private void EnrichToolResult(string toolName, Dictionary<string, object> input, ToolResult result)
        {
            try
            {
                var data = result.Data;
                if (data == null) return;

                // Build filters_echo from input parameters (echo back what was asked)
                var filtersEcho = BuildFiltersEcho(toolName, input);
                if (filtersEcho != null && filtersEcho.Count > 0)
                    data["filters_echo"] = filtersEcho;

                // Store in RefStore and get result_ref
                string resultRef = _sessionContext.RefStore.Store(toolName, data, filtersEcho);
                data["result_ref"] = resultRef;

                // Build next_actions based on tool type
                var nextActions = BuildNextActions(toolName, data);
                if (nextActions != null && nextActions.Count > 0)
                    data["next_actions"] = nextActions;

                // Update working memory
                _sessionContext.Memory.UpdateFromToolResult(toolName, resultRef, data);
            }
            catch (Exception ex)
            {
                ZexusLogger.Warn($"EnrichToolResult error: {ex.Message}");
                // Non-fatal — don't break tool execution
            }
        }

        /// <summary>
        /// Extract relevant input parameters as a compact echo.
        /// </summary>
        private Dictionary<string, object> BuildFiltersEcho(string toolName, Dictionary<string, object> input)
        {
            if (input == null) return null;

            var echo = new Dictionary<string, object>();
            // Only echo meaningful filter/query parameters, not all inputs
            var filterKeys = new HashSet<string>
            {
                "category", "family", "type", "level", "parameter_name", "parameter_value",
                "family_name", "type_name", "level_name",  // SearchElements actual param names
                "mode", "view_type", "view_name", "schedule_name", "schedule_id",
                "field_name", "format", "sheet_numbers", "temporary"
            };

            foreach (var kv in input)
            {
                if (filterKeys.Contains(kv.Key) && kv.Value != null)
                    echo[kv.Key] = kv.Value;
            }

            return echo.Count > 0 ? echo : null;
        }

        /// <summary>
        /// Suggest next logical actions based on what just happened.
        /// Returns compact tool name + key param hints.
        /// </summary>
        private List<string> BuildNextActions(string toolName, Dictionary<string, object> data)
        {
            var actions = new List<string>();

            switch (toolName)
            {
                case "SearchElements":
                    actions.Add("SelectElements(element_ids_ref) to highlight results");
                    actions.Add("IsolateElements(element_ids_ref) to focus view");
                    if (data.TryGetValue("has_more", out var hasMore) && hasMore is bool hm && hm
                        && data.TryGetValue("next_offset", out var nextOff))
                        actions.Add($"SearchElements(same filters, offset={nextOff}) to get next batch");
                    break;

                case "CreateSchedule":
                    actions.Add("AddScheduleField(schedule_id) to add columns");
                    actions.Add("ModifyScheduleSort(schedule_id) to sort/group");
                    actions.Add("ActivateView(view_name) to open the schedule");
                    break;

                case "GetModelOverview":
                    actions.Add("SearchElements to find specific elements");
                    break;

                case "SelectElements":
                    actions.Add("IsolateElements to focus view on selection");
                    actions.Add("SetElementParameter to modify element");
                    break;
            }

            return actions.Count > 0 ? actions : null;
        }

        // ───────────────────────────────────────────────────────
        // Error helpers
        // ───────────────────────────────────────────────────────

        /// <summary>
        /// Extract a compact error pattern from a compilation error message.
        /// Returns a short string like "ElementId.IntegerValue does not exist in Revit 2025+"
        /// that gets injected into subsequent prompts to prevent the LLM from repeating the mistake.
        /// </summary>
        private string ExtractCompilationErrorPattern(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage)) return null;

            // Extract the first meaningful error line (skip "Compilation failed" prefix)
            var lines = errorMessage.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // Skip meta lines
                if (trimmed.StartsWith("Compilation failed")) continue;
                if (trimmed.Length < 10) continue;

                // Extract the core error: strip line numbers like "Line 5: " or "(12,34): error CS1234:"
                var cleaned = Regex.Replace(trimmed, @"^(Line \d+:\s*|\(\d+,\d+\):\s*(error|warning) CS\d+:\s*)", "");

                if (cleaned.Length > 10)
                {
                    // Cap at 200 chars for prompt injection
                    return cleaned.Length > 200 ? cleaned.Substring(0, 200) : cleaned;
                }
            }

            return null;
        }

        /// <summary>
        /// Format a user-friendly rate limit error message
        /// </summary>
        /// <summary>
        /// Check if the API response was truncated due to max output tokens.
        /// Handles different provider stop_reason strings.
        /// </summary>
        private static bool IsMaxTokensTruncated(string stopReason)
        {
            if (string.IsNullOrEmpty(stopReason)) return false;
            var lower = stopReason.ToLowerInvariant();
            return lower == "max_tokens"          // Anthropic
                || lower == "length"              // OpenAI
                || lower == "max_output_tokens"   // Alternative
                || lower.Contains("max_tokens");  // Catch-all
        }

        internal string FormatRateLimitError()
        {
            var completedSteps = _sessionContext.CurrentTask?.Steps?.Count ?? 0;
            var cachedDataCount = _sessionContext.DataCache?.Count ?? 0;

            var msg = new System.Text.StringBuilder();
            msg.AppendLine("⚠️ **API Rate Limit**");
            msg.AppendLine();
            msg.AppendLine("API rate limit reached. This typically happens with free-tier or low-tier API keys.");
            msg.AppendLine();
            msg.AppendLine("🔑 **Why this matters:** Zexus is an AI agent that chains multiple tool calls per request. A single task may require 3–8 API calls. Free-tier keys have very low rate limits (e.g. 5 requests/min) which are insufficient for agent workflows. A **paid API key (Tier 1+)** is strongly recommended for a usable experience.");
            msg.AppendLine();

            if (completedSteps > 0 || cachedDataCount > 0)
            {
                msg.AppendLine("✅ **Progress saved:**");
                msg.AppendLine($"- {completedSteps} steps completed");
                msg.AppendLine($"- {cachedDataCount} data sets cached");
                msg.AppendLine();
                msg.AppendLine("💡 Wait about 1 minute, then send **\"continue\"** to resume from where you left off.");
            }
            else
            {
                msg.AppendLine("💡 Please wait about 1 minute and try again.");
            }

            return msg.ToString();
        }

    }

    /// <summary>
    /// Thrown when a *_ref parameter references a ref key that has been evicted from the FIFO store.
    /// Caught in the tool execution loop to return a clear error to the LLM.
    /// </summary>
    internal class RefExpiredException : Exception
    {
        public string RefParam { get; }
        public string RefKey { get; }

        public RefExpiredException(string refParam, string refKey)
            : base($"Reference '{refKey}' (from {refParam}) has expired from the store. Please re-run the original search/query to get fresh results.")
        {
            RefParam = refParam;
            RefKey = refKey;
        }
    }
}
