using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace Zexus.Models
{
    public enum ThinkingNodeStatus { Pending, Active, Completed, Failed }

    // ─── Output Preview Panel Models ───

    public enum OutputRecordType
    {
        ScheduleCreated,
        ScheduleModified,
        ParameterCreated,
        ParameterSet,
        FileExported,
        FilePrinted,
        ViewCreated,
        ViewModified,       // Category visibility, view filters, etc.
        CodeExecuted
    }

    /// <summary>
    /// One row inside a batch parameter change record.
    /// </summary>
    public class ParameterChangeEntry
    {
        public long ElementId { get; set; }
        public string ElementName { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public bool IsReverted { get; set; }
    }

    /// <summary>
    /// One row inside a schedule modification aggregation record.
    /// Supports undo: UndoToolName + UndoData describe how to reverse this change.
    /// </summary>
    public class ScheduleChangeEntry
    {
        public string ChangeType { get; set; }   // "Field" / "Format" / "Filter" / "Sort"
        public string Action { get; set; }        // "added" / "removed" / "reordered" / "set" / "cleared"
        public string FieldName { get; set; }     // the field/column name involved
        public string Detail { get; set; }        // extra info (e.g. "position 3", "= Level 1")

        // ── Undo support ──
        public bool IsReverted { get; set; }
        public bool CanUndo { get; set; }
        public string UndoAction { get; set; }    // "undo" or "delete"
        public string UndoToolName { get; set; }  // tool to call for reversal (e.g. "AddScheduleField")
        public Dictionary<string, object> UndoData { get; set; } // parameters for the reversal call
    }

    public class OutputRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public OutputRecordType RecordType { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string IconGlyph { get; set; }
        public Color IconColor { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string ToolName { get; set; }

        // ── Navigation fields ──
        public long? ViewId { get; set; }
        public string FilePath { get; set; }
        public List<string> FilePaths { get; set; }
        public string FolderPath { get; set; }

        // ── Batch parameter changes (expandable) ──
        public string ParameterName { get; set; }
        public List<ParameterChangeEntry> ChangeEntries { get; set; }

        // ── Schedule modification aggregation (expandable) ──
        public string ScheduleName { get; set; }
        public long? ScheduleId { get; set; }
        public List<ScheduleChangeEntry> ScheduleChangeEntries { get; set; }

        // ── Parent-child relationship ──
        // When a child record's host (View/Schedule) is deleted, the child is grayed out.
        public string ParentRecordId { get; set; }

        // ── Record-level undo (for ViewModified, etc.) ──
        public bool CanUndoRecord { get; set; }
        public bool IsRecordReverted { get; set; }
        public string UndoToolName { get; set; }
        public Dictionary<string, object> UndoData { get; set; }

        // ── Full result data ──
        public Dictionary<string, object> Data { get; set; }

        // ── Computed ──
        public bool IsClickable => ViewId.HasValue || FilePath != null || (FilePaths != null && FilePaths.Count > 0);
        public bool IsExpandable =>
            (ChangeEntries != null && ChangeEntries.Count > 0) ||
            (ScheduleChangeEntries != null && ScheduleChangeEntries.Count > 0);

        // ── Undo support (parameter changes) ──
        public bool IsFullyReverted =>
            ChangeEntries != null && ChangeEntries.Count > 0 &&
            ChangeEntries.All(e => e.IsReverted);
        public int RevertedCount =>
            ChangeEntries?.Count(e => e.IsReverted) ?? 0;

        // ── Undo support (schedule changes) ──
        public bool IsScheduleFullyReverted =>
            ScheduleChangeEntries != null && ScheduleChangeEntries.Count > 0 &&
            ScheduleChangeEntries.Where(e => e.CanUndo).All(e => e.IsReverted);
        public int ScheduleRevertedCount =>
            ScheduleChangeEntries?.Count(e => e.IsReverted) ?? 0;
        public bool IsDeleted { get; set; }
    }

    public class ThinkingChainNode
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public ThinkingNodeStatus Status { get; set; }
        public string IconGlyph { get; set; }
        public Color NodeColor { get; set; }
        public DateTime Timestamp { get; set; }

        // ── Rich data fields ──
        public string ToolName { get; set; }
        public string Description { get; set; }
        public string CodeSnippet { get; set; }
        public string Output { get; set; }
        public Dictionary<string, object> InputParams { get; set; }
        public Dictionary<string, object> ResultData { get; set; }
        public long DurationMs { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Transaction Journal entry — system-level record of what happened.
    /// Zero LLM dependency (except optional ZEXUS_JSON enrichment).
    /// </summary>
    /// <summary>
    /// Detail about a single element affected by a Transaction.
    /// </summary>
    public class ElementDetail
    {
        public long ElementId { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public bool IsView { get; set; }     // True if element is a View (for click-to-navigate)
    }

    public class TransactionJournalEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public int TurnIndex { get; set; }
        public DateTime Timestamp { get; set; }
        public string Description { get; set; }        // From Transaction name, strip "AI Agent: " prefix
        public bool IsWrite { get; set; }               // has_transaction
        public bool Success { get; set; }
        public int CreatedCount { get; set; }
        public int ModifiedCount { get; set; }
        public int DeletedCount { get; set; }

        // System-level element tracking (from DocumentChanged event)
        public List<ElementDetail> CreatedElements { get; set; } = new List<ElementDetail>();
        public List<ElementDetail> ModifiedElements { get; set; } = new List<ElementDetail>();
        public List<ElementDetail> DeletedElements { get; set; } = new List<ElementDetail>();

        // Optional — from ZEXUS_JSON if available
        public long? ViewId { get; set; }               // For click-to-navigate
        public string OutputType { get; set; }           // For icon selection

        // For display
        public string IconGlyph => IsWrite
            ? (CreatedCount > 0 ? "➕" : ModifiedCount > 0 ? "✏️" : DeletedCount > 0 ? "🗑" : "📝")
            : "🔍";
        public string ImpactSummary => IsWrite
            ? string.Join(", ", new[] {
                CreatedCount > 0 ? $"created {CreatedCount}" : null,
                ModifiedCount > 0 ? $"modified {ModifiedCount}" : null,
                DeletedCount > 0 ? $"deleted {DeletedCount}" : null
              }.Where(s => s != null))
            : "Read only";
    }

    /// <summary>
    /// Groups multiple TransactionJournalEntries from the same turn for expandable display.
    /// </summary>
    public class TransactionJournalTurnGroup
    {
        public int TurnIndex { get; set; }
        public DateTime Timestamp { get; set; }
        public string TurnSummary { get; set; }          // First EC description of this turn
        public List<TransactionJournalEntry> Entries { get; set; } = new List<TransactionJournalEntry>();
        public bool IsExpanded { get; set; } = false;

        // Aggregated counts across all entries in this turn
        public int TotalCreated => Entries.Sum(e => e.CreatedCount);
        public int TotalModified => Entries.Sum(e => e.ModifiedCount);
        public int TotalDeleted => Entries.Sum(e => e.DeletedCount);

        public string AggregatedImpact
        {
            get
            {
                var parts = new List<string>();
                if (TotalCreated > 0) parts.Add($"➕ {TotalCreated} created");
                if (TotalModified > 0) parts.Add($"✏️ {TotalModified} modified");
                if (TotalDeleted > 0) parts.Add($"🗑 {TotalDeleted} deleted");
                return parts.Count > 0 ? string.Join("  " , parts) : "No changes";
            }
        }
    }

    public class WorkspaceState
    {
        public string TaskName { get; set; }
        public bool IsActive { get; set; }
        public int CompletedSteps { get; set; }
        public int TotalExpectedSteps { get; set; }
        public DateTime StartTime { get; set; }
        public List<ThinkingChainNode> ThinkingChain { get; set; } = new List<ThinkingChainNode>();
    }
}
