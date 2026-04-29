using System;
using System.Diagnostics;

namespace Zexus.Services
{
    /// <summary>
    /// Centralized logging for Zexus. Writes to Debug output (visible in
    /// Revit journal and Visual Studio Output window).
    /// Future: can be extended to write to file or structured logging.
    /// </summary>
    internal static class ZexusLogger
    {
        public static void Info(string message) =>
            Debug.WriteLine($"[Zexus] {DateTime.Now:HH:mm:ss.fff} {message}");

        public static void Warn(string message) =>
            Debug.WriteLine($"[Zexus:WARN] {DateTime.Now:HH:mm:ss.fff} {message}");

        public static void Error(string message) =>
            Debug.WriteLine($"[Zexus:ERROR] {DateTime.Now:HH:mm:ss.fff} {message}");

        public static void Error(string message, Exception ex) =>
            Debug.WriteLine($"[Zexus:ERROR] {DateTime.Now:HH:mm:ss.fff} {message}: {ex.Message}");
    }
}
