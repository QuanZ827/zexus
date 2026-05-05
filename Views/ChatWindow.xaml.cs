using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zexus.Models;
using Zexus.Services;

using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Visibility = System.Windows.Visibility;
using SysProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Zexus.Views
{
    public partial class ChatWindow : Window
    {
        private readonly AgentService _agentService;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isProcessing;
        private Border _currentStreamingBubble;
        private TextBox _currentStreamingText;

        // ─── Image Input (clipboard paste, v0.2.1+) ───
        private readonly List<ImageAttachment> _pendingImages = new List<ImageAttachment>();
        private const int MAX_IMAGES_PER_MESSAGE = 3;

        // ─── Workspace State ───
        private WorkspaceState _workspaceState;
        private StackPanel _wsChainContainer;

        // ─── Bubble → Thinking Chain correlation ───
        // Maps agent bubble UI elements to their thinking chain snapshot.
        // When user clicks a bubble, we restore the snapshot to the workspace panel.
        private readonly Dictionary<FrameworkElement, List<ThinkingChainNode>> _bubbleThinkingChains
            = new Dictionary<FrameworkElement, List<ThinkingChainNode>>();

        // ─── Reasoning text for Thinking Chain (captured from LLM text before tool calls) ───
        private string _lastReasoningText;

        // ─── Inline Confirmation Buttons ───
        private StackPanel _confirmationPanel;

        // ─── Transaction Journal ───
        private readonly List<TransactionJournalTurnGroup> _journalTurnGroups = new List<TransactionJournalTurnGroup>();
        private int _currentTurnIndex;

        // ─── Output Preview Panel ───
        private readonly List<OutputRecord> _outputRecords = new List<OutputRecord>();
        private StackPanel _outputCardsContainer;
        private readonly HashSet<string> _expandedRecordIds = new HashSet<string>();

        // ─── Design Tokens — forwarded from ThemeManager (Dark/Light) ───
        static Color ColBg         => ThemeManager.ColBg;
        static Color ColSurface    => ThemeManager.ColSurface;
        static Color ColCard       => ThemeManager.ColCard;
        static Color ColBorder     => ThemeManager.ColBorder;
        static Color ColPrimary    => ThemeManager.ColPrimary;
        static Color ColPrimaryLt  => ThemeManager.ColPrimaryLt;
        static Color ColAccent     => ThemeManager.ColAccent;
        static Color ColSuccess    => ThemeManager.ColSuccess;
        static Color ColWarning    => ThemeManager.ColWarning;
        static Color ColError      => ThemeManager.ColError;
        static Color ColText       => ThemeManager.ColText;
        static Color ColTextSec    => ThemeManager.ColTextSec;
        static Color ColMuted      => ThemeManager.ColMuted;
        static Color ColGlass      => ThemeManager.ColGlass;
        static Color ColGlassBorder => ThemeManager.ColGlassBorder;
        static Color ColCodeBg     => ThemeManager.ColCodeBg;

        static readonly FontFamily MainFont = new FontFamily("Google Sans, Segoe UI Variable, Segoe UI, Segoe UI Emoji");
        static readonly FontFamily MonoFont = new FontFamily("Cascadia Code, Consolas, Courier New");

        public ChatWindow()
        {
            InitializeComponent();
            LoadLogoIcon();

            _agentService = new AgentService();
            _agentService.OnStreamingText += OnStreamingTextReceived;
            _agentService.OnToolExecuting += OnToolExecuting;
            _agentService.OnStatusChanged += OnStatusChanged;
            _agentService.OnProcessingStarted += OnProcessingStarted;
            _agentService.OnToolCompleted += OnToolCompleted;
            _agentService.OnReasoningForThinkingChain += OnReasoningForThinkingChain;
            _agentService.OnProcessingCompleted += OnProcessingCompleted;

            // Image-paste interceptor — must be PreviewKeyDown so it fires before the
            // built-in paste handling. (KeyDown="OnInputKeyDown" in XAML stays untouched.)
            MessageInput.PreviewKeyDown += OnInputPreviewKeyDown;

            PositionWindow();
            ThemeManager.ThemeChanged += () => Dispatcher.Invoke(ApplyTheme);

            // Apply theme after visual tree is ready (XAML defaults to dark-mode colors;
            // this ensures Light mode is applied if saved in config, and sets toggle icon)
            Loaded += (s, e) => ApplyTheme();

            _agentService.EnsureToolRegistryInitialized();

            if (!ConfigManager.IsConfigured())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowApiKeyPrompt()));
            }
        }

        private void PositionWindow()
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            if (Width > screenWidth - 40) Width = screenWidth - 40;
            Left = screenWidth - Width - 20;
            Top = (screenHeight - Height) / 2;
        }

        /// <summary>
        /// Re-apply current theme colors to all structural XAML elements.
        /// Dynamic chat bubbles pick up new colors automatically (property forwarders),
        /// but named XAML elements with hardcoded hex need explicit brush swaps.
        /// </summary>
        private void ApplyTheme()
        {
            var isDark = ThemeManager.Current == ThemeMode.Dark;

            // Toggle button icon: sun for dark → switch to light, moon for light → switch to dark
            ThemeToggleBtn.Content = isDark ? "\u2600" : "\u263D";  // ☀ : ☽
            ThemeToggleBtn.ToolTip = isDark ? "Switch to Light mode" : "Switch to Dark mode";

            // Main background
            MainBgBorder.Background = new SolidColorBrush(ColBg);

            // Outer shell gradient border
            var outerGrad = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 1) };
            if (isDark)
            {
                outerGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x20, 0x42, 0x85, 0xF4), 0));
                outerGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 0.5));
                outerGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x15, 0x7B, 0x61, 0xFF), 1));
            }
            else
            {
                outerGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 0x1A, 0x73, 0xE8), 0));
                outerGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, 0xD8, 0xDA, 0xE0), 0.5));
                outerGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x20, 0x6C, 0x47, 0xFF), 1));
            }
            OuterShellBorder.Background = outerGrad;

            // Title bar, doc bar, input bar, status bar — glass surfaces
            var surfaceBrush = new SolidColorBrush(ColSurface);
            TitleBarBorder.Background = surfaceBrush;
            DocLabelBar.Background = new SolidColorBrush(isDark
                ? Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
            InputBarBorder.Background = surfaceBrush;
            StatusBar.Background = surfaceBrush;

            // Splitters
            var borderBrush = new SolidColorBrush(ColBorder);
            WorkspaceSplitter.Background = borderBrush;
            OutputSplitter.Background = borderBrush;
            WorkspacePanel.Background = new SolidColorBrush(isDark
                ? Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            WorkspacePanel.BorderBrush = new SolidColorBrush(ColBorder);
            OutputPanel.Background = WorkspacePanel.Background;
            OutputPanel.BorderBrush = WorkspacePanel.BorderBrush;

            // Selection inspector
            SelectionInspectorBar.Background = WorkspacePanel.Background;

            // Input TextBox re-style
            MessageInput.Background = new SolidColorBrush(isDark
                ? Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            MessageInput.Foreground = new SolidColorBrush(ColText);
            MessageInput.BorderBrush = new SolidColorBrush(ColBorder);
            MessageInput.CaretBrush = new SolidColorBrush(ColText);

            // Walk the ENTIRE visual tree and remap all foreground/background colors
            RemapVisualTree(this);
        }

        // ─── Dark-mode solid color constants (XAML originals, used for bidirectional mapping) ───
        static readonly Color DarkText    = Color.FromRgb(0xE8, 0xEA, 0xED);
        static readonly Color DarkTextSec = Color.FromRgb(0x9A, 0xA0, 0xA6);
        static readonly Color DarkMuted   = Color.FromRgb(0x5F, 0x63, 0x68);
        static readonly Color LightText    = Color.FromRgb(0x1F, 0x29, 0x37);
        static readonly Color LightTextSec = Color.FromRgb(0x6B, 0x72, 0x80);
        static readonly Color LightMuted   = Color.FromRgb(0x9C, 0xA3, 0xAF);

        /// <summary>
        /// Walk the entire visual tree via VisualTreeHelper and remap all
        /// TextBlock/Button foregrounds and semi-transparent Border backgrounds.
        /// </summary>
        private void RemapVisualTree(DependencyObject root)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

                // TextBlock foreground
                if (child is TextBlock tb && tb.Foreground is SolidColorBrush tbBrush)
                {
                    var mapped = RemapForeground(tbBrush.Color);
                    if (mapped != tbBrush.Color)
                        tb.Foreground = new SolidColorBrush(mapped);
                }

                // TextBox foreground (selectable text in chat bubbles)
                if (child is System.Windows.Controls.TextBox txb && txb.Foreground is SolidColorBrush txbBrush)
                {
                    var mapped = RemapForeground(txbBrush.Color);
                    if (mapped != txbBrush.Color)
                        txb.Foreground = new SolidColorBrush(mapped);
                }

                // Button foreground (header buttons, etc.)
                if (child is Button btn && btn.Foreground is SolidColorBrush btnBrush)
                {
                    var mapped = RemapForeground(btnBrush.Color);
                    if (mapped != btnBrush.Color)
                        btn.Foreground = new SolidColorBrush(mapped);
                }

                // Border: remap semi-transparent solid backgrounds (glass surfaces)
                if (child is Border bd && bd.Background is SolidColorBrush bdBrush)
                {
                    var mapped = RemapSemiTransparent(bdBrush.Color);
                    if (mapped != bdBrush.Color)
                        bd.Background = new SolidColorBrush(mapped);
                }

                // Recurse
                RemapVisualTree(child);
            }
        }

        /// <summary>
        /// Map a foreground color between dark↔light mode.
        /// Handles solid known colors + semi-transparent white↔black pattern.
        /// </summary>
        private Color RemapForeground(Color c)
        {
            var isDark = ThemeManager.Current == ThemeMode.Dark;

            // ── Solid known colors ──
            // Dark → Light
            if (!isDark)
            {
                if (c == DarkText)    return ColText;
                if (c == DarkTextSec) return ColTextSec;
                if (c == DarkMuted)   return ColMuted;
            }
            // Light → Dark (reverse)
            else
            {
                if (c == LightText)    return ColText;
                if (c == LightTextSec) return ColTextSec;
                if (c == LightMuted)   return ColMuted;
            }

            // ── Semi-transparent white ↔ semi-transparent black ──
            // Boost alpha ×2 in light mode: dark-on-light needs higher opacity for equal readability
            if (!isDark && c.R == 0xFF && c.G == 0xFF && c.B == 0xFF && c.A > 0 && c.A < 0xFF)
            {
                byte boosted = (byte)Math.Min(0xE0, c.A * 2);
                return Color.FromArgb(boosted, 0x00, 0x00, 0x00);
            }
            if (isDark && c.R == 0x00 && c.G == 0x00 && c.B == 0x00 && c.A > 0 && c.A < 0xFF)
            {
                byte halved = (byte)Math.Max(1, c.A / 2);
                return Color.FromArgb(halved, 0xFF, 0xFF, 0xFF);
            }

            return c; // no change
        }

        /// <summary>
        /// Remap semi-transparent background colors (glass surfaces).
        /// Low-alpha white overlays in dark mode → low-alpha black overlays in light mode.
        /// Only affects colors with alpha ≤ 0x20 to avoid touching meaningful backgrounds.
        /// </summary>
        private Color RemapSemiTransparent(Color c)
        {
            var isDark = ThemeManager.Current == ThemeMode.Dark;

            // Only remap very low-alpha glass overlays (≤ 8% opacity = 0x15)
            if (!isDark && c.R == 0xFF && c.G == 0xFF && c.B == 0xFF && c.A > 0 && c.A <= 0x15)
                return Color.FromArgb(c.A, 0x00, 0x00, 0x00);
            if (isDark && c.R == 0x00 && c.G == 0x00 && c.B == 0x00 && c.A > 0 && c.A <= 0x15)
                return Color.FromArgb(c.A, 0xFF, 0xFF, 0xFF);

            return c;
        }

        public void UpdateDocumentContext(Autodesk.Revit.DB.Document doc, Services.ModelBriefing briefing = null)
        {
            Dispatcher.Invoke(() =>
            {
                if (doc != null)
                {
                    DocumentLabel.Text = doc.Title;

                    // Location
                    string pathName = null;
                    try { pathName = doc.PathName; } catch (Exception ex) { Services.ZexusLogger.Warn($"PathName access: {ex.Message}"); }
                    if (!string.IsNullOrEmpty(pathName))
                    {
                        ModelLocationLabel.Text = TruncatePath(pathName, 50);
                        ModelLocationLabel.ToolTip = pathName;
                        ModelLocationLabel.Visibility = Visibility.Visible;
                        ModelLocationSep.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        ModelLocationLabel.Visibility = Visibility.Collapsed;
                        ModelLocationSep.Visibility = Visibility.Collapsed;
                    }

                    // Cloud / ACC info
                    var hubInfo = GetCloudInfo(doc);
                    if (!string.IsNullOrEmpty(hubInfo))
                    {
                        ModelHubLabel.Text = hubInfo;
                        ModelHubLabel.Visibility = Visibility.Visible;
                        ModelHubSep.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        ModelHubLabel.Visibility = Visibility.Collapsed;
                        ModelHubSep.Visibility = Visibility.Collapsed;
                    }

                    // Welcome card model briefing
                    if (briefing != null)
                        PopulateBriefing(briefing);
                }
                else
                {
                    DocumentLabel.Text = "No document loaded";
                    ModelLocationLabel.Visibility = Visibility.Collapsed;
                    ModelLocationSep.Visibility = Visibility.Collapsed;
                    ModelHubLabel.Visibility = Visibility.Collapsed;
                    ModelHubSep.Visibility = Visibility.Collapsed;
                    SelectionInspectorBar.Visibility = Visibility.Collapsed;
                    WelcomeBriefingPanel.Visibility = Visibility.Collapsed;
                }

                _agentService.EnsureToolRegistryInitialized();
            });
        }

        private void PopulateBriefing(Services.ModelBriefing b)
        {
            // ── Discipline section ──
            if (b.Discipline != Services.ModelDiscipline.Unknown)
            {
                BriefingDisciplineLabel.Text = $"\U0001F3D7  {b.DisciplineLabel}";

                if (b.TopCategories != null && b.TopCategories.Count > 0)
                {
                    var parts = b.TopCategories.Select(kv => $"{kv.Value:N0} {kv.Key}");
                    BriefingTopCategories.Text = string.Join("  \u00B7  ", parts);
                }
                else
                {
                    BriefingTopCategories.Text = "";
                }

                BriefingDisciplineSection.Visibility = Visibility.Visible;
            }
            else
            {
                BriefingDisciplineSection.Visibility = Visibility.Collapsed;
            }

            // ── Stats line ──
            BriefingStatsLine.Text =
                $"\U0001F4CA  {b.TotalElements:N0} elements  \u00B7  " +
                $"{b.LevelCount} levels  \u00B7  " +
                $"{b.ViewCount} views  \u00B7  " +
                $"{b.LinkedModelCount} linked models";

            // ── Health section (expandable) ──
            if (b.WarningTotal > 0)
            {
                System.Windows.Media.Color dotColor;
                System.Windows.Media.Color bgTint;

                switch (b.Health)
                {
                    case Services.HealthLevel.Red:
                        dotColor = System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44);
                        bgTint = System.Windows.Media.Color.FromArgb(0x10, 0xEF, 0x44, 0x44);
                        break;
                    case Services.HealthLevel.Yellow:
                        dotColor = System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B);
                        bgTint = System.Windows.Media.Color.FromArgb(0x10, 0xF5, 0x9E, 0x0B);
                        break;
                    default: // Green
                        dotColor = System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E);
                        bgTint = System.Windows.Media.Color.FromArgb(0x10, 0x22, 0xC5, 0x5E);
                        break;
                }

                BriefingHealthDot.Background = new System.Windows.Media.SolidColorBrush(dotColor);
                BriefingHealthSection.Background = new System.Windows.Media.SolidColorBrush(bgTint);

                var parts = new List<string>();
                if (b.WarningHigh > 0) parts.Add($"{b.WarningHigh} high");
                if (b.WarningMedium > 0) parts.Add($"{b.WarningMedium} medium");
                if (b.WarningLow > 0) parts.Add($"{b.WarningLow} low");

                string clickHint = b.WarningGroups.Count > 0 ? "  (click to expand)" : "";
                BriefingHealthText.Text = $"{b.WarningTotal} warning{(b.WarningTotal > 1 ? "s" : "")} \u2014 {string.Join("  \u00B7  ", parts)}{clickHint}";

                // Populate expandable warning group list
                BriefingWarningGroupList.Children.Clear();
                if (b.WarningGroups.Count > 0)
                {
                    BriefingHealthSection.MouseLeftButtonUp -= OnHealthSectionClick;
                    BriefingHealthSection.MouseLeftButtonUp += OnHealthSectionClick;

                    foreach (var group in b.WarningGroups)
                    {
                        var severityDot = group.Severity == "high" ? "\u2022" : group.Severity == "medium" ? "\u2022" : "\u2022";
                        var severityColor = group.Severity == "high"
                            ? System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)
                            : group.Severity == "medium"
                                ? System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B)
                                : System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E);

                        // Truncate long descriptions for display
                        var displayDesc = group.Description.Length > 90
                            ? group.Description.Substring(0, 87) + "..."
                            : group.Description;

                        var row = new Border
                        {
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(8, 5, 8, 5),
                            Margin = new Thickness(0, 2, 0, 2),
                            Background = new System.Windows.Media.SolidColorBrush(ColGlass),
                            Cursor = System.Windows.Input.Cursors.Hand
                        };
                        row.MouseEnter += (s, e) => row.Background = new System.Windows.Media.SolidColorBrush(ColGlassBorder);
                        row.MouseLeave += (s, e) => row.Background = new System.Windows.Media.SolidColorBrush(ColGlass);

                        var rowStack = new StackPanel { Orientation = Orientation.Horizontal };
                        rowStack.Children.Add(new System.Windows.Controls.TextBlock
                        {
                            Text = severityDot,
                            FontSize = 12,
                            Foreground = new System.Windows.Media.SolidColorBrush(severityColor),
                            VerticalAlignment = VerticalAlignment.Top,
                            Margin = new Thickness(0, 0, 6, 0)
                        });
                        rowStack.Children.Add(new System.Windows.Controls.TextBlock
                        {
                            Text = $"{displayDesc} ({group.Count})",
                            FontSize = 10,
                            Foreground = new System.Windows.Media.SolidColorBrush(ColTextSec),
                            TextWrapping = TextWrapping.Wrap,
                            FontFamily = MainFont,
                            MaxWidth = 420
                        });
                        row.Child = rowStack;

                        // Click → auto-send chat message to investigate this warning
                        var warningDesc = group.Description;
                        row.MouseLeftButtonUp += (s, e) =>
                        {
                            e.Handled = true; // don't trigger parent expand/collapse
                            AutoSendWarningQuery(warningDesc);
                        };

                        BriefingWarningGroupList.Children.Add(row);
                    }
                }

                BriefingHealthSection.Visibility = Visibility.Visible;
            }
            else
            {
                BriefingHealthSection.Visibility = Visibility.Collapsed;
            }

            // ── Suggestions section ──
            BriefingSuggestionsContainer.Children.Clear();
            if (b.Suggestions != null && b.Suggestions.Count > 0)
            {
                foreach (var suggestion in b.Suggestions)
                {
                    BriefingSuggestionsContainer.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = $"\U0001F4A1  {suggestion}",
                        FontSize = 10.5,
                        Foreground = new System.Windows.Media.SolidColorBrush(ColMuted),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2),
                        LineHeight = 16,
                        FontFamily = MainFont
                    });
                }
                BriefingSuggestionsSection.Visibility = Visibility.Visible;
            }
            else
            {
                BriefingSuggestionsSection.Visibility = Visibility.Collapsed;
            }

            WelcomeBriefingPanel.Visibility = Visibility.Visible;
        }

        private bool _healthExpanded = false;

        private void OnHealthSectionClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _healthExpanded = !_healthExpanded;
            BriefingWarningGroupList.Visibility = _healthExpanded ? Visibility.Visible : Visibility.Collapsed;
            BriefingHealthChevron.Text = _healthExpanded ? "\u25BE" : "\u25B8"; // ▾ or ▸
        }

        private void AutoSendWarningQuery(string warningDescription)
        {
            if (_isProcessing) return;
            MessageInput.Text = $"Select and highlight elements with this warning: \"{warningDescription}\"";
            ProcessMessage();
        }

        /// <summary>
        /// Called from App.OnIdling when the Revit selection changes.
        /// </summary>
        public void UpdateSelectionInspector(string selectionSummary)
        {
            Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrEmpty(selectionSummary))
                {
                    SelectionInspectorBar.Visibility = Visibility.Collapsed;
                    SelectionInfoText.Text = "";
                }
                else
                {
                    SelectionInfoText.Text = selectionSummary;
                    SelectionInspectorBar.Visibility = Visibility.Visible;
                }
            });
        }

        private static string TruncatePath(string path, int maxLength)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
                return path;

            var parts = path.Replace('/', '\\').Split('\\');
            if (parts.Length <= 2)
                return "..." + path.Substring(path.Length - Math.Min(path.Length, maxLength - 3));

            var result = parts[parts.Length - 1];
            for (int i = parts.Length - 2; i >= 0; i--)
            {
                var candidate = parts[i] + "\\" + result;
                if (candidate.Length > maxLength - 3)
                {
                    result = "...\\" + result;
                    break;
                }
                result = candidate;
            }
            return result;
        }

        private static string GetCloudInfo(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                if (doc.IsModelInCloud)
                {
                    var pathName = doc.PathName;
                    if (!string.IsNullOrEmpty(pathName))
                    {
                        if (pathName.Contains("BIM 360://")) return "BIM 360";
                        if (pathName.Contains("ACC://")) return "ACC";
                    }
                    return "Cloud";
                }
                return "Local";
            }
            catch { return null; }
        }

        private void LoadLogoIcon()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("Zexus.Resources.icon_96.png"))
                {
                    if (stream != null)
                    {
                        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        LogoImage.Source = decoder.Frames[0];
                    }
                }
            }
            catch { /* icon load failure is non-fatal */ }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximizeRestore();
            }
            else
            {
                DragMove();
            }
        }

        private void OnMinimize(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnMaximizeRestore(object sender, RoutedEventArgs e)
        {
            ToggleMaximizeRestore();
        }

        private void ToggleMaximizeRestore()
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                MaxRestoreBtn.Content = "\uE922";
                MaxRestoreBtn.ToolTip = "Maximize";
            }
            else
            {
                WindowState = WindowState.Maximized;
                MaxRestoreBtn.Content = "\uE923";
                MaxRestoreBtn.ToolTip = "Restore";
            }
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        private void OnSend(object sender, RoutedEventArgs e) => ProcessMessage();

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                ProcessMessage();
            }
        }

        // ═══════════════════════════════════════════════════════
        // Image paste (clipboard → preview thumbnail)
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Intercepts Ctrl+V on the input box. If the clipboard contains an image and
        /// we're under MAX_IMAGES_PER_MESSAGE, pulls it as PNG bytes, adds it to the
        /// pending list, refreshes the preview, and consumes the event so the underlying
        /// TextBox does NOT also try to paste.
        ///
        /// Silent no-ops (covers the spec's "4th paste is silently ignored" case):
        ///   - Clipboard has no image                 → fall through to default paste
        ///   - Already 3 pending images               → fall through (textbox handles V)
        ///   - PngBitmapEncoder produces empty bytes  → fall through
        /// </summary>
        private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.V) return;
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (!Clipboard.ContainsImage()) return;
            if (_pendingImages.Count >= MAX_IMAGES_PER_MESSAGE) return;

            BitmapSource bitmapSource;
            try { bitmapSource = Clipboard.GetImage(); }
            catch { return; } // clipboard race — let the default paste try

            if (bitmapSource == null) return;

            byte[] pngBytes;
            try { pngBytes = ConvertToPngBytes(bitmapSource); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Image paste encode failed: {ex.Message}");
                return;
            }

            if (pngBytes == null || pngBytes.Length == 0) return;

            _pendingImages.Add(new ImageAttachment { Data = pngBytes });
            UpdateImagePreview();
            e.Handled = true; // critical — without this WPF also pastes the bitmap as garbage text
        }

        /// <summary>
        /// Encode a clipboard BitmapSource to PNG bytes, downscaling first if either
        /// dimension exceeds 2048px. Vision APIs tile internally, so don't compress harder
        /// than this — over-compression loses quality without saving meaningful tokens.
        /// </summary>
        private byte[] ConvertToPngBytes(BitmapSource source)
        {
            const int MAX_DIM = 2048;
            if (source.PixelWidth > MAX_DIM || source.PixelHeight > MAX_DIM)
            {
                double scale = Math.Min(
                    (double)MAX_DIM / source.PixelWidth,
                    (double)MAX_DIM / source.PixelHeight);
                source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Rebuild the ImagePreviewPanel from _pendingImages. Each entry shows a 48px-tall
        /// thumbnail plus a "✕" remove button. When the list is empty, hide the whole bar.
        /// </summary>
        private void UpdateImagePreview()
        {
            ImagePreviewPanel.Children.Clear();

            if (_pendingImages.Count == 0)
            {
                ImagePreviewBar.Visibility = Visibility.Collapsed;
                return;
            }

            ImagePreviewBar.Visibility = Visibility.Visible;

            for (int i = 0; i < _pendingImages.Count; i++)
            {
                int capturedIndex = i;
                var imgData = _pendingImages[i].Data;

                // Thumbnail — DecodePixelHeight=48 makes WPF discard pixel data we'll never
                // render, so a 4K paste doesn't keep a 4K BitmapSource alive in memory.
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(imgData);
                bmp.DecodePixelHeight = 48;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                var thumb = new System.Windows.Controls.Image
                {
                    Source = bmp,
                    Height = 48,
                    Margin = new Thickness(0, 0, 4, 0)
                };

                var removeBtn = new TextBlock
                {
                    Text = "✕",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(ColTextSec),
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(-8, 0, 8, 0),
                    ToolTip = "Remove image"
                };
                removeBtn.MouseLeftButtonDown += (s, evt) =>
                {
                    if (capturedIndex < _pendingImages.Count)
                    {
                        _pendingImages.RemoveAt(capturedIndex);
                        UpdateImagePreview();
                    }
                    evt.Handled = true;
                };

                ImagePreviewPanel.Children.Add(thumb);
                ImagePreviewPanel.Children.Add(removeBtn);
            }
        }

        private void OnNewChat(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;

            if (MessageBox.Show("Start a new conversation?", "New Chat",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _agentService.NewSession();
                while (ChatContainer.Children.Count > 1)
                    ChatContainer.Children.RemoveAt(1);
                AddSystemMessage("New conversation started.");
                CollapseWorkspace();
                _workspaceState = null;
                _outputRecords.Clear();
                _expandedRecordIds.Clear();
                _journalTurnGroups.Clear();
                _currentTurnIndex = 0;
                CollapseOutputPanel();
            }
        }

        private void OnToggleTheme(object sender, RoutedEventArgs e)
        {
            ThemeManager.Toggle(); // fires ThemeChanged → ApplyTheme via Dispatcher
        }

        private void OnTogglePin(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            PinBtn.Content = Topmost ? "Pinned" : "Pin";
        }

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;
            ShowSettingsDialog();
        }

        private void OnExportLog(object sender, RoutedEventArgs e)
        {
            try
            {
                // Reports root: %AppData%\Zexus\reports\
                var reportsRoot = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Zexus", "reports");
                if (!System.IO.Directory.Exists(reportsRoot))
                    System.IO.Directory.CreateDirectory(reportsRoot);

                var reporter = SessionReporter.Instance;
                if (reporter.HasData)
                {
                    // Save current session + copy to clipboard
                    var content = reporter.GetReportContent();
                    var filePath = reporter.GenerateReport();

                    if (!string.IsNullOrEmpty(content))
                        Clipboard.SetText(content);

                    if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                    {
                        // Open Explorer and select the just-exported file
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                        AddSystemMessage("Session report exported and copied to clipboard.");
                        return;
                    }
                }

                // No current session data (or save failed) — open the reports folder
                System.Diagnostics.Process.Start("explorer.exe", reportsRoot);
                AddSystemMessage("Opened reports folder — browse previous sessions here.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export report: {ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ProcessMessage()
        {
            var message = MessageInput.Text?.Trim();

            // Snapshot any pending images and clear the preview bar IMMEDIATELY so the UI
            // feels responsive (the user shouldn't see thumbnails linger while we wait on
            // the LLM). The captured list is the one this turn will send.
            //
            // NOTE: this snapshot is currently dropped at the AgentService boundary —
            // the agent service still uses the legacy text-only signature. Step 5 wires
            // pendingImages through to the LLM clients.
            List<ImageAttachment> pendingImages = null;
            if (_pendingImages.Count > 0)
            {
                pendingImages = new List<ImageAttachment>(_pendingImages);
                _pendingImages.Clear();
                UpdateImagePreview();
            }

            // Empty send: nothing to do unless there's text OR at least one image.
            bool hasText = !string.IsNullOrEmpty(message);
            bool hasImages = pendingImages != null && pendingImages.Count > 0;
            if (!hasText && !hasImages) return;

            // Hide confirmation buttons when user sends any message
            HideConfirmationButtons();

            if (!ConfigManager.IsConfigured())
            {
                ShowApiKeyPrompt();
                return;
            }

            // Cancel any ongoing request before starting new one
            if (_isProcessing && _cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                if (_currentStreamingBubble != null)
                {
                    ChatContainer.Children.Remove(_currentStreamingBubble);
                    _currentStreamingBubble = null;
                    _currentStreamingText = null;
                }
                await System.Threading.Tasks.Task.Delay(100);
            }

            _agentService.EnsureToolRegistryInitialized();

            _isProcessing = true;
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                AddUserBubble(message);
                MessageInput.Text = "";
                SetStatus("Thinking...", true);
                CreateStreamingBubble();

                var response = await _agentService.ProcessMessageAsync(message, _cancellationTokenSource.Token);

                // Capture streaming text before FinalizeStreamingBubble clears it
                var streamedText = _currentStreamingText?.Text;
                FinalizeStreamingBubble(response);

                // Show confirmation buttons if the final response asks for confirmation
                var textToCheck = response?.Content;
                if (string.IsNullOrEmpty(textToCheck))
                    textToCheck = streamedText;
                if (!string.IsNullOrEmpty(textToCheck) && IsConfirmationRequest(textToCheck))
                    ShowConfirmationButtons();
            }
            catch (OperationCanceledException)
            {
                if (_currentStreamingBubble != null)
                {
                    ChatContainer.Children.Remove(_currentStreamingBubble);
                    _currentStreamingBubble = null;
                    _currentStreamingText = null;
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Error: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
                SetStatus("", false);
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private void OnStreamingTextReceived(string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_currentStreamingText != null)
                {
                    _currentStreamingText.AppendText(text);
                    ChatScrollViewer.ScrollToEnd();
                }
                else
                {
                    // Streaming bubble was not yet created or was removed — recreate it
                    // This prevents silent text drops during tool execution cycles
                    CreateStreamingBubble();
                    if (_currentStreamingText != null)
                    {
                        _currentStreamingText.AppendText(text);
                        ChatScrollViewer.ScrollToEnd();
                    }
                }
            }));
        }

        private void OnToolExecuting(string toolName, Dictionary<string, object> input)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SetStatus($"Executing: {toolName}...", true);

                if (_currentStreamingBubble != null)
                {
                    var content = _currentStreamingBubble.Child as StackPanel;
                    if (content != null)
                    {
                        // Tool execution indicator with accent color
                        var toolPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };

                        // Small colored dot
                        var dot = new Border
                        {
                            Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                            Background = new SolidColorBrush(ColPrimaryLt),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 6, 0)
                        };
                        toolPanel.Children.Add(dot);

                        var toolLabel = new TextBlock
                        {
                            Text = toolName,
                            FontSize = 11,
                            FontFamily = MainFont,
                            Foreground = new SolidColorBrush(ColPrimaryLt),
                        };
                        toolPanel.Children.Add(toolLabel);

                        content.Children.Insert(content.Children.Count - 1, toolPanel);
                    }
                }

                // ── Update Workspace thinking chain ──
                if (_workspaceState != null)
                {
                    // Mark previous active node as completed
                    var activeNode = _workspaceState.ThinkingChain.LastOrDefault(n => n.Status == ThinkingNodeStatus.Active);
                    if (activeNode != null)
                        activeNode.Status = ThinkingNodeStatus.Completed;

                    var (title, color, icon) = MapToolToThinkingNode(toolName);

                    // Extract description and code from input
                    string description = null;
                    string codeSnippet = null;
                    if (input != null)
                    {
                        if (input.ContainsKey("description"))
                            description = input["description"]?.ToString();
                        if (toolName == "ExecuteCode" && input.ContainsKey("code"))
                            codeSnippet = input["code"]?.ToString();
                    }

                    // For ExecuteCode: use the description as the primary title (not "Execute Code")
                    if (toolName == "ExecuteCode" && !string.IsNullOrEmpty(description))
                    {
                        title = description;
                        icon = "⚙";
                    }

                    _workspaceState.ThinkingChain.Add(new ThinkingChainNode
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = title,
                        Subtitle = $"Running {toolName}...",
                        Status = ThinkingNodeStatus.Active,
                        NodeColor = color,
                        IconGlyph = icon,
                        Timestamp = DateTime.Now,
                        ToolName = toolName,
                        Description = description,
                        CodeSnippet = codeSnippet,
                        InputParams = input
                    });

                    _workspaceState.TotalExpectedSteps++;
                    RebuildThinkingChain();
                }
            }));
        }

        private void OnReasoningForThinkingChain(string reasoningText)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _lastReasoningText = reasoningText;

                // Add a 💭 reasoning node to the thinking chain
                if (_workspaceState != null && !string.IsNullOrEmpty(reasoningText))
                {
                    _workspaceState.ThinkingChain.Add(new ThinkingChainNode
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = "Reasoning",
                        Subtitle = reasoningText,
                        Status = ThinkingNodeStatus.Completed,
                        NodeColor = ColMuted,
                        IconGlyph = "💭",
                        Timestamp = DateTime.Now,
                        ToolName = "_reasoning",
                        Description = reasoningText
                    });
                    RebuildThinkingChain();
                }
            }));
        }

        private void OnStatusChanged(string status)
        {
            Dispatcher.BeginInvoke(new Action(() => SetStatus(status, !status.StartsWith("Complete"))));
        }

        private void AddUserBubble(string message)
        {
            var bubble = CreateBubble(message, true, null);
            ChatContainer.Children.Add(bubble);
            ChatScrollViewer.ScrollToEnd();
        }

        private void AddSystemMessage(string message)
        {
            var bubble = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(40, 4, 40, 10),
                Background = new SolidColorBrush(ColSurface),
                BorderBrush = new SolidColorBrush(ColGlassBorder),
                BorderThickness = new Thickness(1, 1, 1, 1),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            bubble.Child = CreateSelectableText(message, 12, ColMuted, textAlignment: TextAlignment.Center);

            ChatContainer.Children.Add(bubble);
            ChatScrollViewer.ScrollToEnd();
        }

        private FrameworkElement CreateBubble(string message, bool isUser, System.Collections.Generic.List<ToolCall> toolCalls)
        {
            var bubble = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16, 14, 16, 14),
                BorderThickness = new Thickness(1, 1, 1, 1)
            };

            if (isUser)
            {
                // User bubble: primary indigo, right-aligned
                bubble.Margin = new Thickness(60, 2, 0, 10);
                bubble.HorizontalAlignment = HorizontalAlignment.Right;
                bubble.Background = new SolidColorBrush(Color.FromArgb(0x28, ColPrimary.R, ColPrimary.G, ColPrimary.B));
                bubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, ColPrimary.R, ColPrimary.G, ColPrimary.B));
            }
            else
            {
                // Agent bubble: glass surface, left-aligned
                bubble.Margin = new Thickness(0, 2, 60, 10);
                bubble.HorizontalAlignment = HorizontalAlignment.Left;
                bubble.Background = new SolidColorBrush(ColGlass);
                bubble.BorderBrush = new SolidColorBrush(ColGlassBorder);
            }

            var content = new StackPanel { Orientation = Orientation.Vertical };

            // Role label
            content.Children.Add(new TextBlock
            {
                Text = isUser ? "You" : "Agent",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(isUser ? ColPrimaryLt : ColAccent),
                Margin = new Thickness(0, 0, 0, 6),
                FontFamily = MainFont
            });

            // Tool call indicators
            if (toolCalls != null)
            {
                foreach (var call in toolCalls)
                {
                    var statusColor = call.Status == ToolCallStatus.Completed ? ColSuccess :
                                      call.Status == ToolCallStatus.Failed ? ColError :
                                      ColWarning;

                    var toolPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

                    // Status dot
                    toolPanel.Children.Add(new Border
                    {
                        Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                        Background = new SolidColorBrush(statusColor),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0)
                    });

                    toolPanel.Children.Add(new TextBlock
                    {
                        Text = call.Name,
                        FontSize = 11,
                        FontFamily = MainFont,
                        Foreground = new SolidColorBrush(statusColor),
                    });

                    content.Children.Add(toolPanel);
                }
            }

            // Message text (selectable)
            content.Children.Add(CreateSelectableText(message ?? "(No response)", 13.5, ColText));

            bubble.Child = content;

            // Wrap with hover copy button
            var wrapped = WrapWithCopyButton(bubble, message ?? "");
            // Preserve alignment from the bubble on the wrapper grid
            wrapped.HorizontalAlignment = bubble.HorizontalAlignment;
            wrapped.Margin = bubble.Margin;
            bubble.Margin = new Thickness(0);

            return wrapped;
        }

        private void CreateStreamingBubble()
        {
            _currentStreamingBubble = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 2, 60, 10),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(ColGlass),
                BorderBrush = new SolidColorBrush(ColGlassBorder),
                BorderThickness = new Thickness(1)
            };

            var content = new StackPanel { Orientation = Orientation.Vertical };

            content.Children.Add(new TextBlock
            {
                Text = "Agent",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColAccent),
                Margin = new Thickness(0, 0, 0, 6),
                FontFamily = MainFont
            });

            _currentStreamingText = CreateSelectableText("", 13.5, ColText);
            content.Children.Add(_currentStreamingText);

            _currentStreamingBubble.Child = content;
            ChatContainer.Children.Add(_currentStreamingBubble);
            ChatScrollViewer.ScrollToEnd();
        }

        private void FinalizeStreamingBubble(ChatMessage response)
        {
            FrameworkElement agentBubble = null;

            if (_currentStreamingText != null && string.IsNullOrEmpty(_currentStreamingText.Text))
            {
                // Case 1: Streaming bubble was created but no text arrived — replace with final bubble
                ChatContainer.Children.Remove(_currentStreamingBubble);

                if (response.Role == MessageRole.System)
                {
                    AddSystemMessage(response.Content);
                }
                else
                {
                    agentBubble = CreateBubble(response.Content, false, response.ToolCalls);
                    ChatContainer.Children.Add(agentBubble);
                }
            }
            else if (_currentStreamingText != null && response.ToolCalls != null && response.ToolCalls.Count > 0)
            {
                // Case 2: Has streaming text AND tool calls — replace with merged bubble
                ChatContainer.Children.Remove(_currentStreamingBubble);
                agentBubble = CreateBubble(response.Content ?? _currentStreamingText.Text, false, response.ToolCalls);
                ChatContainer.Children.Add(agentBubble);
            }
            else if (_currentStreamingText != null)
            {
                // Case 3: Has streaming text but NO tool calls — finalize the bubble with final content
                ChatContainer.Children.Remove(_currentStreamingBubble);
                var finalContent = response.Content ?? _currentStreamingText.Text;
                agentBubble = CreateBubble(finalContent, false, response.ToolCalls);
                ChatContainer.Children.Add(agentBubble);
            }

            // ── Capture thinking chain snapshot and link to agent bubble ──
            if (agentBubble != null && _workspaceState != null && _workspaceState.ThinkingChain.Count > 0)
            {
                // Deep-copy the thinking chain nodes so later turns don't overwrite
                var snapshot = _workspaceState.ThinkingChain
                    .Select(n => new ThinkingChainNode
                    {
                        Id = n.Id,
                        Title = n.Title,
                        Subtitle = n.Subtitle,
                        Status = n.Status,
                        IconGlyph = n.IconGlyph,
                        NodeColor = n.NodeColor,
                        Timestamp = n.Timestamp,
                        ToolName = n.ToolName,
                        Description = n.Description,
                        CodeSnippet = n.CodeSnippet,
                        Output = n.Output,
                        InputParams = n.InputParams,
                        ResultData = n.ResultData,
                        DurationMs = n.DurationMs,
                        ErrorMessage = n.ErrorMessage
                    }).ToList();

                _bubbleThinkingChains[agentBubble] = snapshot;

                // Add click handler — clicking the bubble shows its thinking chain
                // The copy button already handles e.Handled=true so won't trigger this.
                // TextBox clicks also don't bubble. Only clicks on Border/Grid/TextBlock arrive here.
                agentBubble.MouseLeftButtonDown += (s, e) =>
                {
                    // Don't interfere with text selection in TextBox
                    if (e.OriginalSource is TextBox) return;

                    ShowBubbleThinkingChain(agentBubble);
                    // Don't set e.Handled here — allow text selection on TextBlock to still work
                };
                agentBubble.Cursor = Cursors.Hand;
                agentBubble.ToolTip = "Click to view thinking chain";
            }

            _currentStreamingBubble = null;
            _currentStreamingText = null;
            ChatScrollViewer.ScrollToEnd();
        }

        private void SetStatus(string status, bool visible)
        {
            StatusText.Text = status;
            StatusBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            SendBtn.IsEnabled = !visible;
            MessageInput.IsEnabled = !visible;
        }

        /// <summary>
        /// Create a custom dark dropdown control (replaces ComboBox which can't be dark-themed in Revit's WPF host).
        /// Returns: (container Border, populate action, getSelectedTag func, setSelected action).
        /// </summary>
        private (Border container, Action<List<KeyValuePair<string, object>>> populate, Func<object> getTag, Action<int> select)
            CreateDarkDropdown(double width, double height, Action<object> onSelectionChanged)
        {
            var darkBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x22));
            var darkBgHover = new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x32));
            var textBrush = new SolidColorBrush(ColText);
            var borderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

            object selectedTag = null;

            // Selected item text
            var selectedText = new TextBlock
            {
                Text = "",
                FontSize = 13, FontFamily = MainFont,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                IsHitTestVisible = false
            };

            // Arrow ▼
            var arrow = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse("M 0 0 L 4 4 L 8 0 Z"),
                Fill = textBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                IsHitTestVisible = false
            };

            // Popup items container
            var itemsPanel = new StackPanel();
            var popupBorder = new Border
            {
                Background = darkBg,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                MinWidth = width,
                MaxHeight = 300,
                Child = itemsPanel
            };

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                StaysOpen = false,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                AllowsTransparency = true,
                Child = popupBorder
            };

            // Root container
            var rootGrid = new Grid();
            rootGrid.Children.Add(selectedText);
            rootGrid.Children.Add(arrow);
            rootGrid.Children.Add(popup);

            var container = new Border
            {
                Width = width, Height = height,
                Background = darkBg,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                Child = rootGrid
            };

            popup.PlacementTarget = container;

            // Track popup-just-closed to prevent immediate reopen on same click gesture
            bool dropdownJustClosed = false;
            popup.Closed += (s, e) =>
            {
                dropdownJustClosed = true;
                Dispatcher.BeginInvoke(new Action(() => dropdownJustClosed = false),
                    System.Windows.Threading.DispatcherPriority.Input);
            };

            // MouseDown: block event to prevent dialog DragMove from capturing the mouse
            container.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
            };

            // MouseUp: open popup (avoids StaysOpen=false auto-closing on the same Up event)
            container.MouseLeftButtonUp += (s, e) =>
            {
                if (!dropdownJustClosed)
                    popup.IsOpen = true;
                e.Handled = true;
            };

            // Populate: store tag in Border.Tag for easy retrieval
            void PopulateEnhanced(List<KeyValuePair<string, object>> items)
            {
                itemsPanel.Children.Clear();
                foreach (var item in items)
                {
                    var display = item.Key;
                    var tag = item.Value;

                    var itemText = new TextBlock
                    {
                        Text = display,
                        FontSize = 13, FontFamily = MainFont,
                        Foreground = textBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10, 0, 10, 0),
                        IsHitTestVisible = false
                    };

                    var itemBorder = new Border
                    {
                        Height = 30,
                        Background = darkBg,
                        Cursor = Cursors.Hand,
                        Tag = tag,
                        Child = itemText
                    };

                    itemBorder.MouseEnter += (s, e) => itemBorder.Background = darkBgHover;
                    itemBorder.MouseLeave += (s, e) => itemBorder.Background = darkBg;
                    itemBorder.MouseLeftButtonUp += (s, e) =>
                    {
                        selectedText.Text = display;
                        selectedTag = tag;
                        popup.IsOpen = false;
                        onSelectionChanged?.Invoke(tag);
                        e.Handled = true;
                    };

                    itemsPanel.Children.Add(itemBorder);
                }
            }

            void SelectByIndex(int index)
            {
                if (index >= 0 && index < itemsPanel.Children.Count)
                {
                    var itemBorder = (Border)itemsPanel.Children[index];
                    var txt = (TextBlock)itemBorder.Child;
                    selectedText.Text = txt.Text;
                    selectedTag = itemBorder.Tag;
                }
            }

            return (container, PopulateEnhanced, () => selectedTag, SelectByIndex);
        }

        private void ShowApiKeyPrompt()
        {
            var currentProvider = ConfigManager.GetProvider();
            var currentModel = ConfigManager.GetModel();

            var dialog = new Window
            {
                Title = "API Key Required",
                Width = 460, Height = 340,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize
            };

            // Outer border with glassmorphism
            var outerBorder = new Border
            {
                Background = new SolidColorBrush(ColBg),
                CornerRadius = new CornerRadius(16),
                BorderBrush = new SolidColorBrush(ColBorder),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black }
            };

            var grid = new Grid { Margin = new Thickness(24) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: provider selector
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: model selector
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: label
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3: textbox
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4: buttons

            // ── Row 0: Provider selector (custom dark dropdown) ──
            var providerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            providerPanel.Children.Add(new TextBlock
            {
                Text = "Provider",
                Width = 60,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                FontFamily = MainFont,
                Foreground = new SolidColorBrush(ColTextSec),
                VerticalAlignment = VerticalAlignment.Center
            });

            // Forward-declare model dropdown parts (needed in provider's onSelectionChanged)
            Action<List<KeyValuePair<string, object>>> populateModel = null;
            Action<int> selectModel = null;
            Func<object> getModelTag = null;

            // Label refs (updated when provider changes)
            System.Windows.Documents.Run labelTitle = null;
            System.Windows.Documents.Run labelHint = null;

            // Helper: build model items list for a provider, returning the selected index
            (List<KeyValuePair<string, object>> items, int selectedIdx) BuildModelItems(LlmProvider provider, string selectModelId)
            {
                var models = LlmProviderInfo.GetAvailableModels(provider);
                var items = new List<KeyValuePair<string, object>>();
                int selectedIdx = 0;
                for (int i = 0; i < models.Count; i++)
                {
                    items.Add(new KeyValuePair<string, object>(models[i].Value, models[i].Key));
                    if (models[i].Key.Equals(selectModelId, StringComparison.OrdinalIgnoreCase))
                        selectedIdx = i;
                }
                return (items, selectedIdx);
            }

            var (providerDropdown, populateProvider, getProviderTag, selectProvider) = CreateDarkDropdown(200, 32, tag =>
            {
                var selected = (LlmProvider)tag;
                if (labelTitle != null) labelTitle.Text = LlmProviderInfo.GetApiKeyLabel(selected);
                if (labelHint != null) labelHint.Text = LlmProviderInfo.GetApiKeyHint(selected);
                // Repopulate model dropdown
                var (modelItems, modelIdx) = BuildModelItems(selected, LlmProviderInfo.GetDefaultModel(selected));
                populateModel?.Invoke(modelItems);
                selectModel?.Invoke(modelIdx);
            });

            // Populate providers
            var providerItems = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>(LlmProviderInfo.GetDisplayName(LlmProvider.Anthropic), LlmProvider.Anthropic),
                new KeyValuePair<string, object>(LlmProviderInfo.GetDisplayName(LlmProvider.OpenAI), LlmProvider.OpenAI),
                new KeyValuePair<string, object>(LlmProviderInfo.GetDisplayName(LlmProvider.Google), LlmProvider.Google),
            };
            populateProvider(providerItems);

            // Select current provider
            for (int i = 0; i < providerItems.Count; i++)
            {
                if ((LlmProvider)providerItems[i].Value == currentProvider) { selectProvider(i); break; }
            }

            providerPanel.Children.Add(providerDropdown);
            Grid.SetRow(providerPanel, 0);
            grid.Children.Add(providerPanel);

            // ── Row 1: Model selector (custom dark dropdown) ──
            var modelPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            modelPanel.Children.Add(new TextBlock
            {
                Text = "Model",
                Width = 60,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                FontFamily = MainFont,
                Foreground = new SolidColorBrush(ColTextSec),
                VerticalAlignment = VerticalAlignment.Center
            });

            Border modelDropdown;
            (modelDropdown, populateModel, getModelTag, selectModel) = CreateDarkDropdown(200, 32, tag => { /* model selected */ });

            // Populate models for current provider
            var (initModelItems, initModelIdx) = BuildModelItems(currentProvider, currentModel);
            populateModel(initModelItems);
            selectModel(initModelIdx);

            modelPanel.Children.Add(modelDropdown);
            Grid.SetRow(modelPanel, 1);
            grid.Children.Add(modelPanel);

            // ── Row 2: Dynamic label ──
            var label = new TextBlock
            {
                FontSize = 14,
                FontFamily = MainFont,
                Foreground = new SolidColorBrush(ColText),
                Margin = new Thickness(0, 0, 0, 14)
            };
            labelTitle = new System.Windows.Documents.Run(LlmProviderInfo.GetApiKeyLabel(currentProvider)) { FontWeight = FontWeights.SemiBold };
            labelHint = new System.Windows.Documents.Run(LlmProviderInfo.GetApiKeyHint(currentProvider)) { Foreground = new SolidColorBrush(ColMuted), FontSize = 12 };
            label.Inlines.Add(labelTitle);
            label.Inlines.Add(new System.Windows.Documents.LineBreak());
            label.Inlines.Add(labelHint);
            Grid.SetRow(label, 2);
            grid.Children.Add(label);

            // ── Row 3: API key input ──
            var textBox = new TextBox
            {
                Height = 38, FontSize = 13, FontFamily = MainFont,
                Padding = new Thickness(12, 8, 12, 8),
                Background = new SolidColorBrush(ColBg),
                Foreground = new SolidColorBrush(ColText),
                BorderBrush = new SolidColorBrush(ColBorder),
                CaretBrush = new SolidColorBrush(ColText)
            };
            Grid.SetRow(textBox, 3);
            grid.Children.Add(textBox);

            // ── Row 4: Buttons ──
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var saveBtn = new Button
            {
                Content = "Save", Width = 80, Height = 34,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(ColPrimary),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontFamily = MainFont, FontSize = 13, FontWeight = FontWeights.SemiBold
            };
            saveBtn.Click += (s, ev) =>
            {
                var providerTag = getProviderTag();
                var modelTag = getModelTag();
                var selectedProvider = providerTag != null ? (LlmProvider)providerTag : currentProvider;
                var selectedModel = modelTag?.ToString() ?? LlmProviderInfo.GetDefaultModel(selectedProvider);
                var key = textBox.Text?.Trim();
                if (!string.IsNullOrEmpty(key) && LlmProviderInfo.ValidateApiKey(selectedProvider, key))
                {
                    ConfigManager.SetProvider(selectedProvider);
                    ConfigManager.SetModel(selectedModel);
                    ConfigManager.SetApiKey(key);
                    _agentService.InitializeClient();
                    var modelName = LlmProviderInfo.GetModelDisplayName(selectedProvider, selectedModel);
                    AddSystemMessage($"{LlmProviderInfo.GetDisplayName(selectedProvider)} ({modelName}) configured successfully.");
                    dialog.Close();
                }
                else
                {
                    MessageBox.Show(LlmProviderInfo.GetApiKeyValidationError(selectedProvider), "Invalid Key",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            buttonPanel.Children.Add(saveBtn);

            var cancelBtn = new Button
            {
                Content = "Cancel", Width = 80, Height = 34,
                Background = new SolidColorBrush(ColCard),
                Foreground = new SolidColorBrush(ColTextSec),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontFamily = MainFont, FontSize = 13
            };
            cancelBtn.Click += (s, ev) => dialog.Close();
            buttonPanel.Children.Add(cancelBtn);

            Grid.SetRow(buttonPanel, 4);
            grid.Children.Add(buttonPanel);

            outerBorder.Child = grid;
            dialog.Content = outerBorder;

            // Allow dragging the dialog
            outerBorder.MouseLeftButtonDown += (s, ev) => { try { dialog.DragMove(); } catch { /* DragMove fails if mouse not pressed — expected */ } };

            dialog.ShowDialog();
        }

        private void ShowSettingsDialog()
        {
            var currentProvider = ConfigManager.GetProvider();
            var providerName = LlmProviderInfo.GetDisplayName(currentProvider);
            var modelName = LlmProviderInfo.GetModelDisplayName(currentProvider, ConfigManager.GetModel());
            var currentKey = ConfigManager.GetApiKey();
            var maskedKey = !string.IsNullOrEmpty(currentKey)
                ? $"{currentKey.Substring(0, Math.Min(10, currentKey.Length))}...{currentKey.Substring(Math.Max(0, currentKey.Length - 4))}"
                : "Not set";

            if (MessageBox.Show($"Provider: {providerName}\nModel: {modelName}\nAPI Key: {maskedKey}\n\nChange settings?", "Settings",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                ShowApiKeyPrompt();
            }
        }

        #region Workspace Panel

        private void OnProcessingStarted(string userMessage)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _currentTurnIndex++;
                ShowWorkspace(userMessage);
            }));
        }

        private void OnToolCompleted(string toolName, ToolResult result, long durationMs)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_workspaceState == null) return;

                // Mark active node as completed or failed, and store rich data
                var activeNode = _workspaceState.ThinkingChain.LastOrDefault(n => n.Status == ThinkingNodeStatus.Active);
                if (activeNode != null)
                {
                    activeNode.Status = result.Success ? ThinkingNodeStatus.Completed : ThinkingNodeStatus.Failed;
                    activeNode.ResultData = result.Data;
                    activeNode.DurationMs = durationMs;

                    if (!result.Success)
                    {
                        // Classify failure type for display
                        string failType = result.Data != null && result.Data.ContainsKey("failure_type")
                            ? result.Data["failure_type"]?.ToString() : "error";
                        if (failType == "compilation")
                            activeNode.Subtitle = "❌ Compile error → retrying...";
                        else if (failType == "runtime")
                            activeNode.Subtitle = "⚠️ Runtime error → retrying...";
                        else
                            activeNode.Subtitle = "❌ Failed";
                        activeNode.ErrorMessage = result.Message;
                        activeNode.Output = result.Message;
                    }
                    else
                    {
                        // Build a concise success summary, stripping ZEXUS_JSON from display
                        var cleanMessage = StripZexusJson(result.Message);
                        activeNode.Output = cleanMessage;
                        var summary = BuildToolResultSummary(toolName, result);
                        activeNode.Subtitle = summary ?? cleanMessage;
                    }
                }

                _workspaceState.CompletedSteps++;
                RebuildThinkingChain();

                // ── Generate Output Record (skip for ExecuteCode writes — Transaction Journal handles those) ──
                bool isEcWrite = toolName == "ExecuteCode" && result.Success &&
                    result.Data != null && result.Data.ContainsKey("has_transaction") &&
                    result.Data["has_transaction"] is bool ht && ht;
                if (!isEcWrite)
                {
                    bool hadRecordsBefore = _outputRecords.Count > 0;
                    var outputRecord = MapToolResultToOutputRecord(toolName, result);
                    if (outputRecord != null)
                    {
                        _outputRecords.Add(outputRecord);
                        ShowOutputPanel();
                        RebuildOutputCards();
                    }
                    else if (_outputRecords.Count > 0 && hadRecordsBefore)
                    {
                        RebuildOutputCards();
                    }
                }

                // ── Generate Transaction Journal Entry (write-only ExecuteCode) ──
                if (toolName == "ExecuteCode" && result.Success)
                {
                    var journalEntry = BuildJournalEntry(result);
                    // 7.2: Only add write operations to journal (read-only stays in Thinking Chain only)
                    if (journalEntry != null && journalEntry.IsWrite)
                    {
                        // 7.3: Group by turn
                        var turnGroup = _journalTurnGroups.LastOrDefault(g => g.TurnIndex == _currentTurnIndex);
                        if (turnGroup == null)
                        {
                            turnGroup = new TransactionJournalTurnGroup
                            {
                                TurnIndex = _currentTurnIndex,
                                Timestamp = DateTime.Now,
                                TurnSummary = journalEntry.Description
                            };
                            _journalTurnGroups.Add(turnGroup);
                        }
                        turnGroup.Entries.Add(journalEntry);
                        ShowJournalPanel();
                        RebuildJournalCards();
                    }

                    // Auto-navigate if exactly 1 new view was created (zero LLM dependency)
                    if (result.Data.ContainsKey("sys_created")
                        && result.Data["sys_created"] is List<ElementDetail> createdForNav)
                    {
                        var newViews = createdForNav.Where(e => e.IsView).ToList();
                        if (newViews.Count == 1)
                            _ = App.RevitEventHandler.NavigateToViewAsync(newViews[0].ElementId);
                    }
                }
            }));
        }

        private void OnProcessingCompleted(ChatMessage response)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_workspaceState == null) return;

                foreach (var node in _workspaceState.ThinkingChain)
                    if (node.Status == ThinkingNodeStatus.Active)
                        node.Status = ThinkingNodeStatus.Completed;

                _workspaceState.ThinkingChain.Add(new ThinkingChainNode
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = "Task Complete",
                    Subtitle = "Done",
                    Status = ThinkingNodeStatus.Completed,
                    NodeColor = ColSuccess,
                    IconGlyph = "✓",
                    Timestamp = DateTime.Now,
                    ToolName = "_complete"
                });

                _workspaceState.IsActive = false;
                RebuildThinkingChain();
            }));
        }

        // ── Step 6: Inline Confirmation Buttons ──

        private bool IsConfirmationRequest(string assistantMessage)
        {
            if (string.IsNullOrEmpty(assistantMessage)) return false;
            var lower = assistantMessage.ToLower();
            var patterns = new[]
            {
                "请确认", "确认是否", "是否执行", "是否继续",
                "shall i proceed", "should i continue", "do you want me to",
                "proceed?", "go ahead?",
                "是否执行此操作", "请在回复中告诉我"
            };
            return patterns.Any(p => lower.Contains(p));
        }

        private void ShowConfirmationButtons()
        {
            HideConfirmationButtons();

            _confirmationPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 8)
            };

            var yesBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x5A, 0x27)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 12, 0),
                Cursor = Cursors.Hand
            };
            yesBtn.Child = new TextBlock
            {
                Text = "✅ 确认执行",
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = MainFont,
                FontWeight = FontWeights.Medium
            };
            yesBtn.MouseLeftButtonDown += (s, e) =>
            {
                HideConfirmationButtons();
                SendMessageProgrammatically("确认");
            };
            yesBtn.MouseEnter += (s, e) => yesBtn.Background = new SolidColorBrush(Color.FromRgb(0x3D, 0x7A, 0x37));
            yesBtn.MouseLeave += (s, e) => yesBtn.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x5A, 0x27));

            var noBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x27, 0x27)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 8, 16, 8),
                Cursor = Cursors.Hand
            };
            noBtn.Child = new TextBlock
            {
                Text = "❌ 取消",
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = MainFont,
                FontWeight = FontWeights.Medium
            };
            noBtn.MouseLeftButtonDown += (s, e) =>
            {
                HideConfirmationButtons();
                SendMessageProgrammatically("取消，不要执行");
            };
            noBtn.MouseEnter += (s, e) => noBtn.Background = new SolidColorBrush(Color.FromRgb(0x7A, 0x37, 0x37));
            noBtn.MouseLeave += (s, e) => noBtn.Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x27, 0x27));

            _confirmationPanel.Children.Add(yesBtn);
            _confirmationPanel.Children.Add(noBtn);

            ChatContainer.Children.Add(_confirmationPanel);
            ChatScrollViewer.ScrollToEnd();
        }

        private void HideConfirmationButtons()
        {
            if (_confirmationPanel != null)
            {
                ChatContainer.Children.Remove(_confirmationPanel);
                _confirmationPanel = null;
            }
        }

        private void SendMessageProgrammatically(string message)
        {
            // Hide any existing confirmation buttons
            HideConfirmationButtons();
            // Set the message and trigger send
            MessageInput.Text = message;
            ProcessMessage();
        }

        private void ShowWorkspace(string taskName)
        {
            _workspaceState = new WorkspaceState
            {
                TaskName = taskName.Length > 80 ? taskName.Substring(0, 80) + "..." : taskName,
                IsActive = true,
                StartTime = DateTime.Now,
                CompletedSteps = 0,
                TotalExpectedSteps = 0
            };

            _workspaceState.ThinkingChain.Add(new ThinkingChainNode
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Understand Intent",
                Subtitle = taskName.Length > 80 ? taskName.Substring(0, 80) + "..." : taskName,
                Status = ThinkingNodeStatus.Active,
                NodeColor = Color.FromRgb(0xD9, 0x46, 0xEF),
                IconGlyph = "◉",
                Timestamp = DateTime.Now,
                ToolName = "_intent"
            });

            BuildWorkspaceUI();

            WorkspaceColumnDef.Width = new GridLength(420);
            WorkspaceSplitter.Visibility = Visibility.Visible;
            WorkspacePanel.Visibility = Visibility.Visible;

            if (ActualWidth < 800) Width = 1040;
        }

        private void CollapseWorkspace()
        {
            WorkspaceColumnDef.Width = new GridLength(0);
            WorkspaceSplitter.Visibility = Visibility.Collapsed;
            WorkspacePanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Shows the thinking chain snapshot associated with a clicked agent bubble.
        /// Replaces the current workspace panel content with the historical snapshot.
        /// </summary>
        private void ShowBubbleThinkingChain(FrameworkElement bubble)
        {
            if (!_bubbleThinkingChains.TryGetValue(bubble, out var snapshot)) return;
            if (snapshot == null || snapshot.Count == 0) return;

            // Create a temporary workspace state with the snapshot
            _workspaceState = new WorkspaceState
            {
                TaskName = snapshot.FirstOrDefault()?.Subtitle ?? "Previous Turn",
                IsActive = false,
                StartTime = snapshot.First().Timestamp,
                CompletedSteps = snapshot.Count(n => n.Status == ThinkingNodeStatus.Completed),
                TotalExpectedSteps = snapshot.Count
            };
            _workspaceState.ThinkingChain.AddRange(snapshot);

            // Build and show workspace panel
            BuildWorkspaceUI();

            WorkspaceColumnDef.Width = new GridLength(420);
            WorkspaceSplitter.Visibility = Visibility.Visible;
            WorkspacePanel.Visibility = Visibility.Visible;

            if (ActualWidth < 800) Width = 1040;
        }

        private void BuildWorkspaceUI()
        {
            WorkspaceContainer.Children.Clear();

            // ── Section Header ──
            var header = new TextBlock
            {
                Text = "THINKING CHAIN",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColAccent),
                Margin = new Thickness(4, 4, 0, 10),
                FontFamily = MainFont
            };
            WorkspaceContainer.Children.Add(header);

            // ── Thinking Chain Container ──
            _wsChainContainer = new StackPanel();
            RebuildThinkingChain();
            WorkspaceContainer.Children.Add(_wsChainContainer);
        }

        private void RebuildThinkingChain()
        {
            if (_wsChainContainer == null || _workspaceState == null) return;
            _wsChainContainer.Children.Clear();

            for (int i = 0; i < _workspaceState.ThinkingChain.Count; i++)
            {
                var node = _workspaceState.ThinkingChain[i];
                bool isLast = (i == _workspaceState.ThinkingChain.Count - 1);

                var card = BuildThinkingNodeCard(node, isLast);
                _wsChainContainer.Children.Add(card);
            }
        }

        /// <summary>
        /// Builds a rich card for a single thinking chain node.
        /// </summary>
        private FrameworkElement BuildThinkingNodeCard(ThinkingChainNode node, bool isLast)
        {
            var wrapper = new StackPanel();

            // ── Status color ──
            var statusColor = node.Status == ThinkingNodeStatus.Completed ? ColSuccess
                : node.Status == ThinkingNodeStatus.Active ? node.NodeColor
                : node.Status == ThinkingNodeStatus.Failed ? ColError
                : ColMuted;

            // ── Card border ──
            var card = new Border
            {
                Background = new SolidColorBrush(ColGlass),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 0)
            };

            if (node.Status == ThinkingNodeStatus.Failed)
                card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, ColError.R, ColError.G, ColError.B));
            else if (node.Status == ThinkingNodeStatus.Active)
                card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, node.NodeColor.R, node.NodeColor.G, node.NodeColor.B));
            else
                card.BorderBrush = new SolidColorBrush(ColGlassBorder);

            var content = new StackPanel();

            // ── Row 1: Status circle + Title + Duration ──
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Status circle
            var circle = new Border
            {
                Width = 18, Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(0x28, statusColor.R, statusColor.G, statusColor.B)),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (node.Status == ThinkingNodeStatus.Active)
            {
                circle.BorderBrush = new SolidColorBrush(statusColor);
                circle.BorderThickness = new Thickness(1.5);
            }
            circle.Child = new TextBlock
            {
                Text = node.Status == ThinkingNodeStatus.Completed ? "✓"
                     : node.Status == ThinkingNodeStatus.Failed ? "✗"
                     : node.IconGlyph ?? "?",
                FontSize = 8, FontFamily = MainFont,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(statusColor)
            };
            Grid.SetColumn(circle, 0);
            headerGrid.Children.Add(circle);

            // Title
            var titleText = new TextBlock
            {
                Text = node.Title,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(node.Status == ThinkingNodeStatus.Pending ? ColMuted : ColText),
                FontFamily = MainFont,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            Grid.SetColumn(titleText, 1);
            headerGrid.Children.Add(titleText);

            // Duration badge
            if (node.DurationMs > 0)
            {
                var durationStr = node.DurationMs >= 1000
                    ? $"{node.DurationMs / 1000.0:F1}s"
                    : $"{node.DurationMs}ms";

                var durationBadge = new TextBlock
                {
                    Text = durationStr,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(ColMuted),
                    FontFamily = MainFont,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(durationBadge, 2);
                headerGrid.Children.Add(durationBadge);
            }

            content.Children.Add(headerGrid);

            // ── Row 2: Description (if present, selectable) ──
            if (!string.IsNullOrEmpty(node.Description))
            {
                var descText = CreateSelectableText(node.Description, 11, ColTextSec);
                descText.Margin = new Thickness(24, 4, 0, 0);
                content.Children.Add(descText);
            }

            // ── Row 3: Input params summary (for non-ExecuteCode tools) ──
            if (node.ToolName != "ExecuteCode" && node.ToolName != "_intent" && node.ToolName != "_complete"
                && node.InputParams != null && node.InputParams.Count > 0)
            {
                var paramStr = FormatInputParams(node.ToolName, node.InputParams);
                if (!string.IsNullOrEmpty(paramStr))
                {
                    var paramText = CreateSelectableText(paramStr, 10.5, ColMuted);
                    paramText.Margin = new Thickness(24, 3, 0, 0);
                    content.Children.Add(paramText);
                }
            }

            // ── Row 4: Code block (ExecuteCode only, collapsed by default) ──
            if (node.ToolName == "ExecuteCode" && !string.IsNullOrEmpty(node.CodeSnippet))
            {
                var codeBlock = BuildCodeBlock(node.CodeSnippet);
                codeBlock.Margin = new Thickness(4, 2, 0, 0);
                codeBlock.Visibility = Visibility.Collapsed;

                var toggleBtn = new TextBlock
                {
                    Text = "📝 Show code ▶",
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(ColMuted),
                    FontFamily = MainFont,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(24, 4, 0, 0)
                };
                toggleBtn.MouseLeftButtonDown += (s, e) =>
                {
                    if (codeBlock.Visibility == Visibility.Collapsed)
                    {
                        codeBlock.Visibility = Visibility.Visible;
                        toggleBtn.Text = "📝 Hide code ▼";
                    }
                    else
                    {
                        codeBlock.Visibility = Visibility.Collapsed;
                        toggleBtn.Text = "📝 Show code ▶";
                    }
                };
                content.Children.Add(toggleBtn);
                content.Children.Add(codeBlock);
            }

            // ── Row 5: Output/Result ──
            if (node.Status == ThinkingNodeStatus.Completed && !string.IsNullOrEmpty(node.Output)
                && node.ToolName != "_intent" && node.ToolName != "_complete" && node.ToolName != "_reasoning")
            {
                var resultPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 6, 0, 0) };
                resultPanel.Children.Add(new TextBlock
                {
                    Text = "✓ ",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColSuccess),
                    FontFamily = MainFont,
                    VerticalAlignment = VerticalAlignment.Top
                });
                resultPanel.Children.Add(CreateSelectableText(node.Output, 11, ColTextSec, maxWidth: 340));
                content.Children.Add(resultPanel);
            }

            // ── Row 5b: Error message (failed nodes) ──
            if (node.Status == ThinkingNodeStatus.Failed && !string.IsNullOrEmpty(node.ErrorMessage))
            {
                var errorPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 6, 0, 0) };
                errorPanel.Children.Add(new TextBlock
                {
                    Text = "✗ ",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColError),
                    FontFamily = MainFont,
                    VerticalAlignment = VerticalAlignment.Top
                });
                var errorMsg = node.ErrorMessage.Length > 200 ? node.ErrorMessage.Substring(0, 200) + "..." : node.ErrorMessage;
                errorPanel.Children.Add(CreateSelectableText(errorMsg, 10.5, ColError, MonoFont, maxWidth: 340));
                content.Children.Add(errorPanel);
            }

            // ── Active status subtitle ──
            if (node.Status == ThinkingNodeStatus.Active)
            {
                content.Children.Add(new TextBlock
                {
                    Text = node.Subtitle ?? "Processing...",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColMuted),
                    FontFamily = MainFont,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(24, 4, 0, 0)
                });
            }

            card.Child = content;

            // Build copy text from node info
            var copyParts = new List<string>();
            if (!string.IsNullOrEmpty(node.Title)) copyParts.Add(node.Title);
            if (!string.IsNullOrEmpty(node.Description)) copyParts.Add(node.Description);
            if (!string.IsNullOrEmpty(node.CodeSnippet)) copyParts.Add(node.CodeSnippet);
            if (!string.IsNullOrEmpty(node.Output)) copyParts.Add(node.Output);
            if (!string.IsNullOrEmpty(node.ErrorMessage)) copyParts.Add(node.ErrorMessage);
            var cardWithCopy = WrapWithCopyButton(card, string.Join("\n", copyParts));
            wrapper.Children.Add(cardWithCopy);

            // ── Connecting line ──
            if (!isLast)
            {
                var line = new Border
                {
                    Width = 2, Height = 12,
                    Background = new SolidColorBrush(ColBorder),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(16, 0, 0, 0)
                };
                wrapper.Children.Add(line);
            }

            return wrapper;
        }

        /// <summary>
        /// Builds a monospace code block with dark background.
        /// </summary>
        private Border BuildCodeBlock(string code)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(ColCodeBg),
                BorderBrush = new SolidColorBrush(ColBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8)
            };

            border.Child = CreateSelectableText(code, 10.5, ColTextSec, MonoFont);

            return border;
        }

        // ─── Text Selection & Copy Helpers ───

        /// <summary>
        /// Creates a read-only TextBox styled to look like a TextBlock, with native text selection support.
        /// </summary>
        private TextBox CreateSelectableText(string text, double fontSize, Color foreground,
            FontFamily fontFamily = null, TextWrapping wrapping = TextWrapping.Wrap,
            double lineHeight = 0, FontWeight? fontWeight = null, FontStyle? fontStyle = null,
            double maxWidth = 0, TextAlignment textAlignment = TextAlignment.Left)
        {
            var tb = new TextBox
            {
                Text = text ?? "",
                FontSize = fontSize,
                FontFamily = fontFamily ?? MainFont,
                Foreground = new SolidColorBrush(foreground),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsReadOnly = true,
                IsTabStop = false,
                TextWrapping = wrapping,
                Cursor = Cursors.IBeam,
                SelectionBrush = new SolidColorBrush(ColPrimary),
                CaretBrush = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                TextAlignment = textAlignment
            };

            if (fontWeight.HasValue) tb.FontWeight = fontWeight.Value;
            if (fontStyle.HasValue) tb.FontStyle = fontStyle.Value;
            if (maxWidth > 0) tb.MaxWidth = maxWidth;

            // Bubble mouse wheel to parent ScrollViewer (prevent TextBox from stealing scroll)
            tb.PreviewMouseWheel += (sender, e) =>
            {
                e.Handled = true;
                var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent
                };
                ((UIElement)sender).RaiseEvent(args);
            };

            return tb;
        }

        /// <summary>
        /// Wraps content in a Grid with a hover-reveal copy button at top-right.
        /// </summary>
        private Grid WrapWithCopyButton(FrameworkElement content, string copyText)
        {
            var grid = new Grid();
            grid.Children.Add(content);

            var copyBtn = new Border
            {
                Width = 26, Height = 26,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, ColBg.R, ColBg.G, ColBg.B)),
                BorderBrush = new SolidColorBrush(ColBorder),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                Cursor = Cursors.Hand,
                Opacity = 0,
                ToolTip = "Copy"
            };

            var copyIcon = new TextBlock
            {
                Text = "\U0001F4CB", // clipboard emoji
                FontSize = 12,
                FontFamily = MainFont,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(ColTextSec)
            };
            copyBtn.Child = copyIcon;

            // Hover show/hide
            grid.MouseEnter += (s, e) => copyBtn.Opacity = 1;
            grid.MouseLeave += (s, e) => copyBtn.Opacity = 0;

            // Click to copy
            copyBtn.MouseLeftButtonDown += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(copyText ?? "");
                    // Brief check mark feedback
                    copyIcon.Text = "✓";
                    copyIcon.Foreground = new SolidColorBrush(ColSuccess);

                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                    timer.Tick += (t, args) =>
                    {
                        copyIcon.Text = "\U0001F4CB";
                        copyIcon.Foreground = new SolidColorBrush(ColTextSec);
                        timer.Stop();
                    };
                    timer.Start();
                }
                catch (Exception ex) { Services.ZexusLogger.Warn($"Clipboard copy failed: {ex.Message}"); }
                e.Handled = true;
            };

            grid.Children.Add(copyBtn);
            return grid;
        }

        /// <summary>
        /// Extracts the most meaningful input parameters for a one-line summary.
        /// </summary>
        /// <summary>
        /// Build a concise result summary from tool result data (for Thinking Chain display).
        /// </summary>
        /// <summary>
        /// Strip ZEXUS_JSON protocol lines from display text.
        /// </summary>
        private static string StripZexusJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var lines = text.Split('\n')
                .Where(line => !line.TrimStart().StartsWith("ZEXUS_JSON:"))
                .ToArray();
            return string.Join("\n", lines).TrimEnd();
        }

        private string BuildToolResultSummary(string toolName, ToolResult result)
        {
            if (result?.Data == null) return null;
            try
            {
                var parts = new List<string>();

                // Side effects counts
                int created = 0, modified = 0, deleted = 0;
                if (result.Data.ContainsKey("created_count") && result.Data["created_count"] is long cc) created = (int)cc;
                if (result.Data.ContainsKey("modified_count") && result.Data["modified_count"] is long mc) modified = (int)mc;
                if (result.Data.ContainsKey("deleted_count") && result.Data["deleted_count"] is long dc) deleted = (int)dc;

                if (created > 0) parts.Add($"created {created}");
                if (modified > 0) parts.Add($"modified {modified}");
                if (deleted > 0) parts.Add($"deleted {deleted}");

                if (parts.Count > 0)
                    return "✅ " + string.Join(", ", parts);

                // Check has_transaction for read-only info
                bool hasTransaction = result.Data.ContainsKey("has_transaction") && result.Data["has_transaction"] is bool ht && ht;
                if (!hasTransaction && toolName == "ExecuteCode")
                {
                    // Truncate output for display
                    var output = result.Data.ContainsKey("output") ? result.Data["output"]?.ToString() : result.Message;
                    if (!string.IsNullOrEmpty(output))
                    {
                        var firstLine = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                        if (firstLine != null && firstLine.Length > 80)
                            firstLine = firstLine.Substring(0, 77) + "...";
                        return "✅ " + (firstLine ?? "Done");
                    }
                }

                return null;
            }
            catch { return null; }
        }

        private string FormatInputParams(string toolName, Dictionary<string, object> input)
        {
            if (input == null || input.Count == 0) return null;

            var parts = new List<string>();
            try
            {
                switch (toolName)
                {
                    case "SearchElements":
                        if (input.ContainsKey("category")) parts.Add($"category: {input["category"]}");
                        if (input.ContainsKey("filter_param")) parts.Add($"filter: {input["filter_param"]} {(input.ContainsKey("filter_operator") ? input["filter_operator"] : "=")} {(input.ContainsKey("filter_value") ? input["filter_value"] : "")}");
                        break;

                    case "SetElementParameter":
                        if (input.ContainsKey("element_id")) parts.Add($"id: {input["element_id"]}");
                        if (input.ContainsKey("parameter_name")) parts.Add($"param: {input["parameter_name"]}");
                        if (input.ContainsKey("value")) parts.Add($"value: {input["value"]}");
                        break;

                    case "GetParameterValues":
                        if (input.ContainsKey("category")) parts.Add($"category: {input["category"]}");
                        if (input.ContainsKey("parameter_name")) parts.Add($"param: {input["parameter_name"]}");
                        break;

                    case "CreateProjectParameter":
                        if (input.ContainsKey("parameter_name")) parts.Add($"name: {input["parameter_name"]}");
                        if (input.ContainsKey("data_type")) parts.Add($"type: {input["data_type"]}");
                        if (input.ContainsKey("binding")) parts.Add($"binding: {input["binding"]}");
                        break;

                    case "AddScheduleField":
                        if (input.ContainsKey("schedule_name")) parts.Add($"schedule: {input["schedule_name"]}");
                        if (input.ContainsKey("mode")) parts.Add($"mode: {input["mode"]}");
                        if (input.ContainsKey("field_name")) parts.Add($"field: {input["field_name"]}");
                        break;

                    case "ModifyScheduleFilter":
                        if (input.ContainsKey("schedule_name")) parts.Add($"schedule: {input["schedule_name"]}");
                        if (input.ContainsKey("mode")) parts.Add($"mode: {input["mode"]}");
                        if (input.ContainsKey("field_name")) parts.Add($"field: {input["field_name"]}");
                        if (input.ContainsKey("operator")) parts.Add($"op: {input["operator"]}");
                        break;

                    case "ModifyScheduleSort":
                        if (input.ContainsKey("schedule_name")) parts.Add($"schedule: {input["schedule_name"]}");
                        if (input.ContainsKey("mode")) parts.Add($"mode: {input["mode"]}");
                        if (input.ContainsKey("field_name")) parts.Add($"field: {input["field_name"]}");
                        break;

                    case "SelectElements":
                    case "IsolateElements":
                        if (input.ContainsKey("element_ids"))
                        {
                            var ids = input["element_ids"];
                            if (ids is System.Collections.ICollection col)
                                parts.Add($"{col.Count} elements");
                            else
                                parts.Add($"elements: {ids}");
                        }
                        if (input.ContainsKey("mode")) parts.Add($"mode: {input["mode"]}");
                        break;

                    case "GetModelOverview":
                        parts.Add("full model scan");
                        break;

                    case "GetWarnings":
                        if (input.ContainsKey("category")) parts.Add($"category: {input["category"]}");
                        break;

                    case "ListSheets":
                    case "ListViews":
                        // Minimal params
                        break;

                    case "PrintSheets":
                        if (input.ContainsKey("sheet_numbers")) parts.Add($"sheets: {input["sheet_numbers"]}");
                        if (input.ContainsKey("output_path")) parts.Add($"→ {input["output_path"]}");
                        break;

                    case "ExportDocument":
                        if (input.ContainsKey("format")) parts.Add($"format: {input["format"]}");
                        if (input.ContainsKey("output_folder")) parts.Add($"→ {input["output_folder"]}");
                        break;

                    case "BatchSetParameter":
                        if (input.ContainsKey("parameter_name")) parts.Add($"param: {input["parameter_name"]}");
                        if (input.ContainsKey("value")) parts.Add($"→ {input["value"]}");
                        if (input.ContainsKey("element_ids") && input["element_ids"] is System.Collections.ICollection bspCol)
                            parts.Add($"{bspCol.Count} elements");
                        if (input.ContainsKey("preview")) parts.Add(Convert.ToBoolean(input["preview"]) ? "preview" : "execute");
                        break;

                    case "ColorElements":
                        if (input.ContainsKey("color")) parts.Add($"color: {input["color"]}");
                        if (input.ContainsKey("category")) parts.Add($"category: {input["category"]}");
                        if (input.ContainsKey("group_by_parameter")) parts.Add($"group by: {input["group_by_parameter"]}");
                        if (input.ContainsKey("clear") && Convert.ToBoolean(input["clear"])) parts.Add("clear");
                        break;

                    case "GetViewsOnSheet":
                        if (input.ContainsKey("sheet_number")) parts.Add($"sheet: {input["sheet_number"]}");
                        break;

                    case "GetElementLocation":
                        if (input.ContainsKey("element_ids") && input["element_ids"] is System.Collections.ICollection locCol)
                            parts.Add($"{locCol.Count} elements");
                        break;

                    case "LinkedModel":
                        if (input.ContainsKey("mode")) parts.Add($"mode: {input["mode"]}");
                        if (input.ContainsKey("link_name")) parts.Add($"link: {input["link_name"]}");
                        if (input.ContainsKey("category")) parts.Add($"category: {input["category"]}");
                        break;

                    case "CompareLinkedElementLocations":
                        if (input.ContainsKey("link_name")) parts.Add($"link: {input["link_name"]}");
                        if (input.ContainsKey("host_category")) parts.Add($"host: {input["host_category"]}");
                        if (input.ContainsKey("key_parameter")) parts.Add($"key: {input["key_parameter"]}");
                        break;

                    case "SheetSet":
                        if (input.ContainsKey("mode")) parts.Add($"mode: {input["mode"]}");
                        if (input.ContainsKey("sheet_set_name")) parts.Add($"set: {input["sheet_set_name"]}");
                        break;

                    case "Revision":
                        if (input.ContainsKey("mode")) parts.Add($"mode: {input["mode"]}");
                        if (input.ContainsKey("revision_id")) parts.Add($"rev: {input["revision_id"]}");
                        if (input.ContainsKey("preview")) parts.Add(Convert.ToBoolean(input["preview"]) ? "preview" : "execute");
                        break;

                    case "GetElementsByScope":
                        if (input.ContainsKey("mode")) parts.Add($"scope: {input["mode"]}");
                        break;

                    case "CheckMissingParameters":
                        if (input.ContainsKey("parameter_names")) parts.Add($"params: {input["parameter_names"]}");
                        if (input.ContainsKey("element_ids") && input["element_ids"] is System.Collections.ICollection cmpCol)
                            parts.Add($"{cmpCol.Count} elements");
                        break;

                    case "AnalyzeParameterValues":
                        if (input.ContainsKey("parameter_name")) parts.Add($"param: {input["parameter_name"]}");
                        if (input.ContainsKey("outlier_mode")) parts.Add($"outlier: {input["outlier_mode"]}");
                        break;

                    case "CreateQCEvidencePack":
                        if (input.ContainsKey("name")) parts.Add($"name: {input["name"]}");
                        if (input.ContainsKey("dry_run")) parts.Add(Convert.ToBoolean(input["dry_run"]) ? "dry run" : "execute");
                        break;

                    default:
                        // Generic: show first 2 key-value pairs
                        foreach (var kv in input.Take(2))
                        {
                            if (kv.Key != "description")
                            {
                                var val = kv.Value?.ToString() ?? "";
                                if (val.Length > 30) val = val.Substring(0, 30) + "...";
                                parts.Add($"{kv.Key}: {val}");
                            }
                        }
                        break;
                }
            }
            catch (Exception ex) { Services.ZexusLogger.Warn($"FormatToolInputSummary: {ex.Message}"); }

            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }


        // ── Tool name → thinking chain display mapping ──
        private (string title, Color color, string icon) MapToolToThinkingNode(string toolName)
        {
            switch (toolName)
            {
                case "GetModelOverview": return ("Query Model", ColPrimary, "Q");
                case "SearchElements": return ("Search Elements", ColPrimary, "S");
                case "GetParameterValues": return ("Query Parameters", ColPrimary, "P");
                case "GetSelection": return ("Read Selection", ColPrimary, "R");
                case "GetWarnings": return ("Check Warnings", ColWarning, "W");
                case "SelectElements": return ("Select Elements", ColAccent, "H");
                case "IsolateElements": return ("Isolate View", ColAccent, "I");
                case "SetCategoryVisibility": return ("Category Visibility", ColAccent, "C");
                case "SetElementParameter": return ("Set Parameter", ColWarning, "W");
                case "CreateProjectParameter": return ("Create Param", ColWarning, "P");
                case "AddScheduleField": return ("Schedule Field", ColAccent, "F");
                case "ModifyScheduleFilter": return ("Schedule Filter", ColAccent, "F");
                case "ModifyScheduleSort": return ("Schedule Sort", ColAccent, "S");
                case "CreateView": return ("Create View", ColSuccess, "V");
                case "ExecuteCode": return ("Execute Code", ColWarning, "X");
                case "ListSheets": return ("List Sheets", ColSuccess, "S");
                case "ListViews": return ("List Views", ColSuccess, "V");
                case "PrintSheets": return ("Print PDF", ColSuccess, "P");
                case "ExportDocument": return ("Export", ColSuccess, "E");
                // Activated reserves
                case "BatchSetParameter": return ("Batch Set", ColWarning, "B");
                case "ColorElements": return ("Color Elements", ColAccent, "C");
                case "GetViewsOnSheet": return ("Views on Sheet", ColPrimary, "V");
                // New tools
                case "GetElementLocation": return ("Get Location", ColPrimary, "L");
                case "LinkedModel": return ("Linked Model", ColPrimary, "L");
                case "CompareLinkedElementLocations": return ("Compare Links", ColWarning, "C");
                case "SheetSet": return ("Sheet Set", ColSuccess, "S");
                case "Revision": return ("Revision", ColWarning, "R");
                // QAQC tools
                case "GetElementsByScope": return ("Scope Elements", ColPrimary, "S");
                case "CheckWorksetAssignment": return ("Check Workset", ColWarning, "W");
                case "CheckMissingParameters": return ("Check Missing", ColWarning, "M");
                case "AnalyzeParameterValues": return ("Analyze Values", ColPrimary, "A");
                case "CreateQCEvidencePack": return ("QC Evidence", ColWarning, "Q");
                // MEP tools
                case "SystemBrowser": return ("MEP Systems", ColPrimary, "M");
                case "CheckMEPSystems": return ("Check MEP", ColWarning, "M");
                case "ShowDisconnects": return ("Disconnects", ColWarning, "D");
                // Documentation Pipeline tools
                case "CreateSheets": return ("Create Sheets", ColSuccess, "S");
                case "CreateViews": return ("Create Views", ColSuccess, "V");
                case "PlaceViewsOnSheets": return ("Place on Sheets", ColSuccess, "P");
                case "TagElements": return ("Tag Elements", ColWarning, "T");
                case "DimensionElements": return ("Add Dimensions", ColWarning, "D");
                case "DuplicateView": return ("Duplicate View", ColSuccess, "D");
                default: return ("Execute", ColTextSec, "?");
            }
        }

        #endregion

        #region Output Preview Panel

        /// <summary>
        /// Only creates output records for NEW artifacts added to the model or exported files.
        /// Edits, queries, and navigation are NOT tracked here — they belong in the thinking chain.
        /// </summary>
        private OutputRecord MapToolResultToOutputRecord(string toolName, ToolResult result)
        {
            if (!result.Success) return null;
            var data = result.Data;

            switch (toolName)
            {
                // ── New view created ──
                case "CreateSchedule":
                {
                    long? viewId = null;
                    if (data != null && data.TryGetValue("schedule_id", out var sid))
                        viewId = ConvertToLong(sid);

                    string catName = data != null && data.TryGetValue("category", out var c) ? c?.ToString() : "";
                    string schedName = data != null && data.TryGetValue("schedule_name", out var sn) ? sn?.ToString() : "Schedule";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ScheduleCreated,
                        Title = schedName,
                        Subtitle = $"Created \u2022 {catName}",
                        IconGlyph = "\U0001F4CA",
                        IconColor = ColSuccess,
                        ToolName = toolName,
                        ViewId = viewId,
                        ScheduleId = viewId, // same as schedule_id from tool result
                        ScheduleName = schedName,
                        Data = data
                    };
                }

                // ── New view created (3D, Section, Elevation, FloorPlan, etc.) ──
                case "CreateView":
                {
                    long? cvViewId = null;
                    if (data != null && data.TryGetValue("view_id", out var cvid))
                        cvViewId = ConvertToLong(cvid);

                    string cvViewName = data != null && data.TryGetValue("view_name", out var cvn) ? cvn?.ToString() : "View";
                    string cvViewType = data != null && data.TryGetValue("view_type", out var cvt) ? cvt?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewCreated,
                        Title = cvViewName,
                        Subtitle = $"Created \u2022 {cvViewType}",
                        IconGlyph = "\U0001F441",
                        IconColor = ColSuccess,
                        ToolName = toolName,
                        ViewId = cvViewId,
                        Data = data
                    };
                }

                // ── View visibility changed (category show/hide) ──
                case "SetCategoryVisibility":
                {
                    string visMode = data != null && data.TryGetValue("mode", out var vm) ? vm?.ToString() : "";
                    string viewName = data != null && data.TryGetValue("view", out var vn) ? vn?.ToString() : "View";
                    bool isTemp = data != null && data.TryGetValue("temporary", out var tmpVal) && Convert.ToBoolean(tmpVal);
                    string persistLabel = isTemp ? "Temp" : "Permanent";

                    // "reset" is itself the undo action — no card needed (it restores default state)
                    if (visMode == "reset") return null;

                    // Build a readable subtitle
                    var catShown = ExtractStringList(data, "categories_shown") ?? ExtractStringList(data, "categories");
                    string catLabel = catShown != null && catShown.Count > 0
                        ? string.Join(", ", catShown.Take(3)) + (catShown.Count > 3 ? $" +{catShown.Count - 3}" : "")
                        : "";
                    string visSub = visMode == "show_only" ? $"Show only \u2022 {catLabel} \u2022 {persistLabel}"
                           : visMode == "hide" ? $"Hidden \u2022 {catLabel} \u2022 {persistLabel}"
                           : $"Visible \u2022 {catLabel} \u2022 {persistLabel}";

                    // Find the parent ViewCreated/ScheduleCreated record that owns this view
                    string parentId = FindParentRecordId(viewName);

                    // Undo = call SetCategoryVisibility with mode="reset" and same temporary flag
                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewModified,
                        Title = viewName,
                        Subtitle = visSub,
                        IconGlyph = "\U0001F441",
                        IconColor = ColAccent,
                        ToolName = toolName,
                        ParentRecordId = parentId,
                        Data = data,
                        CanUndoRecord = true,
                        UndoToolName = "SetCategoryVisibility",
                        UndoData = new Dictionary<string, object>
                        {
                            ["mode"] = "reset",
                            ["temporary"] = isTemp
                        }
                    };
                }

                // ── New parameter created ──
                case "CreateProjectParameter":
                {
                    string paramName = data != null && data.TryGetValue("parameter_name", out var pn) ? pn?.ToString() : "Parameter";
                    string paramType = data != null && data.TryGetValue("parameter_type", out var pt) ? pt?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ParameterCreated,
                        Title = paramName,
                        Subtitle = $"Created \u2022 {paramType}",
                        IconGlyph = "\u270F",
                        IconColor = ColWarning,
                        ToolName = toolName,
                        Data = data
                    };
                }

                // ── File exported ──
                case "ExportDocument":
                {
                    string format = data != null && data.TryGetValue("format", out var f) ? f?.ToString() : "File";
                    string folder = data != null && data.TryGetValue("output_folder", out var fo) ? fo?.ToString() : null;
                    var files = ExtractFileList(data, "output_files");
                    int count = files?.Count ?? 0;

                    string firstFile = files != null && files.Count > 0 ? files[0] : null;
                    if (firstFile != null && folder != null && !System.IO.Path.IsPathRooted(firstFile))
                        firstFile = System.IO.Path.Combine(folder, firstFile);

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.FileExported,
                        Title = $"{format} Export",
                        Subtitle = count == 1 ? System.IO.Path.GetFileName(firstFile ?? "file") : $"{count} files exported",
                        IconGlyph = "\U0001F4C1",
                        IconColor = ColSuccess,
                        ToolName = toolName,
                        FilePath = count == 1 ? firstFile : null,
                        FilePaths = count > 1 ? files : null,
                        FolderPath = folder,
                        Data = data
                    };
                }

                // ── Sheets printed to PDF ──
                case "PrintSheets":
                {
                    var files = ExtractFileList(data, "output_files");
                    string firstFile = files != null && files.Count > 0 ? files[0] : null;

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.FilePrinted,
                        Title = "PDF Print",
                        Subtitle = firstFile != null ? System.IO.Path.GetFileName(firstFile) : "Printed",
                        IconGlyph = "\U0001F5A8",
                        IconColor = ColSuccess,
                        ToolName = toolName,
                        FilePath = firstFile,
                        FilePaths = files != null && files.Count > 1 ? files : null,
                        Data = data
                    };
                }

                // ── Parameter value modified ──
                case "SetElementParameter":
                {
                    // Skip preview mode
                    if (data != null && data.TryGetValue("preview", out var prev) && Convert.ToBoolean(prev))
                        return null;

                    string paramName = data != null && data.TryGetValue("parameter_name", out var pn) ? pn?.ToString() : "Parameter";
                    string oldVal = data != null && data.TryGetValue("old_value", out var ov) ? ov?.ToString() : "";
                    string newVal = data != null && data.TryGetValue("new_value", out var nv) ? nv?.ToString() : "";
                    string elemName = data != null && data.TryGetValue("element_name", out var en) ? en?.ToString() : "";
                    long elemId = 0;
                    if (data != null && data.TryGetValue("element_id", out var eid))
                        elemId = ConvertToLong(eid) ?? 0;

                    var entry = new ParameterChangeEntry
                    {
                        ElementId = elemId,
                        ElementName = elemName,
                        OldValue = oldVal,
                        NewValue = newVal
                    };

                    // Try to aggregate into existing record for same parameter name
                    var existing = _outputRecords.LastOrDefault(r =>
                        r.RecordType == OutputRecordType.ParameterSet &&
                        r.ParameterName == paramName);

                    if (existing != null)
                    {
                        existing.ChangeEntries.Add(entry);
                        existing.Subtitle = $"{existing.ChangeEntries.Count} elements modified";
                        existing.Timestamp = DateTime.Now;
                        return null; // signal caller to just rebuild cards, not add new record
                    }

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ParameterSet,
                        Title = paramName,
                        Subtitle = $"{elemName}: {oldVal} \u2192 {newVal}",
                        IconGlyph = "\u270F",
                        IconColor = ColWarning,
                        ToolName = toolName,
                        ParameterName = paramName,
                        ChangeEntries = new List<ParameterChangeEntry> { entry },
                        Data = data
                    };
                }

                // ── Schedule modifications (aggregated per schedule) ──
                case "AddScheduleField":
                case "FormatScheduleField":
                case "ModifyScheduleFilter":
                case "ModifyScheduleSort":
                {
                    // Skip read-only list operations
                    if (data != null && data.TryGetValue("mode", out var mode) && mode?.ToString() == "list")
                        return null;

                    string schedName = data != null && data.TryGetValue("schedule_name", out var sn) ? sn?.ToString() : null;
                    if (string.IsNullOrEmpty(schedName)) return null;

                    long? schedId = null;
                    if (data != null && data.TryGetValue("schedule_id", out var schId))
                        schedId = ConvertToLong(schId);

                    var changeEntry = BuildScheduleChangeEntry(toolName, data);
                    if (changeEntry == null) return null;

                    // Try to aggregate into existing record for same schedule
                    var existing = _outputRecords.LastOrDefault(r =>
                        r.RecordType == OutputRecordType.ScheduleModified &&
                        r.ScheduleName == schedName);

                    if (existing != null)
                    {
                        existing.ScheduleChangeEntries.Add(changeEntry);
                        existing.Subtitle = FormatScheduleModSummary(existing.ScheduleChangeEntries);
                        existing.Timestamp = DateTime.Now;
                        if (schedId.HasValue && !existing.ScheduleId.HasValue)
                            existing.ScheduleId = schedId;
                        return null; // signal caller to rebuild cards
                    }

                    var entries = new List<ScheduleChangeEntry> { changeEntry };
                    string schedParentId = FindParentRecordIdByElementId(schedId);
                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ScheduleModified,
                        Title = schedName,
                        Subtitle = FormatScheduleModSummary(entries),
                        IconGlyph = "\U0001F4CB",
                        IconColor = ColPrimary,
                        ToolName = toolName,
                        ScheduleName = schedName,
                        ScheduleId = schedId,
                        ParentRecordId = schedParentId,
                        ScheduleChangeEntries = entries,
                        Data = data
                    };
                }

                // ── Code execution — only show card for Actions (has Transaction), skip Queries ──
                case "ExecuteCode":
                {
                    // ── OutputHint routing: if ZEXUS_JSON includes output_type, generate rich cards ──
                    string outputType = data != null && data.TryGetValue("output_type", out var ot)
                        ? ot?.ToString() : null;

                    if (!string.IsNullOrEmpty(outputType))
                        return MapExecuteCodeOutputHint(outputType, data);

                    // ── Fallback: generic ExecuteCode card ──
                    // Query (no Transaction) → no card (thinking chain only)
                    // Action (has Transaction) → generic ⚙ card
                    bool hasTx = data != null && data.TryGetValue("has_transaction", out var htx)
                        && htx is bool htxBool && htxBool;
                    if (!hasTx) return null;

                    string desc = data != null && data.TryGetValue("description", out var d) ? d?.ToString() : null;
                    if (string.IsNullOrEmpty(desc)) return null;

                    string ecOutput = data != null && data.TryGetValue("output", out var o) ? o?.ToString() : "";
                    string retVal = data != null && data.TryGetValue("return_value", out var rv) ? rv?.ToString() : "";

                    string subtitle = !string.IsNullOrEmpty(ecOutput) ? ecOutput.Split('\n')[0] : retVal;
                    if (subtitle != null && subtitle.Length > 60) subtitle = subtitle.Substring(0, 60) + "...";
                    if (string.IsNullOrEmpty(subtitle)) subtitle = "Executed";

                    long? codeViewId = null;
                    if (data != null && data.TryGetValue("view_id", out var codeVid))
                        codeViewId = ConvertToLong(codeVid);

                    // Fallback: if ZEXUS_JSON didn't provide view_id, check sys_created for views
                    if (codeViewId == null && data != null && data.ContainsKey("sys_created")
                        && data["sys_created"] is List<ElementDetail> sysCreated)
                    {
                        var firstView = sysCreated.FirstOrDefault(e => e.IsView);
                        if (firstView != null) codeViewId = firstView.ElementId;
                    }

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.CodeExecuted,
                        Title = desc,
                        Subtitle = subtitle,
                        IconGlyph = "\u2699",
                        IconColor = ColWarning,
                        ToolName = toolName,
                        ViewId = codeViewId,
                        Data = data
                    };
                }

                // ── QC Evidence Pack — view with isolated elements ──
                case "CreateQCEvidencePack":
                {
                    bool isDryRun = data != null && data.TryGetValue("dry_run", out var dr)
                        && dr is bool drBool && drBool;
                    if (isDryRun) return null;

                    long? qcViewId = null;
                    if (data != null && data.TryGetValue("created_view_id", out var qcVid))
                        qcViewId = ConvertToLong(qcVid);

                    string qcViewName = data != null && data.TryGetValue("created_view_name", out var qcVn) ? qcVn?.ToString() : "QC View";
                    string qcElemCount = data != null && data.TryGetValue("element_count", out var qcEc) ? qcEc?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewCreated,
                        Title = qcViewName,
                        Subtitle = $"Created \u2022 {qcElemCount} elements isolated",
                        IconGlyph = "\U0001F441",
                        IconColor = ColSuccess,
                        ToolName = toolName,
                        ViewId = qcViewId,
                        Data = data
                    };
                }

                // ── Color override applied to elements ──
                case "ColorElements":
                {
                    bool isCleared = data != null && data.ContainsKey("cleared_count");
                    if (isCleared) return null;

                    bool isGroupBy = data != null && data.ContainsKey("groups_count");
                    if (isGroupBy)
                    {
                        string ceParam = data.TryGetValue("parameter", out var cep) ? cep?.ToString() : "Parameter";
                        string ceTotalEl = data.TryGetValue("total_elements", out var cete) ? cete?.ToString() : "";
                        string ceGroups = data.TryGetValue("groups_count", out var ceg) ? ceg?.ToString() : "";

                        return new OutputRecord
                        {
                            RecordType = OutputRecordType.ViewModified,
                            Title = $"Color by {ceParam}",
                            Subtitle = $"{ceTotalEl} elements in {ceGroups} groups",
                            IconGlyph = "\U0001F3A8",
                            IconColor = ColAccent,
                            ToolName = toolName,
                            CanUndoRecord = true,
                            Data = data
                        };
                    }

                    string ceCount = data != null && data.TryGetValue("colored_count", out var cec) ? cec?.ToString() : "";
                    string ceColor = data != null && data.TryGetValue("color", out var cecc) ? cecc?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewModified,
                        Title = "Color Override",
                        Subtitle = $"{ceCount} elements colored {ceColor}",
                        IconGlyph = "\U0001F3A8",
                        IconColor = ColAccent,
                        ToolName = toolName,
                        CanUndoRecord = true,
                        Data = data
                    };
                }

                // ── Isolate/hide elements in view ──
                case "IsolateElements":
                {
                    string ieMode = data != null && data.TryGetValue("mode", out var iem) ? iem?.ToString() : "";
                    if (ieMode == "reset") return null;

                    string ieView = data != null && data.TryGetValue("view", out var iev) ? iev?.ToString() : "Current View";
                    string ieCount = data != null && data.TryGetValue("element_count", out var iec) ? iec?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewModified,
                        Title = ieView,
                        Subtitle = $"Isolated \u2022 {ieCount} elements",
                        IconGlyph = "\U0001F441",
                        IconColor = ColAccent,
                        ToolName = toolName,
                        CanUndoRecord = true,
                        UndoToolName = "IsolateElements",
                        UndoData = new Dictionary<string, object> { ["mode"] = "reset" },
                        Data = data
                    };
                }

                // ── Tag elements in view ──
                case "TagElements":
                {
                    string teCount = data != null && data.TryGetValue("tagged_count", out var tec) ? tec?.ToString() : "";
                    string teCat = data != null && data.TryGetValue("category", out var tecat) ? tecat?.ToString() : "";
                    string teTagType = data != null && data.TryGetValue("tag_type_used", out var tett) ? tett?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewModified,
                        Title = $"Tag {teCat}",
                        Subtitle = $"{teCount} elements tagged \u2022 {teTagType}",
                        IconGlyph = "\U0001F3F7",
                        IconColor = ColWarning,
                        ToolName = toolName,
                        Data = data
                    };
                }

                // ── View filter created/applied ──
                case "CreateViewFilter":
                {
                    string vfName = data != null && data.TryGetValue("filter_name", out var vfn) ? vfn?.ToString() : "Filter";
                    string vfRule = data != null && data.TryGetValue("rule", out var vfr) ? vfr?.ToString() : "";
                    string vfViewName = data != null && data.TryGetValue("view_name", out var vfvn) ? vfvn?.ToString() : null;

                    string vfParentId = !string.IsNullOrEmpty(vfViewName) ? FindParentRecordId(vfViewName) : null;

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewModified,
                        Title = vfName,
                        Subtitle = $"Filter \u2022 {vfRule}",
                        IconGlyph = "\U0001F50D",
                        IconColor = ColSuccess,
                        ToolName = toolName,
                        ParentRecordId = vfParentId,
                        Data = data
                    };
                }

                // ── Batch parameter set ──
                case "BatchSetParameter":
                {
                    bool bspPreview = data != null && data.TryGetValue("preview", out var bspp)
                        && bspp is bool bspBool && bspBool;
                    if (bspPreview) return null;

                    string bspParam = data != null && data.TryGetValue("parameter_name", out var bspn) ? bspn?.ToString() : "Parameter";
                    string bspValue = data != null && data.TryGetValue("new_value", out var bspv) ? bspv?.ToString() : "";
                    string bspCount = data != null && data.TryGetValue("success_count", out var bspc) ? bspc?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ParameterSet,
                        Title = bspParam,
                        Subtitle = $"{bspCount} elements set to '{bspValue}'",
                        IconGlyph = "\u270F",
                        IconColor = ColWarning,
                        ToolName = toolName,
                        ParameterName = bspParam,
                        Data = data
                    };
                }

                // Everything else (queries, edits, navigation) → no output record
                default:
                    return null;
            }
        }

        /// <summary>
        /// Route ExecuteCode results with ZEXUS_JSON output_type to rich UI cards.
        /// This bridges the gap when LLM uses ExecuteCode for operations that
        /// predefined tools would normally handle (view creation, visibility changes, etc.).
        /// Falls back to generic ⚙ card for unrecognized output_types.
        /// </summary>
        private OutputRecord MapExecuteCodeOutputHint(string outputType, Dictionary<string, object> data)
        {
            switch (outputType)
            {
                // ── View created via ExecuteCode ──
                case "view_created":
                {
                    long? viewId = data.TryGetValue("view_id", out var vid) ? ConvertToLong(vid) : null;
                    string viewName = data.TryGetValue("view_name", out var vn) ? vn?.ToString() : "View";
                    string viewType = data.TryGetValue("view_type", out var vt) ? vt?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewCreated,
                        Title = viewName,
                        Subtitle = $"Created \u2022 {viewType}",
                        IconGlyph = "\U0001F441",
                        IconColor = ColSuccess,
                        ToolName = "ExecuteCode",
                        ViewId = viewId,
                        Data = data
                    };
                }

                // ── Schedule created via ExecuteCode ──
                case "schedule_created":
                {
                    long? schedId = data.TryGetValue("schedule_id", out var sid) ? ConvertToLong(sid) : null;
                    string schedName = data.TryGetValue("schedule_name", out var sn) ? sn?.ToString() : "Schedule";
                    string catName = data.TryGetValue("category", out var c) ? c?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ScheduleCreated,
                        Title = schedName,
                        Subtitle = $"Created \u2022 {catName}",
                        IconGlyph = "\U0001F4CA",
                        IconColor = ColSuccess,
                        ToolName = "ExecuteCode",
                        ViewId = schedId,
                        ScheduleId = schedId,
                        ScheduleName = schedName,
                        Data = data
                    };
                }

                // ── Sheets created via ExecuteCode ──
                case "sheets_created":
                {
                    int count = data.TryGetValue("created_count", out var cc) ? Convert.ToInt32(cc) : 0;
                    string firstSheet = data.TryGetValue("first_sheet", out var fs) ? fs?.ToString() : "";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.ViewCreated,
                        Title = count > 1 ? $"{count} Sheets Created" : firstSheet,
                        Subtitle = count > 1 ? $"First: {firstSheet}" : "Created",
                        IconGlyph = "\U0001F4C4",
                        IconColor = ColSuccess,
                        ToolName = "ExecuteCode",
                        Data = data
                    };
                }

                // ── Elements modified (batch) via ExecuteCode ──
                case "elements_modified":
                {
                    string desc = data.TryGetValue("description", out var d) ? d?.ToString() : "Modified";
                    int modCount = data.TryGetValue("modified_count", out var mc) ? Convert.ToInt32(mc) : 0;
                    string paramName = data.TryGetValue("parameter_name", out var pn) ? pn?.ToString() : null;

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.CodeExecuted,
                        Title = !string.IsNullOrEmpty(paramName) ? paramName : desc,
                        Subtitle = $"{modCount} elements modified",
                        IconGlyph = "\u270F",
                        IconColor = ColWarning,
                        ToolName = "ExecuteCode",
                        Data = data
                    };
                }

                // ── Elements deleted via ExecuteCode ──
                case "elements_deleted":
                {
                    int delCount = data.TryGetValue("deleted_count", out var dc) ? Convert.ToInt32(dc) : 0;
                    string desc = data.TryGetValue("description", out var d) ? d?.ToString() : "Deleted";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.CodeExecuted,
                        Title = desc,
                        Subtitle = $"{delCount} elements deleted",
                        IconGlyph = "\U0001F5D1",
                        IconColor = ColError,
                        ToolName = "ExecuteCode",
                        Data = data
                    };
                }

                // ── File exported via ExecuteCode ──
                case "file_exported":
                {
                    string filePath = data.TryGetValue("file_path", out var fp) ? fp?.ToString() : null;
                    string format = data.TryGetValue("format", out var f) ? f?.ToString() : "File";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.FileExported,
                        Title = $"{format} Export",
                        Subtitle = filePath != null ? System.IO.Path.GetFileName(filePath) : "Exported",
                        IconGlyph = "\U0001F4C1",
                        IconColor = ColSuccess,
                        ToolName = "ExecuteCode",
                        FilePath = filePath,
                        Data = data
                    };
                }

                // ── Unrecognized output_type: generic card with view_id if available ──
                default:
                {
                    string desc = data.TryGetValue("description", out var d) ? d?.ToString() : outputType;
                    long? viewId = data.TryGetValue("view_id", out var vid) ? ConvertToLong(vid) : null;

                    string ecOutput = data.TryGetValue("output", out var o) ? o?.ToString() : "";
                    string subtitle = !string.IsNullOrEmpty(ecOutput) ? ecOutput.Split('\n')[0] : "Executed";
                    if (subtitle.Length > 60) subtitle = subtitle.Substring(0, 60) + "...";

                    return new OutputRecord
                    {
                        RecordType = OutputRecordType.CodeExecuted,
                        Title = desc,
                        Subtitle = subtitle,
                        IconGlyph = "\u2699",
                        IconColor = ColWarning,
                        ToolName = "ExecuteCode",
                        ViewId = viewId,
                        Data = data
                    };
                }
            }
        }

        private void ShowOutputPanel()
        {
            if (OutputColumnDef.Width.Value > 0) return; // already visible

            OutputColumnDef.Width = new GridLength(350);
            OutputSplitter.Visibility = Visibility.Visible;
            OutputPanel.Visibility = Visibility.Visible;

            BuildOutputPanelUI();

            if (ActualWidth < 1000) Width = 1300;
        }

        private void CollapseOutputPanel()
        {
            OutputColumnDef.Width = new GridLength(0);
            OutputSplitter.Visibility = Visibility.Collapsed;
            OutputPanel.Visibility = Visibility.Collapsed;
            _outputCardsContainer = null;
        }

        private void BuildOutputPanelUI()
        {
            OutputContainer.Children.Clear();

            // ── Section Header ──
            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var header = new TextBlock
            {
                Text = "OUTPUT PREVIEW",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColSuccess),
                Margin = new Thickness(4, 4, 0, 10),
                FontFamily = MainFont,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(header, 0);
            headerRow.Children.Add(header);

            // Collapse button
            var collapseBtn = new TextBlock
            {
                Text = "\u2715",
                FontSize = 11,
                Foreground = new SolidColorBrush(ColMuted),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 4, 10)
            };
            collapseBtn.MouseLeftButtonDown += (s, e) => CollapseOutputPanel();
            Grid.SetColumn(collapseBtn, 1);
            headerRow.Children.Add(collapseBtn);

            OutputContainer.Children.Add(headerRow);

            // ── Cards Container ──
            _outputCardsContainer = new StackPanel();
            OutputContainer.Children.Add(_outputCardsContainer);

            RebuildOutputCards();
        }

        // ─── Transaction Journal Methods ───

        private TransactionJournalEntry BuildJournalEntry(ToolResult result)
        {
            if (result?.Data == null) return null;

            bool hasTransaction = result.Data.ContainsKey("has_transaction") && result.Data["has_transaction"] is bool ht && ht;

            // Extract description from result data
            string desc = result.Data.ContainsKey("description") ? result.Data["description"]?.ToString() : null;
            if (!string.IsNullOrEmpty(desc) && desc.StartsWith("AI Agent: "))
                desc = desc.Substring("AI Agent: ".Length);

            // Get side effect counts
            int created = 0, modified = 0, deleted = 0;
            if (result.Data.ContainsKey("created_count") && result.Data["created_count"] is long cc) created = (int)cc;
            if (result.Data.ContainsKey("modified_count") && result.Data["modified_count"] is long mc) modified = (int)mc;
            if (result.Data.ContainsKey("deleted_count") && result.Data["deleted_count"] is long dc) deleted = (int)dc;

            var entry = new TransactionJournalEntry
            {
                TurnIndex = _currentTurnIndex,
                Timestamp = DateTime.Now,
                Description = desc ?? "Code execution",
                IsWrite = hasTransaction,
                Success = result.Success,
                CreatedCount = created,
                ModifiedCount = modified,
                DeletedCount = deleted
            };

            // System-level element tracking (from DocumentChanged)
            if (result.Data.ContainsKey("sys_created") && result.Data["sys_created"] is List<ElementDetail> createdElements)
                entry.CreatedElements = createdElements;
            if (result.Data.ContainsKey("sys_modified_ids") && result.Data["sys_modified_ids"] is List<long> modIds)
                entry.ModifiedElements = modIds.Select(id => new ElementDetail { ElementId = id }).ToList();
            if (result.Data.ContainsKey("sys_deleted_ids") && result.Data["sys_deleted_ids"] is List<long> delIds)
                entry.DeletedElements = delIds.Select(id => new ElementDetail { ElementId = id }).ToList();

            // Optional ZEXUS_JSON enrichment
            if (result.Data.ContainsKey("output_type"))
                entry.OutputType = result.Data["output_type"]?.ToString();
            if (result.Data.ContainsKey("view_id"))
            {
                try { entry.ViewId = Convert.ToInt64(result.Data["view_id"]); }
                catch { /* non-fatal */ }
            }

            return entry;
        }

        private void ShowJournalPanel()
        {
            // Reuse the Output Panel column — show it if not already visible
            ShowOutputPanel();
        }

        private void RebuildJournalCards()
        {
            if (_outputCardsContainer == null)
            {
                BuildOutputPanelUI();
                return;
            }

            // Journal section is rebuilt inside RebuildOutputCards — just trigger it
            RebuildOutputCards();
        }

        private FrameworkElement BuildJournalEntryCard(TransactionJournalEntry entry)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(ColGlass),
                BorderBrush = new SolidColorBrush(ColGlassBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Icon
            var icon = new TextBlock
            {
                Text = entry.IconGlyph,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            content.Children.Add(icon);

            // Description + impact
            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var descText = new TextBlock
            {
                Text = entry.Description,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(ColText),
                FontFamily = MainFont,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            textStack.Children.Add(descText);

            var impactText = new TextBlock
            {
                Text = entry.ImpactSummary,
                FontSize = 10,
                Foreground = new SolidColorBrush(entry.IsWrite ? ColSuccess : ColMuted),
                FontFamily = MainFont
            };
            textStack.Children.Add(impactText);

            Grid.SetColumn(textStack, 1);
            content.Children.Add(textStack);

            // Undo hint (write operations only)
            if (entry.IsWrite)
            {
                var undoHint = new TextBlock
                {
                    Text = "↩",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(ColMuted),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Use Ctrl+Z in Revit to undo"
                };
                Grid.SetColumn(undoHint, 2);
                content.Children.Add(undoHint);
            }

            // Build wrapper to hold card + expandable detail
            var wrapper = new StackPanel();
            card.Child = content;
            wrapper.Children.Add(card);

            // Per-element detail list (expandable)
            var allElements = new List<(ElementDetail detail, string action)>();
            foreach (var el in entry.CreatedElements) allElements.Add((el, "created"));
            foreach (var el in entry.DeletedElements) allElements.Add((el, "deleted"));

            if (allElements.Count > 0)
            {
                var detailPanel = new StackPanel
                {
                    Margin = new Thickness(28, 0, 0, 0),
                    Visibility = Visibility.Collapsed
                };

                foreach (var (el, action) in allElements)
                {
                    var row = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF)),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8, 4, 8, 4),
                        Margin = new Thickness(0, 1, 0, 1)
                    };

                    var rowGrid = new Grid();
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    // Action icon
                    var actionIcon = new TextBlock
                    {
                        Text = action == "created" ? "➕" : "🗑",
                        FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0)
                    };
                    Grid.SetColumn(actionIcon, 0);
                    rowGrid.Children.Add(actionIcon);

                    // Element name (clickable if it's a View)
                    var nameText = new TextBlock
                    {
                        Text = $"{el.Name}",
                        FontSize = 10.5,
                        Foreground = new SolidColorBrush(ColText),
                        FontFamily = MainFont,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = $"{el.Category} (ID: {el.ElementId})"
                    };

                    if (el.IsView)
                    {
                        nameText.Cursor = Cursors.Hand;
                        nameText.TextDecorations = TextDecorations.Underline;
                        nameText.Foreground = new SolidColorBrush(ColPrimaryLt);
                        var capturedId = el.ElementId;
                        nameText.MouseLeftButtonDown += async (s, ev) =>
                        {
                            await App.RevitEventHandler.NavigateToViewAsync(capturedId);
                            ev.Handled = true;
                        };
                    }
                    Grid.SetColumn(nameText, 1);
                    rowGrid.Children.Add(nameText);

                    // Delete button (for created elements only)
                    if (action == "created")
                    {
                        var deleteBtn = new TextBlock
                        {
                            Text = "🗑",
                            FontSize = 11,
                            Foreground = new SolidColorBrush(ColMuted),
                            Cursor = Cursors.Hand,
                            VerticalAlignment = VerticalAlignment.Center,
                            ToolTip = $"Delete {el.Name}",
                            Margin = new Thickness(6, 0, 0, 0)
                        };
                        var capturedDeleteId = el.ElementId;
                        var capturedRow = row;
                        deleteBtn.MouseLeftButtonDown += async (s, ev) =>
                        {
                            await App.RevitEventHandler.DeleteElementsAsync(new[] { capturedDeleteId });
                            capturedRow.Opacity = 0.3;
                            deleteBtn.Visibility = Visibility.Collapsed;
                            ev.Handled = true;
                        };
                        deleteBtn.MouseEnter += (s, ev) => deleteBtn.Foreground = new SolidColorBrush(ColError);
                        deleteBtn.MouseLeave += (s, ev) => deleteBtn.Foreground = new SolidColorBrush(ColMuted);
                        Grid.SetColumn(deleteBtn, 2);
                        rowGrid.Children.Add(deleteBtn);
                    }

                    row.Child = rowGrid;
                    detailPanel.Children.Add(row);
                }

                wrapper.Children.Add(detailPanel);

                // Make the card clickable to expand/collapse
                card.Cursor = Cursors.Hand;
                card.MouseLeftButtonDown += (s, e) =>
                {
                    detailPanel.Visibility = detailPanel.Visibility == Visibility.Visible
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                    e.Handled = true;
                };
            }
            else if (entry.ViewId.HasValue)
            {
                // Fallback: click-to-navigate from ZEXUS_JSON
                card.Cursor = Cursors.Hand;
                card.ToolTip = "Click to navigate to view";
                var viewId = entry.ViewId.Value;
                card.MouseLeftButtonDown += async (s, e) =>
                {
                    await App.RevitEventHandler.NavigateToViewAsync(viewId);
                };
            }

            return wrapper;
        }

        private FrameworkElement BuildJournalTurnGroupCard(TransactionJournalTurnGroup turnGroup)
        {
            var wrapper = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };

            // ── Turn header card (always visible) ──
            var headerCard = new Border
            {
                Background = new SolidColorBrush(ColGlass),
                BorderBrush = new SolidColorBrush(ColGlassBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Cursor = Cursors.Hand
            };

            var headerContent = new StackPanel();

            // Turn summary line
            var summaryRow = new Grid();
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var summaryText = new TextBlock
            {
                Text = $"📋 Turn {turnGroup.TurnIndex}: {turnGroup.TurnSummary}",
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(ColText),
                FontFamily = MainFont,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(summaryText, 0);
            summaryRow.Children.Add(summaryText);

            var timeText = new TextBlock
            {
                Text = turnGroup.Timestamp.ToString("HH:mm"),
                FontSize = 10,
                Foreground = new SolidColorBrush(ColMuted),
                FontFamily = MainFont,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(timeText, 1);
            summaryRow.Children.Add(timeText);
            headerContent.Children.Add(summaryRow);

            // Impact summary line
            var impactText = new TextBlock
            {
                Text = turnGroup.AggregatedImpact,
                FontSize = 10,
                Foreground = new SolidColorBrush(ColSuccess),
                FontFamily = MainFont,
                Margin = new Thickness(0, 2, 0, 0)
            };
            headerContent.Children.Add(impactText);

            // ── Expandable detail section ──
            var detailPanel = new StackPanel
            {
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = turnGroup.IsExpanded ? Visibility.Visible : Visibility.Collapsed
            };

            foreach (var entry in turnGroup.Entries)
            {
                var entryCard = BuildJournalEntryCard(entry);
                detailPanel.Children.Add(entryCard);
            }
            headerContent.Children.Add(detailPanel);

            // Toggle expand/collapse on click
            var expandIndicator = new TextBlock
            {
                Text = turnGroup.IsExpanded ? " ▼" : " ▶",
                FontSize = 10,
                Foreground = new SolidColorBrush(ColMuted)
            };
            summaryText.Inlines.Add(new System.Windows.Documents.Run(turnGroup.IsExpanded ? " ▼" : " ▶")
            {
                FontSize = 10,
                Foreground = new SolidColorBrush(ColMuted)
            });

            headerCard.MouseLeftButtonDown += (s, e) =>
            {
                turnGroup.IsExpanded = !turnGroup.IsExpanded;
                detailPanel.Visibility = turnGroup.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
                // Rebuild to update expand indicator
                RebuildOutputCards();
            };

            headerCard.Child = headerContent;
            wrapper.Children.Add(headerCard);
            return wrapper;
        }

        private void RebuildOutputCards()
        {
            if (_outputCardsContainer == null)
            {
                BuildOutputPanelUI();
                return;
            }

            _outputCardsContainer.Children.Clear();

            // ── Transaction Journal section (grouped by turn, always first) ──
            if (_journalTurnGroups.Count > 0)
            {
                var journalHeader = new TextBlock
                {
                    Text = "TRANSACTION JOURNAL",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(ColWarning),
                    Margin = new Thickness(0, 0, 0, 6),
                    FontFamily = MainFont
                };
                _outputCardsContainer.Children.Add(journalHeader);

                foreach (var turnGroup in _journalTurnGroups)
                {
                    var turnCard = BuildJournalTurnGroupCard(turnGroup);
                    _outputCardsContainer.Children.Add(turnCard);
                }

                // Separator if there are also output records
                if (_outputRecords.Count > 0)
                {
                    var separator = new Border
                    {
                        Height = 1,
                        Background = new SolidColorBrush(ColBorder),
                        Margin = new Thickness(0, 8, 0, 8)
                    };
                    _outputCardsContainer.Children.Add(separator);
                }
            }

            // ── Output Preview records ──
            foreach (var record in _outputRecords)
            {
                var card = BuildOutputRecordCard(record);
                _outputCardsContainer.Children.Add(card);
            }

            // Auto-scroll to bottom
            OutputScrollViewer?.ScrollToEnd();
        }

        private FrameworkElement BuildOutputRecordCard(OutputRecord record)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(ColGlass),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(ColGlassBorder),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 6)
            };

            bool isInteractive = record.IsClickable || record.IsExpandable;
            if (isInteractive)
            {
                card.Cursor = Cursors.Hand;
                card.MouseEnter += (s, e) =>
                {
                    card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, ColPrimary.R, ColPrimary.G, ColPrimary.B));
                    card.Background = new SolidColorBrush(Color.FromArgb(0x18, ColPrimary.R, ColPrimary.G, ColPrimary.B));
                };
                card.MouseLeave += (s, e) =>
                {
                    card.BorderBrush = new SolidColorBrush(ColGlassBorder);
                    card.Background = new SolidColorBrush(ColGlass);
                };
            }

            if (record.IsClickable && !record.IsExpandable)
                card.MouseLeftButtonDown += (s, e) => OnOutputRecordClicked(record);

            // Dim fully-reverted or deleted cards
            bool isFullyReverted =
                (record.RecordType == OutputRecordType.ParameterSet && record.IsFullyReverted) ||
                (record.RecordType == OutputRecordType.ScheduleModified && record.IsScheduleFullyReverted) ||
                record.IsRecordReverted ||
                record.IsDeleted;
            if (isFullyReverted) card.Opacity = 0.5;

            var content = new StackPanel();

            // ── Row 1: Icon + Title + Time + (Undo) ──
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

            // Icon circle
            var iconCircle = new Border
            {
                Width = 18, Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(0x28, record.IconColor.R, record.IconColor.G, record.IconColor.B)),
                VerticalAlignment = VerticalAlignment.Center
            };
            iconCircle.Child = new TextBlock
            {
                Text = record.IconGlyph ?? "?",
                FontSize = 8, FontFamily = MainFont,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(record.IconColor)
            };
            Grid.SetColumn(iconCircle, 0);
            headerGrid.Children.Add(iconCircle);

            // Title
            var titleText = new TextBlock
            {
                Text = record.Title ?? "",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColText),
                FontFamily = MainFont,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(titleText, 1);
            headerGrid.Children.Add(titleText);

            // Timestamp
            var timeText = new TextBlock
            {
                Text = record.Timestamp.ToString("HH:mm"),
                FontSize = 10,
                Foreground = new SolidColorBrush(ColMuted),
                FontFamily = MainFont,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(timeText, 2);
            headerGrid.Children.Add(timeText);

            // Card-level action button (hide when fully reverted/deleted)
            if (!isFullyReverted)
            {
                if (record.RecordType == OutputRecordType.ParameterSet)
                {
                    // Parameter undo: revert all changes
                    var undoBtn = new TextBlock
                    {
                        Text = "\u21A9",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(ColWarning),
                        FontFamily = MainFont,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Cursor = Cursors.Hand,
                        ToolTip = "Undo all changes"
                    };
                    undoBtn.MouseEnter += (s, e) => undoBtn.Foreground = new SolidColorBrush(ColText);
                    undoBtn.MouseLeave += (s, e) => undoBtn.Foreground = new SolidColorBrush(ColWarning);
                    undoBtn.MouseLeftButtonDown += (s, e) =>
                    {
                        ShowUndoAllConfirmation(content, record);
                        e.Handled = true;
                    };
                    Grid.SetColumn(undoBtn, 3);
                    headerGrid.Children.Add(undoBtn);
                }
                else if (record.RecordType == OutputRecordType.ScheduleModified &&
                         record.ScheduleChangeEntries != null &&
                         record.ScheduleChangeEntries.Any(e => e.CanUndo && !e.IsReverted))
                {
                    // Schedule undo: revert all undoable changes
                    var undoBtn = new TextBlock
                    {
                        Text = "\u21A9",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(ColWarning),
                        FontFamily = MainFont,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Cursor = Cursors.Hand,
                        ToolTip = "Undo all schedule changes"
                    };
                    undoBtn.MouseEnter += (s, e) => undoBtn.Foreground = new SolidColorBrush(ColText);
                    undoBtn.MouseLeave += (s, e) => undoBtn.Foreground = new SolidColorBrush(ColWarning);
                    undoBtn.MouseLeftButtonDown += (s, e) =>
                    {
                        ShowUndoAllScheduleConfirmation(content, record);
                        e.Handled = true;
                    };
                    Grid.SetColumn(undoBtn, 3);
                    headerGrid.Children.Add(undoBtn);
                }
                else if (record.RecordType == OutputRecordType.ViewModified && record.CanUndoRecord && !record.IsRecordReverted)
                {
                    // View modification undo: reset category visibility
                    var undoBtn = new TextBlock
                    {
                        Text = "\u21A9",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(ColWarning),
                        FontFamily = MainFont,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Cursor = Cursors.Hand,
                        ToolTip = "Undo visibility change"
                    };
                    undoBtn.MouseEnter += (s, e) => undoBtn.Foreground = new SolidColorBrush(ColText);
                    undoBtn.MouseLeave += (s, e) => undoBtn.Foreground = new SolidColorBrush(ColWarning);
                    undoBtn.MouseLeftButtonDown += (s, e) =>
                    {
                        ShowUndoViewModifiedConfirmation(content, record);
                        e.Handled = true;
                    };
                    Grid.SetColumn(undoBtn, 3);
                    headerGrid.Children.Add(undoBtn);
                }
                else if ((record.RecordType == OutputRecordType.ScheduleCreated && record.ScheduleId.HasValue) ||
                         (record.RecordType == OutputRecordType.ViewCreated && record.ViewId.HasValue))
                {
                    // Delete: remove the created schedule or view
                    string deleteLabel = record.RecordType == OutputRecordType.ScheduleCreated
                        ? "Delete this schedule" : "Delete this view";
                    var deleteBtn = new TextBlock
                    {
                        Text = "\U0001F5D1",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(ColError),
                        FontFamily = MainFont,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Cursor = Cursors.Hand,
                        ToolTip = deleteLabel
                    };
                    deleteBtn.MouseEnter += (s, e) => deleteBtn.Foreground = new SolidColorBrush(ColText);
                    deleteBtn.MouseLeave += (s, e) => deleteBtn.Foreground = new SolidColorBrush(ColError);
                    deleteBtn.MouseLeftButtonDown += (s, e) =>
                    {
                        ShowDeleteScheduleConfirmation(content, record);
                        e.Handled = true;
                    };
                    Grid.SetColumn(deleteBtn, 3);
                    headerGrid.Children.Add(deleteBtn);
                }
            }

            content.Children.Add(headerGrid);

            // ── Row 2: Subtitle ──
            if (!string.IsNullOrEmpty(record.Subtitle))
            {
                var subtitleText = new TextBlock
                {
                    Text = record.Subtitle,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColTextSec),
                    FontFamily = MainFont,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(22, 3, 0, 0),
                    MaxHeight = 36
                };
                content.Children.Add(subtitleText);
            }

            // ── Row 3: Expandable detail list ──
            if (record.IsExpandable)
            {
                bool isExpanded = _expandedRecordIds.Contains(record.Id);
                int detailCount = record.ChangeEntries?.Count ?? record.ScheduleChangeEntries?.Count ?? 0;

                // Toggle hint
                var toggleText = new TextBlock
                {
                    Text = isExpanded
                        ? $"\u25BC  Hide details ({detailCount})"
                        : $"\u25B6  Show details ({detailCount})",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(ColPrimaryLt),
                    FontFamily = MainFont,
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(22, 5, 0, 0)
                };
                toggleText.MouseLeftButtonDown += (s, e) =>
                {
                    if (_expandedRecordIds.Contains(record.Id))
                        _expandedRecordIds.Remove(record.Id);
                    else
                        _expandedRecordIds.Add(record.Id);
                    RebuildOutputCards();
                    e.Handled = true;
                };
                content.Children.Add(toggleText);

                // Expanded entries table
                if (isExpanded)
                {
                    var detailBorder = new Border
                    {
                        Background = new SolidColorBrush(ColCodeBg),
                        BorderBrush = new SolidColorBrush(ColBorder),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(8, 6, 8, 6),
                        Margin = new Thickness(22, 4, 0, 0),
                        MaxHeight = 240
                    };

                    var detailScroll = new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                    };

                    var detailStack = new StackPanel();

                    // ── Branch A: ParameterSet batch (Element | Old → New + Undo) ──
                    if (record.ChangeEntries != null && record.ChangeEntries.Count > 0)
                    {
                        BuildParameterChangeDetailTable(detailStack, record);
                    }
                    // ── Branch B: Schedule modifications (Action | Field | Detail + Undo) ──
                    else if (record.ScheduleChangeEntries != null && record.ScheduleChangeEntries.Count > 0)
                    {
                        BuildScheduleChangeDetailTable(detailStack, record);
                    }

                    detailScroll.Content = detailStack;
                    detailBorder.Child = detailScroll;
                    content.Children.Add(detailBorder);
                }
            }

            // ── Row 4: Clickable hint (non-expandable only) ──
            if (record.IsClickable && !record.IsExpandable)
            {
                string hint = record.ViewId.HasValue ? "Click to open in Revit"
                    : record.FilePath != null ? "Click to open file"
                    : record.FilePaths != null ? "Click to open folder"
                    : "";

                if (!string.IsNullOrEmpty(hint))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = hint,
                        FontSize = 9.5,
                        Foreground = new SolidColorBrush(ColPrimaryLt),
                        FontFamily = MainFont,
                        FontStyle = FontStyles.Italic,
                        Margin = new Thickness(22, 3, 0, 0)
                    });
                }
            }

            card.Child = content;
            return card;
        }

        /// <summary>
        /// Builds the expanded detail table for ParameterSet batch changes.
        /// Columns: Element | Old | → | New | Undo. Each row is clickable to highlight the element.
        /// Reverted rows show strikethrough + "Reverted" label.
        /// </summary>
        private void BuildParameterChangeDetailTable(StackPanel detailStack, OutputRecord record)
        {
            var entries = record.ChangeEntries;

            // Column header (5 columns: name, old, arrow, new, undo)
            var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

            var hdrName = new TextBlock { Text = "Element", FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColMuted), FontFamily = MonoFont };
            var hdrOld = new TextBlock { Text = "Old", FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColMuted), FontFamily = MonoFont, TextAlignment = TextAlignment.Right };
            var hdrArrow = new TextBlock { Text = "\u2192", FontSize = 9.5, Foreground = new SolidColorBrush(ColMuted),
                FontFamily = MonoFont, TextAlignment = TextAlignment.Center };
            var hdrNew = new TextBlock { Text = "New", FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColMuted), FontFamily = MonoFont };

            Grid.SetColumn(hdrName, 0); Grid.SetColumn(hdrOld, 1);
            Grid.SetColumn(hdrArrow, 2); Grid.SetColumn(hdrNew, 3);
            headerRow.Children.Add(hdrName); headerRow.Children.Add(hdrOld);
            headerRow.Children.Add(hdrArrow); headerRow.Children.Add(hdrNew);
            detailStack.Children.Add(headerRow);

            detailStack.Children.Add(new Border
            {
                Height = 1, Background = new SolidColorBrush(ColBorder),
                Margin = new Thickness(0, 2, 0, 2)
            });

            // Each entry row — clickable to highlight element in Revit
            foreach (var entry in entries)
            {
                var rowWrapper = new StackPanel(); // wraps row + potential inline confirm

                var rowBorder = new Border
                {
                    Margin = new Thickness(0, 1, 0, 1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(2, 1, 2, 1),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Opacity = entry.IsReverted ? 0.5 : 1.0
                };

                if (!entry.IsReverted)
                {
                    rowBorder.ToolTip = $"Click to select element #{entry.ElementId} in Revit";
                    rowBorder.MouseEnter += (s, e) =>
                    {
                        rowBorder.Background = new SolidColorBrush(Color.FromArgb(0x18, ColPrimary.R, ColPrimary.G, ColPrimary.B));
                    };
                    rowBorder.MouseLeave += (s, e) =>
                    {
                        rowBorder.Background = Brushes.Transparent;
                    };

                    var capturedId = entry.ElementId;
                    rowBorder.MouseLeftButtonDown += (s, e) =>
                    {
                        OnElementEntryClicked(capturedId);
                        e.Handled = true;
                    };
                }

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

                var nameLabel = entry.ElementName ?? $"#{entry.ElementId}";
                if (nameLabel.Length > 18) nameLabel = nameLabel.Substring(0, 18) + "..";

                if (entry.IsReverted)
                {
                    // ── Reverted row: strikethrough name + "Reverted" label ──
                    var colName = new TextBlock { Text = nameLabel, FontSize = 9.5,
                        Foreground = new SolidColorBrush(ColMuted), FontFamily = MonoFont,
                        TextDecorations = TextDecorations.Strikethrough,
                        TextTrimming = TextTrimming.CharacterEllipsis };
                    var colReverted = new TextBlock { Text = "Reverted", FontSize = 9.5,
                        Foreground = new SolidColorBrush(ColSuccess), FontFamily = MonoFont,
                        FontStyle = FontStyles.Italic };
                    Grid.SetColumn(colName, 0);
                    Grid.SetColumn(colReverted, 1); Grid.SetColumnSpan(colReverted, 3);
                    row.Children.Add(colName);
                    row.Children.Add(colReverted);
                }
                else
                {
                    // ── Normal row: Element | Old | → | New | ↩ ──
                    var colName = new TextBlock { Text = nameLabel, FontSize = 9.5,
                        Foreground = new SolidColorBrush(ColTextSec), FontFamily = MonoFont,
                        TextTrimming = TextTrimming.CharacterEllipsis };
                    var colOld = new TextBlock { Text = entry.OldValue ?? "", FontSize = 9.5,
                        Foreground = new SolidColorBrush(ColMuted), FontFamily = MonoFont,
                        TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
                    var colArrow = new TextBlock { Text = "\u2192", FontSize = 9.5,
                        Foreground = new SolidColorBrush(ColMuted), FontFamily = MonoFont,
                        TextAlignment = TextAlignment.Center };
                    var colNew = new TextBlock { Text = entry.NewValue ?? "", FontSize = 9.5,
                        Foreground = new SolidColorBrush(ColSuccess), FontFamily = MonoFont,
                        TextTrimming = TextTrimming.CharacterEllipsis };

                    // Per-row undo button
                    var rowUndoBtn = new TextBlock
                    {
                        Text = "\u21A9", FontSize = 10,
                        Foreground = new SolidColorBrush(ColWarning), FontFamily = MainFont,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor = Cursors.Hand,
                        ToolTip = "Undo this change"
                    };
                    rowUndoBtn.MouseEnter += (s, e) => rowUndoBtn.Foreground = new SolidColorBrush(ColText);
                    rowUndoBtn.MouseLeave += (s, e) => rowUndoBtn.Foreground = new SolidColorBrush(ColWarning);

                    var capturedEntry = entry;
                    var capturedWrapper = rowWrapper;
                    rowUndoBtn.MouseLeftButtonDown += (s, e) =>
                    {
                        ShowUndoSingleConfirmation(capturedWrapper, record, capturedEntry);
                        e.Handled = true;
                    };

                    Grid.SetColumn(colName, 0); Grid.SetColumn(colOld, 1);
                    Grid.SetColumn(colArrow, 2); Grid.SetColumn(colNew, 3);
                    Grid.SetColumn(rowUndoBtn, 4);
                    row.Children.Add(colName); row.Children.Add(colOld);
                    row.Children.Add(colArrow); row.Children.Add(colNew);
                    row.Children.Add(rowUndoBtn);
                }

                rowBorder.Child = row;
                rowWrapper.Children.Add(rowBorder);
                detailStack.Children.Add(rowWrapper);
            }
        }

        /// <summary>
        /// Builds the expanded detail table for schedule modification aggregation.
        /// Columns: Action | Field | Detail | Undo. Color-coded by change type.
        /// Reverted rows show strikethrough + "Reverted" label.
        /// </summary>
        private void BuildScheduleChangeDetailTable(StackPanel detailStack, OutputRecord record)
        {
            var entries = record.ScheduleChangeEntries;
            bool hasUndoable = entries.Any(e => e.CanUndo);

            // Column header (4 columns: action, field, detail, undo)
            var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (hasUndoable)
                headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

            var hdrAction = new TextBlock { Text = "Action", FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColMuted), FontFamily = MonoFont };
            var hdrField = new TextBlock { Text = "Field", FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColMuted), FontFamily = MonoFont };
            var hdrDetail = new TextBlock { Text = "Detail", FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColMuted), FontFamily = MonoFont };

            Grid.SetColumn(hdrAction, 0); Grid.SetColumn(hdrField, 1); Grid.SetColumn(hdrDetail, 2);
            headerRow.Children.Add(hdrAction); headerRow.Children.Add(hdrField); headerRow.Children.Add(hdrDetail);
            detailStack.Children.Add(headerRow);

            detailStack.Children.Add(new Border
            {
                Height = 1, Background = new SolidColorBrush(ColBorder),
                Margin = new Thickness(0, 2, 0, 2)
            });

            foreach (var entry in entries)
            {
                var rowWrapper = new StackPanel(); // wraps row + potential inline confirm

                // Color-code by change type
                Color typeColor;
                switch (entry.ChangeType)
                {
                    case "Field":  typeColor = ColSuccess; break;  // green
                    case "Format": typeColor = ColWarning; break;  // amber
                    case "Filter": typeColor = ColPrimary; break;  // indigo
                    case "Sort":   typeColor = ColAccent;  break;  // purple
                    default:       typeColor = ColTextSec; break;
                }

                string actionPrefix = entry.Action == "added" ? "+" :
                                      entry.Action == "removed" ? "\u2212" :
                                      entry.Action == "cleared" ? "\u00D7" : "~";

                var rowBorder = new Border
                {
                    Margin = new Thickness(0, 1, 0, 1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(2, 1, 2, 1),
                    Background = Brushes.Transparent,
                    Opacity = entry.IsReverted ? 0.5 : 1.0
                };

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (hasUndoable)
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

                if (entry.IsReverted)
                {
                    // ── Reverted row: strikethrough action + "Reverted" label ──
                    var colAction = new TextBlock
                    {
                        Text = $"{actionPrefix} {entry.ChangeType}",
                        FontSize = 9.5, FontFamily = MonoFont,
                        Foreground = new SolidColorBrush(ColMuted),
                        TextDecorations = TextDecorations.Strikethrough,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    var colField = new TextBlock
                    {
                        Text = entry.FieldName ?? "",
                        FontSize = 9.5, FontFamily = MonoFont,
                        Foreground = new SolidColorBrush(ColMuted),
                        TextDecorations = TextDecorations.Strikethrough,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    var colReverted = new TextBlock
                    {
                        Text = "Reverted", FontSize = 9.5,
                        Foreground = new SolidColorBrush(ColSuccess), FontFamily = MonoFont,
                        FontStyle = FontStyles.Italic
                    };
                    Grid.SetColumn(colAction, 0); Grid.SetColumn(colField, 1); Grid.SetColumn(colReverted, 2);
                    row.Children.Add(colAction); row.Children.Add(colField); row.Children.Add(colReverted);
                }
                else
                {
                    // ── Normal row: Action | Field | Detail | (↩) ──
                    var colAction = new TextBlock
                    {
                        Text = $"{actionPrefix} {entry.ChangeType}",
                        FontSize = 9.5, FontFamily = MonoFont,
                        Foreground = new SolidColorBrush(typeColor),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    var colField = new TextBlock
                    {
                        Text = entry.FieldName ?? "",
                        FontSize = 9.5, FontFamily = MonoFont,
                        Foreground = new SolidColorBrush(ColTextSec),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    var colDetail = new TextBlock
                    {
                        Text = entry.Detail ?? "",
                        FontSize = 9.5, FontFamily = MonoFont,
                        Foreground = new SolidColorBrush(ColMuted),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };

                    Grid.SetColumn(colAction, 0); Grid.SetColumn(colField, 1); Grid.SetColumn(colDetail, 2);
                    row.Children.Add(colAction); row.Children.Add(colField); row.Children.Add(colDetail);

                    // Per-row undo button (only if this entry is undoable)
                    if (entry.CanUndo && hasUndoable)
                    {
                        var rowUndoBtn = new TextBlock
                        {
                            Text = "\u21A9", FontSize = 10,
                            Foreground = new SolidColorBrush(ColWarning), FontFamily = MainFont,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Cursor = Cursors.Hand,
                            ToolTip = "Undo this change"
                        };
                        rowUndoBtn.MouseEnter += (s, e) => rowUndoBtn.Foreground = new SolidColorBrush(ColText);
                        rowUndoBtn.MouseLeave += (s, e) => rowUndoBtn.Foreground = new SolidColorBrush(ColWarning);

                        var capturedEntry = entry;
                        var capturedWrapper = rowWrapper;
                        rowUndoBtn.MouseLeftButtonDown += (s, e) =>
                        {
                            ShowUndoSingleScheduleConfirmation(capturedWrapper, record, capturedEntry);
                            e.Handled = true;
                        };

                        Grid.SetColumn(rowUndoBtn, 3);
                        row.Children.Add(rowUndoBtn);
                    }
                }

                rowBorder.Child = row;
                rowWrapper.Children.Add(rowBorder);
                detailStack.Children.Add(rowWrapper);
            }
        }

        private async void OnOutputRecordClicked(OutputRecord record)
        {
            try
            {
                if (record.ViewId.HasValue)
                {
                    // Navigate to view in Revit — direct API, no tool registry dependency
                    await App.RevitEventHandler.NavigateToViewAsync(record.ViewId.Value);
                }
                else if (record.FilePath != null && System.IO.File.Exists(record.FilePath))
                {
                    SysProcess.Start(new ProcessStartInfo(record.FilePath) { UseShellExecute = true });
                }
                else if (record.FilePaths != null && record.FilePaths.Count > 0)
                {
                    // Open the folder containing the files
                    string folder = record.FolderPath;
                    if (string.IsNullOrEmpty(folder) && record.FilePaths.Count > 0)
                        folder = System.IO.Path.GetDirectoryName(record.FilePaths[0]);

                    if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
                        SysProcess.Start("explorer.exe", folder);
                }
                else if (!string.IsNullOrEmpty(record.FolderPath) && System.IO.Directory.Exists(record.FolderPath))
                {
                    SysProcess.Start("explorer.exe", record.FolderPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Output record click error: {ex.Message}");
            }
        }

        /// <summary>
        /// Selects and highlights an element in Revit when user clicks on an entry row
        /// in the expanded parameter change detail table.
        /// Uses SelectElements tool which will select the element (blue highlight) and zoom to fit.
        /// </summary>
        private async void OnElementEntryClicked(long elementId)
        {
            try
            {
                if (elementId <= 0) return;

                SetStatus($"Selecting element #{elementId}...", true);

                var args = new Dictionary<string, object>
                {
                    ["element_ids"] = new List<long> { elementId },
                    ["zoom_to_fit"] = true,
                    ["clear_previous"] = true
                };

                await App.RevitEventHandler.ExecuteToolAsync("SelectElements", args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Element highlight click error: {ex.Message}");
            }
            finally
            {
                SetStatus("", false);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Undo Parameter Changes — inline confirmation + execution
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Shows an inline confirmation bar at the bottom of the card for "Undo All" action.
        /// </summary>
        private void ShowUndoAllConfirmation(StackPanel cardContent, OutputRecord record)
        {
            // Prevent duplicate confirms
            const string confirmTag = "UndoAllConfirm";
            foreach (UIElement child in cardContent.Children)
            {
                if (child is FrameworkElement fe && (string)fe.Tag == confirmTag) return;
            }

            int remaining = record.ChangeEntries.Count(e => !e.IsReverted);
            var confirmBar = BuildInlineConfirmBar(
                $"Revert all {remaining} changes?",
                confirmTag,
                () => OnUndoAllParameterChanges(record),
                () => { /* No-op, bar removes itself */ }
            );
            cardContent.Children.Add(confirmBar);
        }

        /// <summary>
        /// Shows an inline confirmation bar below a single entry row.
        /// </summary>
        private void ShowUndoSingleConfirmation(StackPanel rowWrapper, OutputRecord record, ParameterChangeEntry entry)
        {
            // Prevent duplicate confirms
            string confirmTag = $"UndoSingle_{entry.ElementId}";
            foreach (UIElement child in rowWrapper.Children)
            {
                if (child is FrameworkElement fe && (string)fe.Tag == confirmTag) return;
            }

            string elemName = entry.ElementName ?? $"#{entry.ElementId}";
            var confirmBar = BuildInlineConfirmBar(
                $"Revert {elemName} to {entry.OldValue}?",
                confirmTag,
                () => OnUndoSingleParameterChange(record, entry),
                () => { /* No-op, bar removes itself */ }
            );
            rowWrapper.Children.Add(confirmBar);
        }

        /// <summary>
        /// Builds a styled inline confirmation bar with Yes/No buttons.
        /// </summary>
        private Border BuildInlineConfirmBar(string message, string tag, Action onYes, Action onNo)
        {
            var bar = new Border
            {
                Tag = tag,
                Background = new SolidColorBrush(Color.FromArgb(0x26, ColWarning.R, ColWarning.G, ColWarning.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, ColWarning.R, ColWarning.G, ColWarning.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 3, 0, 1)
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal };

            stack.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(ColText),
                FontFamily = MainFont,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            // Yes button
            var yesBtn = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, ColWarning.R, ColWarning.G, ColWarning.B)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand
            };
            var yesTxt = new TextBlock { Text = "Yes", FontSize = 10, Foreground = new SolidColorBrush(ColWarning),
                FontFamily = MainFont, FontWeight = FontWeights.SemiBold };
            yesBtn.Child = yesTxt;
            yesBtn.MouseEnter += (s, e) => yesBtn.Background = new SolidColorBrush(Color.FromArgb(0x55, ColWarning.R, ColWarning.G, ColWarning.B));
            yesBtn.MouseLeave += (s, e) => yesBtn.Background = new SolidColorBrush(Color.FromArgb(0x33, ColWarning.R, ColWarning.G, ColWarning.B));
            yesBtn.MouseLeftButtonDown += (s, e) =>
            {
                // Remove the confirm bar
                if (bar.Parent is Panel parent) parent.Children.Remove(bar);
                onYes?.Invoke();
                e.Handled = true;
            };
            stack.Children.Add(yesBtn);

            // No button
            var noBtn = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 2, 8, 2),
                Cursor = Cursors.Hand
            };
            var noTxt = new TextBlock { Text = "No", FontSize = 10, Foreground = new SolidColorBrush(ColMuted),
                FontFamily = MainFont };
            noBtn.Child = noTxt;
            noBtn.MouseEnter += (s, e) => noBtn.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            noBtn.MouseLeave += (s, e) => noBtn.Background = Brushes.Transparent;
            noBtn.MouseLeftButtonDown += (s, e) =>
            {
                // Just remove the confirm bar
                if (bar.Parent is Panel parent) parent.Children.Remove(bar);
                onNo?.Invoke();
                e.Handled = true;
            };
            stack.Children.Add(noBtn);

            bar.Child = stack;
            return bar;
        }

        /// <summary>
        /// Reverts a single parameter change by writing back the old value via SetElementParameter tool.
        /// </summary>
        private async void OnUndoSingleParameterChange(OutputRecord record, ParameterChangeEntry entry)
        {
            try
            {
                SetStatus($"Reverting {entry.ElementName ?? $"#{entry.ElementId}"}...", true);

                var args = new Dictionary<string, object>
                {
                    ["element_id"] = entry.ElementId,
                    ["parameter_name"] = record.ParameterName,
                    ["value"] = entry.OldValue
                };

                var result = await App.RevitEventHandler.ExecuteToolAsync("SetElementParameter", args);

                // Check if the tool returned success
                var toolResult = result as Models.ToolResult;
                if (toolResult != null && toolResult.Success)
                {
                    entry.IsReverted = true;
                    UpdateRecordSubtitleAfterRevert(record);
                    RebuildOutputCards();
                }
                else
                {
                    string errMsg = toolResult?.Message ?? "Unknown error";
                    System.Diagnostics.Debug.WriteLine($"[Zexus] Undo failed: {errMsg}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Undo single error: {ex.Message}");
            }
            finally
            {
                SetStatus("", false);
            }
        }

        /// <summary>
        /// Reverts all non-reverted parameter changes in a record.
        /// </summary>
        private async void OnUndoAllParameterChanges(OutputRecord record)
        {
            try
            {
                var pending = record.ChangeEntries.Where(e => !e.IsReverted).ToList();
                if (pending.Count == 0) return;

                SetStatus($"Reverting {pending.Count} changes...", true);

                int successCount = 0;
                foreach (var entry in pending)
                {
                    var args = new Dictionary<string, object>
                    {
                        ["element_id"] = entry.ElementId,
                        ["parameter_name"] = record.ParameterName,
                        ["value"] = entry.OldValue
                    };

                    var result = await App.RevitEventHandler.ExecuteToolAsync("SetElementParameter", args);
                    var toolResult = result as Models.ToolResult;
                    if (toolResult != null && toolResult.Success)
                    {
                        entry.IsReverted = true;
                        successCount++;
                    }
                    else
                    {
                        string errMsg = toolResult?.Message ?? "Unknown error";
                        System.Diagnostics.Debug.WriteLine($"[Zexus] Undo failed for #{entry.ElementId}: {errMsg}");
                    }
                }

                UpdateRecordSubtitleAfterRevert(record);
                RebuildOutputCards();

                SetStatus($"Reverted {successCount}/{pending.Count} changes", false);
                await System.Threading.Tasks.Task.Delay(2000);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Undo all error: {ex.Message}");
            }
            finally
            {
                SetStatus("", false);
            }
        }

        /// <summary>
        /// Updates the subtitle of a ParameterSet record after one or more entries have been reverted.
        /// </summary>
        private void UpdateRecordSubtitleAfterRevert(OutputRecord record)
        {
            if (record.ChangeEntries == null) return;

            int total = record.ChangeEntries.Count;
            int reverted = record.RevertedCount;

            if (record.IsFullyReverted)
            {
                record.Subtitle = $"All {total} changes reverted";
            }
            else if (reverted > 0)
            {
                record.Subtitle = $"{total} elements modified ({reverted} reverted)";
            }
            else
            {
                record.Subtitle = $"{total} elements modified";
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Undo/Delete Schedule Changes — inline confirmation + execution
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Shows an inline confirmation bar for "Undo All" on a ScheduleModified card.
        /// </summary>
        private void ShowUndoAllScheduleConfirmation(StackPanel cardContent, OutputRecord record)
        {
            const string confirmTag = "UndoAllScheduleConfirm";
            foreach (UIElement child in cardContent.Children)
            {
                if (child is FrameworkElement fe && (string)fe.Tag == confirmTag) return;
            }

            int remaining = record.ScheduleChangeEntries.Count(e => e.CanUndo && !e.IsReverted);
            var confirmBar = BuildInlineConfirmBar(
                $"Revert all {remaining} schedule changes?",
                confirmTag,
                () => OnUndoAllScheduleChanges(record),
                () => { }
            );
            cardContent.Children.Add(confirmBar);
        }

        /// <summary>
        /// Shows an inline confirmation bar below a single schedule change entry row.
        /// </summary>
        private void ShowUndoSingleScheduleConfirmation(StackPanel rowWrapper, OutputRecord record, ScheduleChangeEntry entry)
        {
            string confirmTag = $"UndoSchedule_{entry.ChangeType}_{entry.FieldName}";
            foreach (UIElement child in rowWrapper.Children)
            {
                if (child is FrameworkElement fe && (string)fe.Tag == confirmTag) return;
            }

            var confirmBar = BuildInlineConfirmBar(
                $"Revert {entry.ChangeType.ToLower()} change on {entry.FieldName}?",
                confirmTag,
                () => OnUndoSingleScheduleChange(record, entry),
                () => { }
            );
            rowWrapper.Children.Add(confirmBar);
        }

        /// <summary>
        /// Shows an inline confirmation bar for deleting a ScheduleCreated or ViewCreated record.
        /// </summary>
        private void ShowDeleteScheduleConfirmation(StackPanel cardContent, OutputRecord record)
        {
            const string confirmTag = "DeleteScheduleConfirm";
            foreach (UIElement child in cardContent.Children)
            {
                if (child is FrameworkElement fe && (string)fe.Tag == confirmTag) return;
            }

            string entityType = record.RecordType == OutputRecordType.ViewCreated ? "view" : "schedule";
            var confirmBar = BuildInlineConfirmBar(
                $"Delete {entityType} \"{record.Title}\"?",
                confirmTag,
                () => OnDeleteSchedule(record),
                () => { }
            );
            cardContent.Children.Add(confirmBar);
        }

        /// <summary>
        /// Shows an inline confirmation bar for undoing a ViewModified record (category visibility reset).
        /// </summary>
        private void ShowUndoViewModifiedConfirmation(StackPanel cardContent, OutputRecord record)
        {
            const string confirmTag = "UndoViewModifiedConfirm";
            foreach (UIElement child in cardContent.Children)
            {
                if (child is FrameworkElement fe && (string)fe.Tag == confirmTag) return;
            }

            var confirmBar = BuildInlineConfirmBar(
                $"Reset visibility on \"{record.Title}\"?",
                confirmTag,
                () => OnUndoViewModified(record),
                () => { }
            );
            cardContent.Children.Add(confirmBar);
        }

        /// <summary>
        /// Undoes a ViewModified record by calling SetCategoryVisibility with mode="reset".
        /// </summary>
        private async void OnUndoViewModified(OutputRecord record)
        {
            try
            {
                if (record.UndoToolName == null || record.UndoData == null)
                {
                    System.Diagnostics.Debug.WriteLine("[Zexus] ViewModified undo: missing tool name or data");
                    return;
                }

                SetStatus($"Resetting visibility on \"{record.Title}\"...", true);

                var result = await App.RevitEventHandler.ExecuteToolAsync(record.UndoToolName, record.UndoData);
                var toolResult = result as Models.ToolResult;

                if (toolResult != null && toolResult.Success)
                {
                    record.IsRecordReverted = true;
                    record.Subtitle = "Reverted \u2022 " + record.Subtitle;
                    RebuildOutputCards();
                }
                else
                {
                    string errMsg = toolResult?.Message ?? "Unknown error";
                    System.Diagnostics.Debug.WriteLine($"[Zexus] ViewModified undo failed: {errMsg}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] ViewModified undo error: {ex.Message}");
            }
            finally
            {
                SetStatus("", false);
            }
        }

        /// <summary>
        /// Reverts a single schedule change by calling the undo tool with reversed parameters.
        /// </summary>
        private async void OnUndoSingleScheduleChange(OutputRecord record, ScheduleChangeEntry entry)
        {
            try
            {
                if (entry.UndoToolName == null || entry.UndoData == null)
                {
                    System.Diagnostics.Debug.WriteLine("[Zexus] Schedule undo: missing tool name or data");
                    return;
                }

                SetStatus($"Reverting {entry.ChangeType.ToLower()} change on {entry.FieldName}...", true);

                var result = await App.RevitEventHandler.ExecuteToolAsync(entry.UndoToolName, entry.UndoData);
                var toolResult = result as Models.ToolResult;

                if (toolResult != null && toolResult.Success)
                {
                    entry.IsReverted = true;
                    UpdateScheduleRecordSubtitle(record);
                    RebuildOutputCards();
                }
                else
                {
                    string errMsg = toolResult?.Message ?? "Unknown error";
                    System.Diagnostics.Debug.WriteLine($"[Zexus] Schedule undo failed: {errMsg}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Schedule undo single error: {ex.Message}");
            }
            finally
            {
                SetStatus("", false);
            }
        }

        /// <summary>
        /// Reverts all undoable, non-reverted schedule changes in a record.
        /// </summary>
        private async void OnUndoAllScheduleChanges(OutputRecord record)
        {
            try
            {
                var pending = record.ScheduleChangeEntries
                    .Where(e => e.CanUndo && !e.IsReverted && e.UndoToolName != null && e.UndoData != null)
                    .ToList();
                if (pending.Count == 0) return;

                SetStatus($"Reverting {pending.Count} schedule changes...", true);

                int successCount = 0;
                foreach (var entry in pending)
                {
                    var result = await App.RevitEventHandler.ExecuteToolAsync(entry.UndoToolName, entry.UndoData);
                    var toolResult = result as Models.ToolResult;

                    if (toolResult != null && toolResult.Success)
                    {
                        entry.IsReverted = true;
                        successCount++;
                    }
                    else
                    {
                        string errMsg = toolResult?.Message ?? "Unknown error";
                        System.Diagnostics.Debug.WriteLine($"[Zexus] Schedule undo failed for {entry.FieldName}: {errMsg}");
                    }
                }

                UpdateScheduleRecordSubtitle(record);
                RebuildOutputCards();

                SetStatus($"Reverted {successCount}/{pending.Count} schedule changes", false);
                await System.Threading.Tasks.Task.Delay(2000);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Schedule undo all error: {ex.Message}");
            }
            finally
            {
                SetStatus("", false);
            }
        }

        /// <summary>
        /// Deletes a schedule or view element from the Revit document via ExecuteCode.
        /// Works for both ScheduleCreated (uses ScheduleId) and ViewCreated (uses ViewId) records.
        /// </summary>
        private async void OnDeleteSchedule(OutputRecord record)
        {
            try
            {
                // Resolve the element ID — try ScheduleId first, then ViewId
                long? elementId = record.ScheduleId ?? record.ViewId;
                if (!elementId.HasValue) return;

                string entityType = record.RecordType == OutputRecordType.ViewCreated ? "view" : "schedule";
                SetStatus($"Deleting {entityType} \"{record.Title}\"...", true);

                long eid = elementId.Value;

                // ── Step 1: If target is the active view, switch away first ──
                // IMPORTANT: Must be a separate ExecuteToolAsync call because Revit
                // does not finalize ActiveView changes within the same External Event.
                string switchCode = $@"
ElementId eid;
var ctorLong = typeof(ElementId).GetConstructor(new[] {{ typeof(long) }});
if (ctorLong != null)
    eid = (ElementId)ctorLong.Invoke(new object[] {{ (long){eid} }});
else
    eid = new ElementId((int){eid});

if (doc.ActiveView != null && doc.ActiveView.Id == eid)
{{
    var candidates = new FilteredElementCollector(doc)
        .OfClass(typeof(View))
        .Cast<View>()
        .Where(v => !v.IsTemplate && v.Id != eid
                  && v.ViewType != ViewType.Internal
                  && v.ViewType != ViewType.Undefined
                  && v.ViewType != ViewType.DrawingSheet
                  && v.ViewType != ViewType.ProjectBrowser
                  && v.ViewType != ViewType.SystemBrowser)
        .ToList();

    var altView = candidates.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan)
               ?? candidates.FirstOrDefault(v => v.ViewType == ViewType.ThreeD)
               ?? candidates.FirstOrDefault(v => v.ViewType == ViewType.CeilingPlan)
               ?? candidates.FirstOrDefault(v => v.ViewType == ViewType.Section)
               ?? candidates.FirstOrDefault(v => v.ViewType == ViewType.Elevation)
               ?? candidates.FirstOrDefault();

    if (altView == null)
        throw new InvalidOperationException(""Cannot delete — no alternative view available"");

    uiDoc.ActiveView = altView;
    return ""switched"";
}}
return ""not_active"";";

                var switchArgs = new Dictionary<string, object> { ["code"] = switchCode };
                var switchResult = await App.RevitEventHandler.ExecuteToolAsync("ExecuteCode", switchArgs);
                var switchToolResult = switchResult as Models.ToolResult;

                if (switchToolResult != null && !switchToolResult.Success)
                {
                    string errMsg = switchToolResult.Message ?? "Unknown error";
                    System.Diagnostics.Debug.WriteLine($"[Zexus] Switch away failed: {errMsg}");
                    SetStatus($"Delete failed: {errMsg}", false);
                    await System.Threading.Tasks.Task.Delay(3000);
                    return;
                }

                // ── Step 2: Delete the element (now guaranteed not active) ──
                string deleteCode = $@"
ElementId eid;
var ctorLong = typeof(ElementId).GetConstructor(new[] {{ typeof(long) }});
if (ctorLong != null)
    eid = (ElementId)ctorLong.Invoke(new object[] {{ (long){eid} }});
else
    eid = new ElementId((int){eid});

var elem = doc.GetElement(eid);
if (elem == null) throw new InvalidOperationException(""Element not found: {eid}"");

using (var tx = new Transaction(doc, ""Delete {entityType}""))
{{
    tx.Start();
    doc.Delete(eid);
    tx.Commit();
}}
return ""Deleted"";";

                var deleteArgs = new Dictionary<string, object> { ["code"] = deleteCode };
                var result = await App.RevitEventHandler.ExecuteToolAsync("ExecuteCode", deleteArgs);
                var toolResult = result as Models.ToolResult;

                if (toolResult != null && toolResult.Success)
                {
                    record.IsDeleted = true;
                    record.Subtitle = "Deleted";
                    // Cascade grayout to child records (e.g., ScheduleModified, ViewModified)
                    CascadeDeleteToChildren(record.Id);
                    RebuildOutputCards();
                }
                else
                {
                    string errMsg = toolResult?.Message ?? "Unknown error";
                    System.Diagnostics.Debug.WriteLine($"[Zexus] Schedule delete failed: {errMsg}");
                    SetStatus($"Delete failed: {errMsg}", false);
                    await System.Threading.Tasks.Task.Delay(3000);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Schedule delete error: {ex.Message}");
            }
            finally
            {
                SetStatus("", false);
            }
        }

        /// <summary>
        /// Updates the subtitle of a ScheduleModified record after one or more entries have been reverted.
        /// </summary>
        private void UpdateScheduleRecordSubtitle(OutputRecord record)
        {
            if (record.ScheduleChangeEntries == null) return;

            if (record.IsScheduleFullyReverted)
            {
                record.Subtitle = "All changes reverted";
            }
            else
            {
                int reverted = record.ScheduleRevertedCount;
                string summary = FormatScheduleModSummary(record.ScheduleChangeEntries);
                if (reverted > 0)
                    record.Subtitle = $"{summary} ({reverted} reverted)";
                else
                    record.Subtitle = summary;
            }
        }

        /// <summary>
        /// Safely extracts a List&lt;string&gt; from a result data dictionary value.
        /// Handles both List&lt;string&gt; and List&lt;object&gt; (type varies between net48/net8.0).
        /// </summary>
        private List<string> ExtractFileList(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var obj) || obj == null)
                return null;

            if (obj is List<string> strList)
                return strList;

            if (obj is System.Collections.IEnumerable enumerable)
            {
                var result = new List<string>();
                foreach (var item in enumerable)
                {
                    if (item != null)
                        result.Add(item.ToString());
                }
                return result.Count > 0 ? result : null;
            }

            return null;
        }

        private long? ConvertToLong(object val)
        {
            if (val == null) return null;
            if (val is long l) return l;
            if (val is int i) return i;
            if (long.TryParse(val.ToString(), out long parsed)) return parsed;
            return null;
        }

        /// <summary>
        /// Extracts a List of strings from tool result data (handles both List&lt;string&gt; and List&lt;object&gt;).
        /// Reuses the same logic as ExtractFileList but with a different name for clarity.
        /// </summary>
        private List<string> ExtractStringList(Dictionary<string, object> data, string key)
        {
            return ExtractFileList(data, key);
        }

        /// <summary>
        /// Finds the parent record (ViewCreated/ScheduleCreated) that matches a view/schedule name.
        /// Used to set ParentRecordId on child records (ViewModified, ScheduleModified).
        /// </summary>
        private string FindParentRecordId(string viewOrScheduleName)
        {
            if (string.IsNullOrEmpty(viewOrScheduleName)) return null;

            // Search for the most recent ViewCreated or ScheduleCreated record with matching title
            var parent = _outputRecords.LastOrDefault(r =>
                (r.RecordType == OutputRecordType.ViewCreated || r.RecordType == OutputRecordType.ScheduleCreated) &&
                !r.IsDeleted &&
                string.Equals(r.Title, viewOrScheduleName, StringComparison.OrdinalIgnoreCase));

            return parent?.Id;
        }

        /// <summary>
        /// Finds the parent record by element ID (ScheduleId or ViewId).
        /// Used when child records have the schedule/view ID but not necessarily the name.
        /// </summary>
        private string FindParentRecordIdByElementId(long? elementId)
        {
            if (!elementId.HasValue) return null;

            var parent = _outputRecords.LastOrDefault(r =>
                (r.RecordType == OutputRecordType.ScheduleCreated && r.ScheduleId == elementId) ||
                (r.RecordType == OutputRecordType.ViewCreated && r.ViewId == elementId));

            return parent?.Id;
        }

        /// <summary>
        /// When a host record (ViewCreated/ScheduleCreated) is deleted, cascade grayout
        /// to all child records that reference it via ParentRecordId.
        /// </summary>
        private void CascadeDeleteToChildren(string parentRecordId)
        {
            if (string.IsNullOrEmpty(parentRecordId)) return;

            foreach (var record in _outputRecords)
            {
                if (record.ParentRecordId == parentRecordId && !record.IsDeleted)
                {
                    record.IsDeleted = true;
                    record.Subtitle = "Host deleted \u2022 " + record.Subtitle;
                }
            }
        }

        /// <summary>
        /// Builds a ScheduleChangeEntry from a schedule modification tool's result data.
        /// </summary>
        private ScheduleChangeEntry BuildScheduleChangeEntry(string toolName, Dictionary<string, object> data)
        {
            if (data == null) return null;

            string fieldName = data.TryGetValue("field_name", out var fn) ? fn?.ToString() : "";

            switch (toolName)
            {
                case "AddScheduleField":
                {
                    // Determine action from result data
                    bool alreadyExists = data.TryGetValue("already_exists", out var ae) && Convert.ToBoolean(ae);
                    if (alreadyExists) return null; // skip warnings

                    string schedName = data.TryGetValue("schedule_name", out var sn2) ? sn2?.ToString() : "";

                    bool isRemove = data.TryGetValue("removed_field", out var rf) && rf != null;
                    if (isRemove)
                    {
                        // Removed field — cannot re-add automatically (would need field_name + position)
                        return new ScheduleChangeEntry
                        {
                            ChangeType = "Field", Action = "removed",
                            FieldName = rf?.ToString() ?? fieldName,
                            Detail = "",
                            CanUndo = false
                        };
                    }

                    bool isReorder = data.ContainsKey("from_position") && data.ContainsKey("to_position");
                    if (isReorder)
                    {
                        // Reorder — reverse by swapping from/to positions
                        return new ScheduleChangeEntry
                        {
                            ChangeType = "Field", Action = "reordered",
                            FieldName = fieldName,
                            Detail = $"{data["from_position"]} \u2192 {data["to_position"]}",
                            CanUndo = true,
                            UndoAction = "undo",
                            UndoToolName = "AddScheduleField",
                            UndoData = new Dictionary<string, object>
                            {
                                ["schedule_name"] = schedName,
                                ["field_name"] = fieldName,
                                ["mode"] = "reorder",
                                ["position"] = data["from_position"]
                            }
                        };
                    }

                    // Default: field added — undo by removing
                    string pos = data.TryGetValue("position", out var p) ? $"pos {p}" : "";
                    bool hidden = data.TryGetValue("is_hidden", out var h) && Convert.ToBoolean(h);
                    string detail = hidden ? $"{pos}, hidden" : pos;
                    string addedFieldName = data.TryGetValue("column_header", out var ch) ? ch?.ToString() ?? fieldName : fieldName;

                    return new ScheduleChangeEntry
                    {
                        ChangeType = "Field", Action = "added",
                        FieldName = addedFieldName,
                        Detail = detail.Trim().TrimStart(',').Trim(),
                        CanUndo = true,
                        UndoAction = "undo",
                        UndoToolName = "AddScheduleField",
                        UndoData = new Dictionary<string, object>
                        {
                            ["schedule_name"] = schedName,
                            ["field_name"] = fieldName,
                            ["mode"] = "remove"
                        }
                    };
                }

                case "FormatScheduleField":
                {
                    var changes = new List<string>();
                    if (data.TryGetValue("changes", out var ch) && ch is System.Collections.IEnumerable changeList)
                    {
                        foreach (var c in changeList)
                            if (c != null) changes.Add(c.ToString());
                    }

                    string fmtSchedName = data.TryGetValue("schedule_name", out var fsn) ? fsn?.ToString() : "";

                    // Build undo data from old_values returned by FormatScheduleField
                    Dictionary<string, object> undoData = null;
                    if (data.TryGetValue("old_values", out var ov) && ov is Dictionary<string, object> oldVals && oldVals.Count > 0)
                    {
                        undoData = new Dictionary<string, object>
                        {
                            ["schedule_name"] = fmtSchedName,
                            ["field_name"] = fieldName
                        };
                        // Copy old values as the new target values for reversal
                        foreach (var kv in oldVals)
                            undoData[kv.Key] = kv.Value;
                    }

                    return new ScheduleChangeEntry
                    {
                        ChangeType = "Format", Action = "set",
                        FieldName = fieldName,
                        Detail = changes.Count > 0 ? string.Join(", ", changes) : "",
                        CanUndo = undoData != null,
                        UndoAction = "undo",
                        UndoToolName = "FormatScheduleField",
                        UndoData = undoData
                    };
                }

                case "ModifyScheduleFilter":
                {
                    string filtSchedName = data.TryGetValue("schedule_name", out var ftsn) ? ftsn?.ToString() : "";
                    bool isRemove = data.ContainsKey("removed_index");
                    bool isClear = data.ContainsKey("cleared");

                    if (isClear)
                    {
                        int cleared = data.TryGetValue("cleared", out var cl) ? Convert.ToInt32(cl) : 0;
                        return cleared > 0 ? new ScheduleChangeEntry
                        {
                            ChangeType = "Filter", Action = "cleared",
                            FieldName = "", Detail = $"{cleared} filter(s)",
                            CanUndo = false // cannot restore cleared filters
                        } : null;
                    }

                    if (isRemove)
                    {
                        // Removed filter — cannot re-add without knowing original params
                        return new ScheduleChangeEntry
                        {
                            ChangeType = "Filter", Action = "removed",
                            FieldName = fieldName, Detail = "",
                            CanUndo = false
                        };
                    }

                    // Filter added — undo by removing it
                    string op = data.TryGetValue("operator", out var opv) ? opv?.ToString() : "";
                    string val = data.TryGetValue("value", out var v) ? v?.ToString() : "";

                    return new ScheduleChangeEntry
                    {
                        ChangeType = "Filter", Action = "added",
                        FieldName = fieldName,
                        Detail = $"{op} {val}".Trim(),
                        CanUndo = true,
                        UndoAction = "undo",
                        UndoToolName = "ModifyScheduleFilter",
                        UndoData = new Dictionary<string, object>
                        {
                            ["schedule_name"] = filtSchedName,
                            ["field_name"] = fieldName,
                            ["mode"] = "remove"
                        }
                    };
                }

                case "ModifyScheduleSort":
                {
                    string sortSchedName = data.TryGetValue("schedule_name", out var stsn) ? stsn?.ToString() : "";
                    bool isRemove = data.ContainsKey("removed_index");
                    bool isClear = data.ContainsKey("cleared");

                    if (isClear)
                    {
                        int cleared = data.TryGetValue("cleared", out var cl) ? Convert.ToInt32(cl) : 0;
                        return cleared > 0 ? new ScheduleChangeEntry
                        {
                            ChangeType = "Sort", Action = "cleared",
                            FieldName = "", Detail = $"{cleared} sort(s)",
                            CanUndo = false // cannot restore cleared sorts
                        } : null;
                    }

                    if (isRemove)
                    {
                        // Removed sort — cannot re-add without knowing original params
                        return new ScheduleChangeEntry
                        {
                            ChangeType = "Sort", Action = "removed",
                            FieldName = fieldName, Detail = "",
                            CanUndo = false
                        };
                    }

                    // Sort added — undo by removing it
                    string order = data.TryGetValue("sort_order", out var so) ? so?.ToString() : "";
                    return new ScheduleChangeEntry
                    {
                        ChangeType = "Sort", Action = "added",
                        FieldName = fieldName,
                        Detail = order,
                        CanUndo = true,
                        UndoAction = "undo",
                        UndoToolName = "ModifyScheduleSort",
                        UndoData = new Dictionary<string, object>
                        {
                            ["schedule_name"] = sortSchedName,
                            ["field_name"] = fieldName,
                            ["mode"] = "remove"
                        }
                    };
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// Generates a concise summary like "3 fields added, 1 filter set, 1 sort added".
        /// </summary>
        private string FormatScheduleModSummary(List<ScheduleChangeEntry> entries)
        {
            if (entries == null || entries.Count == 0) return "Modified";

            var counts = new Dictionary<string, int>();
            foreach (var e in entries)
            {
                var key = $"{e.ChangeType} {e.Action}";
                if (counts.ContainsKey(key))
                    counts[key]++;
                else
                    counts[key] = 1;
            }

            var parts = new List<string>();
            foreach (var kv in counts)
            {
                parts.Add(kv.Value == 1 ? $"1 {kv.Key}" : $"{kv.Value} {kv.Key}");
            }

            return string.Join(", ", parts);
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            _agentService?.Dispose();
            base.OnClosed(e);
        }
    }
}
