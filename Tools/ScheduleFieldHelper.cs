using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Zexus.Services;

namespace Zexus.Tools
{
    /// <summary>
    /// Shared utility for schedule field lookup with localization support.
    /// Solves the problem where ScheduleField.GetName() returns English API names
    /// but users (or AI agents) may pass localized names (e.g., Chinese "未连接高度"
    /// instead of "Unconnected Height").
    ///
    /// 4-tier matching priority:
    ///   1. Exact GetName()       — English internal API name
    ///   2. Exact ColumnHeading   — user-customized column header
    ///   3. Localized BIP label   — LabelUtils.GetLabelForBuiltInParameter()
    ///   4. Substring contains    — partial match across all tiers
    /// </summary>
    public static class ScheduleFieldHelper
    {
        /// <summary>
        /// Find a schedule field by name with localization-aware 4-tier matching.
        /// Returns the field index and ScheduleField, or (-1, null) if not found.
        /// </summary>
        public static (int index, ScheduleField field) FindField(
            ScheduleDefinition definition, string fieldName, Document doc)
        {
            if (string.IsNullOrEmpty(fieldName) || definition == null)
                return (-1, null);

            int fieldCount = definition.GetFieldCount();
            string input = fieldName.Trim();

            // Tier 1: Exact match on GetName() (English API name)
            for (int i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                if (string.Equals(field.GetName(), input, StringComparison.OrdinalIgnoreCase))
                    return (i, field);
            }

            // Tier 2: Exact match on ColumnHeading (user-customized header)
            for (int i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                if (string.Equals(field.ColumnHeading, input, StringComparison.OrdinalIgnoreCase))
                    return (i, field);
            }

            // Tier 3: Exact match on localized BuiltInParameter label
            for (int i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                string localizedName = TryGetLocalizedName(field, doc);
                if (!string.IsNullOrEmpty(localizedName) &&
                    string.Equals(localizedName, input, StringComparison.OrdinalIgnoreCase))
                    return (i, field);
            }

            // Tier 4: Substring/contains match across all names
            // First try: input is contained in one of the field names
            for (int i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                string apiName = field.GetName() ?? "";
                string heading = field.ColumnHeading ?? "";
                string localized = TryGetLocalizedName(field, doc) ?? "";

                if (apiName.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    heading.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    localized.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0)
                    return (i, field);
            }

            // Second try: one of the field names is contained in the input
            for (int i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                string apiName = field.GetName() ?? "";
                string heading = field.ColumnHeading ?? "";
                string localized = TryGetLocalizedName(field, doc) ?? "";

                if ((!string.IsNullOrEmpty(apiName) && input.IndexOf(apiName, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(heading) && input.IndexOf(heading, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(localized) && input.IndexOf(localized, StringComparison.OrdinalIgnoreCase) >= 0))
                    return (i, field);
            }

            return (-1, null);
        }

        /// <summary>
        /// Build a display list of all fields for error messages.
        /// Format: "Name (Heading) [LocalizedLabel]" — gives AI all possible names to match against.
        /// </summary>
        public static List<string> GetFieldDisplayList(ScheduleDefinition definition, Document doc)
        {
            var result = new List<string>();
            int fieldCount = definition.GetFieldCount();

            for (int i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                string apiName = field.GetName();
                string heading = field.ColumnHeading;
                string localized = TryGetLocalizedName(field, doc);

                string display = apiName;
                if (!string.IsNullOrEmpty(heading) &&
                    !string.Equals(heading, apiName, StringComparison.OrdinalIgnoreCase))
                {
                    display += $" (heading: \"{heading}\")";
                }
                if (!string.IsNullOrEmpty(localized) &&
                    !string.Equals(localized, apiName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(localized, heading, StringComparison.OrdinalIgnoreCase))
                {
                    display += $" [localized: \"{localized}\"]";
                }

                result.Add(display);
            }

            return result;
        }

        /// <summary>
        /// Resolve a ScheduleField's ParameterId to a BuiltInParameter and return
        /// its localized display label via LabelUtils. Returns null if not mappable.
        /// </summary>
        public static string TryGetLocalizedName(ScheduleField field, Document doc)
        {
            try
            {
                var paramId = field.ParameterId;
                if (paramId == null || paramId == ElementId.InvalidElementId)
                    return null;

                long idValue = RevitCompat.GetIdValue(paramId);

                // BuiltInParameter enum values are negative integers
                // (positive IDs are shared/project parameters, not BIPs)
                if (idValue >= 0)
                    return null;

                var bip = (BuiltInParameter)idValue;

                // Convert BuiltInParameter → ForgeTypeId, then get localized label.
                // Revit 2022+ deprecates LabelUtils.GetLabelForBuiltInParameter(BuiltInParameter)
                // and requires ForgeTypeId-based overload via ParameterUtils.
                var paramTypeId = ParameterUtils.GetParameterTypeId(bip);
                if (paramTypeId == null)
                    return null;

                string label = LabelUtils.GetLabelForBuiltInParameter(paramTypeId);
                return string.IsNullOrEmpty(label) ? null : label;
            }
            catch
            {
                // Not all fields map to a BuiltInParameter — silently return null
                return null;
            }
        }

        /// <summary>
        /// Convert a numeric display value to Revit internal units for a schedule field.
        /// Revit stores all values in internal units (feet for length, sq-ft for area, etc.).
        /// The AI agent sends values in project display units, so we must convert.
        ///
        /// Returns (internalValue, success). On failure, returns (rawValue, false).
        /// </summary>
        public static (double internalValue, bool converted) ConvertToInternalUnits(
            ScheduleField field, double rawValue, string rawString, Document doc)
        {
            try
            {
                var specTypeId = field.GetSpecTypeId();
                if (specTypeId == null || specTypeId == SpecTypeId.String.Text
                    || specTypeId == SpecTypeId.Boolean.YesNo
                    || specTypeId == SpecTypeId.Int.Integer)
                {
                    // Non-unit field (text, boolean, integer count) — no conversion needed
                    return (rawValue, true);
                }

#if REVIT_2025_OR_GREATER
                // Revit 2025+: UnitFormatUtils.TryParse handles display → internal conversion.
                // Try with the raw string first (may include unit suffix like "30 ft").
                if (UnitFormatUtils.TryParse(doc.GetUnits(), specTypeId, rawString, out double parsed))
                    return (parsed, true);

                // If string parse failed, manually convert using project display units.
                var formatOptions = doc.GetUnits().GetFormatOptions(specTypeId);
                var displayUnitTypeId = formatOptions.GetUnitTypeId();
                double converted = UnitUtils.ConvertToInternalUnits(rawValue, displayUnitTypeId);
                return (converted, true);
#else
                // Revit 2024 (net48): Use ForgeTypeId-based API (available since Revit 2022).
                var formatOptions = doc.GetUnits().GetFormatOptions(specTypeId);
                var displayUnitTypeId = formatOptions.GetUnitTypeId();
                double converted = UnitUtils.ConvertToInternalUnits(rawValue, displayUnitTypeId);
                return (converted, true);
#endif
            }
            catch
            {
                // Conversion failed — return raw value as fallback
                return (rawValue, false);
            }
        }

        /// <summary>
        /// Resolve a FieldId to a display name by searching schedule fields.
        /// Used by ListFilters/ListSortGroups to show field names in results.
        /// Also returns the localized name if available.
        /// </summary>
        public static (string apiName, string localizedName) ResolveFieldName(
            ScheduleDefinition definition, ScheduleFieldId fieldId, Document doc)
        {
            int fieldCount = definition.GetFieldCount();
            for (int i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                if (field.FieldId == fieldId)
                {
                    string apiName = field.GetName();
                    string localized = TryGetLocalizedName(field, doc);
                    return (apiName, localized);
                }
            }
            return ("(unknown)", null);
        }

        /// <summary>
        /// Resolve a schedule from parameters: prefers schedule_id (fast, unambiguous),
        /// falls back to schedule_name (name-based lookup).
        /// Returns (schedule, resolvedBy) or (null, errorMessage).
        /// </summary>
        public static (ViewSchedule schedule, string resolvedBy) ResolveSchedule(
            Document doc, Dictionary<string, object> parameters)
        {
            // Priority 1: schedule_id (fast, unambiguous)
            if (parameters.TryGetValue("schedule_id", out var idObj) && idObj != null)
            {
                try
                {
                    long schedId = Convert.ToInt64(idObj);
                    var elem = doc.GetElement(RevitCompat.CreateId(schedId));
                    if (elem is ViewSchedule schedule)
                        return (schedule, "schedule_id");
                }
                catch (Exception ex) { ZexusLogger.Warn($"ScheduleFieldHelper: {ex.Message}"); }
                // schedule_id was provided but invalid — don't fall through silently
                return (null, $"schedule_id '{idObj}' is not a valid schedule");
            }

            // Priority 2: schedule_name (name-based lookup)
            if (parameters.TryGetValue("schedule_name", out var nameObj) && nameObj != null)
            {
                string scheduleName = nameObj.ToString();
                if (string.IsNullOrEmpty(scheduleName))
                    return (null, "schedule_name is empty");

                var schedules = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(s => !s.IsTitleblockRevisionSchedule && s.Name.Equals(scheduleName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (schedules.Count == 1)
                    return (schedules[0], "schedule_name");

                if (schedules.Count > 1)
                    return (schedules[0], "schedule_name (multiple matches, using first)");

                return (null, $"Schedule '{scheduleName}' not found");
            }

            return (null, "Either schedule_id or schedule_name is required");
        }
    }
}
