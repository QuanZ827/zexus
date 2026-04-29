using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Zexus.Services
{
    /// <summary>
    /// Session Report Generator — produces an AI-consumable "facts pack" from a single
    /// Zexus session. The report contains everything an AI needs to:
    ///   1. Replay what the user tried to do (intent + context evolution)
    ///   2. See exactly what happened (tool calls with resolved params, ExecuteCode logs)
    ///   3. Identify friction (errors, retries, user corrections, ExecuteCode as gap-filler)
    ///   4. Recommend new tools / tool upgrades (missing tool candidates)
    ///
    /// Data is collected via 3 instrumentation points:
    ///   - AgentService tool loop: pre-execution (input_original + input_resolved), post-execution (result, duration, ref)
    ///   - ExecuteCodeTool: full code, compile/runtime status, stdout, ZEXUS_JSON
    ///   - Per-turn: user message, assistant message, WM snapshot, friction signals
    ///
    /// Export format: single .md file with embedded JSON (human-readable + machine-parseable).
    /// </summary>
    public class SessionReporter
    {
        private static SessionReporter _instance;
        private static readonly object _lock = new object();

        // ── Collected data ──
        private readonly List<TurnRecord> _turns = new List<TurnRecord>();
        private readonly List<ToolCallEvent> _toolCalls = new List<ToolCallEvent>();
        private readonly List<ExecuteCodeEvent> _executeCodeEvents = new List<ExecuteCodeEvent>();

        private string _sessionId;
        private DateTime _sessionStart;       // Local time — used for folder/file naming
        private DateTime _sessionStartUtc;    // UTC — used in report metadata
        private string _documentTitle;
        private int _turnCounter;

        // Current turn tracking
        private TurnRecord _currentTurn;

        private SessionReporter() { }

        public static SessionReporter Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new SessionReporter();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Initialize a new reporting session. Auto-saves the previous session if it has data.
        /// File structure: %AppData%\Zexus\reports\{yyyy-MM-dd}\session_{HH-mm-ss}.md
        /// </summary>
        public void StartSession(string documentTitle = null)
        {
            // Auto-save previous session if it has meaningful data
            if (HasData)
            {
                try
                {
                    SaveReportToFile(BuildReportContent());
                    System.Diagnostics.Debug.WriteLine($"[SessionReporter] Auto-saved previous session: {_sessionId}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SessionReporter] Auto-save failed: {ex.Message}");
                }
            }

            // Start fresh session — use local time for human-readable folder/file naming
            var now = DateTime.Now;
            _sessionStartUtc = DateTime.UtcNow;
            _sessionStart = now;
            _sessionId = now.ToString("HH-mm-ss");
            _documentTitle = documentTitle;
            _turnCounter = 0;
            _turns.Clear();
            _toolCalls.Clear();
            _executeCodeEvents.Clear();
            _currentTurn = null;
        }

        // ═══════════════════════════════════════════════════════
        // Instrumentation Point 1: Per-Turn Recording
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Record the start of a new conversation turn (user message).
        /// Captures user text and WM snapshot at that moment.
        /// </summary>
        public void RecordTurnStart(string userMessage, string workingMemorySnapshot)
        {
            _turnCounter++;
            _currentTurn = new TurnRecord
            {
                TurnIndex = _turnCounter,
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                UserMessage = userMessage,
                WorkingMemorySnapshot = workingMemorySnapshot
            };
            _turns.Add(_currentTurn);
        }

        /// <summary>
        /// Record the assistant's response for the current turn.
        /// Auto-saves the session incrementally after each turn.
        /// </summary>
        public void RecordTurnEnd(string assistantMessage)
        {
            if (_currentTurn == null) return;
            _currentTurn.AssistantMessage = TruncateForReport(assistantMessage, 300);

            // Detect friction signals
            DetectFrictionSignals(_currentTurn);

            // Incremental auto-save: persist after every turn so no data is lost on crash
            TrySaveIncremental();
        }

        // RecordToolFilter removed — no ToolFilteringService in default mode

        /// <summary>
        /// Record a named friction event on the current turn (called from AgentService guardrails).
        /// </summary>
        public void RecordFriction(string frictionType, string detail)
        {
            if (_currentTurn == null) return;
            if (_currentTurn.FrictionSignals == null)
                _currentTurn.FrictionSignals = new FrictionSignals();

            switch (frictionType)
            {
                case "hallucination_detected":
                    _currentTurn.FrictionSignals.HallucinationDetected = true;
                    break;
            }

            System.Diagnostics.Debug.WriteLine($"[SessionReporter] Friction: {frictionType} — {detail}");
        }

        // ═══════════════════════════════════════════════════════
        // Instrumentation Point 2: Tool Call Recording
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Record a tool call with full pre/post execution data.
        /// input_original = what the LLM sent (may contain *_ref params).
        /// input_resolved = after RefResolver expanded refs.
        /// </summary>
        public void RecordToolCall(
            string toolName,
            Dictionary<string, object> inputOriginal,
            Dictionary<string, object> inputResolved,
            bool success,
            string message,
            string warning,
            string errorType,
            Dictionary<string, object> dataSummary,
            string resultRef,
            long durationMs)
        {
            var evt = new ToolCallEvent
            {
                TurnIndex = _turnCounter,
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                ToolName = toolName,
                InputOriginal = SanitizeInput(inputOriginal),
                InputResolved = SummarizeResolved(inputResolved),
                Success = success,
                Message = TruncateForReport(message, 500),
                Warning = warning,
                ErrorType = errorType,
                ResultRef = resultRef,
                DurationMs = durationMs,
                DataSummary = BuildDataSummary(dataSummary)
            };

            _toolCalls.Add(evt);
        }

        // ═══════════════════════════════════════════════════════
        // Instrumentation Point 3: ExecuteCode Recording
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Record an ExecuteCode execution with full code, output, and side effects.
        /// Called from ExecuteCodeTool after execution completes.
        /// </summary>
        public void RecordExecuteCode(
            string code,
            string description,
            bool compileSuccess,
            bool runtimeSuccess,
            string stdout,
            string stderr,
            string exceptionType,
            Dictionary<string, object> zexusJson,
            int createdCount,
            int modifiedCount,
            int deletedCount)
        {
            var evt = new ExecuteCodeEvent
            {
                TurnIndex = _turnCounter,
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Description = description,
                Code = code,
                CompileSuccess = compileSuccess,
                RuntimeSuccess = runtimeSuccess,
                Stdout = TruncateForReport(stdout, 1000),
                Stderr = TruncateForReport(stderr, 500),
                ExceptionType = exceptionType,
                ZexusJson = zexusJson,
                ModelSideEffects = new SideEffects
                {
                    CreatedCount = createdCount,
                    ModifiedCount = modifiedCount,
                    DeletedCount = deletedCount
                },
                // Zexus diagnostic fields
                Phase = ClassifyECPhase(code),
                Outcome = !compileSuccess ? "compile_fail"
                        : !runtimeSuccess ? "runtime_fail"
                        : "success"
            };

            // Error category classification
            if (!compileSuccess) evt.ErrorCategory = ClassifyCompileError(stderr);
            else if (!runtimeSuccess) evt.ErrorCategory = ClassifyRuntimeError(exceptionType);

            _executeCodeEvents.Add(evt);
        }

        // ═══════════════════════════════════════════════════════
        // Report Generation
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Generate the complete session report, save to file, and return the file path.
        /// Path: %AppData%\Zexus\reports\{yyyy-MM-dd}\session_{HH-mm-ss}.md
        /// </summary>
        public string GenerateReport(string userFeedbackGoalMet = null, string userFeedbackPainPoint = null)
        {
            if (!HasData) return null;
            var content = BuildReportContent(userFeedbackGoalMet, userFeedbackPainPoint);
            return SaveReportToFile(content);
        }

        /// <summary>
        /// Get the report content as string (for clipboard).
        /// </summary>
        public string GetReportContent()
        {
            if (!HasData) return null;
            return BuildReportContent();
        }

        /// <summary>
        /// Build the full report markdown content.
        /// </summary>
        private string BuildReportContent(string userFeedbackGoalMet = null, string userFeedbackPainPoint = null)
        {
            var sb = new StringBuilder();
            WriteAiInstructions(sb);
            WriteSessionMeta(sb);
            WriteExecutiveSummary(sb);
            WriteEventLog(sb);
            WriteStatistics(sb);
            WriteMissingToolCandidates(sb);

            if (!string.IsNullOrEmpty(userFeedbackGoalMet) || !string.IsNullOrEmpty(userFeedbackPainPoint))
            {
                sb.AppendLine("\n---\n");
                sb.AppendLine("## USER_FEEDBACK");
                if (!string.IsNullOrEmpty(userFeedbackGoalMet))
                    sb.AppendLine($"goal_achieved: {userFeedbackGoalMet}");
                if (!string.IsNullOrEmpty(userFeedbackPainPoint))
                    sb.AppendLine($"biggest_pain_point: \"{userFeedbackPainPoint}\"");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Save report content to the day-folder structure.
        /// Returns the file path, or null on failure.
        /// </summary>
        private string SaveReportToFile(string content)
        {
            try
            {
                // Day-folder: %AppData%\Zexus\reports\2025-06-15\
                var dayFolder = _sessionStart.ToString("yyyy-MM-dd");
                var reportDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Zexus", "reports", dayFolder);
                if (!Directory.Exists(reportDir))
                    Directory.CreateDirectory(reportDir);

                // File: session_14-30-25.md
                var fileName = $"session_{_sessionId}.md";
                var filePath = Path.Combine(reportDir, fileName);
                File.WriteAllText(filePath, content, Encoding.UTF8);
                return filePath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] SessionReporter save error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Incremental auto-save: writes the current session to disk after each turn.
        /// Overwrites the same file each time (same session_id), so disk usage is constant.
        /// Non-fatal: any failure is silently logged and does not affect the session.
        /// </summary>
        private void TrySaveIncremental()
        {
            if (!HasData) return;
            try
            {
                SaveReportToFile(BuildReportContent());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionReporter] Incremental save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Public API: save the current session to disk. Called from App.OnShutdown
        /// to ensure the last session is persisted when Revit exits.
        /// Also generates a UsageTracker report alongside the session report.
        /// </summary>
        public void SaveOnShutdown()
        {
            if (!HasData) return;
            try
            {
                var path = SaveReportToFile(BuildReportContent());
                if (path != null)
                    System.Diagnostics.Debug.WriteLine($"[SessionReporter] Shutdown save: {path}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionReporter] Shutdown save failed: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════
        // Section Writers
        // ═══════════════════════════════════════════════════════

        private void WriteAiInstructions(StringBuilder sb)
        {
            sb.AppendLine("# Zexus — Session Report");
            sb.AppendLine();
            sb.AppendLine("> **AI Review Instructions** (Zexus Baseline — No-Card diagnostic format)");
            sb.AppendLine("> This is a No-Card Baseline session: the LLM had NO API cookbook, NO capability cards,");
            sb.AppendLine("> NO task decomposition guidance. Only environment truths and behavior contracts were provided.");
            sb.AppendLine(">");
            sb.AppendLine("> Analyze this session through the **failure taxonomy** lens:");
            sb.AppendLine("> 1. **Goal Understanding**: Did the LLM's first ExecuteCode description match the user's intent?");
            sb.AppendLine(">    Compare user message vs EC description for each turn.");
            sb.AppendLine("> 2. **State Awareness**: Did the LLM read/inspect before writing? Look for write-before-read patterns.");
            sb.AppendLine(">    Check if writes failed because of missing state knowledge (e.g., name conflicts, wrong target).");
            sb.AppendLine("> 3. **API Knowledge**: What compile errors occurred? Are they missing_member (API hallucination),");
            sb.AppendLine(">    type_mismatch, or syntax_error? Which hallucinated APIs repeat across turns?");
            sb.AppendLine("> 4. **Execution Continuity**: Did the LLM maintain context across multi-step tasks?");
            sb.AppendLine(">    Did it lose track of IDs, names, or goals mid-chain?");
            sb.AppendLine("> 5. **Safety Flow Friction**: Did PolicyGate, confirmation protocol, or dry-run mechanics");
            sb.AppendLine(">    block operations that the LLM could otherwise complete?");
            sb.AppendLine(">");
            sb.AppendLine("> Output:");
            sb.AppendLine("> 1. **Session summary** — user goals, overall success/failure, model used");
            sb.AppendLine("> 2. **Failure taxonomy** — classify each failure into the 5 categories above");
            sb.AppendLine("> 3. **Repeated patterns** — errors or behaviors that occur across multiple turns");
            sb.AppendLine("> 4. **Minimum necessary fix** — what single smallest change would most improve this session:");
            sb.AppendLine(">    prompt discipline, capability card, loop/state fix, policy change, or pure bug fix");
            sb.AppendLine();
        }

        private void WriteSessionMeta(StringBuilder sb)
        {
            sb.AppendLine("## SESSION_META");
            sb.AppendLine("```json");

            var meta = new Dictionary<string, object>
            {
                ["schema_version"] = "zexus_session_report_v2",
                ["session_id"] = _sessionStart.ToString("yyyy-MM-dd") + "/" + (_sessionId ?? "unknown"),
                ["plugin_version"] = GetPluginVersion(),
                ["revit_version"] = App.RevitVersion.ToString(),
                ["document_title"] = _documentTitle ?? "(unknown)",
                ["llm_provider"] = ConfigManager.GetProvider(),
                ["llm_model"] = ConfigManager.GetModel(),
                ["time_range_utc"] = new Dictionary<string, string>
                {
                    ["start"] = _sessionStartUtc.ToString("o"),
                    ["end"] = DateTime.UtcNow.ToString("o")
                },
                ["total_turns"] = _turns.Count,
                ["total_tool_calls"] = _toolCalls.Count,
                ["total_execute_code"] = _executeCodeEvents.Count,
                // Zexus session manifest
                ["plugin_name"] = "Zexus",
                ["plugin_mode"] = "No-Card Baseline",
                ["prompt_version"] = "v0_minimal",
                ["policy_gate_enabled"] = true,
                ["max_tool_iterations"] = 30,
                ["max_consecutive_ec_failures"] = 4
            };

            sb.AppendLine(JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        private void WriteExecutiveSummary(StringBuilder sb)
        {
            sb.AppendLine("## EXECUTIVE_SUMMARY");
            sb.AppendLine();

            // Session goal (first user message)
            var firstMsg = _turns.FirstOrDefault()?.UserMessage;
            if (!string.IsNullOrEmpty(firstMsg))
                sb.AppendLine($"**Session Goal:** {TruncateForReport(firstMsg, 200)}");

            // Overall stats
            int totalCalls = _toolCalls.Count;
            int successCalls = _toolCalls.Count(tc => tc.Success);
            double successRate = totalCalls > 0 ? 100.0 * successCalls / totalCalls : 0;
            sb.AppendLine($"**Overall:** {_turns.Count} turns, {totalCalls} tool calls ({successRate:F0}% success), {_executeCodeEvents.Count} ExecuteCode");

            // ExecuteCode stats
            if (_executeCodeEvents.Count > 0)
            {
                int ecOk = _executeCodeEvents.Count(e => e.CompileSuccess && e.RuntimeSuccess);
                int ecCompileFail = _executeCodeEvents.Count(e => !e.CompileSuccess);
                int ecRuntimeFail = _executeCodeEvents.Count(e => e.CompileSuccess && !e.RuntimeSuccess);
                sb.AppendLine($"**ExecuteCode:** {ecOk} success, {ecCompileFail} compile fail, {ecRuntimeFail} runtime fail ({(ecOk * 100.0 / _executeCodeEvents.Count):F0}% success)");
            }

            // Top friction
            var frictionTurns = _turns.Where(t => t.FrictionSignals != null && t.FrictionSignals.HasFriction).ToList();
            if (frictionTurns.Count > 0)
            {
                int userRepeats = frictionTurns.Count(t => t.FrictionSignals.UserRepeatedRequest);
                int selfCorrections = frictionTurns.Count(t => t.FrictionSignals.AssistantCorrectedItself);
                int turnErrors = frictionTurns.Count(t => t.FrictionSignals.TurnHadErrors);
                int ecWorkarounds = frictionTurns.Count(t => t.FrictionSignals.ExecuteCodeAsWorkaround);
                sb.AppendLine($"**Friction:** {frictionTurns.Count}/{_turns.Count} turns — {userRepeats} user repeats, {selfCorrections} self-corrections, {turnErrors} error turns, {ecWorkarounds} EC workarounds");
            }

            // Top ExecuteCode intents
            var intentCounts = ClassifyExecuteCodeIntents();
            if (intentCounts.Count > 0)
            {
                var topIntents = intentCounts.OrderByDescending(x => x.Value).Take(3)
                    .Select(x => $"{x.Key}({x.Value}x)");
                sb.AppendLine($"**Top EC Intents:** {string.Join(", ", topIntents)}");
            }

            // Missing tool candidate count
            var ecIntentCandidates = intentCounts.Count(kv => kv.Value >= 3);
            if (ecIntentCandidates > 0)
                sb.AppendLine($"**Missing Tool Signals:** {ecIntentCandidates} intent patterns with 3+ repetitions");

            // Tool filtering summary
            var filteredTurns = _turns.Where(t => t.ToolFilter != null).ToList();
            if (filteredTurns.Count > 0)
            {
                int fallbackCount = filteredTurns.Count(t => t.ToolFilter.UsedFallback);
                double avgOriginal = filteredTurns.Average(t => t.ToolFilter.OriginalCount);
                double avgFiltered = filteredTurns.Average(t => t.ToolFilter.FilteredCount);
                double reductionPct = avgOriginal > 0 ? 100.0 * (1 - avgFiltered / avgOriginal) : 0;
                sb.AppendLine($"**Tool Filtering:** {filteredTurns.Count} turns filtered, avg {avgOriginal:F0}→{avgFiltered:F0} tools ({reductionPct:F0}% reduction), {fallbackCount} fallbacks");
            }

            sb.AppendLine();
        }

        private void WriteEventLog(StringBuilder sb)
        {
            sb.AppendLine("## EVENT_LOG");
            sb.AppendLine();

            foreach (var turn in _turns)
            {
                sb.AppendLine($"### Turn {turn.TurnIndex} ({turn.TimestampUtc})");
                sb.AppendLine();

                // User message
                sb.AppendLine($"**User:** {EscapeMarkdown(TruncateForReport(turn.UserMessage, 300))}");
                sb.AppendLine();

                // Working Memory — compact one-liner showing ref count
                if (!string.IsNullOrEmpty(turn.WorkingMemorySnapshot))
                {
                    sb.AppendLine($"**WM:** `{TruncateForReport(turn.WorkingMemorySnapshot, 150)}`");
                    sb.AppendLine();
                }

                // Tool calls — one line each
                var turnToolCalls = _toolCalls.Where(tc => tc.TurnIndex == turn.TurnIndex).ToList();
                var turnCodeEvents = _executeCodeEvents.Where(ec => ec.TurnIndex == turn.TurnIndex).ToList();

                if (turnToolCalls.Count > 0 || turnCodeEvents.Count > 0)
                {
                    sb.AppendLine("**Calls:**");

                    foreach (var tc in turnToolCalls)
                    {
                        string icon = tc.Success ? "✅" : "❌";
                        string status = tc.Success ? "success" : (tc.ErrorType ?? "failed");

                        if (tc.ToolName == "ExecuteCode")
                        {
                            // Find matching ExecuteCode event for description
                            string desc = "";
                            if (tc.InputOriginal != null && tc.InputOriginal.TryGetValue("description", out var d))
                                desc = $" | \"{d?.ToString() ?? ""}\"";
                            sb.AppendLine($"- {icon} **ExecuteCode** ({tc.DurationMs}ms) → {status}{desc}");

                            if (!tc.Success && !string.IsNullOrEmpty(tc.Message))
                                sb.AppendLine($"  - error: {TruncateForReport(tc.Message, 150)}");
                        }
                        else
                        {
                            // Non-ExecuteCode tools: show key params summary
                            string paramSummary = BuildCompactParamSummary(tc.InputOriginal);
                            sb.AppendLine($"- {icon} **{tc.ToolName}** ({tc.DurationMs}ms) → {status}{paramSummary}");

                            if (!string.IsNullOrEmpty(tc.ResultRef))
                                sb.Append($" ref=`{tc.ResultRef}`");
                            if (tc.DataSummary != null && tc.DataSummary.Count > 0)
                                sb.AppendLine($"  - data: `{SerializeCompact(tc.DataSummary)}`");
                            if (!tc.Success && !string.IsNullOrEmpty(tc.Message))
                                sb.AppendLine($"  - error: {TruncateForReport(tc.Message, 150)}");
                            if (!string.IsNullOrEmpty(tc.Warning))
                                sb.AppendLine($"  - warning: {tc.Warning}");
                        }
                    }
                    sb.AppendLine();
                }

                // Friction signals — compact inline
                if (turn.FrictionSignals != null && turn.FrictionSignals.HasFriction)
                {
                    var signals = new List<string>();
                    if (turn.FrictionSignals.UserRepeatedRequest) signals.Add("user_repeated");
                    if (turn.FrictionSignals.AssistantCorrectedItself) signals.Add("self_corrected");
                    if (turn.FrictionSignals.TurnHadErrors) signals.Add("had_errors");
                    if (turn.FrictionSignals.ExecuteCodeAsWorkaround) signals.Add("ec_workaround");
                    if (turn.FrictionSignals.HallucinationDetected) signals.Add("hallucination");
                    sb.AppendLine($"**Friction:** {string.Join(", ", signals)}");
                    sb.AppendLine();
                }

                sb.AppendLine("---");
                sb.AppendLine();
            }

            // ── JSONL section (machine-readable) ──
            WriteEventLogJsonl(sb);
        }

        private void WriteEventLogJsonl(StringBuilder sb)
        {
            sb.AppendLine("## EVENT_LOG_JSONL");
            sb.AppendLine();
            sb.AppendLine("```jsonl");

            var jsonOpts = new JsonSerializerOptions { WriteIndented = false };

            foreach (var turn in _turns)
            {
                var turnObj = new Dictionary<string, object>
                {
                    ["type"] = "turn",
                    ["turn_index"] = turn.TurnIndex,
                    ["ts_utc"] = turn.TimestampUtc,
                    ["user_message"] = turn.UserMessage,
                    ["working_memory_summary"] = TruncateForReport(turn.WorkingMemorySnapshot, 100)
                };
                if (turn.FrictionSignals != null && turn.FrictionSignals.HasFriction)
                {
                    turnObj["friction"] = new Dictionary<string, bool>
                    {
                        ["user_repeated"] = turn.FrictionSignals.UserRepeatedRequest,
                        ["self_corrected"] = turn.FrictionSignals.AssistantCorrectedItself,
                        ["had_errors"] = turn.FrictionSignals.TurnHadErrors,
                        ["ec_workaround"] = turn.FrictionSignals.ExecuteCodeAsWorkaround,
                        ["hallucination"] = turn.FrictionSignals.HallucinationDetected
                    };
                }

                // Tool filtering snapshot
                if (turn.ToolFilter != null)
                {
                    var tf = new Dictionary<string, object>
                    {
                        ["original_count"] = turn.ToolFilter.OriginalCount,
                        ["filtered_count"] = turn.ToolFilter.FilteredCount,
                        ["used_fallback"] = turn.ToolFilter.UsedFallback,
                        ["message_type"] = turn.ToolFilter.MessageType
                    };
                    if (turn.ToolFilter.SelectedBuckets != null)
                        tf["selected_buckets"] = turn.ToolFilter.SelectedBuckets;
                    if (turn.ToolFilter.BucketScores != null)
                    {
                        // Only include top 3 scores to keep JSONL compact
                        var topScores = turn.ToolFilter.BucketScores
                            .OrderByDescending(s => s.Value).Take(3)
                            .ToDictionary(s => s.Key, s => (object)s.Value);
                        tf["bucket_scores_top3"] = topScores;
                    }
                    if (turn.ToolFilter.SelectedTools != null)
                        tf["selected_tools"] = turn.ToolFilter.SelectedTools;
                    if (turn.ToolFilter.UsedFallback && !string.IsNullOrEmpty(turn.ToolFilter.FallbackReason))
                        tf["fallback_reason"] = turn.ToolFilter.FallbackReason;
                    turnObj["tool_filter"] = tf;
                }

                sb.AppendLine(JsonSerializer.Serialize(turnObj, jsonOpts));
            }

            foreach (var tc in _toolCalls)
            {
                var tcObj = new Dictionary<string, object>
                {
                    ["type"] = "tool_call",
                    ["turn_index"] = tc.TurnIndex,
                    ["tool_name"] = tc.ToolName,
                    ["duration_ms"] = tc.DurationMs,
                    ["success"] = tc.Success,
                    ["result_ref"] = tc.ResultRef
                };

                // For non-ExecuteCode tools, include compact input summary
                if (tc.ToolName != "ExecuteCode")
                {
                    tcObj["input_original"] = tc.InputOriginal;
                    tcObj["input_resolved"] = tc.InputResolved;
                    tcObj["data_summary"] = tc.DataSummary;
                }
                else
                {
                    // For ExecuteCode, just include description
                    if (tc.InputOriginal != null && tc.InputOriginal.TryGetValue("description", out var desc))
                        tcObj["description"] = desc;
                }

                if (!tc.Success)
                {
                    tcObj["error_type"] = tc.ErrorType;
                    tcObj["message"] = TruncateForReport(tc.Message, 150);
                }
                sb.AppendLine(JsonSerializer.Serialize(tcObj, jsonOpts));
            }

            foreach (var ec in _executeCodeEvents)
            {
                var ecObj = new Dictionary<string, object>
                {
                    ["type"] = "execute_code",
                    ["turn_index"] = ec.TurnIndex,
                    ["description"] = ec.Description,
                    ["code_line_count"] = string.IsNullOrEmpty(ec.Code) ? 0 : ec.Code.Split('\n').Length,
                    ["code_first_line"] = TruncateForReport(ec.Code?.Split('\n').FirstOrDefault(), 80),
                    ["compile_success"] = ec.CompileSuccess,
                    ["runtime_success"] = ec.RuntimeSuccess,
                    ["stdout_summary"] = TruncateForReport(ec.Stdout, 150),
                    ["stderr_first_line"] = TruncateForReport(ec.Stderr?.Split('\n').FirstOrDefault(), 120),
                    ["exception_type"] = ec.ExceptionType,
                    ["zexus_json"] = ec.ZexusJson,
                    // Zexus diagnostic fields
                    ["phase"] = ec.Phase,
                    ["outcome"] = ec.Outcome,
                    ["error_category"] = ec.ErrorCategory
                };

                if (ec.ModelSideEffects != null && ec.ModelSideEffects.HasEffects)
                {
                    ecObj["side_effects"] = new Dictionary<string, int>
                    {
                        ["created"] = ec.ModelSideEffects.CreatedCount,
                        ["modified"] = ec.ModelSideEffects.ModifiedCount,
                        ["deleted"] = ec.ModelSideEffects.DeletedCount
                    };
                }

                sb.AppendLine(JsonSerializer.Serialize(ecObj, jsonOpts));
            }

            sb.AppendLine("```");
            sb.AppendLine();
        }

        private void WriteStatistics(StringBuilder sb)
        {
            sb.AppendLine("## STATISTICS");
            sb.AppendLine();

            // A) Tool Coverage & Pain Map
            sb.AppendLine("### Tool Coverage & Pain Map");
            sb.AppendLine("```");
            sb.AppendLine($"{"tool",-30} | {"calls",5} | {"success%",8} | {"avg_ms",6} | {"top_error",-40}");
            sb.AppendLine(new string('-', 100));

            var toolGroups = _toolCalls.GroupBy(tc => tc.ToolName)
                .OrderByDescending(g => g.Count())
                .ToList();

            foreach (var g in toolGroups)
            {
                int total = g.Count();
                int success = g.Count(tc => tc.Success);
                double rate = total > 0 ? 100.0 * success / total : 0;
                long avgMs = total > 0 ? (long)g.Average(tc => tc.DurationMs) : 0;
                string topErr = g.Where(tc => !tc.Success && !string.IsNullOrEmpty(tc.ErrorType))
                    .GroupBy(tc => tc.ErrorType)
                    .OrderByDescending(eg => eg.Count())
                    .Select(eg => eg.Key)
                    .FirstOrDefault() ?? "";

                sb.AppendLine($"{g.Key,-30} | {total,5} | {rate,7:F0}% | {avgMs,6} | {topErr,-40}");
            }
            sb.AppendLine("```");
            sb.AppendLine();

            // Failed-then-ExecuteCode ratio
            int failedFollowedByCode = 0;
            for (int i = 0; i < _toolCalls.Count - 1; i++)
            {
                if (!_toolCalls[i].Success && _toolCalls[i + 1].ToolName == "ExecuteCode")
                    failedFollowedByCode++;
            }
            if (failedFollowedByCode > 0)
            {
                sb.AppendLine($"**Failed → ExecuteCode fallback**: {failedFollowedByCode} occurrences (strong signal: tool lacks capability)");
                sb.AppendLine();
            }

            // B) ExecuteCode Demand Profile
            if (_executeCodeEvents.Count > 0)
            {
                sb.AppendLine("### ExecuteCode Demand Profile");
                sb.AppendLine();

                int ecTotal = _executeCodeEvents.Count;
                int ecCompileOk = _executeCodeEvents.Count(e => e.CompileSuccess);
                int ecRuntimeOk = _executeCodeEvents.Count(e => e.CompileSuccess && e.RuntimeSuccess);
                int ecWithSideEffects = _executeCodeEvents.Count(e => e.ModelSideEffects != null && e.ModelSideEffects.HasEffects);
                int ecWithZexusJson = _executeCodeEvents.Count(e => e.ZexusJson != null && e.ZexusJson.Count > 0);

                sb.AppendLine($"- Total: {ecTotal}");
                sb.AppendLine($"- Compile success: {ecCompileOk}/{ecTotal} ({(ecTotal > 0 ? 100.0 * ecCompileOk / ecTotal : 0):F0}%)");
                sb.AppendLine($"- Runtime success: {ecRuntimeOk}/{ecTotal} ({(ecTotal > 0 ? 100.0 * ecRuntimeOk / ecTotal : 0):F0}%)");
                sb.AppendLine($"- With model side effects: {ecWithSideEffects}");
                sb.AppendLine($"- With ZEXUS_JSON return: {ecWithZexusJson}");
                sb.AppendLine();

                // Classify by intent (rule-based)
                var intentCounts = ClassifyExecuteCodeIntents();
                if (intentCounts.Count > 0)
                {
                    sb.AppendLine("**Intent Classification (rule-based):**");
                    sb.AppendLine("```");
                    foreach (var kv in intentCounts.OrderByDescending(x => x.Value))
                        sb.AppendLine($"  {kv.Key,-30}: {kv.Value}x");
                    sb.AppendLine("```");
                    sb.AppendLine();
                }

                // List each ExecuteCode with summary
                sb.AppendLine("**ExecuteCode Invocations:**");
                foreach (var ec in _executeCodeEvents)
                {
                    string status = (ec.CompileSuccess && ec.RuntimeSuccess) ? "OK" : "FAIL";
                    string effects = "";
                    if (ec.ModelSideEffects != null && ec.ModelSideEffects.HasEffects)
                        effects = $" [c={ec.ModelSideEffects.CreatedCount},m={ec.ModelSideEffects.ModifiedCount},d={ec.ModelSideEffects.DeletedCount}]";
                    sb.AppendLine($"  - Turn {ec.TurnIndex} | {status}{effects} | {ec.Description}");
                }
                sb.AppendLine();
            }

            // C) Friction Summary
            var frictionTurns = _turns.Where(t => t.FrictionSignals != null && t.FrictionSignals.HasFriction).ToList();
            if (frictionTurns.Count > 0)
            {
                sb.AppendLine("### Friction Summary");
                sb.AppendLine();
                sb.AppendLine($"- Turns with friction: {frictionTurns.Count}/{_turns.Count}");
                sb.AppendLine($"- User repeated request: {frictionTurns.Count(t => t.FrictionSignals.UserRepeatedRequest)}");
                sb.AppendLine($"- Assistant self-corrections: {frictionTurns.Count(t => t.FrictionSignals.AssistantCorrectedItself)}");
                sb.AppendLine($"- Turns with errors: {frictionTurns.Count(t => t.FrictionSignals.TurnHadErrors)}");
                sb.AppendLine($"- ExecuteCode workarounds: {frictionTurns.Count(t => t.FrictionSignals.ExecuteCodeAsWorkaround)}");
                sb.AppendLine($"- Hallucinations detected: {frictionTurns.Count(t => t.FrictionSignals.HallucinationDetected)}");
                sb.AppendLine();
            }

            // D) Error Type Distribution
            var errorTypes = _toolCalls.Where(tc => !tc.Success && !string.IsNullOrEmpty(tc.ErrorType))
                .GroupBy(tc => tc.ErrorType)
                .OrderByDescending(g => g.Count())
                .ToList();
            if (errorTypes.Count > 0)
            {
                sb.AppendLine("### Error Type Distribution");
                sb.AppendLine("```");
                foreach (var g in errorTypes)
                    sb.AppendLine($"  {g.Key,-25}: {g.Count()}x");
                sb.AppendLine("```");
                sb.AppendLine();
            }

            // E) Turn Chain Summary (Zexus diagnostic)
            if (_executeCodeEvents.Count > 0)
            {
                WriteTurnChainSummary(sb);
            }
        }

        private void WriteMissingToolCandidates(StringBuilder sb)
        {
            // In default mode, we don't recommend new tools.
            // Instead, analyze what type of fix would most help.
            sb.AppendLine("## MINIMUM_NECESSARY_FIX_ANALYSIS");
            sb.AppendLine();

            // Count error categories
            var syntaxErrors = _executeCodeEvents.Count(e => !e.CompileSuccess);
            var runtimeErrors = _executeCodeEvents.Count(e => e.CompileSuccess && !e.RuntimeSuccess);
            var apiHallucinations = _executeCodeEvents.Count(e =>
                !e.CompileSuccess && !string.IsNullOrEmpty(e.Stderr) &&
                (e.Stderr.Contains("does not contain a definition") || e.Stderr.Contains("has no member")));
            var userRepeats = _turns.Count(t => t.FrictionSignals?.UserRepeatedRequest == true);

            sb.AppendLine("**Error distribution:**");
            sb.AppendLine($"- Compilation errors (syntax/using/etc): {syntaxErrors}");
            sb.AppendLine($"- API hallucinations (missing member): {apiHallucinations}");
            sb.AppendLine($"- Runtime errors: {runtimeErrors}");
            sb.AppendLine($"- User had to repeat request: {userRepeats}");
            sb.AppendLine();

            // Suggest fix category
            if (syntaxErrors > apiHallucinations && syntaxErrors > runtimeErrors)
                sb.AppendLine("**Suggested fix type:** Code template / preprocessing (strip using statements, fix common patterns)");
            else if (apiHallucinations > syntaxErrors && apiHallucinations > runtimeErrors)
                sb.AppendLine("**Suggested fix type:** Capability card for hallucinated APIs (add correct API patterns to prompt)");
            else if (runtimeErrors > 0)
                sb.AppendLine("**Suggested fix type:** Runtime guard (name sanitization, precondition checks)");
            else if (userRepeats > 0)
                sb.AppendLine("**Suggested fix type:** Prompt discipline (task understanding, scope clarity)");
            else
                sb.AppendLine("**Suggested fix type:** None — session completed successfully");

            sb.AppendLine();
        }

        // ═══════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Detect friction signals by comparing current turn with history.
        /// </summary>
        private void DetectFrictionSignals(TurnRecord turn)
        {
            turn.FrictionSignals = new FrictionSignals();

            // Check if user repeated/rephrased their request
            if (_turns.Count >= 2)
            {
                var prevTurn = _turns[_turns.Count - 2];
                if (IsSimilarMessage(prevTurn.UserMessage, turn.UserMessage))
                    turn.FrictionSignals.UserRepeatedRequest = true;
            }

            // Check for user correction keywords
            var lowerMsg = turn.UserMessage?.ToLower() ?? "";
            var correctionKeywords = new[] { "not that", "wrong", "no i mean", "i said", "that's not",
                "you forgot", "again", "retry", "try again", "not what i asked", "incorrect" };
            foreach (var keyword in correctionKeywords)
            {
                if (lowerMsg.Contains(keyword))
                {
                    turn.FrictionSignals.UserRepeatedRequest = true;
                    break;
                }
            }

            // Check if assistant corrected itself (same tool called twice in one turn, first failed)
            var turnCalls2 = _toolCalls.Where(tc => tc.TurnIndex == turn.TurnIndex).ToList();
            var toolNameGroups = turnCalls2.GroupBy(tc => tc.ToolName);
            foreach (var grp in toolNameGroups)
            {
                var calls = grp.ToList();
                if (calls.Count >= 2 && !calls[0].Success && calls[calls.Count - 1].Success)
                {
                    turn.FrictionSignals.AssistantCorrectedItself = true;
                    break;
                }
            }

            // Check if this turn had errors
            var turnErrors = _toolCalls.Where(tc => tc.TurnIndex == turn.TurnIndex && !tc.Success);
            if (turnErrors.Any())
                turn.FrictionSignals.TurnHadErrors = true;

            // Check if ExecuteCode was used as a workaround (follows a failed tool call)
            var turnCalls = _toolCalls.Where(tc => tc.TurnIndex == turn.TurnIndex).ToList();
            for (int i = 0; i < turnCalls.Count - 1; i++)
            {
                if (!turnCalls[i].Success && turnCalls[i + 1].ToolName == "ExecuteCode")
                {
                    turn.FrictionSignals.ExecuteCodeAsWorkaround = true;
                    break;
                }
            }

            // Check for ExecuteCode doing work that a dedicated tool should handle
            var turnCodeEvents = _executeCodeEvents.Where(ec => ec.TurnIndex == turn.TurnIndex).ToList();
            if (turnCodeEvents.Any(ec => ec.ModelSideEffects != null && ec.ModelSideEffects.ModifiedCount > 5))
                turn.FrictionSignals.ExecuteCodeAsWorkaround = true;
        }

        /// <summary>
        /// Classify ExecuteCode events by intent (rule-based).
        /// </summary>
        private Dictionary<string, int> ClassifyExecuteCodeIntents()
        {
            var counts = new Dictionary<string, int>();
            foreach (var ec in _executeCodeEvents)
            {
                string intent = ClassifySingleIntent(ec.Code);
                if (!counts.ContainsKey(intent)) counts[intent] = 0;
                counts[intent]++;
            }
            return counts;
        }

        private string ClassifySingleIntent(string code)
        {
            if (string.IsNullOrEmpty(code)) return "unknown";
            var lower = code.ToLower();

            // Order matters — more specific patterns first
            if (lower.Contains("export") || lower.Contains("file.write") || lower.Contains("streamwriter"))
                return "export/report";
            if (lower.Contains("viewschedule") || lower.Contains("schedulefilter") || lower.Contains("schedulefield"))
                return "schedule/view_automation";
            if (lower.Contains("transaction") && (lower.Contains(".set(") || lower.Contains("param.set")))
                return "batch_modify_parameter";
            if (lower.Contains("transaction") && lower.Contains("doc.create"))
                return "element_creation";
            if (lower.Contains("transaction") && lower.Contains("doc.delete"))
                return "element_deletion";
            if (lower.Contains("transaction"))
                return "model_modification";
            if (lower.Contains("uidoc.selection") || lower.Contains("pickobject"))
                return "ui/selection";
            if (lower.Contains("overridegraphicsettings") || lower.Contains("setvisibility") || lower.Contains("hideelements"))
                return "visibility/graphics";
            if (lower.Contains("filteredelementcollector") && lower.Contains("lookupparameter"))
                return "search_and_read_parameter";
            if (lower.Contains("filteredelementcollector"))
                return "search/query";
            if (lower.Contains("lookupparameter") || lower.Contains("get_parameter"))
                return "parameter_read";
            if (lower.Contains("get_boundingbox") || lower.Contains("get_geometry") || lower.Contains("get_location"))
                return "geometry_query";
            if (lower.Contains("getwarnings"))
                return "warnings_query";

            return "other";
        }

        private string SuggestToolName(string intent)
        {
            return intent; // In default mode, we don't suggest tools — just report the intent
        }

        /// <summary>
        /// Simple similarity check for user messages (detects rephrasing).
        /// </summary>
        private bool IsSimilarMessage(string msg1, string msg2)
        {
            if (string.IsNullOrEmpty(msg1) || string.IsNullOrEmpty(msg2)) return false;

            // Normalize
            var a = msg1.ToLower().Trim();
            var b = msg2.ToLower().Trim();

            // Exact match
            if (a == b) return true;

            // One contains the other
            if (a.Length > 10 && b.Length > 10)
            {
                if (a.Contains(b) || b.Contains(a)) return true;
            }

            // Word overlap > 60%
            var wordsA = new HashSet<string>(a.Split(new[] { ' ', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries));
            var wordsB = new HashSet<string>(b.Split(new[] { ' ', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries));
            if (wordsA.Count > 3 && wordsB.Count > 3)
            {
                int overlap = wordsA.Intersect(wordsB).Count();
                double ratio = (double)overlap / Math.Min(wordsA.Count, wordsB.Count);
                if (ratio > 0.6) return true;
            }

            return false;
        }

        /// <summary>
        /// Sanitize tool input for report — remove very large arrays (element_ids),
        /// keep only summary info.
        /// </summary>
        private Dictionary<string, object> SanitizeInput(Dictionary<string, object> input)
        {
            if (input == null) return null;

            var clean = new Dictionary<string, object>();
            foreach (var kv in input)
            {
                if (kv.Key == "code")
                {
                    // Don't duplicate code in tool_call — it's in execute_code events
                    clean[kv.Key] = "(see ExecuteCode event)";
                    continue;
                }

                if (kv.Value is System.Collections.IList list && list.Count > 20)
                {
                    clean[kv.Key] = $"[{list.Count} items]";
                    continue;
                }

                clean[kv.Key] = kv.Value;
            }
            return clean;
        }

        /// <summary>
        /// Summarize resolved input — show element_ids count instead of full array.
        /// Only include if different from original.
        /// </summary>
        private Dictionary<string, object> SummarizeResolved(Dictionary<string, object> resolved)
        {
            if (resolved == null) return null;

            var summary = new Dictionary<string, object>();
            foreach (var kv in resolved)
            {
                if (kv.Key == "code") continue;

                if (kv.Value is System.Collections.IList list)
                {
                    summary[$"{kv.Key}_count"] = list.Count;
                }
                else
                {
                    summary[kv.Key] = kv.Value;
                }
            }
            return summary.Count > 0 ? summary : null;
        }

        /// <summary>
        /// Build a compact summary of result data — counts and key fields only.
        /// </summary>
        private Dictionary<string, object> BuildDataSummary(Dictionary<string, object> data)
        {
            if (data == null) return null;

            var summary = new Dictionary<string, object>();
            var importantKeys = new HashSet<string>
            {
                "total_count", "selected_count", "applied_count", "affected_count",
                "schedule_id", "schedule_name", "view_id", "view_name",
                "result_ref", "output_path", "format", "field_count",
                "filter_count", "sort_count", "modified_count", "created_count"
            };

            foreach (var kv in data)
            {
                if (importantKeys.Contains(kv.Key))
                {
                    summary[kv.Key] = kv.Value;
                }
                else if (kv.Value is System.Collections.IList list)
                {
                    summary[$"{kv.Key}_count"] = list.Count;
                }
                // Skip large/unimportant fields
            }

            return summary.Count > 0 ? summary : null;
        }

        /// <summary>
        /// Build a compact parameter summary for non-ExecuteCode tool calls.
        /// Shows only the most important 2-3 params as key=value pairs.
        /// </summary>
        private string BuildCompactParamSummary(Dictionary<string, object> input)
        {
            if (input == null || input.Count == 0) return "";

            var parts = new List<string>();
            // Priority keys to show
            var priorityKeys = new[] { "category", "categories", "mode", "link_name", "link_names",
                "parameter_name", "color", "element_ids_ref", "view_name", "filter", "scope" };

            foreach (var key in priorityKeys)
            {
                if (input.TryGetValue(key, out var val) && val != null)
                {
                    var valStr = val.ToString();
                    if (val is System.Collections.IList list)
                        valStr = $"[{list.Count} items]";
                    else if (valStr.Length > 40)
                        valStr = valStr.Substring(0, 37) + "...";
                    parts.Add($"{key}={valStr}");
                    if (parts.Count >= 3) break;
                }
            }

            // If no priority keys found, show first 2 non-code params
            if (parts.Count == 0)
            {
                foreach (var kv in input)
                {
                    if (kv.Key == "code") continue;
                    var valStr = kv.Value?.ToString() ?? "null";
                    if (kv.Value is System.Collections.IList list)
                        valStr = $"[{list.Count} items]";
                    else if (valStr.Length > 40)
                        valStr = valStr.Substring(0, 37) + "...";
                    parts.Add($"{kv.Key}={valStr}");
                    if (parts.Count >= 2) break;
                }
            }

            return parts.Count > 0 ? $" | {string.Join(", ", parts)}" : "";
        }

        private string SerializeCompact(object obj)
        {
            try
            {
                var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false });
                return json.Length > 300 ? json.Substring(0, 297) + "..." : json;
            }
            catch
            {
                return "(serialization error)";
            }
        }

        private static string TruncateForReport(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.Length <= maxLen) return text;
            return text.Substring(0, maxLen - 3) + "...";
        }

        private static string EscapeMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // Replace newlines with markdown-safe line breaks
            return text.Replace("\n", "\n> ");
        }

        private static string GetPluginVersion()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                return assembly.GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        public bool HasData => _turns.Count > 0 || _toolCalls.Count > 0;

        // ── Zexus Diagnostic Classifiers ──

        private static string ClassifyECPhase(string code)
        {
            if (string.IsNullOrEmpty(code)) return "unknown";
            var lower = code.ToLower();

            bool hasTransaction = lower.Contains("transaction") && (lower.Contains(".start()") || lower.Contains(".commit()"));
            bool hasCollector = lower.Contains("filteredelementcollector") || lower.Contains("lookupparameter") || lower.Contains("getparameter");
            bool hasUiOnly = (lower.Contains("requestviewchange") || lower.Contains("selection.set") || lower.Contains("showelements"))
                             && !hasTransaction;

            if (hasUiOnly && !hasTransaction && !hasCollector) return "ui_only";
            if (hasTransaction && hasCollector) return "mixed";
            if (hasTransaction) return "write";
            if (hasCollector) return "read";
            return "unknown";
        }

        private static string ClassifyCompileError(string stderr)
        {
            if (string.IsNullOrEmpty(stderr)) return null;
            var lower = stderr.ToLower();
            if (lower.Contains("does not contain a definition for")) return "missing_member";
            if (lower.Contains("cannot convert from") || lower.Contains("cannot implicitly convert")) return "type_mismatch";
            if (lower.Contains("identifier expected") || lower.Contains("modifier") || lower.Contains("; expected")) return "syntax_error";
            return "other";
        }

        private static string ClassifyRuntimeError(string exceptionType)
        {
            if (string.IsNullOrEmpty(exceptionType)) return null;
            if (exceptionType.Contains("NullReference")) return "null_reference";
            if (exceptionType.Contains("InvalidOperation")) return "invalid_operation";
            if (exceptionType.Contains("Argument")) return "invalid_argument";
            return "other";
        }

        private void WriteTurnChainSummary(StringBuilder sb)
        {
            sb.AppendLine("### Turn Chain Summary");
            sb.AppendLine();

            var turnGroups = _executeCodeEvents.GroupBy(ec => ec.TurnIndex).OrderBy(g => g.Key);
            foreach (var group in turnGroups)
            {
                int turnIdx = group.Key;
                var events = group.ToList();
                var turn = _turns.FirstOrDefault(t => t.TurnIndex == turnIdx);
                string userMsg = TruncateForReport(turn?.UserMessage, 80);

                // Build chain string
                var chain = events.Select(ec => {
                    string phaseUpper = (ec.Phase ?? "?").ToUpper();
                    string status = ec.Outcome == "success" ? "OK"
                                  : ec.Outcome == "compile_fail" ? $"FAIL(compile:{ec.ErrorCategory ?? "?"})"
                                  : ec.Outcome == "runtime_fail" ? $"FAIL(runtime:{ec.ErrorCategory ?? "?"})"
                                  : ec.Outcome ?? "?";
                    return $"{phaseUpper}_{status}";
                });

                // Compute pattern flags
                bool writeBeforeRead = false;
                int firstWriteIdx = events.FindIndex(e => e.Phase == "write" || e.Phase == "mixed");
                int firstReadIdx = events.FindIndex(e => e.Phase == "read" || e.Phase == "mixed");
                writeBeforeRead = firstWriteIdx >= 0 && (firstReadIdx < 0 || firstWriteIdx < firstReadIdx);

                int compileErrors = events.Count(e => e.Outcome == "compile_fail");
                string finalOutcome = events.Last().Outcome ?? "unknown";
                bool hasSuccessfulWrite = events.Any(e => (e.Phase == "write" || e.Phase == "mixed") && e.Outcome == "success");

                sb.AppendLine($"**Turn {turnIdx}**: {userMsg}");
                sb.AppendLine($"  Chain: {string.Join(" → ", chain)}");
                sb.AppendLine($"  EC calls: {events.Count} | compile fails: {compileErrors} | write-before-read: {writeBeforeRead} | has_successful_write: {hasSuccessfulWrite} | last_ec_outcome: {finalOutcome}");
                sb.AppendLine();
            }
        }
    }

    // ═══════════════════════════════════════════════════════
    // Data Models
    // ═══════════════════════════════════════════════════════

    #region SessionReporter Data Models

    internal class TurnRecord
    {
        public int TurnIndex;
        public string TimestampUtc;
        public string UserMessage;
        public string AssistantMessage;
        public string WorkingMemorySnapshot;
        public FrictionSignals FrictionSignals;
        public ToolFilterSnapshot ToolFilter;
    }

    internal class FrictionSignals
    {
        public bool UserRepeatedRequest;
        public bool AssistantCorrectedItself;
        public bool TurnHadErrors;
        public bool ExecuteCodeAsWorkaround;
        public bool HallucinationDetected;

        public bool HasFriction => UserRepeatedRequest || AssistantCorrectedItself ||
                                   TurnHadErrors || ExecuteCodeAsWorkaround || HallucinationDetected;
    }

    internal class ToolCallEvent
    {
        public int TurnIndex;
        public string TimestampUtc;
        public string ToolName;
        public Dictionary<string, object> InputOriginal;
        public Dictionary<string, object> InputResolved;
        public bool Success;
        public string Message;
        public string Warning;
        public string ErrorType;
        public string ResultRef;
        public long DurationMs;
        public Dictionary<string, object> DataSummary;
    }

    internal class ExecuteCodeEvent
    {
        public int TurnIndex;
        public string TimestampUtc;
        public string Description;
        public string Code;
        public bool CompileSuccess;
        public bool RuntimeSuccess;
        public string Stdout;
        public string Stderr;
        public string ExceptionType;
        public Dictionary<string, object> ZexusJson;
        public SideEffects ModelSideEffects;

        // Zexus diagnostic fields
        public string Phase;         // "read", "write", "mixed", "ui_only", "unknown"
        public string Outcome;       // "success", "compile_fail", "runtime_fail"
        public string ErrorCategory; // "missing_member", "type_mismatch", "syntax_error", "null_reference", "invalid_operation", "other"
    }

    internal class SideEffects
    {
        public int CreatedCount;
        public int ModifiedCount;
        public int DeletedCount;

        public bool HasEffects => CreatedCount > 0 || ModifiedCount > 0 || DeletedCount > 0;
    }

    internal class ToolFilterSnapshot
    {
        public int OriginalCount;
        public int FilteredCount;
        public List<string> SelectedBuckets;
        public Dictionary<string, int> BucketScores;
        public List<string> SelectedTools;
        public bool UsedFallback;
        public string FallbackReason;
        public string MessageType;  // normal_request | confirmation_message | short_message_fallback
    }

    #endregion
}
