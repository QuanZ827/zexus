using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Zexus.Services;
using Zexus.Tools;
using Zexus.Views;

namespace Zexus
{
    /// <summary>
    /// Zexus - ExecuteCode-only AI Agent for Revit BIM workflows (Testing Build)
    /// </summary>
    public class App : IExternalApplication
    {
        public static ExternalEvent RevitExternalEvent { get; private set; }
        public static RevitEventHandler RevitEventHandler { get; private set; }

        /// <summary>Revit major version (e.g. 2024, 2025, 2026). Set on startup.</summary>
        public static int RevitVersion { get; private set; }

        /// <summary>True when running on Revit 2025+ (.NET 8, new ElementId API).</summary>
        public static bool IsRevit2025OrGreater => RevitVersion >= 2025;

        private static ChatWindow _chatWindow;

        // Selection Inspector: Idling-based polling
        private static DateTime _lastSelectionPollTime = DateTime.MinValue;
        private static HashSet<long> _lastSelectionIds = new HashSet<long>();
        private const int SELECTION_POLL_INTERVAL_MS = 500;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // One-shot migration of user data from the previous brand path.
                // Safe to call every startup — no-ops once new path exists.
                MigrateUserDataFromPureEC();

                // Detect Revit version from ControlledApplication.VersionNumber
                if (int.TryParse(application.ControlledApplication.VersionNumber, out int ver))
                    RevitVersion = ver;
                else
                    RevitVersion = 2024; // safe fallback

                System.Diagnostics.Debug.WriteLine($"[Zexus] Revit version detected: {RevitVersion}");

                // Initialize ExternalEvent handler
                RevitEventHandler = new RevitEventHandler();
                RevitExternalEvent = ExternalEvent.Create(RevitEventHandler);

                // Create Ribbon UI
                CreateRibbonUI(application);

                // Subscribe to document events
                application.ControlledApplication.DocumentOpened += OnDocumentOpened;
                application.ControlledApplication.DocumentClosed += OnDocumentClosed;

                // Subscribe to Idling for Selection Inspector polling
                application.Idling += OnIdling;

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Zexus Error", $"Failed to initialize: {ex.Message}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.Idling -= OnIdling;
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            application.ControlledApplication.DocumentClosed -= OnDocumentClosed;

            // Save session data before Revit exits — prevents data loss on normal shutdown
            try
            {
                Services.SessionReporter.Instance.SaveOnShutdown();
            }
            catch (Exception ex) { ZexusLogger.Warn($"SessionReporter shutdown save failed: {ex.Message}"); }

            _chatWindow?.Close();
            RevitExternalEvent?.Dispose();

            return Result.Succeeded;
        }

        private void CreateRibbonUI(UIControlledApplication application)
        {
            string tabName = "Zexus";

            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch { /* Tab might already exist — expected on reload */ }

            var panel = application.CreateRibbonPanel(tabName, "Zexus (Testing)");

            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var buttonData = new PushButtonData(
                "ZexusAgent",
                "Zexus",
                assemblyPath,
                typeof(OpenAgentCommand).FullName
            );

            buttonData.ToolTip = "Zexus — ExecuteCode Only — Testing Build";
            buttonData.LongDescription = "A pure ExecuteCode AI agent for Revit — testing build with no predefined tools.";

            // Load icons — use high-res sources with DPI trick for sharp Ribbon display
            // pyRevit technique: embed more pixels but set higher DPI so WPF thinks it's 32x32 / 16x16 DIPs
            buttonData.LargeImage = GetHiResRibbonIcon("Zexus.Resources.icon_96.png", 32);
            buttonData.Image = GetHiResRibbonIcon("Zexus.Resources.icon_32.png", 16);

            panel.AddItem(buttonData);
        }

        /// <summary>
        /// Load an embedded PNG and set its DPI so WPF treats it as targetDip × targetDip
        /// device-independent pixels, while keeping all the source pixels for sharp rendering.
        /// E.g. 96px source with targetDip=32 → DPI = 96/32*96 = 288 → Revit sees 32×32 DIPs
        /// but renders 96 physical pixels on high-DPI screens.
        /// </summary>
        private BitmapSource GetHiResRibbonIcon(string resourceName, int targetDip)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Zexus] Resource not found: {resourceName}");
                        return null;
                    }

                    var decoder = new PngBitmapDecoder(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames[0];

                    int pixelW = frame.PixelWidth;
                    int pixelH = frame.PixelHeight;

                    // Calculate DPI so that pixelW / dpi * 96 = targetDip
                    // → dpi = pixelW * 96.0 / targetDip
                    double scaledDpi = pixelW * 96.0 / targetDip;

                    // Copy raw pixels and recreate with adjusted DPI
                    int stride = (pixelW * frame.Format.BitsPerPixel + 7) / 8;
                    byte[] pixels = new byte[stride * pixelH];
                    frame.CopyPixels(pixels, stride, 0);

                    var result = BitmapSource.Create(
                        pixelW, pixelH,
                        scaledDpi, scaledDpi,
                        frame.Format, frame.Palette,
                        pixels, stride);
                    result.Freeze();
                    return result;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Failed to load icon: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// One-shot migration of user data from %AppData%\ZexusPureEC\ to %AppData%\Zexus\.
        /// Runs only when the old path exists and the new path does not.
        /// Copies (does not move) so the old path remains as a fallback.
        /// Writes .migrated_from_pureec flag in the new path on success.
        /// </summary>
        private static void MigrateUserDataFromPureEC()
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var oldPath = Path.Combine(appData, "ZexusPureEC");
                var newPath = Path.Combine(appData, "Zexus");

                if (!Directory.Exists(oldPath)) return;
                if (Directory.Exists(newPath)) return;

                CopyDirectoryRecursive(oldPath, newPath);

                var flagPath = Path.Combine(newPath, ".migrated_from_pureec");
                File.WriteAllText(
                    flagPath,
                    "Migrated from ZexusPureEC at " + DateTime.UtcNow.ToString("o") + Environment.NewLine);

                System.Diagnostics.Debug.WriteLine(
                    $"[Zexus] Migrated user data from {oldPath} -> {newPath}");
            }
            catch (Exception ex)
            {
                // Non-fatal: never block startup over a user-data migration.
                System.Diagnostics.Debug.WriteLine($"[Zexus] User data migration failed: {ex.Message}");
            }
        }

        private static void CopyDirectoryRecursive(string source, string dest)
        {
            Directory.CreateDirectory(dest);

            foreach (var file in Directory.GetFiles(source))
            {
                var destFile = Path.Combine(dest, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: false);
            }

            foreach (var subdir in Directory.GetDirectories(source))
            {
                var destSubdir = Path.Combine(dest, Path.GetFileName(subdir));
                CopyDirectoryRecursive(subdir, destSubdir);
            }
        }

        private void OnDocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
            var briefing = ModelAnalyzer.Analyze(e.Document);
            _chatWindow?.UpdateDocumentContext(e.Document, briefing);
        }

        private void OnDocumentClosed(object sender, Autodesk.Revit.DB.Events.DocumentClosedEventArgs e)
        {
            _chatWindow?.UpdateDocumentContext(null);
        }

        private static void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastSelectionPollTime).TotalMilliseconds < SELECTION_POLL_INTERVAL_MS)
                return;
            _lastSelectionPollTime = now;

            if (_chatWindow == null || !_chatWindow.IsLoaded)
                return;

            try
            {
                var uiApp = sender as UIApplication;
                var uiDoc = uiApp?.ActiveUIDocument;
                if (uiDoc == null)
                {
                    if (_lastSelectionIds.Count > 0)
                    {
                        _lastSelectionIds.Clear();
                        _chatWindow.UpdateSelectionInspector(null);
                        SessionContext.Instance.CurrentSelectionCount = 0;
                        SessionContext.Instance.CurrentSelectionIds.Clear();
                        SessionContext.Instance.CurrentSelectionSummary = null;
                    }
                    return;
                }

                var doc = uiDoc.Document;
                var selectedIds = uiDoc.Selection.GetElementIds();

                var currentIds = new HashSet<long>();
                foreach (var id in selectedIds)
                    currentIds.Add(RevitCompat.GetIdValue(id));

                if (currentIds.SetEquals(_lastSelectionIds))
                    return;

                _lastSelectionIds = currentIds;

                if (currentIds.Count == 0)
                {
                    _chatWindow.UpdateSelectionInspector(null);
                    SessionContext.Instance.CurrentSelectionCount = 0;
                    SessionContext.Instance.CurrentSelectionIds.Clear();
                    SessionContext.Instance.CurrentSelectionSummary = null;
                    return;
                }

                var summary = BuildSelectionSummary(doc, selectedIds);
                _chatWindow.UpdateSelectionInspector(summary);

                // Push selection state to SessionContext for Working Memory injection
                SessionContext.Instance.CurrentSelectionCount = currentIds.Count;
                SessionContext.Instance.CurrentSelectionIds = currentIds.ToList();
                SessionContext.Instance.CurrentSelectionSummary = summary;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Selection poll error: {ex.Message}");
            }
        }

        private static string BuildSelectionSummary(Document doc, ICollection<ElementId> selectedIds)
        {
            if (selectedIds.Count == 1)
            {
                var id = selectedIds.First();
                var elem = doc.GetElement(id);
                if (elem == null) return $"1 element (Id: {RevitCompat.GetIdValue(id)})";

                var parts = new List<string>();

                var cat = elem.Category?.Name;
                if (!string.IsNullOrEmpty(cat)) parts.Add(cat);

                if (elem is FamilyInstance fi)
                {
                    var family = fi.Symbol?.Family?.Name;
                    var type = fi.Symbol?.Name;
                    if (!string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(type))
                        parts.Add($"{family}: {type}");
                    else if (!string.IsNullOrEmpty(type))
                        parts.Add(type);
                }
                else
                {
                    var typeId = elem.GetTypeId();
                    if (typeId != null && typeId != ElementId.InvalidElementId)
                    {
                        var typeName = doc.GetElement(typeId)?.Name;
                        if (!string.IsNullOrEmpty(typeName)) parts.Add(typeName);
                    }
                }

                parts.Add($"Id: {RevitCompat.GetIdValue(id)}");

                try
                {
                    var lvlParam = elem.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                    if (lvlParam != null && lvlParam.HasValue)
                    {
                        var lvl = doc.GetElement(lvlParam.AsElementId()) as Level;
                        if (lvl != null) parts.Add(lvl.Name);
                    }
                }
                catch (Exception ex) { ZexusLogger.Warn($"Selection inspector level lookup: {ex.Message}"); }

                try
                {
                    if (doc.IsWorkshared)
                    {
                        var wsTable = doc.GetWorksetTable();
                        var ws = wsTable.GetWorkset(elem.WorksetId);
                        if (ws != null && !string.IsNullOrEmpty(ws.Name))
                            parts.Add($"WS: {ws.Name}");
                    }
                }
                catch (Exception ex) { ZexusLogger.Warn($"Selection inspector workset lookup: {ex.Message}"); }

                try
                {
                    var markParam = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (markParam != null && markParam.HasValue)
                    {
                        var mark = markParam.AsString();
                        if (!string.IsNullOrEmpty(mark)) parts.Add($"Mark: {mark}");
                    }
                }
                catch (Exception ex) { ZexusLogger.Warn($"Selection inspector mark lookup: {ex.Message}"); }

                return string.Join("  \u00B7  ", parts);
            }
            else
            {
                var byCategory = new Dictionary<string, int>();
                foreach (var id in selectedIds)
                {
                    var elem = doc.GetElement(id);
                    var cat = elem?.Category?.Name ?? "Unknown";
                    if (byCategory.ContainsKey(cat))
                        byCategory[cat]++;
                    else
                        byCategory[cat] = 1;
                }

                var catParts = byCategory
                    .OrderByDescending(kv => kv.Value)
                    .Take(5)
                    .Select(kv => $"{kv.Key}({kv.Value})");

                var suffix = byCategory.Count > 5 ? $" +{byCategory.Count - 5} more" : "";
                return $"{selectedIds.Count} selected  \u2014  {string.Join(", ", catParts)}{suffix}";
            }
        }

        public static void ShowChatWindow(Document doc)
        {
            if (_chatWindow == null || !_chatWindow.IsLoaded)
            {
                _chatWindow = new ChatWindow();
                _chatWindow.Closed += (s, e) => _chatWindow = null;
            }

            var briefing = doc != null ? ModelAnalyzer.Analyze(doc) : null;
            _chatWindow.UpdateDocumentContext(doc, briefing);
            _chatWindow.Show();
            _chatWindow.Activate();
        }
    }

    /// <summary>
    /// Command to open the Zexus chat window
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenAgentCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                App.ShowChatWindow(doc);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
