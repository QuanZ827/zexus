using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zexus.Services
{
    /// <summary>
    /// Session Context Manager - persists tool call results, supports resume after interruption.
    /// Solves the issue of losing progress after Rate Limit interruption.
    /// </summary>
    public class SessionContext
    {
        private static SessionContext _instance;
        private static readonly object _lock = new object();

        // Current Revit selection state (updated from App.OnIdling)
        public string CurrentSelectionSummary { get; set; }
        public int CurrentSelectionCount { get; set; }
        public List<long> CurrentSelectionIds { get; set; } = new List<long>();

        // Current task state
        public TaskState CurrentTask { get; private set; }

        // Tool call history (last 50 calls)
        public List<ToolCallRecord> ToolHistory { get; private set; } = new List<ToolCallRecord>();

        // Intermediate data cache
        public Dictionary<string, object> DataCache { get; private set; } = new Dictionary<string, object>();

        // Last error
        public ErrorInfo LastError { get; private set; }

        // Result ref store — maps short refs to cached data (large payloads like element_ids)
        public ResultRefStore RefStore { get; private set; } = new ResultRefStore();

        // Working memory — compact per-turn context for the LLM
        public WorkingMemory Memory { get; private set; } = new WorkingMemory();

        private SessionContext() { }

        public static SessionContext Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new SessionContext();
                    }
                }
                return _instance;
            }
        }

        #region Task Management

        /// <summary>
        /// Start a new task
        /// </summary>
        public void StartTask(string taskDescription)
        {
            CurrentTask = new TaskState
            {
                TaskId = Guid.NewGuid().ToString("N").Substring(0, 8),
                Description = taskDescription,
                Status = TaskStatus.InProgress,
                StartTime = DateTime.Now,
                Steps = new List<TaskStep>()
            };
        }

        /// <summary>
        /// Add a task step
        /// </summary>
        public void AddStep(string stepName, string status = "pending")
        {
            if (CurrentTask == null) return;

            CurrentTask.Steps.Add(new TaskStep
            {
                StepNumber = CurrentTask.Steps.Count + 1,
                Name = stepName,
                Status = status,
                StartTime = DateTime.Now
            });
        }

        /// <summary>
        /// Update current step status
        /// </summary>
        public void UpdateCurrentStep(string status, string result = null)
        {
            if (CurrentTask?.Steps == null || CurrentTask.Steps.Count == 0) return;

            var currentStep = CurrentTask.Steps[CurrentTask.Steps.Count - 1];
            currentStep.Status = status;
            currentStep.Result = result;
            currentStep.EndTime = DateTime.Now;
        }

        /// <summary>
        /// Complete the task
        /// </summary>
        public void CompleteTask(string summary)
        {
            if (CurrentTask == null) return;

            CurrentTask.Status = TaskStatus.Completed;
            CurrentTask.EndTime = DateTime.Now;
            CurrentTask.Summary = summary;
        }

        /// <summary>
        /// Mark task as interrupted (e.g. Rate Limit)
        /// </summary>
        public void InterruptTask(string reason)
        {
            if (CurrentTask == null) return;

            CurrentTask.Status = TaskStatus.Interrupted;
            CurrentTask.InterruptReason = reason;
            CurrentTask.InterruptTime = DateTime.Now;
        }

        #endregion

        #region Tool History

        /// <summary>
        /// Record a tool call
        /// </summary>
        public void RecordToolCall(string toolName, Dictionary<string, object> parameters, object result, bool success)
        {
            var record = new ToolCallRecord
            {
                ToolName = toolName,
                Parameters = parameters,
                Result = result,
                Success = success,
                Timestamp = DateTime.Now
            };

            ToolHistory.Add(record);

            // Keep last 50 records
            if (ToolHistory.Count > 50)
            {
                ToolHistory.RemoveAt(0);
            }
        }

        /// <summary>
        /// Get the most recent call result for a specific tool
        /// </summary>
        public ToolCallRecord GetLastToolCall(string toolName)
        {
            for (int i = ToolHistory.Count - 1; i >= 0; i--)
            {
                if (ToolHistory[i].ToolName == toolName)
                    return ToolHistory[i];
            }
            return null;
        }

        #endregion

        #region Data Cache

        /// <summary>
        /// Cache data (e.g. search results, element lists)
        /// </summary>
        public void CacheData(string key, object data)
        {
            DataCache[key] = data;
        }

        /// <summary>
        /// Get cached data
        /// </summary>
        public T GetCachedData<T>(string key)
        {
            if (DataCache.TryGetValue(key, out var data))
            {
                if (data is T typedData)
                    return typedData;

                // Try JSON deserialization
                try
                {
                    var json = JsonSerializer.Serialize(data);
                    return JsonSerializer.Deserialize<T>(json);
                }
                catch (Exception ex) { ZexusLogger.Warn($"SessionContext: {ex.Message}"); }
            }
            return default;
        }

        /// <summary>
        /// Check if cached data exists for a key
        /// </summary>
        public bool HasCachedData(string key)
        {
            return DataCache.ContainsKey(key);
        }

        #endregion

        #region Error Handling

        /// <summary>
        /// Record an error
        /// </summary>
        public void RecordError(string errorType, string message, bool isRecoverable)
        {
            LastError = new ErrorInfo
            {
                ErrorType = errorType,
                Message = message,
                IsRecoverable = isRecoverable,
                Timestamp = DateTime.Now
            };

            if (errorType == "rate_limit")
            {
                InterruptTask("API Rate Limit exceeded");
            }
        }

        /// <summary>
        /// Check if there is a recoverable interruption
        /// </summary>
        public bool HasRecoverableInterrupt()
        {
            return CurrentTask?.Status == TaskStatus.Interrupted &&
                   LastError?.IsRecoverable == true;
        }

        #endregion

        #region Context Summary for AI

        /// <summary>
        /// Generate a context summary for the AI to understand the current state
        /// </summary>
        public string GenerateContextSummary()
        {
            var summary = new System.Text.StringBuilder();

            if (CurrentTask != null)
            {
                summary.AppendLine($"## Current Task Context");
                summary.AppendLine($"- Task: {CurrentTask.Description}");
                summary.AppendLine($"- Status: {CurrentTask.Status}");

                if (CurrentTask.Steps.Count > 0)
                {
                    summary.AppendLine($"- Progress: {CurrentTask.Steps.Count} steps completed");
                    summary.AppendLine("- Completed Steps:");
                    foreach (var step in CurrentTask.Steps)
                    {
                        summary.AppendLine($"  {step.StepNumber}. {step.Name}: {step.Status}");
                        if (!string.IsNullOrEmpty(step.Result))
                            summary.AppendLine($"     Result: {step.Result}");
                    }
                }

                if (CurrentTask.Status == TaskStatus.Interrupted)
                {
                    summary.AppendLine($"- Warning: Interrupted: {CurrentTask.InterruptReason}");
                    summary.AppendLine("- User said 'continue' - resume from last successful step");
                }
            }

            // Add cached data summary
            if (DataCache.Count > 0)
            {
                summary.AppendLine("\n## Cached Data Available:");
                foreach (var key in DataCache.Keys)
                {
                    var data = DataCache[key];
                    string dataInfo = data is System.Collections.ICollection col
                        ? $"{col.Count} items"
                        : data?.GetType().Name ?? "null";
                    summary.AppendLine($"- {key}: {dataInfo}");
                }
            }

            return summary.ToString();
        }

        #endregion

        #region Reset

        /// <summary>
        /// Clear current session (called when starting a new task)
        /// </summary>
        public void Reset()
        {
            CurrentTask = null;
            DataCache.Clear();
            LastError = null;
            RefStore.Clear();
            Memory.Clear();
            // Selection state is NOT cleared on Reset — it's tied to Revit UI, not session
            // Keep ToolHistory for analysis
        }

        #endregion
    }

    #region Data Models

    public class TaskState
    {
        public string TaskId { get; set; }
        public string Description { get; set; }
        public TaskStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime? InterruptTime { get; set; }
        public string InterruptReason { get; set; }
        public string Summary { get; set; }
        public List<TaskStep> Steps { get; set; }
    }

    public class TaskStep
    {
        public int StepNumber { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }  // pending, running, completed, failed
        public string Result { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }

    public enum TaskStatus
    {
        NotStarted,
        InProgress,
        Completed,
        Interrupted,
        Failed
    }

    public class ToolCallRecord
    {
        public string ToolName { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public object Result { get; set; }
        public bool Success { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ErrorInfo
    {
        public string ErrorType { get; set; }  // rate_limit, api_error, tool_error
        public string Message { get; set; }
        public bool IsRecoverable { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion

    #region ResultRefStore — maps short refs to cached data

    /// <summary>
    /// Stores large tool result payloads (element_ids, sheet_numbers, etc.)
    /// keyed by short human-readable refs like "search_walls_42".
    /// Downstream tools can accept *_ref inputs that resolve to cached data.
    /// Max 50 entries, FIFO eviction.
    /// </summary>
    public class ResultRefStore
    {
        private readonly Dictionary<string, RefEntry> _refs = new Dictionary<string, RefEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new List<string>(); // insertion order for FIFO
        private int _counter;
        private const int MaxEntries = 50;

        /// <summary>
        /// Store data under a generated ref key. Returns the ref string.
        /// </summary>
        public string Store(string toolName, Dictionary<string, object> data, Dictionary<string, object> filtersEcho = null)
        {
            _counter++;
            // Generate short, readable ref: "search_walls_42"
            string shortTool = toolName.Length > 16 ? toolName.Substring(0, 16) : toolName;
            string refKey = $"{shortTool.ToLower()}_{_counter}";

            // FIFO eviction
            while (_refs.Count >= MaxEntries && _order.Count > 0)
            {
                var oldest = _order[0];
                _order.RemoveAt(0);
                _refs.Remove(oldest);
            }

            _refs[refKey] = new RefEntry
            {
                ToolName = toolName,
                Data = data,
                FiltersEcho = filtersEcho,
                Timestamp = DateTime.Now
            };
            _order.Add(refKey);

            return refKey;
        }

        /// <summary>
        /// Resolve a ref key to its stored data. Returns null if not found.
        /// </summary>
        public RefEntry Resolve(string refKey)
        {
            if (string.IsNullOrEmpty(refKey)) return null;
            _refs.TryGetValue(refKey, out var entry);
            return entry;
        }

        /// <summary>
        /// Try to extract a specific data key from a ref (e.g., "element_ids" from a search ref).
        /// </summary>
        public object ResolveDataKey(string refKey, string dataKey)
        {
            var entry = Resolve(refKey);
            if (entry?.Data == null) return null;
            entry.Data.TryGetValue(dataKey, out var value);
            return value;
        }

        public void Clear()
        {
            _refs.Clear();
            _order.Clear();
            _counter = 0;
        }

        public IReadOnlyDictionary<string, RefEntry> All => _refs;
    }

    public class RefEntry
    {
        public string ToolName { get; set; }
        public Dictionary<string, object> Data { get; set; }
        public Dictionary<string, object> FiltersEcho { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion

    #region WorkingMemory — compact per-turn context

    /// <summary>
    /// Compact working memory injected into every LLM turn.
    /// Stores refs and summaries only — never raw ID lists.
    /// Max ~500 chars when serialized.
    /// </summary>
    public class WorkingMemory
    {
        // Current active view
        public string CurrentViewName { get; set; }

        // Last search summary (ref + count + filters, no raw IDs)
        public string LastSearchRef { get; set; }
        public int LastSearchCount { get; set; }
        public string LastSearchFilters { get; set; }  // compact: "category=Walls, level=Level 1"
        public bool LastSearchTruncated { get; set; }
        public int? LastSearchNextOffset { get; set; }

        // Last selection ref
        public string LastSelectionRef { get; set; }
        public int LastSelectionCount { get; set; }

        // Active schedules: id → name (max 5, FIFO)
        public Dictionary<long, string> ActiveSchedules { get; set; } = new Dictionary<long, string>();

        // Active views created in this session: id → name (max 5, FIFO)
        public Dictionary<long, string> ActiveViews { get; set; } = new Dictionary<long, string>();

        // Recent tool result refs (max 5, FIFO) — tool_name → result_ref
        public Dictionary<string, string> RecentRefs { get; set; } = new Dictionary<string, string>();

        private const int MaxSchedules = 5;
        private const int MaxViews = 5;
        private const int MaxRefs = 5;

        /// <summary>
        /// Update working memory after a tool execution.
        /// Called from AgentService middleware.
        /// </summary>
        public void UpdateFromToolResult(string toolName, string resultRef, Dictionary<string, object> data)
        {
            if (data == null) return;

            // Track result ref
            if (!string.IsNullOrEmpty(resultRef))
            {
                // FIFO on refs
                if (RecentRefs.Count >= MaxRefs)
                {
                    var oldest = RecentRefs.Keys.First();
                    RecentRefs.Remove(oldest);
                }
                RecentRefs[toolName] = resultRef;
            }

            // Tool-specific updates
            switch (toolName)
            {
                case "SearchElements":
                    LastSearchRef = resultRef;
                    if (data.TryGetValue("total_count", out var tc)) LastSearchCount = Convert.ToInt32(tc);
                    if (data.TryGetValue("has_more", out var hm2)) LastSearchTruncated = hm2 is bool b2 && b2;
                    else LastSearchTruncated = false;
                    if (data.TryGetValue("next_offset", out var no2) && no2 != null) LastSearchNextOffset = Convert.ToInt32(no2);
                    else LastSearchNextOffset = null;
                    // Build compact filters echo
                    if (data.TryGetValue("filters_echo", out var fe) && fe is Dictionary<string, object> filters)
                        LastSearchFilters = string.Join(", ", filters.Select(kv => $"{kv.Key}={kv.Value}"));
                    break;

                case "GetSelection":
                    LastSelectionRef = resultRef;
                    if (data.TryGetValue("selected_count", out var sc)) LastSelectionCount = Convert.ToInt32(sc);
                    break;

                case "CreateSchedule":
                    if (data.TryGetValue("schedule_id", out var sid) && data.TryGetValue("schedule_name", out var sn))
                    {
                        long schedId = Convert.ToInt64(sid);
                        if (ActiveSchedules.Count >= MaxSchedules)
                        {
                            var oldest = ActiveSchedules.Keys.First();
                            ActiveSchedules.Remove(oldest);
                        }
                        ActiveSchedules[schedId] = sn?.ToString();
                    }
                    break;

                case "CreateView":
                    if (data.TryGetValue("view_id", out var vid) && data.TryGetValue("view_name", out var vn))
                    {
                        long viewId = Convert.ToInt64(vid);
                        if (ActiveViews.Count >= MaxViews)
                        {
                            var oldest = ActiveViews.Keys.First();
                            ActiveViews.Remove(oldest);
                        }
                        ActiveViews[viewId] = vn?.ToString();
                    }
                    break;

                case "ActivateView":
                    if (data.TryGetValue("view_name", out var avName))
                        CurrentViewName = avName?.ToString();
                    break;
            }
        }

        /// <summary>
        /// Generate compact working memory string for LLM injection.
        /// Format: [Working Memory] key=value | key=value
        /// Target: under 500 chars.
        /// </summary>
        public string ToCompactString()
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(CurrentViewName))
                parts.Add($"view={CurrentViewName}");

            // Inject current Revit selection from SessionContext
            var ctx = SessionContext.Instance;
            if (ctx.CurrentSelectionCount > 0)
            {
                if (ctx.CurrentSelectionCount <= 10)
                {
                    var idList = string.Join(",", ctx.CurrentSelectionIds);
                    parts.Add($"selection={ctx.CurrentSelectionCount} elements ({ctx.CurrentSelectionSummary}) ids:[{idList}]");
                }
                else
                {
                    parts.Add($"selection={ctx.CurrentSelectionCount} elements ({ctx.CurrentSelectionSummary})");
                }
            }

            if (!string.IsNullOrEmpty(LastSearchRef))
            {
                var searchPart = $"last_search={LastSearchCount} elements (ref:{LastSearchRef})";
                if (!string.IsNullOrEmpty(LastSearchFilters))
                    searchPart += $" [{LastSearchFilters}]";
                if (LastSearchTruncated && LastSearchNextOffset.HasValue)
                    searchPart += $" TRUNCATED next_offset={LastSearchNextOffset.Value}";
                parts.Add(searchPart);
            }

            if (!string.IsNullOrEmpty(LastSelectionRef))
                parts.Add($"selection={LastSelectionCount} elements (ref:{LastSelectionRef})");

            if (ActiveSchedules.Count > 0)
            {
                var schedParts = ActiveSchedules.Select(kv => $"{kv.Value}(id:{kv.Key})");
                parts.Add($"schedules: {string.Join(", ", schedParts)}");
            }

            if (ActiveViews.Count > 0)
            {
                var viewParts = ActiveViews.Select(kv => $"{kv.Value}(id:{kv.Key})");
                parts.Add($"views: {string.Join(", ", viewParts)}");
            }

            if (RecentRefs.Count > 0)
            {
                var refParts = RecentRefs.Select(kv => $"{kv.Key}→{kv.Value}");
                parts.Add($"refs: {string.Join(", ", refParts)}");
            }

            if (parts.Count == 0) return null; // nothing to inject

            var result = "[Working Memory] " + string.Join(" | ", parts);

            // Hard cap at 600 chars
            if (result.Length > 600)
                result = result.Substring(0, 597) + "...";

            return result;
        }

        public void Clear()
        {
            CurrentViewName = null;
            LastSearchRef = null;
            LastSearchCount = 0;
            LastSearchFilters = null;
            LastSearchTruncated = false;
            LastSearchNextOffset = null;
            LastSelectionRef = null;
            LastSelectionCount = 0;
            ActiveSchedules.Clear();
            ActiveViews.Clear();
            RecentRefs.Clear();
        }
    }

    #endregion
}
