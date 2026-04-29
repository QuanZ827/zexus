using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Zexus.Services;

namespace Zexus.Tools
{
    /// <summary>
    /// Shared helper for fuzzy category name resolution.
    /// Handles singular/plural mismatches, case-insensitive matching,
    /// and provides "did you mean?" suggestions when exact match fails.
    /// </summary>
    public static class CategoryHelper
    {
        /// <summary>
        /// Resolve a category by name with fuzzy matching.
        /// Priority: exact (case-insensitive) → singular/plural variant → contains match.
        /// Returns null only if no reasonable match found.
        /// </summary>
        public static Category ResolveCategory(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || doc == null) return null;

            var trimmed = name.Trim();

            try
            {
                // Pass 1: exact case-insensitive match
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                        return cat;
                }

                // Pass 2: singular/plural variants
                string variant = GetSingularPluralVariant(trimmed);
                if (variant != null)
                {
                    foreach (Category cat in doc.Settings.Categories)
                    {
                        if (cat.Name.Equals(variant, StringComparison.OrdinalIgnoreCase))
                            return cat;
                    }
                }

                // Pass 3: contains match (only if unambiguous — exactly one match)
                var containsMatches = new List<Category>();
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat.Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                        containsMatches.Add(cat);
                }

                if (containsMatches.Count == 1)
                    return containsMatches[0];
            }
            catch (Exception ex) { ZexusLogger.Warn($"CategoryHelper: {ex.Message}"); }

            return null;
        }

        /// <summary>
        /// Resolve a category, returning a ToolResult.Fail with suggestions if not found.
        /// Use this in tool Execute methods for consistent error messages.
        /// </summary>
        public static (Category category, string error) ResolveCategoryOrError(
            Document doc, string name, string context = "model")
        {
            var cat = ResolveCategory(doc, name);
            if (cat != null)
                return (cat, null);

            // Build suggestion message
            var suggestions = GetSuggestions(doc, name, maxSuggestions: 3);
            string msg = $"Category '{name}' not found in {context}.";
            if (suggestions.Count > 0)
                msg += $" Did you mean: {string.Join(", ", suggestions.Select(s => $"'{s}'"))}?";

            return (null, msg);
        }

        /// <summary>
        /// Get category name suggestions for a failed lookup.
        /// </summary>
        public static List<string> GetSuggestions(Document doc, string name, int maxSuggestions = 3)
        {
            if (string.IsNullOrWhiteSpace(name) || doc == null)
                return new List<string>();

            var trimmed = name.Trim().ToLowerInvariant();
            var scored = new List<(string Name, int Score)>();

            try
            {
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (string.IsNullOrEmpty(cat.Name)) continue;

                    var catLower = cat.Name.ToLowerInvariant();
                    int score = 0;

                    // Contains match
                    if (catLower.Contains(trimmed) || trimmed.Contains(catLower))
                        score += 10;

                    // Word overlap
                    var inputWords = trimmed.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
                    var catWords = catLower.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
                    int overlap = inputWords.Count(w => catWords.Any(cw => cw.Contains(w) || w.Contains(cw)));
                    score += overlap * 5;

                    // Starts with same prefix (at least 3 chars)
                    if (trimmed.Length >= 3 && catLower.StartsWith(trimmed.Substring(0, 3)))
                        score += 3;

                    if (score > 0)
                        scored.Add((cat.Name, score));
                }
            }
            catch (Exception ex) { ZexusLogger.Warn($"CategoryHelper: {ex.Message}"); }

            return scored
                .OrderByDescending(s => s.Score)
                .Take(maxSuggestions)
                .Select(s => s.Name)
                .ToList();
        }

        /// <summary>
        /// Try adding/removing trailing 's' or 'es' for singular/plural.
        /// Handles: "Device" → "Devices", "Devices" → "Device",
        ///          "Fitting" → "Fittings", "Fittings" → "Fitting", etc.
        /// </summary>
        private static string GetSingularPluralVariant(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // If ends with 's', try removing it (plural → singular)
            if (name.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                // "Devices" → "Device", "Fittings" → "Fitting"
                return name.Substring(0, name.Length - 1);
            }

            // Singular → plural: add 's'
            return name + "s";
        }
    }
}
