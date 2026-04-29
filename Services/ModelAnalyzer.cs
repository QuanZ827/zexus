using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;

namespace Zexus.Services
{
    // ══════════════════════════════════════════════════════════════
    //  Data Models
    // ══════════════════════════════════════════════════════════════

    public enum ModelDiscipline
    {
        Unknown, Architectural, Structural, Mechanical, Electrical,
        Plumbing, FireProtection, Combined
    }

    public enum HealthLevel { Green, Yellow, Red }

    public class ModelBriefing
    {
        // Basic stats
        public int TotalElements;
        public int LevelCount;
        public int ViewCount;
        public int LinkedModelCount;

        // Discipline
        public ModelDiscipline Discipline;
        public string DisciplineLabel;
        public List<KeyValuePair<string, int>> TopCategories = new List<KeyValuePair<string, int>>();

        // Health
        public HealthLevel Health;
        public int WarningHigh;
        public int WarningMedium;
        public int WarningLow;
        public int WarningTotal;
        public List<WarningGroupInfo> WarningGroups = new List<WarningGroupInfo>();

        // Suggestions (max 3)
        public List<string> Suggestions = new List<string>();
    }

    public class WarningGroupInfo
    {
        public string Description;
        public string Severity; // "high", "medium", "low"
        public int Count;
    }

    // ══════════════════════════════════════════════════════════════
    //  Analyzer
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Local (zero-token) model analysis for the Welcome Card briefing.
    /// Runs on the Revit thread, target: &lt;500ms.
    /// </summary>
    public static class ModelAnalyzer
    {
        private const int PHASE_TIMEOUT_MS = 800;

        // ── Discipline indicator categories ──
        // Key = BuiltInCategory, Value = discipline label
        private static readonly Dictionary<BuiltInCategory, string> DisciplineIndicators
            = new Dictionary<BuiltInCategory, string>
        {
            // Architectural
            { BuiltInCategory.OST_Walls,           "Architectural" },
            { BuiltInCategory.OST_Doors,           "Architectural" },
            { BuiltInCategory.OST_Windows,         "Architectural" },
            { BuiltInCategory.OST_Rooms,           "Architectural" },
            { BuiltInCategory.OST_Floors,          "Architectural" },
            { BuiltInCategory.OST_Ceilings,        "Architectural" },
            { BuiltInCategory.OST_Stairs,          "Architectural" },
            { BuiltInCategory.OST_Furniture,       "Architectural" },
            { BuiltInCategory.OST_CurtainWallPanels, "Architectural" },

            // Structural
            { BuiltInCategory.OST_StructuralFraming,    "Structural" },
            { BuiltInCategory.OST_StructuralColumns,    "Structural" },
            { BuiltInCategory.OST_StructuralFoundation, "Structural" },
            { BuiltInCategory.OST_StructConnections,    "Structural" },
            { BuiltInCategory.OST_Rebar,                "Structural" },

            // Mechanical
            { BuiltInCategory.OST_DuctCurves,       "Mechanical" },
            { BuiltInCategory.OST_DuctFitting,      "Mechanical" },
            { BuiltInCategory.OST_DuctAccessory,    "Mechanical" },
            { BuiltInCategory.OST_DuctTerminal,     "Mechanical" },
            { BuiltInCategory.OST_MechanicalEquipment, "Mechanical" },
            { BuiltInCategory.OST_FlexDuctCurves,   "Mechanical" },

            // Electrical
            { BuiltInCategory.OST_Conduit,          "Electrical" },
            { BuiltInCategory.OST_ConduitFitting,   "Electrical" },
            { BuiltInCategory.OST_CableTray,        "Electrical" },
            { BuiltInCategory.OST_ElectricalEquipment, "Electrical" },
            { BuiltInCategory.OST_LightingFixtures, "Electrical" },
            { BuiltInCategory.OST_ElectricalFixtures, "Electrical" },
            { BuiltInCategory.OST_CommunicationDevices, "Electrical" },

            // Plumbing
            { BuiltInCategory.OST_PipeCurves,       "Plumbing" },
            { BuiltInCategory.OST_PipeFitting,      "Plumbing" },
            { BuiltInCategory.OST_PipeAccessory,    "Plumbing" },
            { BuiltInCategory.OST_PlumbingFixtures, "Plumbing" },
            { BuiltInCategory.OST_FlexPipeCurves,   "Plumbing" },

            // Fire Protection
            { BuiltInCategory.OST_Sprinklers,       "FireProtection" },
        };

        // Discipline weight multipliers (MEP categories are more distinctive)
        private static readonly Dictionary<string, int> DisciplineWeight
            = new Dictionary<string, int>
        {
            { "Architectural", 1 },
            { "Structural", 2 },
            { "Mechanical", 2 },
            { "Electrical", 2 },
            { "Plumbing", 2 },
            { "FireProtection", 3 },
        };

        // Filename keyword → discipline mapping
        private static readonly (string[] Keywords, ModelDiscipline Discipline)[] FilenameHints =
        {
            (new[] { "hvac", "mechanical", "mech" }, ModelDiscipline.Mechanical),
            (new[] { "electrical", "elec", "power" }, ModelDiscipline.Electrical),
            (new[] { "plumb", "piping", "sanitary" }, ModelDiscipline.Plumbing),
            (new[] { "struct", "structure" }, ModelDiscipline.Structural),
            (new[] { "arch", "architecture" }, ModelDiscipline.Architectural),
            (new[] { "fire", "sprinkler" }, ModelDiscipline.FireProtection),
            (new[] { "mep" }, ModelDiscipline.Combined),
        };

        // Discipline display labels
        private static readonly Dictionary<ModelDiscipline, string> DisciplineLabels
            = new Dictionary<ModelDiscipline, string>
        {
            { ModelDiscipline.Architectural, "Architectural" },
            { ModelDiscipline.Structural, "Structural" },
            { ModelDiscipline.Mechanical, "Mechanical (MEP)" },
            { ModelDiscipline.Electrical, "Electrical (MEP)" },
            { ModelDiscipline.Plumbing, "Plumbing (MEP)" },
            { ModelDiscipline.FireProtection, "Fire Protection" },
            { ModelDiscipline.Combined, "Combined / Multi-discipline" },
            { ModelDiscipline.Unknown, "General" },
        };

        // ══════════════════════════════════════════════════════════
        //  Main entry
        // ══════════════════════════════════════════════════════════

        public static ModelBriefing Analyze(Document doc)
        {
            var briefing = new ModelBriefing();
            var sw = Stopwatch.StartNew();

            try
            {
                // Phase 1: Category distribution + basic stats (single pass)
                var categoryBuckets = CollectCategoryDistribution(doc, briefing);
                if (sw.ElapsedMilliseconds > PHASE_TIMEOUT_MS) return Finalize(briefing);

                // Phase 2: Discipline detection
                DetectDiscipline(doc, briefing, categoryBuckets);
                if (sw.ElapsedMilliseconds > PHASE_TIMEOUT_MS) return Finalize(briefing);

                // Phase 3: Warning classification
                ClassifyWarnings(doc, briefing);
                if (sw.ElapsedMilliseconds > PHASE_TIMEOUT_MS) return Finalize(briefing);

                // Phase 4: Generate suggestions
                GenerateSuggestions(briefing);
            }
            catch
            {
                // Return whatever we have so far
            }

            return Finalize(briefing);
        }

        // ══════════════════════════════════════════════════════════
        //  Phase 1: Category distribution
        // ══════════════════════════════════════════════════════════

        private static Dictionary<string, int> CollectCategoryDistribution(
            Document doc, ModelBriefing briefing)
        {
            var buckets = new Dictionary<string, int>();
            int totalElements = 0;
            int levelCount = 0;
            int viewCount = 0;
            int linkedModelCount = 0;

            // Single-pass iteration of all non-type elements
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType();

            foreach (var elem in collector)
            {
                totalElements++;

                // Count special types
                if (elem is Level) levelCount++;
                else if (elem is View v && !v.IsTemplate) viewCount++;
                else if (elem is RevitLinkInstance) linkedModelCount++;

                // Bucket by category name
                var cat = elem.Category;
                if (cat != null && !string.IsNullOrEmpty(cat.Name))
                {
                    if (buckets.ContainsKey(cat.Name))
                        buckets[cat.Name]++;
                    else
                        buckets[cat.Name] = 1;
                }
            }

            briefing.TotalElements = totalElements;
            briefing.LevelCount = levelCount;
            briefing.ViewCount = viewCount;
            briefing.LinkedModelCount = linkedModelCount;

            // Top 5 categories
            briefing.TopCategories = buckets
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => new KeyValuePair<string, int>(kv.Key, kv.Value))
                .ToList();

            return buckets;
        }

        // ══════════════════════════════════════════════════════════
        //  Phase 2: Discipline detection
        // ══════════════════════════════════════════════════════════

        private static void DetectDiscipline(Document doc, ModelBriefing briefing,
            Dictionary<string, int> categoryBuckets)
        {
            // ── Phase 2a: Filename hint ──
            ModelDiscipline filenameHint = ModelDiscipline.Unknown;
            string docName = (doc.Title ?? "").ToLowerInvariant();
            try
            {
                string path = doc.PathName;
                if (!string.IsNullOrEmpty(path))
                    docName = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            }
            catch (Exception ex) { ZexusLogger.Warn($"ModelAnalyzer: {ex.Message}"); }

            foreach (var hint in FilenameHints)
            {
                foreach (var kw in hint.Keywords)
                {
                    if (docName.Contains(kw))
                    {
                        filenameHint = hint.Discipline;
                        break;
                    }
                }
                if (filenameHint != ModelDiscipline.Unknown) break;
            }

            // ── Phase 2b: Category scoring ──
            // Map category names back to BuiltInCategory for scoring
            var disciplineScores = new Dictionary<string, double>
            {
                { "Architectural", 0 }, { "Structural", 0 },
                { "Mechanical", 0 }, { "Electrical", 0 },
                { "Plumbing", 0 }, { "FireProtection", 0 }
            };

            // Build a name → BuiltInCategory lookup from the document
            var catNameLookup = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in DisciplineIndicators)
            {
                try
                {
                    var cat = Category.GetCategory(doc, kv.Key);
                    if (cat != null)
                        catNameLookup[cat.Name] = kv.Key;
                }
                catch (Exception ex) { ZexusLogger.Warn($"ModelAnalyzer: {ex.Message}"); }
            }

            double totalWeightedScore = 0;
            foreach (var kv in categoryBuckets)
            {
                if (catNameLookup.TryGetValue(kv.Key, out var bic) &&
                    DisciplineIndicators.TryGetValue(bic, out var disc))
                {
                    int weight = DisciplineWeight.ContainsKey(disc) ? DisciplineWeight[disc] : 1;
                    double score = kv.Value * weight;
                    disciplineScores[disc] += score;
                    totalWeightedScore += score;
                }
            }

            // ── Combine signals ──
            if (totalWeightedScore < 1)
            {
                // No discipline indicators at all
                briefing.Discipline = filenameHint != ModelDiscipline.Unknown
                    ? filenameHint : ModelDiscipline.Unknown;
            }
            else
            {
                // Check if combined (3+ disciplines each > 15%)
                int significantCount = disciplineScores.Count(kv => kv.Value / totalWeightedScore > 0.15);
                if (significantCount >= 3)
                {
                    briefing.Discipline = ModelDiscipline.Combined;
                }
                else
                {
                    // Highest scoring discipline wins
                    var topDisc = disciplineScores.OrderByDescending(kv => kv.Value).First();
                    briefing.Discipline = ParseDiscipline(topDisc.Key);

                    // Filename hint as tiebreaker if scores are close
                    if (filenameHint != ModelDiscipline.Unknown && filenameHint != briefing.Discipline)
                    {
                        var topScore = topDisc.Value;
                        var hintScore = disciplineScores.ContainsKey(filenameHint.ToString())
                            ? disciplineScores[filenameHint.ToString()] : 0;
                        // If hint discipline is within 30% of top, trust the filename
                        if (hintScore > topScore * 0.7)
                            briefing.Discipline = filenameHint;
                    }
                }
            }

            briefing.DisciplineLabel = DisciplineLabels.ContainsKey(briefing.Discipline)
                ? DisciplineLabels[briefing.Discipline]
                : "General";
        }

        // ══════════════════════════════════════════════════════════
        //  Phase 3: Warning classification
        // ══════════════════════════════════════════════════════════

        private static void ClassifyWarnings(Document doc, ModelBriefing briefing)
        {
            try
            {
                var warnings = doc.GetWarnings();
                if (warnings == null) return;

                // Group by description for expandable list
                var groups = new Dictionary<string, (string severity, int count)>(StringComparer.OrdinalIgnoreCase);

                foreach (var w in warnings)
                {
                    briefing.WarningTotal++;
                    var desc = w.GetDescriptionText() ?? "Unknown warning";
                    var descLower = desc.ToLowerInvariant();
                    string severity;

                    if (descLower.Contains("overlap") || descLower.Contains("duplicate") ||
                        descLower.Contains("collision") || descLower.Contains("constraint") ||
                        descLower.Contains("identical") || descLower.Contains("coincident") ||
                        descLower.Contains("invalid") || descLower.Contains("corrupt") ||
                        descLower.Contains("error"))
                    {
                        briefing.WarningHigh++;
                        severity = "high";
                    }
                    else if (descLower.Contains("not joined") || descLower.Contains("slightly off") ||
                             descLower.Contains("room separation") || descLower.Contains("area separation") ||
                             descLower.Contains("not enclosed") || descLower.Contains("multiple") ||
                             descLower.Contains("redundant") || descLower.Contains("highlighted") ||
                             descLower.Contains("location line"))
                    {
                        briefing.WarningMedium++;
                        severity = "medium";
                    }
                    else
                    {
                        briefing.WarningLow++;
                        severity = "low";
                    }

                    if (groups.ContainsKey(desc))
                    {
                        var existing = groups[desc];
                        groups[desc] = (existing.severity, existing.count + 1);
                    }
                    else
                    {
                        groups[desc] = (severity, 1);
                    }
                }

                // Build sorted groups: high first, then medium, then low; within each by count desc
                var severityOrder = new Dictionary<string, int> { { "high", 0 }, { "medium", 1 }, { "low", 2 } };
                briefing.WarningGroups = groups
                    .Select(kv => new WarningGroupInfo
                    {
                        Description = kv.Key,
                        Severity = kv.Value.severity,
                        Count = kv.Value.count
                    })
                    .OrderBy(g => severityOrder.ContainsKey(g.Severity) ? severityOrder[g.Severity] : 3)
                    .ThenByDescending(g => g.Count)
                    .ToList();
            }
            catch (Exception ex) { ZexusLogger.Warn($"ModelAnalyzer: {ex.Message}"); }

            // Health level
            if (briefing.WarningHigh > 10)
                briefing.Health = HealthLevel.Red;
            else if (briefing.WarningHigh > 0)
                briefing.Health = HealthLevel.Yellow;
            else
                briefing.Health = HealthLevel.Green;
        }

        // ══════════════════════════════════════════════════════════
        //  Phase 4: Suggestions
        // ══════════════════════════════════════════════════════════

        private static void GenerateSuggestions(ModelBriefing briefing)
        {
            var suggestions = new List<string>();

            // P1: High-severity warnings — observation only, let user ask the agent
            if (briefing.WarningHigh > 0)
                suggestions.Add($"{briefing.WarningHigh} high-severity warning{(briefing.WarningHigh > 1 ? "s" : "")} detected \u2014 ask me to investigate");

            // P2: Large view count
            if (briefing.ViewCount > 80)
                suggestions.Add($"{briefing.ViewCount} views in model \u2014 ask me to find unused ones");

            // P3: Large model
            if (suggestions.Count < 3 && briefing.LevelCount > 10)
                suggestions.Add($"Large model with {briefing.LevelCount} levels \u2014 ask me about scope box management");

            // Fallback
            if (suggestions.Count == 0)
                suggestions.Add("Ask me anything \u2014 I can search, analyze, and modify your model");

            briefing.Suggestions = suggestions.Take(3).ToList();
        }

        // ══════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════

        private static ModelBriefing Finalize(ModelBriefing briefing)
        {
            if (briefing.Suggestions == null || briefing.Suggestions.Count == 0)
                GenerateSuggestions(briefing);
            if (string.IsNullOrEmpty(briefing.DisciplineLabel))
                briefing.DisciplineLabel = DisciplineLabels.ContainsKey(briefing.Discipline)
                    ? DisciplineLabels[briefing.Discipline] : "General";
            return briefing;
        }

        private static ModelDiscipline ParseDiscipline(string name)
        {
            switch (name)
            {
                case "Architectural": return ModelDiscipline.Architectural;
                case "Structural": return ModelDiscipline.Structural;
                case "Mechanical": return ModelDiscipline.Mechanical;
                case "Electrical": return ModelDiscipline.Electrical;
                case "Plumbing": return ModelDiscipline.Plumbing;
                case "FireProtection": return ModelDiscipline.FireProtection;
                default: return ModelDiscipline.Unknown;
            }
        }
    }
}
