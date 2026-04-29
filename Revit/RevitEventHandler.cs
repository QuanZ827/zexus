using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Services;

namespace Zexus
{
    public enum RevitRequestType
    {
        None,
        ExecuteTool,
        NavigateToView,
        DeleteElements
    }

    public class RevitRequest
    {
        public RevitRequestType Type { get; set; } = RevitRequestType.None;
        public string ToolName { get; set; }
        public object Parameters { get; set; }
        public TaskCompletionSource<object> CompletionSource { get; set; }
    }

    public class RevitEventHandler : IExternalEventHandler
    {
        private RevitRequest _currentRequest;
        private readonly object _lockObject = new object();
        private Tools.ToolRegistry _toolRegistry;

        private const int TOOL_TIMEOUT_MS = 30000;

        public bool IsRegistryInitialized => _toolRegistry != null;
        public int ToolCount => _toolRegistry?.Count ?? 0;

        public void SetToolRegistry(Tools.ToolRegistry registry)
        {
            _toolRegistry = registry;
            ZexusLogger.Info($"ToolRegistry set with {registry?.Count ?? 0} tools");
        }

        public async Task<object> ExecuteToolAsync(string toolName, object parameters)
        {
            ZexusLogger.Info($"ExecuteToolAsync: {toolName}");

            if (_toolRegistry == null)
            {
                return Models.ToolResult.Fail("Tool registry not initialized.");
            }

            if (!_toolRegistry.HasTool(toolName))
            {
                return Models.ToolResult.Fail($"Unknown tool: {toolName}");
            }

            if (App.RevitExternalEvent == null)
            {
                return Models.ToolResult.Fail("Revit external event not initialized.");
            }

            var request = new RevitRequest
            {
                Type = RevitRequestType.ExecuteTool,
                ToolName = toolName,
                Parameters = parameters,
                CompletionSource = new TaskCompletionSource<object>()
            };

            lock (_lockObject)
            {
                _currentRequest = request;
            }

            // Retry logic: when Revit is still processing a previous heavy operation,
            // Raise() returns Pending. Retry with exponential backoff.
            const int maxRetries = 5;
            int retryDelayMs = 500;
            ExternalEventRequest raiseResult = ExternalEventRequest.Denied;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                raiseResult = App.RevitExternalEvent.Raise();
                ZexusLogger.Info($"ExternalEvent.Raise() attempt {attempt + 1}: {raiseResult}");

                if (raiseResult == ExternalEventRequest.Accepted)
                    break;

                if (raiseResult == ExternalEventRequest.Pending && attempt < maxRetries - 1)
                {
                    ZexusLogger.Info($"Revit busy (Pending), waiting {retryDelayMs}ms before retry...");
                    await Task.Delay(retryDelayMs);
                    retryDelayMs = Math.Min(retryDelayMs * 2, 4000); // cap at 4s
                    continue;
                }

                // Denied or final Pending attempt
                return Models.ToolResult.Fail(
                    raiseResult == ExternalEventRequest.Pending
                        ? "Revit is still processing a previous operation. Please wait a moment and try again."
                        : $"Failed to raise Revit event: {raiseResult}");
            }

            try
            {
                var timeoutTask = Task.Delay(TOOL_TIMEOUT_MS);
                var completedTask = await Task.WhenAny(request.CompletionSource.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    return Models.ToolResult.Fail($"Tool execution timed out after {TOOL_TIMEOUT_MS / 1000} seconds.");
                }

                return await request.CompletionSource.Task;
            }
            catch (Exception ex)
            {
                return Models.ToolResult.Fail($"Tool execution error: {ex.Message}");
            }
        }

        public void Execute(UIApplication app)
        {
            ZexusLogger.Info("ExternalEventHandler.Execute() called");

            RevitRequest request;

            lock (_lockObject)
            {
                request = _currentRequest;
                _currentRequest = null;
            }

            if (request == null) return;

            try
            {
                object result = null;

                if (request.Type == RevitRequestType.ExecuteTool)
                {
                    result = ExecuteTool(app, request.ToolName, request.Parameters);
                }
                else if (request.Type == RevitRequestType.NavigateToView)
                {
                    result = NavigateToView(app, (long)request.Parameters);
                }
                else if (request.Type == RevitRequestType.DeleteElements)
                {
                    result = DeleteElements(app, (long[])request.Parameters);
                }

                request.CompletionSource.TrySetResult(result);
            }
            catch (Exception ex)
            {
                ZexusLogger.Error($"Execute exception: {ex.Message}");
                request.CompletionSource.TrySetResult(Models.ToolResult.Fail($"Execution error: {ex.Message}"));
            }
        }

        private object ExecuteTool(UIApplication app, string toolName, object parameters)
        {
            ZexusLogger.Info($"ExecuteTool: {toolName}");

            if (_toolRegistry == null)
            {
                return Models.ToolResult.Fail("Tool registry not initialized");
            }

            var tool = _toolRegistry.GetTool(toolName);
            if (tool == null)
            {
                return Models.ToolResult.Fail($"Unknown tool: {toolName}");
            }

            var uiDocument = app.ActiveUIDocument;
            var document = uiDocument?.Document;

            if (document == null)
            {
                return Models.ToolResult.Fail("No active document. Please open a Revit model first.");
            }

            ZexusLogger.Info($"Document: {document.Title}");

            var paramDict = parameters as System.Collections.Generic.Dictionary<string, object>;

            try
            {
                // Inject UIApplication for tools that need it (e.g., PostCommand)
                if (tool is Tools.IAppAwareTool appAware)
                    appAware.SetUIApplication(app);

                var result = tool.Execute(document, uiDocument, paramDict);
                ZexusLogger.Info($"Tool result: Success={result?.Success}");
                return result ?? Models.ToolResult.Fail("Tool returned no result");
            }
            catch (Exception ex)
            {
                ZexusLogger.Error($"Tool.Execute exception: {ex.Message}");
                return Models.ToolResult.Fail($"Tool execution failed: {ex.Message}");
            }
        }

        private object NavigateToView(UIApplication app, long viewIdValue)
        {
            try
            {
                var uiDoc = app.ActiveUIDocument;
                if (uiDoc == null) return false;
                var doc = uiDoc.Document;
                var viewId = Tools.RevitCompat.CreateId(viewIdValue);
                var view = doc.GetElement(viewId) as Autodesk.Revit.DB.View;
                if (view != null && !view.IsTemplate)
                {
                    uiDoc.RequestViewChange(view);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                ZexusLogger.Warn($"NavigateToView failed: {ex.Message}");
                return false;
            }
        }

        private object DeleteElements(UIApplication app, long[] elementIds)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) return false;
                var ids = elementIds.Select(id => Tools.RevitCompat.CreateId(id)).ToList();
                using (var t = new Transaction(doc, "Delete elements"))
                {
                    t.Start();
                    doc.Delete(ids.Where(id => doc.GetElement(id) != null).Select(id => id).ToList());
                    t.Commit();
                }
                return true;
            }
            catch (Exception ex)
            {
                ZexusLogger.Warn($"DeleteElements failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DeleteElementsAsync(long[] elementIds)
        {
            if (App.RevitExternalEvent == null) return false;
            var request = new RevitRequest
            {
                Type = RevitRequestType.DeleteElements,
                Parameters = elementIds,
                CompletionSource = new TaskCompletionSource<object>()
            };
            lock (_lockObject) { _currentRequest = request; }
            var raiseResult = App.RevitExternalEvent.Raise();
            if (raiseResult != ExternalEventRequest.Accepted) return false;
            try
            {
                var timeoutTask = Task.Delay(10000);
                var completedTask = await Task.WhenAny(request.CompletionSource.Task, timeoutTask);
                if (completedTask == timeoutTask) return false;
                return (await request.CompletionSource.Task) is bool b && b;
            }
            catch { return false; }
        }

        /// <summary>
        /// Navigate to a view by ElementId — direct Revit API call, no tool registry dependency.
        /// Used by Output Preview and Transaction Journal click-to-navigate.
        /// </summary>
        public async Task<bool> NavigateToViewAsync(long viewIdValue)
        {
            if (App.RevitExternalEvent == null) return false;

            var request = new RevitRequest
            {
                Type = RevitRequestType.NavigateToView,
                Parameters = viewIdValue,
                CompletionSource = new TaskCompletionSource<object>()
            };

            lock (_lockObject)
            {
                _currentRequest = request;
            }

            var raiseResult = App.RevitExternalEvent.Raise();
            if (raiseResult != ExternalEventRequest.Accepted)
                return false;

            try
            {
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(request.CompletionSource.Task, timeoutTask);
                if (completedTask == timeoutTask) return false;
                return (await request.CompletionSource.Task) is bool b && b;
            }
            catch { return false; }
        }

        public string GetName() => "Zexus Revit Event Handler";

    }
}
