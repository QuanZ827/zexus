using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Zexus.Models;
using Zexus.Services;

namespace Zexus.Tools
{
    /// <summary>
    /// Executes dynamically generated C# code inside the Revit process.
    /// This is the "universal tool" — Claude generates code for operations
    /// not covered by the predefined tools.
    /// </summary>
    public class ExecuteCodeTool : IAgentTool
    {
        private const int MAX_RESULT_CHARS = 12_000;
        private const int TRUNCATION_PREVIEW_LINES = 50;

        public string Name => "ExecuteCode";

        public string Description
        {
            get
            {
                int revitVersion = App.RevitVersion;
                bool is2025Plus = App.IsRevit2025OrGreater;

                string idApi = is2025Plus
                    ? "ElementId: use .Value (long) and new ElementId((long)val). IntegerValue does NOT exist."
                    : "ElementId: use .IntegerValue (int) and new ElementId(intVal).";

                return
            "Execute C# code inside Revit. Write the method body of: " +
            "public static object Execute(Document doc, UIDocument uiDoc, StringBuilder output). " +
            "Use output.AppendLine() for results. Wrap writes in Transaction named 'AI Agent: <desc>'. " +
            "REVIT " + revitVersion + ". " + idApi + " " +
            "Return structured data via ZEXUS_JSON on the last line. " +
            "Do NOT redeclare doc, uiDoc, output. Do NOT add access modifiers (public/private). Do NOT add 'using' statements — all namespaces are pre-imported. " +
            "Revit element names cannot contain: \\\\ / : { } | ; < > ? ` ~ — use RevitCompat.SanitizeRevitName() if generating names dynamically.";
            }
        }

        public ToolSchema GetInputSchema()
        {
            return new ToolSchema
            {
                Type = "object",
                Properties = new Dictionary<string, PropertySchema>
                {
                    ["code"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "C# method body to execute. This is inserted into: " +
                            "public static object Execute(Document doc, UIDocument uiDoc, StringBuilder output) { YOUR_CODE_HERE }. " +
                            "Use output.AppendLine() to print results. Return a value or return null."
                    },
                    ["description"] = new PropertySchema
                    {
                        Type = "string",
                        Description = "Brief description of what this code does (for logging and user transparency)."
                    }
                },
                Required = new List<string> { "code" }
            };
        }

        public ToolResult Execute(Document doc, UIDocument uiDoc, Dictionary<string, object> parameters)
        {
            // Extract parameters
            if (!parameters.ContainsKey("code") || parameters["code"] == null)
            {
                return ToolResult.Fail("Missing required parameter: 'code'");
            }

            var code = parameters["code"].ToString();
            var description = parameters.ContainsKey("description") ? parameters["description"]?.ToString() : "Dynamic code execution";

            if (string.IsNullOrWhiteSpace(code))
            {
                return ToolResult.Fail("Code parameter is empty.");
            }

            // Log what we're executing
            System.Diagnostics.Debug.WriteLine($"[Zexus] ExecuteCode: {description}");
            System.Diagnostics.Debug.WriteLine($"[Zexus] Code:\n{code}");

            // Track element changes via DocumentChanged event
            var addedIds = new List<ElementId>();
            var modifiedIds = new List<ElementId>();
            var deletedIds = new List<ElementId>();
            EventHandler<DocumentChangedEventArgs> changeHandler = (s, e) =>
            {
                addedIds.AddRange(e.GetAddedElementIds());
                modifiedIds.AddRange(e.GetModifiedElementIds());
                deletedIds.AddRange(e.GetDeletedElementIds());
            };
            doc.Application.DocumentChanged += changeHandler;

            // Compile and execute
            Services.CodeExecutionResult result;
            try
            {
                result = CodeExecutionService.CompileAndExecute(code, doc, uiDoc);
            }
            finally
            {
                doc.Application.DocumentChanged -= changeHandler;
            }

            if (!result.Success)
            {
                // Compilation errors — return them so Claude can fix and retry
                if (result.CompilationErrors != null && result.CompilationErrors.Count > 0)
                {
                    var errorMsg = "Compilation failed. Fix the errors and try again:\n" +
                                   string.Join("\n", result.CompilationErrors);
                    var failResult = ToolResult.Fail(errorMsg);
                    failResult.Data["failure_type"] = "compilation";
                    failResult.Data["description"] = description;

                    // Record to session reporter
                    ReportExecuteCode(code, description,
                        compileSuccess: false, runtimeSuccess: false,
                        stdout: null, stderr: string.Join("\n", result.CompilationErrors),
                        exceptionType: null, zexusJson: null);

                    return failResult;
                }

                // Runtime error
                if (!string.IsNullOrEmpty(result.RuntimeError))
                {
                    var errorMsg = "Runtime error:\n" + result.RuntimeError;
                    if (!string.IsNullOrEmpty(result.Output))
                    {
                        errorMsg += "\n\nOutput before error:\n" + result.Output;
                    }
                    var failResult = ToolResult.Fail(errorMsg);
                    failResult.Data["failure_type"] = "runtime";
                    failResult.Data["description"] = description;

                    // Record to session reporter
                    string exType = ExtractExceptionType(result.RuntimeError);
                    ReportExecuteCode(code, description,
                        compileSuccess: true, runtimeSuccess: false,
                        stdout: result.Output, stderr: result.RuntimeError,
                        exceptionType: exType, zexusJson: null);

                    return failResult;
                }

                var unknownFail = ToolResult.Fail("Code execution failed: " + result.Output);
                unknownFail.Data["failure_type"] = "unknown";
                unknownFail.Data["description"] = description;

                ReportExecuteCode(code, description,
                    compileSuccess: true, runtimeSuccess: false,
                    stdout: result.Output, stderr: null,
                    exceptionType: null, zexusJson: null);

                return unknownFail;
            }

            // Success — return output and return value
            bool hasTransaction = Services.ExecuteCodeTransactionRule.HasWriteSignal(code);
            var data = new Dictionary<string, object>
            {
                ["output"] = result.Output ?? "",
                ["description"] = description,
                ["has_transaction"] = hasTransaction
            };

            // Populate system-level element tracking (from DocumentChanged)
            if (hasTransaction && (addedIds.Count > 0 || modifiedIds.Count > 0 || deletedIds.Count > 0))
            {
                data["sys_created"] = BuildElementDetails(doc, addedIds);
                data["sys_modified_ids"] = modifiedIds.Select(id => RevitCompat.GetIdValue(id)).ToList();
                data["sys_deleted_ids"] = deletedIds.Select(id => RevitCompat.GetIdValue(id)).ToList();
                // Set counts from system tracking if ZEXUS_JSON didn't provide them
                if (!data.ContainsKey("created_count")) data["created_count"] = (long)addedIds.Count;
                if (!data.ContainsKey("modified_count")) data["modified_count"] = (long)modifiedIds.Count;
                if (!data.ContainsKey("deleted_count")) data["deleted_count"] = (long)deletedIds.Count;
            }

            if (result.ReturnValue != null)
            {
                data["return_value"] = result.ReturnValue.ToString();
            }

            // Parse ZEXUS_JSON structured return protocol from ORIGINAL (untruncated) output.
            TryParseZexusJson(result.Output, data);

            // Truncate oversized output to prevent context window exhaustion
            var outputText = result.Output ?? "";
            if (outputText.Length > MAX_RESULT_CHARS)
            {
                var lines = outputText.Split('\n');
                var preview = string.Join("\n", lines.Take(TRUNCATION_PREVIEW_LINES));
                outputText = preview +
                    $"\n\n⚠ OUTPUT TRUNCATED — Showing first {TRUNCATION_PREVIEW_LINES} of {lines.Length} lines " +
                    $"({result.Output.Length:N0} chars total). " +
                    "Suggest the user narrow their query or add filters to reduce results.";
                ZexusLogger.Info($"[TRUNCATION] Output truncated from {result.Output.Length} to {outputText.Length} chars");
            }

            // Use truncated output in data
            data["output"] = outputText;

            // Record successful execution to session reporter
            Dictionary<string, object> zexusJsonForReport = null;
            if (data.ContainsKey("view_id") || data.ContainsKey("element_ids") || data.ContainsKey("element_count")
                || data.ContainsKey("modified_count") || data.ContainsKey("created_count"))
            {
                // Extract ZEXUS_JSON fields that were merged into data
                zexusJsonForReport = new Dictionary<string, object>();
                foreach (var key in new[] { "view_id", "element_ids", "element_count",
                    "modified_count", "created_count", "deleted_count" })
                {
                    if (data.ContainsKey(key)) zexusJsonForReport[key] = data[key];
                }
            }
            int modCount = 0, createCount = 0, delCount = 0;
            if (data.ContainsKey("modified_count") && data["modified_count"] is long mc) modCount = (int)mc;
            if (data.ContainsKey("created_count") && data["created_count"] is long cc) createCount = (int)cc;
            if (data.ContainsKey("deleted_count") && data["deleted_count"] is long dc) delCount = (int)dc;
            ReportExecuteCode(code, description,
                compileSuccess: true, runtimeSuccess: true,
                stdout: result.Output, stderr: null,
                exceptionType: null, zexusJson: zexusJsonForReport,
                createdCount: createCount, modifiedCount: modCount, deletedCount: delCount);

            var message = string.IsNullOrEmpty(outputText)
                ? (result.ReturnValue != null ? $"Code executed successfully. Return value: {result.ReturnValue}" : "Code executed successfully (no output).")
                : outputText.TrimEnd();

            return ToolResult.Ok(message, data);
        }

        /// <summary>
        /// Parse ZEXUS_JSON:{...} from the LAST line of output.
        /// Convention: the AI emits output.AppendLine("ZEXUS_JSON:" + jsonString)
        /// as the last line. The JSON is parsed and merged into the result data dict.
        /// Only parses the last occurrence to avoid false matches in output text.
        /// </summary>
        private void TryParseZexusJson(string output, Dictionary<string, object> data)
        {
            if (string.IsNullOrEmpty(output)) return;

            try
            {
                // Find the last line that starts with ZEXUS_JSON:
                const string marker = "ZEXUS_JSON:";
                int lastIndex = output.LastIndexOf(marker, StringComparison.Ordinal);
                if (lastIndex < 0) return;

                // Extract the JSON part — from marker to end of that line
                string jsonPart = output.Substring(lastIndex + marker.Length);
                int newlineIdx = jsonPart.IndexOfAny(new[] { '\r', '\n' });
                if (newlineIdx >= 0) jsonPart = jsonPart.Substring(0, newlineIdx);
                jsonPart = jsonPart.Trim();

                if (string.IsNullOrEmpty(jsonPart) || jsonPart[0] != '{') return;

                // Parse and merge
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonPart);
                if (parsed == null) return;

                foreach (var kv in parsed)
                {
                    data[kv.Key] = ConvertJsonElement(kv.Value);
                }

                System.Diagnostics.Debug.WriteLine($"[Zexus] ZEXUS_JSON parsed: {parsed.Count} keys merged into result data");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] ZEXUS_JSON parse error: {ex.Message}");
                // Non-fatal — don't break the tool result
            }
        }

        /// <summary>
        /// Record an ExecuteCode event to the SessionReporter for the facts pack.
        /// </summary>
        private void ReportExecuteCode(string code, string description,
            bool compileSuccess, bool runtimeSuccess,
            string stdout, string stderr, string exceptionType,
            Dictionary<string, object> zexusJson,
            int createdCount = 0, int modifiedCount = 0, int deletedCount = 0)
        {
            try
            {
                SessionReporter.Instance.RecordExecuteCode(
                    code, description,
                    compileSuccess, runtimeSuccess,
                    stdout, stderr, exceptionType,
                    zexusJson,
                    createdCount, modifiedCount, deletedCount);
            }
            catch
            {
                // Non-fatal — reporting should never break tool execution
            }
        }

        /// <summary>
        /// Extract exception type from runtime error string (e.g., "Autodesk.Revit.Exceptions.ArgumentException").
        /// </summary>
        /// <summary>
        /// Build element detail list from DocumentChanged added/modified IDs.
        /// Looks up name, category, and whether it's a View.
        /// </summary>
        private List<ElementDetail> BuildElementDetails(Document doc, List<ElementId> ids)
        {
            var details = new List<ElementDetail>();
            foreach (var id in ids)
            {
                try
                {
                    var el = doc.GetElement(id);
                    if (el == null) continue;
                    details.Add(new ElementDetail
                    {
                        ElementId = RevitCompat.GetIdValue(id),
                        Name = el.Name ?? "(unnamed)",
                        Category = el.Category?.Name ?? "(no category)",
                        IsView = el is Autodesk.Revit.DB.View v && !v.IsTemplate
                    });
                }
                catch { /* element may have been deleted or inaccessible */ }
            }
            return details;
        }

        private string ExtractExceptionType(string runtimeError)
        {
            if (string.IsNullOrEmpty(runtimeError)) return null;
            // Runtime errors typically start with the exception type
            var firstLine = runtimeError.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstLine != null && firstLine.Contains("Exception"))
            {
                // Extract "Namespace.ExceptionType" from "Namespace.ExceptionType: message"
                var colonIdx = firstLine.IndexOf(':');
                if (colonIdx > 0)
                    return firstLine.Substring(0, colonIdx).Trim();
                return firstLine.Trim();
            }
            return null;
        }

        /// <summary>
        /// Convert JsonElement to a usable .NET type (string, long, double, bool, list, dict).
        /// </summary>
        private object ConvertJsonElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    if (element.TryGetInt64(out long l)) return l;
                    return element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                    return null;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var item in element.EnumerateArray())
                        list.Add(ConvertJsonElement(item));
                    return list;
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in element.EnumerateObject())
                        dict[prop.Name] = ConvertJsonElement(prop.Value);
                    return dict;
                default:
                    return element.ToString();
            }
        }
    }
}
