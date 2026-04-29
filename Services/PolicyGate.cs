using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Zexus.Services
{
    // ═══════════════════════════════════════════════════════
    // PolicyGate — lightweight execution control layer
    // Inserted between ResolveRefParameters and tool execution.
    // Pure C# rules — no LLM calls. Extensible via IPolicyRule.
    // ═══════════════════════════════════════════════════════

    public enum PolicyAction
    {
        Allow,
        RequireConfirmation,
        ForcePreview,
        Block
    }

    public class PolicyVerdict
    {
        public PolicyAction Action { get; set; }
        public string Message { get; set; }
        public string RuleName { get; set; }

        public static PolicyVerdict Allowed() =>
            new PolicyVerdict { Action = PolicyAction.Allow };

        public static PolicyVerdict NeedConfirmation(string ruleName, string message) =>
            new PolicyVerdict { Action = PolicyAction.RequireConfirmation, RuleName = ruleName, Message = message };

        public static PolicyVerdict NeedPreview(string ruleName, string message) =>
            new PolicyVerdict { Action = PolicyAction.ForcePreview, RuleName = ruleName, Message = message };

        public static PolicyVerdict Blocked(string ruleName, string message) =>
            new PolicyVerdict { Action = PolicyAction.Block, RuleName = ruleName, Message = message };
    }

    /// <summary>
    /// Carries session-level state that rules can inspect.
    /// Reset per task (when user starts a new request).
    /// </summary>
    public class PolicyContext
    {
        /// <summary>Operation keys that the user has explicitly confirmed.</summary>
        public HashSet<string> ConfirmedOperations { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Number of ExecuteCode calls in this task.</summary>
        public int ExecuteCodeCallCount { get; set; }

        /// <summary>Consecutive ExecuteCode failures (compilation or runtime).</summary>
        public int ConsecutiveFailures { get; set; }

        /// <summary>
        /// Blanket write confirmation flag. Set when user says "yes"/"confirm" to a
        /// PolicyGate write block. Remains true for the duration of the task so the LLM
        /// can chain multiple ExecuteCode write calls without re-confirming each one.
        /// Reset on ResetTask() (new user task).
        /// </summary>
        public bool WriteConfirmedByUser { get; set; }
    }

    /// <summary>
    /// A single policy rule. Implement this to add new execution constraints.
    /// Rules are evaluated in registration order; first non-Allow verdict wins.
    /// </summary>
    public interface IPolicyRule
    {
        string Name { get; }
        PolicyVerdict Evaluate(string toolName, Dictionary<string, object> input, PolicyContext context);
    }

    /// <summary>
    /// PolicyGate — the main entry point for execution policy checks.
    /// Called from AgentService before every tool execution.
    /// </summary>
    public class PolicyGate
    {
        private readonly List<IPolicyRule> _rules = new List<IPolicyRule>();
        private readonly PolicyContext _context = new PolicyContext();

        public PolicyGate()
        {
            _rules.Add(new ExecuteCodeTransactionRule());
            _rules.Add(new BatchWriteThresholdRule());
        }

        /// <summary>
        /// Evaluate all rules against a pending tool call.
        /// Returns the first non-Allow verdict, or Allow if all rules pass.
        /// </summary>
        public PolicyVerdict Evaluate(string toolName, Dictionary<string, object> input)
        {
            foreach (var rule in _rules)
            {
                var verdict = rule.Evaluate(toolName, input, _context);
                if (verdict.Action != PolicyAction.Allow)
                {
                    ZexusLogger.Warn($"PolicyGate: {rule.Name} → {verdict.Action}");
                    return verdict;
                }
            }
            return PolicyVerdict.Allowed();
        }

        /// <summary>
        /// Mark an operation as user-confirmed. Call this when user says "yes"/"confirm".
        /// The operation key should match what the rule generates (e.g., "exec_write:description").
        /// </summary>
        public void MarkConfirmed(string operationKey) =>
            _context.ConfirmedOperations.Add(operationKey);

        /// <summary>
        /// Set or clear the blanket write confirmation flag.
        /// Call with true when user confirms a write operation.
        /// </summary>
        public void SetWriteConfirmed(bool confirmed) =>
            _context.WriteConfirmedByUser = confirmed;

        /// <summary>Track ExecuteCode success/failure for consecutive failure detection.</summary>
        public void TrackExecuteCode(bool success)
        {
            _context.ExecuteCodeCallCount++;
            if (!success) _context.ConsecutiveFailures++;
            else _context.ConsecutiveFailures = 0;
        }

        public int ConsecutiveFailures => _context.ConsecutiveFailures;
        public int ExecuteCodeCallCount => _context.ExecuteCodeCallCount;

        /// <summary>Reset per-task state. Called when user starts a new request.</summary>
        public void ResetTask()
        {
            _context.ConfirmedOperations.Clear();
            _context.ExecuteCodeCallCount = 0;
            _context.ConsecutiveFailures = 0;
            _context.WriteConfirmedByUser = false;
        }

    }

    // ═══════════════════════════════════════════════════════
    // Rule implementations
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// ExecuteCode containing write signals (Transaction construction or doc.Delete)
    /// must have user confirmation. Uses regex to handle whitespace/formatting variants.
    /// Bulk deletes inside loops get extra scrutiny.
    /// </summary>
    public class ExecuteCodeTransactionRule : IPolicyRule
    {
        public string Name => "ExecuteCode_Transaction";

        // Matches: new Transaction(doc, "..."), new Autodesk.Revit.DB.Transaction(...)
        private static readonly Regex TransactionPattern = new Regex(
            @"\bnew\s+(Autodesk\.Revit\.DB\.)?Transaction\s*\(",
            RegexOptions.Compiled);

        // Matches: doc.Delete(, document.Delete(
        private static readonly Regex DeletePattern = new Regex(
            @"\bdoc(ument)?\s*\.\s*Delete\s*\(",
            RegexOptions.Compiled);

        /// <summary>Returns true if code contains Transaction construction or Delete call.</summary>
        public static bool HasWriteSignal(string code)
            => TransactionPattern.IsMatch(code) || DeletePattern.IsMatch(code);

        public PolicyVerdict Evaluate(string toolName, Dictionary<string, object> input, PolicyContext context)
        {
            if (toolName != "ExecuteCode") return PolicyVerdict.Allowed();

            var code = input != null && input.ContainsKey("code") ? input["code"]?.ToString() : null;
            if (string.IsNullOrEmpty(code)) return PolicyVerdict.Allowed();

            bool hasDelete = DeletePattern.IsMatch(code);

            // Only block BULK DELETES in loops — high risk, irreversible.
            // Regular Transaction writes are handled by prompt-level Task Decomposition Rule
            // (asking user to confirm the plan before executing). Double-gating at code level
            // caused 2-3x confirmation roundtrips and confused weaker LLMs into infinite loops.
            if (!hasDelete) return PolicyVerdict.Allowed();

            // Blanket confirmation: user already said "yes" to a write in this task
            if (context.WriteConfirmedByUser)
                return PolicyVerdict.Allowed();

            // Specific description match (fallback for exact re-calls)
            var desc = input.ContainsKey("description") ? input["description"]?.ToString() ?? "" : "";
            var opKey = $"exec_write:{desc}";

            if (context.ConfirmedOperations.Contains(opKey))
                return PolicyVerdict.Allowed();

            // Bulk delete in loop = block and require confirmation
            bool hasBulkDelete = hasDelete &&
                (code.Contains("foreach") || code.Contains("for (") || code.Contains("for("));

            if (hasBulkDelete)
            {
                return PolicyVerdict.NeedConfirmation(Name,
                    $"[POLICY GATE] Code contains BULK DELETE in a loop. " +
                    "Tell the user what will be deleted and how many elements, then re-call after they confirm.");
            }

            // Single doc.Delete — allow (low risk, prompt-level rules cover confirmation)
            return PolicyVerdict.Allowed();
        }
    }

    /// <summary>
    /// BatchSetParameter targeting more than THRESHOLD elements must use preview first.
    /// </summary>
    public class BatchWriteThresholdRule : IPolicyRule
    {
        public string Name => "Batch_Threshold";
        private const int THRESHOLD = 100;

        public PolicyVerdict Evaluate(string toolName, Dictionary<string, object> input, PolicyContext context)
        {
            if (toolName != "BatchSetParameter") return PolicyVerdict.Allowed();
            if (input == null) return PolicyVerdict.Allowed();

            // Preview mode is always safe
            if (input.ContainsKey("preview"))
            {
                var previewVal = input["preview"];
                if (previewVal is bool b && b) return PolicyVerdict.Allowed();
                if (previewVal is string s && s.Equals("true", StringComparison.OrdinalIgnoreCase))
                    return PolicyVerdict.Allowed();
            }

            if (input.ContainsKey("element_ids") && input["element_ids"] is System.Collections.ICollection ids)
            {
                if (ids.Count > THRESHOLD)
                {
                    return PolicyVerdict.NeedPreview(Name,
                        $"[POLICY GATE] BatchSetParameter targets {ids.Count} elements (>{THRESHOLD}). " +
                        "You MUST call with preview=true first, show the user what will change, " +
                        "then re-call with preview=false after confirmation.");
                }
            }

            return PolicyVerdict.Allowed();
        }
    }

    // BroadScopeWriteRule removed in v0.4.2 hotfix.
    // It blocked FilteredElementCollector + Transaction without OfCategory/OfClass,
    // but this was too aggressive — it caught legitimate operations like collecting
    // all elements on a specific level or workset. The prompt-level Scope-First Rule
    // already ensures the LLM acquires a bounded scope before operating.
}
