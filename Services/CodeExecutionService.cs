using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Zexus.Services
{
    /// <summary>
    /// Compiles and executes C# code dynamically inside the Revit process using Roslyn.
    /// Claude generates a method body; this service wraps it in a class, compiles, and invokes it.
    /// </summary>
    public static class CodeExecutionService
    {
        // Cached metadata references (assembly references don't change during a Revit session)
        private static List<MetadataReference> _cachedReferences;

        // Line offset of the template before Claude's code is inserted
        // Used to adjust error line numbers so Claude sees correct line references
        private const int TEMPLATE_LINE_OFFSET = 17;

        /// <summary>
        /// The code template that wraps Claude's method body into a compilable class.
        /// Claude writes the body of Execute(); we provide doc, uiDoc, and output.
        /// </summary>
        internal static string WrapCode(string userCode)
        {
            return @"
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using JsonSerializer = global::System.Text.Json.JsonSerializer;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Zexus.Tools;

public class DynamicScript
{
    public static object Execute(Document doc, UIDocument uiDoc, StringBuilder output)
    {
" + userCode + @"
    }
}
";
        }

        /// <summary>
        /// Blacklist of assembly name prefixes known to export types that conflict with
        /// BCL/Revit types during Roslyn compilation (e.g. Revizto exports JsonSerializer).
        /// Only these are filtered. Everything else — including Zexus's own NuGet deps — passes through.
        /// The global:: alias in WrapCode is the primary defense; this is belt-and-suspenders.
        /// </summary>
        private static readonly string[] _blockedAssemblyPrefixes = new[]
        {
            "Revizto",          // Revizto.Revit.2024 exports JsonSerializer conflicting with System.Text.Json
        };

        /// <summary>
        /// Returns true if the assembly is on the known-conflict blacklist.
        /// Previous approach (whitelist: filter everything in \Addins\ except Zexus) was fragile —
        /// it depended on deployment path containing "\Zexus\" and accidentally filtered out
        /// Zexus's own NuGet dependencies (System.Text.Json) on machines with different install paths.
        /// Blacklist approach: only block assemblies we KNOW cause conflicts. Safe by default.
        /// </summary>
        internal static bool IsBlockedAssembly(string assemblyName)
        {
            foreach (var prefix in _blockedAssemblyPrefixes)
            {
                if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static List<MetadataReference> GetReferences()
        {
            if (_cachedReferences != null)
                return _cachedReferences;

            _cachedReferences = new List<MetadataReference>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Skip dynamic assemblies (they have no file location)
                    if (assembly.IsDynamic)
                        continue;

                    var location = assembly.Location;
                    if (string.IsNullOrEmpty(location))
                        continue;

                    // Skip assemblies from temp/shadow-copy paths that may not exist
                    if (!File.Exists(location))
                        continue;

                    // Skip assemblies on the known-conflict blacklist (e.g. Revizto).
                    // The global:: alias in WrapCode is the primary defense; this is belt-and-suspenders.
                    var assemblyName = assembly.GetName().Name ?? "";
                    if (IsBlockedAssembly(assemblyName))
                        continue;

                    _cachedReferences.Add(MetadataReference.CreateFromFile(location));
                }
                catch (Exception ex)
                {
                    // Some assemblies may throw on accessing Location; skip them
                    ZexusLogger.Warn($"Skipping assembly reference: {ex.Message}");
                }
            }

            return _cachedReferences;
        }

        /// <summary>
        /// Strip leading 'using' directives that LLMs (especially Gemini) often prepend.
        /// These cause "Identifier expected" compilation errors because the code is a method body.
        /// </summary>
        internal static string StripLeadingUsings(string code)
        {
            if (string.IsNullOrEmpty(code)) return code;
            var lines = code.Split('\n').ToList();
            // Remove leading lines that are 'using' directives or empty
            while (lines.Count > 0)
            {
                var trimmed = lines[0].Trim();
                if (trimmed.StartsWith("using ") && trimmed.EndsWith(";"))
                    lines.RemoveAt(0);
                else if (string.IsNullOrWhiteSpace(trimmed))
                    lines.RemoveAt(0);
                else
                    break;
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Ensures the code ends with a return statement.
        /// LLMs (especially Gemini) frequently forget return null; at the end,
        /// causing "not all code paths return a value" compilation errors.
        /// This auto-appends return null; if no return is found on the last meaningful line.
        /// </summary>
        internal static string EnsureReturnStatement(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return code;

            // Trim trailing whitespace/newlines and find the last meaningful line
            var trimmed = code.TrimEnd();

            // Check if code already ends with a return statement
            // Handle: "return ...;", "return;", or closing brace of a block that contains return
            var lastLine = trimmed.Split('\n').LastOrDefault()?.Trim() ?? "";

            if (lastLine.StartsWith("return ") || lastLine.StartsWith("return;") || lastLine == "return")
                return code;

            // Also check if the last statement before closing braces is a return
            // (handles cases like: if/else blocks that both return)
            // Simple heuristic: if "return " appears in the last 3 lines, assume it's covered
            var lines = trimmed.Split('\n');
            int checkRange = Math.Min(3, lines.Length);
            for (int i = lines.Length - 1; i >= lines.Length - checkRange; i--)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("return ") || line == "return;" || line == "return")
                    return code;
            }

            // No return found — append return null;
            return code + "\nreturn null;";
        }

        /// <summary>
        /// Compiles and executes C# code inside the Revit process.
        /// </summary>
        /// <param name="code">C# method body (not a full class — just the code inside Execute())</param>
        /// <param name="doc">The active Revit Document</param>
        /// <param name="uiDoc">The active UIDocument</param>
        /// <returns>A result object with output text, return value, or error details</returns>
        public static CodeExecutionResult CompileAndExecute(string code, Document doc, UIDocument uiDoc)
        {
            // Step -1: Strip leading 'using' statements — LLMs often add these but method body doesn't accept them
            code = StripLeadingUsings(code);

            // Step 0: Auto-fix missing return statement
            // The Execute method returns object — if the code doesn't end with a return,
            // the compiler will error. This is the #1 compilation failure from Gemini.
            code = EnsureReturnStatement(code);

            // Step 1: Wrap code into a full compilable class
            var fullSource = WrapCode(code);

            // Step 2: Parse
            var syntaxTree = CSharpSyntaxTree.ParseText(fullSource);

            // Step 3: Compile
            var compilation = CSharpCompilation.Create(
                assemblyName: "DynamicScript_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                syntaxTrees: new[] { syntaxTree },
                references: GetReferences(),
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release
                )
            );

            using (var ms = new MemoryStream())
            {
                var emitResult = compilation.Emit(ms);

                // Step 4: Handle compilation errors
                if (!emitResult.Success)
                {
                    var errors = emitResult.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d =>
                        {
                            var lineSpan = d.Location.GetMappedLineSpan();
                            // Adjust line number to reference Claude's code, not the template
                            int adjustedLine = lineSpan.StartLinePosition.Line - TEMPLATE_LINE_OFFSET + 1;
                            if (adjustedLine < 1) adjustedLine = lineSpan.StartLinePosition.Line + 1;
                            return $"Line {adjustedLine}: {d.GetMessage()}";
                        })
                        .ToList();

                    return new CodeExecutionResult
                    {
                        Success = false,
                        CompilationErrors = errors,
                        Output = string.Join("\n", errors)
                    };
                }

                // Step 5: Load and execute
                ms.Seek(0, SeekOrigin.Begin);
                var assemblyBytes = ms.ToArray();

                Assembly assembly;
#if REVIT_2025_OR_GREATER
                // .NET 8: Use collectible AssemblyLoadContext for proper cleanup
                var loadContext = new System.Runtime.Loader.AssemblyLoadContext(null, isCollectible: true);
                assembly = loadContext.LoadFromStream(new MemoryStream(assemblyBytes));
#else
                // .NET Framework 4.8: Load from byte array
                assembly = Assembly.Load(assemblyBytes);
#endif

                var scriptType = assembly.GetType("DynamicScript");
                if (scriptType == null)
                {
                    return new CodeExecutionResult
                    {
                        Success = false,
                        Output = "Internal error: DynamicScript class not found in compiled assembly."
                    };
                }

                var executeMethod = scriptType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (executeMethod == null)
                {
                    return new CodeExecutionResult
                    {
                        Success = false,
                        Output = "Internal error: Execute method not found in DynamicScript class."
                    };
                }

                var output = new StringBuilder();

                try
                {
                    var returnValue = executeMethod.Invoke(null, new object[] { doc, uiDoc, output });

                    var result = new CodeExecutionResult
                    {
                        Success = true,
                        Output = output.ToString(),
                        ReturnValue = returnValue
                    };

                    return result;
                }
                catch (TargetInvocationException ex)
                {
                    // Unwrap the inner exception (the actual error from Claude's code)
                    var inner = ex.InnerException ?? ex;
                    return new CodeExecutionResult
                    {
                        Success = false,
                        Output = output.ToString(),
                        RuntimeError = $"{inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}"
                    };
                }
                catch (Exception ex)
                {
                    return new CodeExecutionResult
                    {
                        Success = false,
                        Output = output.ToString(),
                        RuntimeError = $"{ex.GetType().Name}: {ex.Message}"
                    };
                }
#if REVIT_2025_OR_GREATER
                finally
                {
                    // Allow the collectible AssemblyLoadContext to be garbage collected
                    loadContext.Unload();
                }
#endif
            }
        }
    }

    /// <summary>
    /// Result of a dynamic code execution attempt.
    /// </summary>
    public class CodeExecutionResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = "";
        public object ReturnValue { get; set; }
        public List<string> CompilationErrors { get; set; }
        public string RuntimeError { get; set; }
    }
}
