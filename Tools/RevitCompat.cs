using Autodesk.Revit.DB;

namespace Zexus.Tools
{
    /// <summary>
    /// Compatibility helper for Revit API differences between versions.
    /// Revit 2025+ (.NET 8) changed ElementId from int to long:
    ///   - ElementId.IntegerValue (deprecated) → ElementId.Value
    ///   - new ElementId(int) (deprecated) → new ElementId(long)
    /// </summary>
    public static class RevitCompat
    {
        /// <summary>
        /// Get the numeric value of an ElementId (works on all Revit versions).
        /// </summary>
        public static long GetIdValue(ElementId id)
        {
#if REVIT_2025_OR_GREATER
            return id.Value;
#else
#pragma warning disable CS0618 // Intentional: IntegerValue is needed for Revit 2023/2024
            return id.IntegerValue;
#pragma warning restore CS0618
#endif
        }

        /// <summary>
        /// Create an ElementId from a numeric value (works on all Revit versions).
        /// </summary>
        public static ElementId CreateId(long id)
        {
#if REVIT_2025_OR_GREATER
            return new ElementId(id);
#else
#pragma warning disable CS0618 // Intentional: int constructor is needed for Revit 2023/2024
            return new ElementId((int)id);
#pragma warning restore CS0618
#endif
        }

        /// <summary>
        /// Create an ElementId from a BuiltInCategory enum (works on all Revit versions).
        /// </summary>
        public static ElementId CreateId(BuiltInCategory bic)
        {
#if REVIT_2025_OR_GREATER
            return new ElementId(bic);
#else
            return new ElementId(bic);
#endif
        }
        /// <summary>
        /// Sanitize a string for use as a Revit element name.
        /// Removes characters that cause ArgumentException in Revit naming APIs.
        /// </summary>
        public static string SanitizeRevitName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            char[] prohibited = { '\\', '/', ':', '{', '}', '|', ';', '<', '>', '?', '`', '~' };
            foreach (var c in prohibited)
                name = name.Replace(c, '-');
            return name.Trim();
        }
    }
}
