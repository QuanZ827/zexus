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
    public class AgentService : IDisposable
    {
        private ILlmClient _client;
        private readonly ToolRegistry _toolRegistry;
        private Session _currentSession;
        private List<ToolDefinition> _toolDefinitions;
        
        // Session context for progress preservation and resume capability
        private readonly SessionContext _sessionContext = SessionContext.Instance;

        // Session reporter for AI-consumable facts pack export
        private readonly SessionReporter _reporter = SessionReporter.Instance;

        // Execution policy layer — enforces write safety, scope checks, batch thresholds
        private readonly PolicyGate _policyGate = new PolicyGate();

        // Tool loop controller — ReAct loop + middleware (extracted in Wave 3b)
        private readonly ToolLoopController _toolLoop;

        // Consecutive confirmation counter — detects confirmation spirals
        private int _consecutiveConfirmationCount = 0;

        public event Action<string> OnStreamingText;
        public event Action<string, Dictionary<string, object>> OnToolExecuting;
        public event Action<string> OnStatusChanged;
        public event Action<string> OnProcessingStarted;
        public event Action<string, ToolResult, long> OnToolCompleted;
        public event Action<string> OnReasoningForThinkingChain;
        public event Action<ChatMessage> OnProcessingCompleted;

        public AgentService()
        {
            ZexusLogger.Info("AgentService constructor");

            _toolRegistry = ToolRegistry.CreateDefault();
            _currentSession = new Session();
            _toolDefinitions = _toolRegistry.GetToolDefinitions();
            _reporter.StartSession();

            // Create tool loop controller and wire events
            _toolLoop = new ToolLoopController(_sessionContext, _reporter, _policyGate);
            _toolLoop.OnStreamingText += text => OnStreamingText?.Invoke(text);
            _toolLoop.OnToolExecuting += (name, input) => OnToolExecuting?.Invoke(name, input);
            _toolLoop.OnStatusChanged += status => OnStatusChanged?.Invoke(status);
            _toolLoop.OnToolCompleted += (name, result, ms) => OnToolCompleted?.Invoke(name, result, ms);
            _toolLoop.OnReasoningForThinkingChain += text => OnReasoningForThinkingChain?.Invoke(text);

            ZexusLogger.Info($"ToolRegistry created with {_toolRegistry.Count} tools");

            EnsureToolRegistryInitialized();
        }
        
        public void EnsureToolRegistryInitialized()
        {
            if (App.RevitEventHandler != null)
            {
                if (!App.RevitEventHandler.IsRegistryInitialized)
                {
                    App.RevitEventHandler.SetToolRegistry(_toolRegistry);
                    ZexusLogger.Info("ToolRegistry set on RevitEventHandler");
                }
            }
            else
            {
                ZexusLogger.Warn("RevitEventHandler is null");
            }
        }

        public void InitializeClient()
        {
            _client?.Dispose();

            var apiKey = ConfigManager.GetApiKey();
            if (!string.IsNullOrEmpty(apiKey))
            {
                var provider = ConfigManager.GetProvider();
                _client = LlmClientFactory.Create(provider, apiKey, ConfigManager.GetModel(), ConfigManager.Config.MaxTokens);
                ZexusLogger.Info($"{provider} client initialized");
            }
        }

        public bool IsReady => ConfigManager.IsConfigured();
        public Session CurrentSession => _currentSession;

        public void NewSession(string documentName = null)
        {
            _currentSession = new Session { DocumentName = documentName };
            _reporter.StartSession(documentName);
        }

        public async Task<ChatMessage> ProcessMessageAsync(string userMessage, CancellationToken cancellationToken = default)
        {
            ZexusLogger.Info($"ProcessMessageAsync: {userMessage}");
            
            if (!IsReady)
            {
                return new ChatMessage
                {
                    Role = MessageRole.System,
                    Content = "API key not configured. Please click Settings to enter your API key."
                };
            }

            if (_client == null) InitializeClient();
            
            EnsureToolRegistryInitialized();
            
            if (App.RevitEventHandler == null || !App.RevitEventHandler.IsRegistryInitialized)
            {
                return new ChatMessage
                {
                    Role = MessageRole.System,
                    Content = "Tool registry not initialized. Please restart the add-in."
                };
            }

            // Check if user wants to continue from interrupted task
            string processedMessage = userMessage;
            if (IsContinueCommand(userMessage) && _sessionContext.HasRecoverableInterrupt())
            {
                // Inject context summary for AI to understand previous progress
                var contextSummary = _sessionContext.GenerateContextSummary();
                processedMessage = $"{userMessage}\n\n[SYSTEM: Previous session context - DO NOT repeat completed steps, continue from where you left off]\n{contextSummary}";
                ZexusLogger.Info($"Injecting session context for resume: {contextSummary.Length} chars");
            }
            else if (!IsContinueCommand(userMessage) && !IsConfirmationMessage(userMessage))
            {
                // New task - start fresh context (but keep tool history)
                _sessionContext.StartTask(userMessage);
                _toolLoop.ResetTask();
                _consecutiveConfirmationCount = 0;
            }

            // If there are pending policy confirmations and user message looks like approval,
            // mark them as confirmed so the LLM can re-call the blocked tools
            if (_toolLoop.PendingPolicyConfirmations.Count > 0 && IsConfirmationMessage(userMessage))
            {
                _consecutiveConfirmationCount++;
                ZexusLogger.Info($"User confirmed {_toolLoop.PendingPolicyConfirmations.Count} pending policy operations");
                _toolLoop.HandleUserConfirmation(new List<string>(_toolLoop.PendingPolicyConfirmations));
            }
            else if (_toolLoop.PendingPolicyConfirmations.Count == 0 && IsConfirmationMessage(userMessage))
            {
                _consecutiveConfirmationCount++;

                // User confirmed an LLM-initiated plan (not a PolicyGate block).
                // Pre-set the blanket write flag so PolicyGate won't block the write
                // that the LLM is about to generate. Without this, the user would need
                // to confirm TWICE: once for the LLM plan, once for PolicyGate.
                _toolLoop.HandleUserConfirmation(new List<string>());

                // Inject a system nudge to force the LLM to execute immediately
                // instead of responding with more text/re-asking for confirmation.
                processedMessage += "\n\n[SYSTEM: The user has confirmed. EXECUTE the plan NOW by calling the required tools. Do NOT describe the plan again. Do NOT ask for more confirmation. Call ExecuteCode or the appropriate tool IMMEDIATELY.]";
                ZexusLogger.Info("Injected execution nudge for LLM-initiated confirmation (blanket write enabled)");

                // Circuit breaker: 3+ consecutive confirmations = autoregressive spiral
                if (_consecutiveConfirmationCount >= 3)
                {
                    ZexusLogger.Warn($"[CIRCUIT BREAKER] {_consecutiveConfirmationCount} consecutive confirmations with no execution. Compressing conversation history.");
                    _reporter.RecordFriction("confirmation_spiral", $"{_consecutiveConfirmationCount} consecutive confirmations without tool execution");
                    CompressConfirmationHistory(_currentSession);
                }
            }

            // Track user request for usage analytics
            UsageTracker.StartConversationTurn();
            UsageTracker.RecordUserRequest(userMessage);

            // Notify UI that processing has started (for workspace panel)
            OnProcessingStarted?.Invoke(userMessage);

            // Record turn start for session report (captures user message + WM snapshot)
            var wmSnapshot = _sessionContext.Memory.ToCompactString();
            _reporter.RecordTurnStart(userMessage, wmSnapshot);

            var userMsg = new ChatMessage { Role = MessageRole.User, Content = processedMessage };
            _currentSession.AddMessage(userMsg);

            try
            {
                // No tool filtering needed — only ExecuteCode is registered
                var filteredTools = _toolDefinitions;
                string messageType = IsConfirmationMessage(userMessage) ? "confirmation_message" : "normal_request";
                // Note: RecordToolFilter is not called — no filtering in default mode

                var result = await _toolLoop.RunAsync(_client, _currentSession, filteredTools, cancellationToken);

                // Reset confirmation counter on successful completion
                _consecutiveConfirmationCount = 0;

                // Notify UI that processing completed (for workspace panel)
                // Note: RecordTurnEnd + CompleteTask are handled inside ToolLoopController
                OnProcessingCompleted?.Invoke(result);

                return result;
            }
            catch (OperationCanceledException)
            {
                _sessionContext.InterruptTask("User cancelled");
                return new ChatMessage { Role = MessageRole.System, Content = "Request cancelled." };
            }
            catch (Exception ex)
            {
                ZexusLogger.Error($"ProcessMessageAsync exception: {ex.Message}\n{ex.StackTrace}");

                // Check for Rate Limit error (exception-level, e.g. HttpRequestException with 429)
                bool isRateLimitEx = ex.Message.Contains("429") || ex.Message.Contains("rate_limit")
                    || ex.Message.Contains("overloaded") || ex.Message.Contains("529");
                if (isRateLimitEx)
                {
                    _sessionContext.RecordError("rate_limit", ex.Message, true);
                    return new ChatMessage
                    {
                        Role = MessageRole.System,
                        Content = _toolLoop.FormatRateLimitError()
                    };
                }

                // Check for corporate proxy / firewall block (Zscaler, Palo Alto, etc.)
                var errMsg = ex.Message ?? "";
                bool isProxyBlock = errMsg.Contains("Zscaler") || errMsg.Contains("zscaler")
                    || errMsg.Contains("Website blocked") || errMsg.Contains("Not allowed to browse")
                    || errMsg.Contains("Palo Alto") || errMsg.Contains("Forcepoint")
                    || errMsg.Contains("Websense") || errMsg.Contains("Barracuda")
                    || (errMsg.Contains("403") && (errMsg.Contains("<!DOCTYPE") || errMsg.Contains("<html")));
                if (isProxyBlock)
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

                _sessionContext.RecordError("api_error", ex.Message, false);
                return new ChatMessage { Role = MessageRole.System, Content = $"Error: {ex.Message}" };
            }
        }

        /// <summary>
        /// Check if user message is a "continue" command
        /// </summary>
        private bool IsContinueCommand(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            
            var lower = message.ToLower().Trim();
            var continueKeywords = new[] 
            { 
                "continue", "go on", "keep going",
                "resume", "carry on", "proceed",
                "go ahead", "next", "keep it up"
            };
            
            foreach (var keyword in continueKeywords)
            {
                if (lower.Contains(keyword)) return true;
            }
            
            return false;
        }

        /// <summary>
        /// Check if user message is a confirmation/approval (for PolicyGate unblocking).
        /// Covers English and Chinese confirmation patterns.
        /// </summary>
        internal static bool IsConfirmationMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;

            var lower = message.ToLower().Trim();
            var confirmKeywords = new[]
            {
                "yes", "confirm", "go ahead", "proceed", "approve", "do it",
                "ok", "okay", "sure", "agreed", "execute", "run it", "y",
                "是", "确认", "好的", "可以", "执行", "没问题", "继续", "同意",
                "好", "行", "嗯", "对", "1"
            };

            foreach (var keyword in confirmKeywords)
            {
                if (lower.Contains(keyword)) return true;
            }

            return false;
        }

        /// <summary>
        /// Remove intermediate confirmation/re-description cycles from session history.
        /// Keeps the original request, first assistant plan response, and the latest confirmation.
        /// This breaks the autoregressive pattern that traps the LLM in a confirm loop.
        /// </summary>
        private void CompressConfirmationHistory(Session session)
        {
            var messages = session.Messages;
            if (messages.Count < 6) return; // Need at least 3 pairs to compress

            // Find the first assistant message (contains the plan)
            int firstAssistantIdx = -1;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role == MessageRole.Assistant)
                {
                    firstAssistantIdx = i;
                    break;
                }
            }
            if (firstAssistantIdx < 0) return;

            // Keep: messages[0..firstAssistantIdx] (original request + plan)
            // Remove: messages[firstAssistantIdx+1 .. Count-2] (all intermediate confirms/responses)
            // Keep: messages[Count-1] (the latest user confirmation — will be added fresh)
            int removeStart = firstAssistantIdx + 1;
            int removeEnd = messages.Count - 1; // exclusive — keep the last message
            if (removeEnd <= removeStart) return;

            int removedCount = removeEnd - removeStart;
            messages.RemoveRange(removeStart, removedCount);

            ZexusLogger.Info($"CompressConfirmationHistory: removed {removedCount} intermediate messages, {messages.Count} remain");
        }

        public void Dispose()
        {
            _client?.Dispose();
        }

    }
}
