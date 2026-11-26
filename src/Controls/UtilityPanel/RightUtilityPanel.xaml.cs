using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LlmodelBox.Common;
using LlmodelBox.Infrastructure;
using LlmodelBox.Infrastructure.RightPanel;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime;
using LlmodelBox.Runtime.Context;
using LlmodelBox.Runtime.Memory;
using LlmodelBox.Runtime.Middleware;
using LlmodelBox.Runtime.Tools;
using LlmodelBox.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace LlmodelBox.Controls.UtilityPanel
{
    public enum RightPanelDisplayMode
    {
        Expanded,
        CollapsedRail,
        Hidden
    }

    public sealed class OpenFileCandidateItem
    {
        public string FileName { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public string Badge { get; init; } = "FILE";
    }

    public partial class RightUtilityPanel : UserControl
    {
        // Bound dependencies (configured via Configure(...))
        private IAiRuntime? _runtime;
        private ISettingsService? _settingsService;
        private UserSettings _settings = new();
        private IWorkspaceService _workspace = new WorkspaceService();
        private IGitService _git = new GitService();
        private IBuildTestRunner _buildRunner = new BuildTestRunner();
        private IMemoryStore _memoryStore = new JsonMemoryStore();
        private ITokenUsageService? _tokenUsageService;
        private ToolRegistry? _toolRegistry;
        private IRealtimeEventEngine? _realtimeEvents;
        private Func<List<ModelItem>>? _getModels;
        private Func<ModelItem?>? _getActiveModel;
        private Func<string?>? _getOpenFilePath;
        private Func<string>? _getOpenEditorText;

        // Part 2 modular services
        private readonly ITerminalSessionManager _terminal = new TerminalSessionManager();
        private WorkspaceReviewService? _reviewService;
        private readonly BrowserSessionService _browser = new();
        private readonly PluginManagerService _plugins = new();
        private OmniboxService? _omnibox;

        // Live state
        private string _workspaceRoot = string.Empty;
        private List<string> _workspaceFiles = new();
        private string _activeTool = "Review";
        private string _reviewFilter = "All";
        private RightPanelDisplayMode _displayMode = RightPanelDisplayMode.Expanded;
        private bool _reviewSideBySide = false;
        private ReviewableFileChange? _selectedReviewFile;
        private string? _activeTerminalId;
        private int _terminalHistoryIdx = -1;
        private bool _webViewInitialized = false;
        private bool _webViewInitAttempted = false;
        private BrowserTabState? _activeBrowserTab;
        private readonly ObservableCollection<ChatMessageItem> _sideMessages = new();
        private readonly List<string> _sideTempAttachments = new();
        private bool _sideBusy = false;
        private BuildRunResult? _lastBuildResult;
        private readonly List<string> _activityEntries = new();
        private Action<AgentSignal>? _activitySub;

        public event Action<RightPanelDisplayMode>? DisplayModeChangeRequested;
        public event Action<string>? OpenFileInEditorRequested;
        public event Action<string, int, int>? OpenFileAtLocationRequested;
        public event Action<IReadOnlyList<LlmodelBox.Infrastructure.Editor.EditorDiagnostic>>? BuildDiagnosticsUpdated;
        public event Action<string>? SendToMainChatRequested;
        public event Action<string>? ActiveToolChanged;
        public event Action? WorkspaceFilesMutated;
        public event Action<string>? ExplorerShortcutRequested;

        public RightUtilityPanel()
        {
            InitializeComponent();
            _terminal.OutputReceived += OnTerminalOutputReceived;
            _terminal.SessionStateChanged += OnTerminalSessionStateChanged;
            _plugins.PluginsChanged += () => Dispatcher.BeginInvoke(RenderPluginsView);

            _activitySub = sig =>
            {
                string line = $"[{sig.TimestampUtc:HH:mm:ss}] {sig.Node}: {sig.Detail}";
                lock (_activityEntries)
                {
                    _activityEntries.Insert(0, line);
                    if (_activityEntries.Count > 120) _activityEntries.RemoveAt(_activityEntries.Count - 1);
                }
                if (_activeTool == "Activity" && _displayMode == RightPanelDisplayMode.Expanded)
                {
                    Dispatcher.BeginInvoke(() => RefreshUtilityView());
                }
            };
            AgentActivityBus.SignalEmitted += _activitySub;
            Unloaded += (_, _) =>
            {
                if (_activitySub != null) AgentActivityBus.SignalEmitted -= _activitySub;
                _terminal.Dispose();
            };
        }

        public string ActiveTool => _activeTool;
        public RightPanelDisplayMode DisplayMode => _displayMode;
        public ITerminalSessionManager TerminalManager => _terminal;
        public WorkspaceReviewService? ReviewService => _reviewService;
        public BrowserSessionService BrowserService => _browser;
        public PluginManagerService PluginService => _plugins;

        public void Configure(
            IAiRuntime runtime,
            ISettingsService settingsService,
            UserSettings settings,
            IWorkspaceService workspace,
            IGitService git,
            IBuildTestRunner buildRunner,
            ITokenUsageService tokenUsageService,
            ToolRegistry? toolRegistry,
            IRealtimeEventEngine? realtimeEvents,
            Func<List<ModelItem>> getModels,
            Func<ModelItem?> getActiveModel,
            Func<string?> getOpenFilePath,
            Func<string> getOpenEditorText)
        {
            _runtime = runtime;
            _settingsService = settingsService;
            _settings = settings ?? new UserSettings();
            _workspace = workspace ?? new WorkspaceService();
            _git = git ?? new GitService();
            _buildRunner = buildRunner ?? new BuildTestRunner();
            _tokenUsageService = tokenUsageService;
            _toolRegistry = toolRegistry;
            _realtimeEvents = realtimeEvents;
            _getModels = getModels;
            _getActiveModel = getActiveModel;
            _getOpenFilePath = getOpenFilePath;
            _getOpenEditorText = getOpenEditorText;

            _reviewService = new WorkspaceReviewService(_workspace, _git);
            _omnibox = new OmniboxService(_toolRegistry, _plugins);

            _browser.AllowDownloads = _settings.BrowserAllowDownloads;
            _browser.AllowAiAutomation = _settings.BrowserAllowAutomation;
            if (!string.IsNullOrWhiteSpace(_settings.BrowserHomeUrl))
                _browser.HomeUrl = _settings.BrowserHomeUrl;

            ChkBrowserAllowDownloads.IsChecked = _browser.AllowDownloads;
            ChkBrowserAllowAutomation.IsChecked = _browser.AllowAiAutomation;
            TxtBrowserHomeUrl.Text = _browser.HomeUrl;

            RefreshSideModelList();
            if (!string.IsNullOrWhiteSpace(_settings.RightPanelActiveTool))
                SelectTool(_settings.RightPanelActiveTool, persist: false);

            SetDisplayMode(ParseMode(_settings.RightPanelState));
        }

        public void SetWorkspaceContext(string rootPath, IEnumerable<string> relativeFiles)
        {
            _workspaceRoot = rootPath ?? string.Empty;
            _workspaceFiles = (relativeFiles ?? Array.Empty<string>()).ToList();

            if (BadgeFilesCount != null) BadgeFilesCount.Text = _workspaceFiles.Count.ToString();
            UpdateToolBadges();

            if (_activeTool == "Review") _ = RefreshReviewAsync();
            else if (_activeTool == "OpenFile") RefreshOpenFileList();
            else if (_activeTool is "SourceControl" or "Memory" or "Context" or "Problems" or "ModelUsage" or "ProjectInfo")
                RefreshUtilityView();
        }

        private void UpdateToolBadges()
        {
            if (BadgeTerminalCount != null)
                BadgeTerminalCount.Text = _terminal.Sessions.Count > 0 ? _terminal.Sessions.Count.ToString() : "PS";
            if (BadgePluginsCount != null)
            {
                int active = _plugins.GetAllPlugins().Count(p => p.State.IsEnabled);
                BadgePluginsCount.Text = active > 0 ? $"{active} ON" : "EXT";
            }
        }

        public void RecordManualBufferChange(string? fullFilePath, string diskBefore, string editorNow)
        {
            if (_reviewService == null || string.IsNullOrWhiteSpace(_workspaceRoot) || string.IsNullOrWhiteSpace(fullFilePath))
                return;
            _reviewService.RecordManualUnsavedEdit(_workspaceRoot, fullFilePath, diskBefore, editorNow);
            if (_activeTool == "Review" && _displayMode == RightPanelDisplayMode.Expanded)
            {
                _ = RefreshReviewAsync();
            }
        }

        public void RecordAiFileChanges(IEnumerable<ChangedFileEntry> entries)
        {
            if (_reviewService == null || string.IsNullOrWhiteSpace(_workspaceRoot)) return;
            _reviewService.RecordAiDiffs(_workspaceRoot, entries);
            if (_activeTool == "Review") _ = RefreshReviewAsync();
        }

        public void TrackRecentFile(string relativeOrFullPath)
        {
            if (string.IsNullOrWhiteSpace(relativeOrFullPath)) return;
            _settings.RecentFiles.RemoveAll(f => string.Equals(f, relativeOrFullPath, StringComparison.OrdinalIgnoreCase));
            _settings.RecentFiles.Insert(0, relativeOrFullPath);
            if (_settings.RecentFiles.Count > 20) _settings.RecentFiles.RemoveRange(20, _settings.RecentFiles.Count - 20);
            _settingsService?.SaveSettings(_settings);
        }

        public void FocusOmnibox()
        {
            if (_displayMode != RightPanelDisplayMode.Expanded)
                DisplayModeChangeRequested?.Invoke(RightPanelDisplayMode.Expanded);
            TxtOmnibox.Focus();
            TxtOmnibox.SelectAll();
            ShowOmniboxResults(TxtOmnibox.Text);
        }

        public void SetDisplayMode(RightPanelDisplayMode mode)
        {
            _displayMode = mode;
            ExpandedRoot.Visibility = mode == RightPanelDisplayMode.Expanded ? Visibility.Visible : Visibility.Collapsed;
            IconRailRoot.Visibility = mode == RightPanelDisplayMode.CollapsedRail ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SelectTool(string toolKey, bool persist = true)
        {
            _activeTool = string.IsNullOrWhiteSpace(toolKey) ? "Review" : toolKey;

            // Update 3x2 Bento Grid tile active states
            var primaryBtns = new[] { BtnToolReview, BtnToolOpenFile, BtnToolTerminal, BtnToolBrowser, BtnToolSideQuestion, BtnToolPlugins };
            foreach (var b in primaryBtns)
            {
                bool active = string.Equals(b.Tag?.ToString(), _activeTool, StringComparison.OrdinalIgnoreCase);
                b.Style = TryFindResource(active ? "BentoTileBtnActive" : "BentoTileBtn") as Style ?? b.Style;
            }
            UpdateToolBadges();

            ViewReview.Visibility = _activeTool == "Review" ? Visibility.Visible : Visibility.Collapsed;
            ViewOpenFile.Visibility = _activeTool == "OpenFile" ? Visibility.Visible : Visibility.Collapsed;
            ViewTerminal.Visibility = _activeTool == "Terminal" ? Visibility.Visible : Visibility.Collapsed;
            ViewBrowser.Visibility = _activeTool == "Browser" ? Visibility.Visible : Visibility.Collapsed;
            ViewSideQuestion.Visibility = _activeTool == "SideQuestion" ? Visibility.Visible : Visibility.Collapsed;
            ViewPlugins.Visibility = _activeTool == "Plugins" ? Visibility.Visible : Visibility.Collapsed;

            bool isExtraUtility = !new[] { "Review", "OpenFile", "Terminal", "Browser", "SideQuestion", "Plugins" }.Contains(_activeTool);
            ViewUtilitiesHost.Visibility = isExtraUtility ? Visibility.Visible : Visibility.Collapsed;

            AnimateToolViewportTransition();

            if (_activeTool == "Review") _ = RefreshReviewAsync();
            else if (_activeTool == "OpenFile") RefreshOpenFileList();
            else if (_activeTool == "Terminal") EnsureTerminalStarted();
            else if (_activeTool == "Browser") _ = EnsureBrowserInitializedAsync();
            else if (_activeTool == "SideQuestion") RefreshSideModelList();
            else if (_activeTool == "Plugins") RenderPluginsView();
            else if (isExtraUtility) RefreshUtilityView();

            if (persist)
            {
                _settings.RightPanelActiveTool = _activeTool;
                _settingsService?.SaveSettings(_settings);
            }
            ActiveToolChanged?.Invoke(_activeTool);
        }

        private void AnimateToolViewportTransition()
        {
            if (ToolViewportGrid == null || ToolViewportTranslate == null || !IsLoaded) return;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            var slide = new System.Windows.Media.Animation.DoubleAnimation(10.0, 0.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            ToolViewportGrid.BeginAnimation(OpacityProperty, fade);
            ToolViewportTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        // =================== COLLAPSE / HIDE / EXPAND ===================
        private void BtnCollapseToRail_Click(object sender, RoutedEventArgs e)
            => DisplayModeChangeRequested?.Invoke(RightPanelDisplayMode.CollapsedRail);

        private void BtnHidePanel_Click(object sender, RoutedEventArgs e)
            => DisplayModeChangeRequested?.Invoke(RightPanelDisplayMode.Hidden);

        private void BtnExpandFromRail_Click(object sender, RoutedEventArgs e)
            => DisplayModeChangeRequested?.Invoke(RightPanelDisplayMode.Expanded);

        private void BtnRailTool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string key)
            {
                SelectTool(key);
                DisplayModeChangeRequested?.Invoke(RightPanelDisplayMode.Expanded);
            }
        }

        private void BtnSelectTool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string key) SelectTool(key);
        }

        private void BtnMoreUtilities_Click(object sender, RoutedEventArgs e)
            => MoreUtilitiesPopup.IsOpen = !MoreUtilitiesPopup.IsOpen;

        private void BtnSelectUtilityPopup_Click(object sender, RoutedEventArgs e)
        {
            MoreUtilitiesPopup.IsOpen = false;
            if (sender is Button btn && btn.Tag is string key) SelectTool(key);
        }

        // =================== OMNIBOX UNIVERSAL SEARCH / URL BAR ===================
        private void TxtOmnibox_GotFocus(object sender, RoutedEventArgs e)
            => ShowOmniboxResults(TxtOmnibox.Text);

        private void TxtOmnibox_TextChanged(object sender, TextChangedEventArgs e)
            => ShowOmniboxResults(TxtOmnibox.Text);

        private void TxtOmnibox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && OmniboxResultsList.Items.Count > 0)
            {
                OmniboxPopup.IsOpen = true;
                OmniboxResultsList.SelectedIndex = 0;
                OmniboxResultsList.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (OmniboxResultsList.SelectedItem is OmniboxSearchItem sel)
                {
                    ExecuteOmniboxItem(sel);
                }
                else if (OmniboxResultsList.Items.Count > 0 && OmniboxResultsList.Items[0] is OmniboxSearchItem first)
                {
                    ExecuteOmniboxItem(first);
                }
            }
            else if (e.Key == Key.Escape)
            {
                OmniboxPopup.IsOpen = false;
            }
        }

        private void OmniboxResultsList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && OmniboxResultsList.SelectedItem is OmniboxSearchItem item)
            {
                e.Handled = true;
                ExecuteOmniboxItem(item);
            }
        }

        private void OmniboxResultsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (OmniboxResultsList.SelectedItem is OmniboxSearchItem item)
            {
                ExecuteOmniboxItem(item);
            }
        }

        private void ShowOmniboxResults(string query)
        {
            if (_omnibox == null) _omnibox = new OmniboxService(_toolRegistry, _plugins);
            var commands = new List<(string Id, string Title, string Subtitle)>
            {
                ("tool:Review", "View: Review All Changes", "Inspect AI, Manual, and Git diffs + hunks"),
                ("tool:OpenFile", "View: Open File", "Fuzzy workspace file finder"),
                ("tool:Terminal", "View: Terminal", "Multi-tab persistent PowerShell / CMD / Git Bash"),
                ("tool:Browser", "View: Embedded Browser", "WebView2 integrated browser"),
                ("tool:SideQuestion", "View: Side Question", "Isolated secondary AI conversation"),
                ("tool:Plugins", "View: Plugins", "Manage extensions & permissions"),
                ("tool:CodeSearch", "View: Code Search", "Search code across workspace"),
                ("tool:Problems", "View: Problems", "Build diagnostics & TODOs"),
                ("tool:RunTest", "View: Run & Test", "Execute dotnet build / dotnet test"),
                ("tool:SourceControl", "View: Source Control", "Git branch & status"),
                ("tool:Memory", "View: Memory Inspector", "Inspect project memory"),
                ("tool:Context", "View: Context Inspector", "View context & token breakdown"),
                ("cmd:Collapse", "Layout: Collapse Right Panel", "Shrink to 44px icon rail"),
                ("cmd:Hide", "Layout: Hide Right Panel", "Hide right utility panel (Ctrl+Shift+U)")
            };

            var items = _omnibox.Search(query, _workspaceFiles, _settings.RecentFiles, commands, _settings.RecentCommands);
            OmniboxResultsList.ItemsSource = items;
            OmniboxPopup.IsOpen = items.Count > 0;
        }

        private void ExecuteOmniboxItem(OmniboxSearchItem item)
        {
            OmniboxPopup.IsOpen = false;
            switch (item.Kind)
            {
                case OmniboxResultKind.Url:
                    SelectTool("Browser");
                    _ = NavigateBrowserAsync(item.TargetKey);
                    break;
                case OmniboxResultKind.File:
                case OmniboxResultKind.RecentFile:
                    OpenWorkspacePath(item.TargetKey);
                    break;
                case OmniboxResultKind.RecentCommand:
                    SelectTool("Terminal");
                    EnsureTerminalStarted();
                    if (_activeTerminalId != null)
                        _ = _terminal.SendInputAsync(_activeTerminalId, item.TargetKey);
                    break;
                case OmniboxResultKind.Command:
                case OmniboxResultKind.PluginCommand:
                    if (item.TargetKey == "cmd:Collapse") DisplayModeChangeRequested?.Invoke(RightPanelDisplayMode.CollapsedRail);
                    else if (item.TargetKey == "cmd:Hide") DisplayModeChangeRequested?.Invoke(RightPanelDisplayMode.Hidden);
                    else if (item.TargetKey.StartsWith("tool:", StringComparison.Ordinal))
                        SelectTool(item.TargetKey.Substring(5));
                    else if (item.TargetKey == "plugin.git.refresh")
                        SelectTool("SourceControl");
                    else if (item.TargetKey == "plugin.dotnet.build")
                        SelectTool("RunTest");
                    else if (item.TargetKey == "plugin.docs.mslearn")
                    {
                        SelectTool("Browser");
                        _ = NavigateBrowserAsync("https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-8/overview");
                    }
                    break;
                case OmniboxResultKind.Tool:
                    SelectTool("Activity");
                    break;
            }
        }

        // =================== TOOL 1: REVIEW ===================
        public async Task RefreshReviewAsync()
        {
            if (_reviewService == null || string.IsNullOrWhiteSpace(_workspaceRoot)) return;
            var all = await _reviewService.CollectAllChangesAsync(_workspaceRoot);
            if (BadgeReviewCount != null) BadgeReviewCount.Text = all.Count.ToString();

            var filtered = all.Where(c => _reviewFilter switch
            {
                "AI" => c.Origin == ChangeOriginKind.AiGenerated,
                "Manual" => c.Origin == ChangeOriginKind.ManualEdit,
                "Git" => c.Origin == ChangeOriginKind.GitWorkingTree,
                _ => true
            }).ToList();

            TxtReviewCount.Text = $"{filtered.Count}";
            ReviewFilesList.ItemsSource = filtered;
            ReviewHistoryList.ItemsSource = _reviewService.History.Select(h => $"{h.TimestampUtc:HH:mm:ss} {h.Action}: {h.RelativePath}").ToList();

            if (filtered.Count > 0)
            {
                ReviewFilesList.Visibility = Visibility.Visible;
                ReviewActionBar.Visibility = Visibility.Visible;
                if (_selectedReviewFile == null || !filtered.Any(f => string.Equals(f.RelativePath, _selectedReviewFile.RelativePath, StringComparison.OrdinalIgnoreCase)))
                    ReviewFilesList.SelectedIndex = 0;
                else
                    RenderSelectedReviewFile(filtered.First(f => string.Equals(f.RelativePath, _selectedReviewFile.RelativePath, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                ReviewFilesList.Visibility = Visibility.Collapsed;
                ReviewActionBar.Visibility = Visibility.Collapsed;
                RenderCleanWorkspaceBentoHub();
            }
        }

        private void RenderCleanWorkspaceBentoHub()
        {
            ReviewDiffHost.Children.Clear();

            var hero = new Border
            {
                Background = Brush("BrushCardInnerBg", "#16181a"),
                BorderBrush = Brush("BrushBorder", "#27272a"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 2, 0, 8)
            };
            var heroStack = new StackPanel();
            var topRow = new Grid();
            topRow.Children.Add(new TextBlock
            {
                Text = "Workspace Clean ✓",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("BrushSuccess", "#22c55e"),
                VerticalAlignment = VerticalAlignment.Center
            });
            topRow.Children.Add(new Border
            {
                Background = Brush("BrushCardBg", "#111114"),
                BorderBrush = Brush("BrushBorderSubtle", "#27272a"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(7, 1, 7, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBlock
                {
                    Text = $"{_workspaceFiles.Count} files",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 9.5,
                    Foreground = Brush("BrushTextDim", "#9ca3af")
                }
            });
            heroStack.Children.Add(topRow);
            heroStack.Children.Add(new TextBlock
            {
                Text = "AI edits, manual edits, and Git diffs appear here live with hunk controls.",
                FontSize = 10.5,
                Foreground = Brush("BrushTextSecondary", "#a1a1aa"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            hero.Child = heroStack;
            ReviewDiffHost.Children.Add(hero);

            // 3-Column Bento Mini Telemetry Cards Row
            var metricsRow = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
            var activeModel = _getActiveModel?.Invoke();
            var openPath = _getOpenFilePath?.Invoke();
            metricsRow.Children.Add(CreateMetricBentoCard("FILES", _workspaceFiles.Count.ToString(), "Indexed", new Thickness(0, 0, 5, 0)));
            metricsRow.Children.Add(CreateMetricBentoCard("BUFFER", string.IsNullOrEmpty(openPath) ? "—" : Path.GetFileName(openPath), "Active", new Thickness(0, 0, 5, 0)));
            metricsRow.Children.Add(CreateMetricBentoCard("MODEL", activeModel?.Provider ?? "Ready", activeModel?.Name ?? "None", new Thickness(0)));
            ReviewDiffHost.Children.Add(metricsRow);

            // 2x2 Animated Hover Bento Quick Launch Grid
            var bentoGrid = new System.Windows.Controls.Primitives.UniformGrid
            {
                Rows = 2,
                Columns = 2
            };

            Button MakeBentoAction(string badge, string title, string sub, string toolKey, Thickness margin)
            {
                var stack = new StackPanel();
                var hdr = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                hdr.Children.Add(new Border
                {
                    Background = Brush("BrushCardBg", "#111114"),
                    BorderBrush = Brush("BrushBorderSubtle", "#27272a"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(5, 1, 5, 1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = badge, FontFamily = new FontFamily("Consolas"), FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Brush("BrushAccentLight", "#60a5fa") }
                });
                hdr.Children.Add(new TextBlock { Text = "→", FontSize = 11, Foreground = Brush("BrushTextDim", "#71717a"), HorizontalAlignment = HorizontalAlignment.Right });
                stack.Children.Add(hdr);
                stack.Children.Add(new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brush("BrushTextPrimary", "#fafafa") });
                stack.Children.Add(new TextBlock { Text = sub, FontSize = 10, Foreground = Brush("BrushTextDim", "#71717a"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

                var btn = new Button
                {
                    Content = stack,
                    Style = TryFindResource("BentoTileBtn") as Style,
                    Margin = margin,
                    Padding = new Thickness(10, 9, 10, 9),
                    VerticalContentAlignment = VerticalAlignment.Top
                };
                btn.Click += (_, _) => SelectTool(toolKey);
                return btn;
            }

            bentoGrid.Children.Add(MakeBentoAction("SHELL", "Terminal", "PowerShell, CMD & Git Bash", "Terminal", new Thickness(0, 0, 6, 6)));
            bentoGrid.Children.Add(MakeBentoAction("⌘P", "Quick Open", "Fuzzy file finder & preview", "OpenFile", new Thickness(0, 0, 0, 6)));
            bentoGrid.Children.Add(MakeBentoAction("TEST", "Run & Test", "dotnet build & xUnit test", "RunTest", new Thickness(0, 0, 6, 0)));
            bentoGrid.Children.Add(MakeBentoAction("SIDE AI", "Side Question", "Isolated secondary AI chat", "SideQuestion", new Thickness(0, 0, 0, 0)));

            ReviewDiffHost.Children.Add(bentoGrid);
        }

        private Border CreateMetricBentoCard(string label, string value, string sub, Thickness margin)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = label, FontFamily = new FontFamily("Consolas"), FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Brush("BrushTextDim", "#71717a") });
            stack.Children.Add(new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brush("BrushTextPrimary", "#fafafa"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 1) });
            stack.Children.Add(new TextBlock { Text = sub, FontSize = 9.5, Foreground = Brush("BrushAccentLight", "#93c5fd"), TextTrimming = TextTrimming.CharacterEllipsis });
            return new Border
            {
                Background = Brush("BrushCardInnerBg", "#16181a"),
                BorderBrush = Brush("BrushBorderSubtle", "#27272a"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 7, 9, 7),
                Margin = margin,
                Child = stack
            };
        }

        private void BtnRefreshReview_Click(object sender, RoutedEventArgs e) => _ = RefreshReviewAsync();

        private void BtnReviewFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;
            _reviewFilter = tag;
            var filterBtns = new[] { BtnFilterAll, BtnFilterAi, BtnFilterManual, BtnFilterGit };
            foreach (var fb in filterBtns)
            {
                bool active = string.Equals(fb.Tag?.ToString(), _reviewFilter, StringComparison.OrdinalIgnoreCase);
                fb.Style = TryFindResource(active ? "SettingsTabBtnActive" : "SettingsTabBtn") as Style ?? fb.Style;
            }
            _ = RefreshReviewAsync();
        }

        private void BtnReviewModeToggle_Click(object sender, RoutedEventArgs e)
        {
            _reviewSideBySide = !_reviewSideBySide;
            BtnReviewModeToggle.Content = _reviewSideBySide ? "Inline" : "Split";
            if (_selectedReviewFile != null) RenderSelectedReviewFile(_selectedReviewFile);
        }

        private void ReviewFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ReviewFilesList.SelectedItem is ReviewableFileChange ch)
            {
                _selectedReviewFile = ch;
                RenderSelectedReviewFile(ch);
            }
        }

        private void RenderSelectedReviewFile(ReviewableFileChange file)
        {
            ReviewDiffHost.Children.Clear();
            if (_reviewService == null) return;

            if (_reviewSideBySide)
            {
                var rows = _reviewService.BuildSideBySide(file.BeforeContent, file.AfterContent);
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                for (int r = 0; r < rows.Count; r++)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var row = rows[r];
                    AddSideCell(grid, r, 0, row.LeftLineNumber, row.LeftType, isLineNo: true);
                    AddSideCell(grid, r, 1, row.LeftText, row.LeftType, isLineNo: false);
                    AddSideCell(grid, r, 2, row.RightLineNumber, row.RightType, isLineNo: true);
                    AddSideCell(grid, r, 3, row.RightText, row.RightType, isLineNo: false);
                }
                ReviewDiffHost.Children.Add(grid);
                return;
            }

            if (file.Hunks.Count == 0)
            {
                ReviewDiffHost.Children.Add(new TextBlock { Text = "No diff hunks.", Foreground = Brush("BrushTextDim", "#71717a"), FontSize = 11 });
                return;
            }

            foreach (var hunk in file.Hunks)
            {
                var hunkCard = new Border
                {
                    Background = Brush("BrushCardBg", "#141418"),
                    BorderBrush = Brush("BrushBorder", "#27272a"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var sp = new StackPanel();
                var hdr = new DockPanel { Background = Brush("BrushCardInnerBg", "#1a1a1f"), LastChildFill = true };
                var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(4) };
                DockPanel.SetDock(actions, Dock.Right);

                var acceptBtn = new Button { Content = hunk.IsAccepted ? "Accepted ✓" : "Accept", Style = TryFindResource(hunk.IsAccepted ? "PrimaryActionBtn" : "BlackBoxActionBtn") as Style, Padding = new Thickness(7, 2, 7, 2), FontSize = 9.5, Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand };
                acceptBtn.Click += (_, _) => { _reviewService.AcceptHunk(file.RelativePath, hunk.Id); RenderSelectedReviewFile(file); };
                var rejectBtn = new Button { Content = "Reject", Style = TryFindResource("BlackBoxActionBtn") as Style, Padding = new Thickness(7, 2, 7, 2), FontSize = 9.5, Cursor = Cursors.Hand };
                rejectBtn.Click += (_, _) =>
                {
                    if (_reviewService.RejectHunk(_workspaceRoot, file.RelativePath, hunk.Id))
                    {
                        WorkspaceFilesMutated?.Invoke();
                        _ = RefreshReviewAsync();
                    }
                };
                actions.Children.Add(acceptBtn);
                actions.Children.Add(rejectBtn);
                hdr.Children.Add(actions);
                hdr.Children.Add(new TextBlock { Text = hunk.Header, FontFamily = new FontFamily("Consolas"), FontSize = 10, Foreground = Brush("BrushAccentLight", "#93c5fd"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 4, 4, 4) });
                sp.Children.Add(hdr);

                foreach (var dl in hunk.Lines)
                {
                    var lineBg = dl.Type == "Added" ? Color.FromArgb(30, 34, 197, 94) : dl.Type == "Removed" ? Color.FromArgb(30, 239, 68, 68) : Colors.Transparent;
                    var lineFg = dl.Type == "Added" ? "#4ade80" : dl.Type == "Removed" ? "#f87171" : "#d4d4d8";
                    sp.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(lineBg),
                        Padding = new Thickness(6, 1, 6, 1),
                        Child = new TextBlock
                        {
                            Text = $"{dl.DisplayOldLine,3} {dl.DisplayNewLine,3} {dl.Prefix} {dl.Content}",
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 10.5,
                            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(lineFg))
                        }
                    });
                }

                hunkCard.Child = sp;
                ReviewDiffHost.Children.Add(hunkCard);
            }
        }

        private static void AddSideCell(Grid g, int row, int col, string text, string diffType, bool isLineNo)
        {
            var bg = diffType == "Added" ? Color.FromArgb(28, 34, 197, 94)
                   : diffType == "Removed" ? Color.FromArgb(28, 239, 68, 68)
                   : Colors.Transparent;
            var b = new Border
            {
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
                BorderThickness = new Thickness(0, 0, col == 1 ? 1 : 0, 0),
                Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10.5,
                    TextAlignment = isLineNo ? TextAlignment.Right : TextAlignment.Left,
                    Foreground = isLineNo ? Brushes.Gray : (diffType == "Added" ? new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)) : diffType == "Removed" ? new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71)) : Brushes.LightGray)
                }
            };
            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            g.Children.Add(b);
        }

        private void BtnReviewOpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedReviewFile != null) OpenWorkspacePath(_selectedReviewFile.RelativePath);
        }

        private void BtnReviewRevertFile_Click(object sender, RoutedEventArgs e)
        {
            if (_reviewService != null && _selectedReviewFile != null)
            {
                if (MessageBox.Show($"Revert '{_selectedReviewFile.RelativePath}' back to pre-change content?", "Revert File", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _reviewService.RevertFile(_workspaceRoot, _selectedReviewFile);
                    WorkspaceFilesMutated?.Invoke();
                    _ = RefreshReviewAsync();
                }
            }
        }

        // =================== TOOL 2: OPEN FILE ===================
        private void RefreshOpenFileList()
        {
            string q = TxtOpenFileSearch.Text ?? string.Empty;
            var items = new List<OpenFileCandidateItem>();

            foreach (var rf in _settings.RecentFiles)
            {
                if (!string.IsNullOrWhiteSpace(q) && OmniboxService.ComputeFuzzyScore(q, rf) <= 0) continue;
                items.Add(new OpenFileCandidateItem
                {
                    Badge = "RECENT",
                    FileName = Path.GetFileName(rf),
                    RelativePath = rf,
                    FullPath = ResolveFullPath(rf)
                });
            }

            var scored = _workspaceFiles
                .Select(f => new { Path = f, Score = OmniboxService.ComputeFuzzyScore(q, System.IO.Path.GetFileName(f)) + OmniboxService.ComputeFuzzyScore(q, f) })
                .Where(x => x.Score > 0 || string.IsNullOrWhiteSpace(q))
                .OrderByDescending(x => x.Score)
                .Take(40);

            foreach (var s in scored)
            {
                if (items.Any(i => string.Equals(i.RelativePath, s.Path, StringComparison.OrdinalIgnoreCase))) continue;
                items.Add(new OpenFileCandidateItem
                {
                    Badge = Path.GetExtension(s.Path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "FILE",
                    FileName = Path.GetFileName(s.Path),
                    RelativePath = s.Path,
                    FullPath = ResolveFullPath(s.Path)
                });
            }

            OpenFileResultsList.ItemsSource = items;
        }

        private void TxtOpenFileSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshOpenFileList();

        private void OpenFileResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (OpenFileResultsList.SelectedItem is not OpenFileCandidateItem item) return;
            TxtOpenFilePreviewTitle.Text = item.RelativePath.ToUpperInvariant();
            try
            {
                if (File.Exists(item.FullPath))
                {
                    var lines = File.ReadLines(item.FullPath).Take(80);
                    TxtOpenFilePreview.Text = string.Join(Environment.NewLine, lines);
                }
                else TxtOpenFilePreview.Text = "[File not found on disk]";
            }
            catch (Exception ex)
            {
                TxtOpenFilePreview.Text = $"[Preview error: {ex.Message}]";
            }
        }

        private void OpenFileResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
            => BtnOpenFileInEditor_Click(sender, e);

        private void BtnOpenFileInEditor_Click(object sender, RoutedEventArgs e)
        {
            if (OpenFileResultsList.SelectedItem is OpenFileCandidateItem item)
            {
                OpenWorkspacePath(item.FullPath);
            }
        }

        private void BtnOpenFileExternal_Click(object sender, RoutedEventArgs e)
        {
            if (OpenFileResultsList.SelectedItem is OpenFileCandidateItem item && File.Exists(item.FullPath))
            {
                if (MessageBox.Show($"Open '{item.FileName}' with the default Windows application?", "Confirm External Launch", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    try { Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); } catch { }
                }
            }
        }

        // =================== TOOL 3: PERSISTENT TERMINAL ===================
        private void EnsureTerminalStarted()
        {
            if (_terminal.Sessions.Count == 0)
            {
                var session = _terminal.CreateSession(TerminalShellKind.PowerShell, _workspaceRoot);
                _activeTerminalId = session.Id;
                RenderTerminalTabs();
                SelectTerminalTab(session.Id);
            }
        }

        private void BtnNewTerminalTab_Click(object sender, RoutedEventArgs e)
        {
            string shellName = (CmbTerminalShell.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "PowerShell";
            var kind = shellName.Contains("CMD", StringComparison.OrdinalIgnoreCase) ? TerminalShellKind.Cmd
                     : shellName.Contains("Git", StringComparison.OrdinalIgnoreCase) ? TerminalShellKind.GitBash
                     : TerminalShellKind.PowerShell;

            var env = ParseEnvVars(TxtTerminalEnvVars.Text);
            var session = _terminal.CreateSession(kind, _workspaceRoot, env);
            _activeTerminalId = session.Id;
            RenderTerminalTabs();
            SelectTerminalTab(session.Id);
        }

        private void BtnToggleTerminalEnv_Click(object sender, RoutedEventArgs e)
            => TerminalEnvBar.Visibility = TerminalEnvBar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        private void RenderTerminalTabs()
        {
            TerminalTabsBar.Children.Clear();
            foreach (var s in _terminal.Sessions)
            {
                bool active = s.Id == _activeTerminalId;
                var panel = new Border
                {
                    Background = active ? Brush("BrushCardBg", "#1f2937") : Brush("BrushCardInnerBg", "#0f141c"),
                    BorderBrush = active ? Brush("BrushAccent", "#3b82f6") : Brush("BrushBorderSubtle", "#1f2937"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Margin = new Thickness(0, 0, 5, 0),
                    Padding = new Thickness(8, 3, 6, 3)
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = s.IsRunning ? Brush("BrushSuccess", "#22c55e") : Brush("BrushDanger", "#ef4444"),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var titleBtn = new Button
                {
                    Content = s.Title + (s.IsRunning ? "" : $" [{s.ExitCode ?? 0}]"),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = active ? Brush("BrushTextPrimary", "#fafafa") : Brush("BrushTextSecondary", "#9ca3af"),
                    FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                    FontSize = 10.5,
                    Cursor = Cursors.Hand
                };
                string id = s.Id;
                titleBtn.Click += (_, _) => SelectTerminalTab(id);
                var closeBtn = new Button
                {
                    Content = "✕",
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = Brush("BrushTextDim", "#9ca3af"),
                    FontSize = 9.5,
                    Margin = new Thickness(6, 0, 0, 0),
                    Cursor = Cursors.Hand
                };
                closeBtn.Click += (_, _) =>
                {
                    _terminal.CloseSession(id);
                    if (_activeTerminalId == id)
                        _activeTerminalId = _terminal.Sessions.FirstOrDefault()?.Id;
                    RenderTerminalTabs();
                    if (_activeTerminalId != null) SelectTerminalTab(_activeTerminalId);
                    else
                    {
                        TxtTerminalTranscript.Text = string.Empty;
                        TxtTerminalStatus.Text = "No session";
                        TxtTerminalCwd.Text = "Terminal Stream";
                    }
                };
                sp.Children.Add(dot);
                sp.Children.Add(titleBtn);
                sp.Children.Add(closeBtn);
                panel.Child = sp;
                TerminalTabsBar.Children.Add(panel);
            }
        }

        private void SelectTerminalTab(string sessionId)
        {
            _activeTerminalId = sessionId;
            var info = _terminal.Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (info != null)
            {
                TxtTerminalTranscript.Text = _terminal.GetTranscript(sessionId);
                TxtTerminalTranscript.ScrollToEnd();
                string mode = info.UsesConPty ? "ConPTY" : "Pipe";
                string state = info.IsRunning ? "Running" : $"Exited {info.ExitCode}";
                TxtTerminalStatus.Text = $"{info.Shell} · {mode} · {state}";
                TxtTerminalCwd.Text = $"{info.WorkingDirectory}  ({mode})";
                TerminalStatusDot.Fill = info.IsRunning ? Brush("BrushSuccess", "#22c55e") : Brush("BrushDanger", "#ef4444");
            }
            RenderTerminalTabs();
        }

        private async void BtnTerminalQuickCmd_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string cmd) return;
            EnsureTerminalStarted();
            if (_activeTerminalId == null) return;
            await _terminal.SendInputAsync(_activeTerminalId, cmd);
        }

        private void OnTerminalOutputReceived(string sessionId, string chunk)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (sessionId == _activeTerminalId)
                {
                    TxtTerminalTranscript.Text = _terminal.GetTranscript(sessionId);
                    TxtTerminalTranscript.ScrollToEnd();
                }
            });
        }

        private void OnTerminalSessionStateChanged(TerminalSessionInfo info)
        {
            Dispatcher.BeginInvoke(() =>
            {
                RenderTerminalTabs();
                if (info.Id == _activeTerminalId) SelectTerminalTab(info.Id);
            });
        }

        private async void BtnTerminalSend_Click(object sender, RoutedEventArgs e)
            => await SendTerminalCommandAsync();

        private async void TxtTerminalInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SendTerminalCommandAsync();
                return;
            }

            var info = _terminal.Sessions.FirstOrDefault(s => s.Id == _activeTerminalId);
            if (info == null || info.CommandHistory.Count == 0) return;

            if (e.Key == Key.Up)
            {
                e.Handled = true;
                _terminalHistoryIdx = _terminalHistoryIdx < 0 ? info.CommandHistory.Count - 1 : Math.Max(0, _terminalHistoryIdx - 1);
                TxtTerminalInput.Text = info.CommandHistory[_terminalHistoryIdx];
                TxtTerminalInput.CaretIndex = TxtTerminalInput.Text.Length;
            }
            else if (e.Key == Key.Down)
            {
                e.Handled = true;
                if (_terminalHistoryIdx < 0) return;
                _terminalHistoryIdx++;
                if (_terminalHistoryIdx >= info.CommandHistory.Count)
                {
                    _terminalHistoryIdx = -1;
                    TxtTerminalInput.Text = string.Empty;
                }
                else
                {
                    TxtTerminalInput.Text = info.CommandHistory[_terminalHistoryIdx];
                    TxtTerminalInput.CaretIndex = TxtTerminalInput.Text.Length;
                }
            }
        }

        private async Task SendTerminalCommandAsync()
        {
            EnsureTerminalStarted();
            if (_activeTerminalId == null) return;
            string cmd = TxtTerminalInput.Text;
            TxtTerminalInput.Text = string.Empty;
            _terminalHistoryIdx = -1;

            if (!string.IsNullOrWhiteSpace(cmd))
            {
                _settings.RecentCommands.RemoveAll(c => string.Equals(c, cmd.Trim(), StringComparison.OrdinalIgnoreCase));
                _settings.RecentCommands.Insert(0, cmd.Trim());
                if (_settings.RecentCommands.Count > 20) _settings.RecentCommands.RemoveRange(20, _settings.RecentCommands.Count - 20);
                _settingsService?.SaveSettings(_settings);
            }

            await _terminal.SendInputAsync(_activeTerminalId, cmd);
        }

        private void BtnTerminalInterrupt_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTerminalId != null) _terminal.Interrupt(_activeTerminalId);
        }

        private void BtnTerminalClear_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTerminalId != null) _terminal.ClearOutput(_activeTerminalId);
        }

        private static Dictionary<string, string>? ParseEnvVars(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) dict[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
            return dict.Count > 0 ? dict : null;
        }

        // =================== TOOL 4: EMBEDDED BROWSER (WebView2) ===================
        public async Task EnsureBrowserInitializedAsync()
        {
            if (_activeBrowserTab == null)
            {
                _activeBrowserTab = _browser.OpenTab(_browser.HomeUrl);
                RenderBrowserTabs();
            }

            if (_webViewInitialized || _webViewInitAttempted) return;
            _webViewInitAttempted = true;

            try
            {
                string udf = Path.Combine(AppConstants.PersistentUserRoot, "WebView2Data");
                Directory.CreateDirectory(udf);
                var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: udf, options: null);
                await EmbeddedWebView.EnsureCoreWebView2Async(env);

                EmbeddedWebView.CoreWebView2.NavigationCompleted += (_, args) =>
                {
                    string currentUrl = EmbeddedWebView.Source?.AbsoluteUri ?? string.Empty;
                    string title = EmbeddedWebView.CoreWebView2.DocumentTitle;
                    if (_activeBrowserTab != null && !string.IsNullOrWhiteSpace(currentUrl))
                    {
                        _browser.RecordNavigation(_activeBrowserTab, currentUrl, title);
                        TxtBrowserUrl.Text = currentUrl;
                        RenderBrowserTabs();
                        RefreshBrowserDrawerList();
                    }
                    TxtBrowserStatus.Text = args.IsSuccess ? $"Loaded: {currentUrl}" : $"Navigation error: {args.WebErrorStatus}";
                };

                EmbeddedWebView.CoreWebView2.DownloadStarting += (_, args) =>
                {
                    if (!_browser.AllowDownloads)
                    {
                        args.Cancel = true;
                        TxtBrowserStatus.Text = "Download blocked by Browser Settings.";
                        return;
                    }
                    _browser.AddDownload(new BrowserDownloadEntry
                    {
                        FileName = Path.GetFileName(args.ResultFilePath),
                        TargetPath = args.ResultFilePath,
                        Url = args.DownloadOperation.Uri,
                        Status = "Started"
                    });
                    RefreshBrowserDrawerList();
                };

                EmbeddedWebView.CoreWebView2.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                    _activeBrowserTab = _browser.OpenTab(args.Uri);
                    RenderBrowserTabs();
                    _ = NavigateBrowserAsync(args.Uri);
                };

                _webViewInitialized = true;
                TxtWebViewFallbackStatus.Visibility = Visibility.Collapsed;
                await NavigateBrowserAsync(_activeBrowserTab.Url);
            }
            catch (Exception ex)
            {
                TxtWebViewFallbackStatus.Text = $"WebView2 Runtime unavailable or blocked:\n{ex.Message}\nUse External Link (↗) or install Evergreen WebView2 Runtime.";
                TxtWebViewFallbackStatus.Visibility = Visibility.Visible;
                TxtBrowserStatus.Text = "WebView2 unavailable";
            }
        }

        public async Task NavigateBrowserAsync(string rawInput)
        {
            await EnsureBrowserInitializedAsync();
            string url = BrowserSessionService.NormalizeInputToUrl(rawInput);
            TxtBrowserUrl.Text = url;

            if (_activeBrowserTab == null)
                _activeBrowserTab = _browser.OpenTab(url);
            else
                _browser.RecordNavigation(_activeBrowserTab, url);

            RenderBrowserTabs();
            RefreshBrowserDrawerList();

            if (_webViewInitialized && EmbeddedWebView.CoreWebView2 != null)
            {
                EmbeddedWebView.CoreWebView2.Navigate(url);
            }
        }

        private void RenderBrowserTabs()
        {
            BrowserTabsBar.Children.Clear();
            foreach (var tab in _browser.Tabs)
            {
                var panel = new Border
                {
                    Background = _activeBrowserTab?.Id == tab.Id ? Brush("BrushCardBg", "#1f2937") : Brushes.Transparent,
                    BorderBrush = Brush("BrushBorder", "#374151"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Margin = new Thickness(0, 0, 4, 0),
                    Padding = new Thickness(6, 2, 4, 2)
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                var btn = new Button
                {
                    Content = tab.Title,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = Brush("BrushTextPrimary", "#fafafa"),
                    FontSize = 10.5,
                    MaxWidth = 110,
                    Cursor = Cursors.Hand
                };
                var captured = tab;
                btn.Click += (_, _) =>
                {
                    _activeBrowserTab = captured;
                    TxtBrowserUrl.Text = captured.Url;
                    RenderBrowserTabs();
                    if (_webViewInitialized && EmbeddedWebView.CoreWebView2 != null)
                        EmbeddedWebView.CoreWebView2.Navigate(captured.Url);
                };
                sp.Children.Add(btn);
                if (_browser.Tabs.Count > 1)
                {
                    var close = new Button
                    {
                        Content = "✕",
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Foreground = Brush("BrushTextDim", "#9ca3af"),
                        FontSize = 9,
                        Margin = new Thickness(4, 0, 0, 0),
                        Cursor = Cursors.Hand
                    };
                    close.Click += (_, _) =>
                    {
                        if (_browser.CloseTab(captured.Id))
                        {
                            if (_activeBrowserTab?.Id == captured.Id)
                                _activeBrowserTab = _browser.Tabs.LastOrDefault();
                            RenderBrowserTabs();
                        }
                    };
                    sp.Children.Add(close);
                }
                panel.Child = sp;
                BrowserTabsBar.Children.Add(panel);
            }
        }

        private void BtnBrowserNewTab_Click(object sender, RoutedEventArgs e)
        {
            _activeBrowserTab = _browser.OpenTab(_browser.HomeUrl);
            RenderBrowserTabs();
            _ = NavigateBrowserAsync(_activeBrowserTab.Url);
        }

        private void BtnBrowserBack_Click(object sender, RoutedEventArgs e)
        {
            if (_webViewInitialized && EmbeddedWebView.CanGoBack) EmbeddedWebView.GoBack();
            else if (_activeBrowserTab != null && _browser.GoBack(_activeBrowserTab) is string url) _ = NavigateBrowserAsync(url);
        }

        private void BtnBrowserForward_Click(object sender, RoutedEventArgs e)
        {
            if (_webViewInitialized && EmbeddedWebView.CanGoForward) EmbeddedWebView.GoForward();
            else if (_activeBrowserTab != null && _browser.GoForward(_activeBrowserTab) is string url) _ = NavigateBrowserAsync(url);
        }

        private void BtnBrowserReload_Click(object sender, RoutedEventArgs e)
        {
            if (_webViewInitialized) EmbeddedWebView.Reload();
        }

        private void BtnBrowserHome_Click(object sender, RoutedEventArgs e)
            => _ = NavigateBrowserAsync(_browser.HomeUrl);

        private void BtnBrowserGo_Click(object sender, RoutedEventArgs e)
            => _ = NavigateBrowserAsync(TxtBrowserUrl.Text);

        private void TxtBrowserUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; _ = NavigateBrowserAsync(TxtBrowserUrl.Text); }
        }

        private void BtnBrowserOpenExternal_Click(object sender, RoutedEventArgs e)
        {
            string url = BrowserSessionService.NormalizeInputToUrl(TxtBrowserUrl.Text);
            if (WebLink.TryParse(url, out var safe) && safe != null)
            {
                try { Process.Start(new ProcessStartInfo(safe.AbsoluteUri) { UseShellExecute = true }); } catch { }
            }
        }

        private void BtnBrowserViewport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string mode) return;
            if (mode == "390")
            {
                BrowserViewportFrame.Width = 390;
                BrowserViewportFrame.HorizontalAlignment = HorizontalAlignment.Center;
            }
            else if (mode == "1024")
            {
                BrowserViewportFrame.Width = 1024;
                BrowserViewportFrame.HorizontalAlignment = HorizontalAlignment.Center;
            }
            else
            {
                BrowserViewportFrame.Width = double.NaN;
                BrowserViewportFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            var active = TryFindResource("PrimaryActionBtn") as Style;
            var normal = TryFindResource("BlackBoxActionBtn") as Style;
            if (active != null && normal != null)
            {
                BtnBrowserSizeAuto.Style = mode == "Auto" ? active : normal;
                BtnBrowserSizeDesktop.Style = mode == "1024" ? active : normal;
                BtnBrowserSizeMobile.Style = mode == "390" ? active : normal;
            }
        }

        private void BtnBrowserSettings_Click(object sender, RoutedEventArgs e)
        {
            BrowserSettingsDrawer.Visibility = BrowserSettingsDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            RefreshBrowserDrawerList();
        }

        private void BrowserPermission_Changed(object sender, RoutedEventArgs e)
        {
            _browser.AllowDownloads = ChkBrowserAllowDownloads.IsChecked == true;
            _browser.AllowAiAutomation = ChkBrowserAllowAutomation.IsChecked == true;
            _settings.BrowserAllowDownloads = _browser.AllowDownloads;
            _settings.BrowserAllowAutomation = _browser.AllowAiAutomation;
            _settingsService?.SaveSettings(_settings);
        }

        private void TxtBrowserHomeUrl_LostFocus(object sender, RoutedEventArgs e)
        {
            string h = BrowserSessionService.NormalizeInputToUrl(TxtBrowserHomeUrl.Text);
            _browser.HomeUrl = h;
            _settings.BrowserHomeUrl = h;
            _settingsService?.SaveSettings(_settings);
        }

        private void RefreshBrowserDrawerList()
        {
            var lines = new List<string>();
            foreach (var d in _browser.Downloads.Take(8))
                lines.Add($"[DL] {d.FileName} ({d.Status})");
            foreach (var h in _browser.History.Take(12))
                lines.Add($"[HIST] {h.Title} — {h.Url}");
            BrowserHistoryAndDownloadsList.ItemsSource = lines;
        }

        private void BrowserHistoryItem_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (BrowserHistoryAndDownloadsList.SelectedItem is string s && s.Contains(" — "))
            {
                string url = s.Split(" — ", 2)[1];
                _ = NavigateBrowserAsync(url);
            }
        }

        // =================== TOOL 5: ISOLATED SIDE QUESTION ===================
        private void RefreshSideModelList()
        {
            var models = _getModels?.Invoke() ?? new List<ModelItem>();
            CmbSideModel.ItemsSource = models;
            if (CmbSideModel.SelectedItem == null)
            {
                var current = _getActiveModel?.Invoke() ?? models.FirstOrDefault();
                if (current != null) CmbSideModel.SelectedItem = current;
            }
            if (SideMessagesList.ItemsSource != _sideMessages) SideMessagesList.ItemsSource = _sideMessages;
        }

        private void BtnSideClear_Click(object sender, RoutedEventArgs e)
        {
            _sideMessages.Clear();
            _sideTempAttachments.Clear();
            TxtSideAttachments.Visibility = Visibility.Collapsed;
        }

        private void BtnSideAttachFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Attach temporary context to Side Question" };
            if (dlg.ShowDialog() == true)
            {
                _sideTempAttachments.Add(dlg.FileName);
                TxtSideAttachments.Text = "Temp attachments: " + string.Join(", ", _sideTempAttachments.Select(Path.GetFileName));
                TxtSideAttachments.Visibility = Visibility.Visible;
            }
        }

        private void TxtSidePrompt_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                BtnSideSend_Click(sender, new RoutedEventArgs());
            }
        }

        private async void BtnSideSend_Click(object sender, RoutedEventArgs e)
        {
            if (_sideBusy || _runtime == null) return;
            string question = (TxtSidePrompt.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(question)) return;

            var model = CmbSideModel.SelectedItem as ModelItem ?? _getActiveModel?.Invoke();
            if (model == null) return;

            TxtSidePrompt.Text = string.Empty;
            _sideMessages.Add(new ChatMessageItem
            {
                SenderRole = "User",
                SenderName = "Side Q",
                Content = question,
                Timestamp = DateTime.Now.ToString("HH:mm")
            });

            var sbPrompt = new StringBuilder(question);
            if (ChkSideIncludeFile.IsChecked == true)
            {
                string? openPath = _getOpenFilePath?.Invoke();
                string openText = _getOpenEditorText?.Invoke() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(openText))
                {
                    if (openText.Length > 8000) openText = openText.Substring(0, 8000) + "\n...[truncated]";
                    sbPrompt.AppendLine($"\n\n[Open File Context: {Path.GetFileName(openPath ?? "editor")}]\n```\n{openText}\n```");
                }
            }
            foreach (var path in _sideTempAttachments)
            {
                try
                {
                    string txt = File.ReadAllText(path);
                    if (txt.Length > 4000) txt = txt.Substring(0, 4000) + "\n...[truncated]";
                    sbPrompt.AppendLine($"\n\n[Temporary Attachment: {Path.GetFileName(path)}]\n```\n{txt}\n```");
                }
                catch { }
            }

            // Build history exclusively from _sideMessages (never touches primary chat or main ProjectKey persistence)
            var sideHistory = new List<AiMessage>();
            for (int i = 0; i < _sideMessages.Count - 1; i++)
            {
                var m = _sideMessages[i];
                if (m.IsUser) sideHistory.Add(AiMessage.User(m.Content));
                else if (m.IsAssistant && !m.IsError) sideHistory.Add(AiMessage.Assistant(m.Content));
            }

            var assistantMsg = new ChatMessageItem
            {
                SenderRole = "Assistant",
                SenderName = model.Name,
                ModelName = model.Name,
                Provider = model.Provider,
                ActivityState = MessageActivityState.Thinking,
                ActivityDetail = $"Side Q via {model.Name}...",
                IsStreaming = true,
                Timestamp = DateTime.Now.ToString("HH:mm")
            };
            _sideMessages.Add(assistantMsg);
            SideChatScroller.ScrollToEnd();

            _sideBusy = true;
            BtnSideSend.IsEnabled = false;
            try
            {
                var req = new ContextRequest
                {
                    Model = model,
                    Settings = _settings,
                    UserPrompt = sbPrompt.ToString(),
                    History = sideHistory,
                    // Deliberately omit ProjectKey so Side Question does not mutate persistent workspace memory
                    ProjectKey = null
                };
                var resp = await _runtime.RunWithProgressAsync(req, evt =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        assistantMsg.ActivityState = evt.State;
                        if (!string.IsNullOrEmpty(evt.TokenDelta))
                            assistantMsg.Content += evt.TokenDelta;
                    });
                }, CancellationToken.None);

                string finalText = ReasoningFilter.Split(resp.IsError ? (resp.ErrorMessage ?? resp.Text) : resp.Text, out _);
                assistantMsg.IsStreaming = false;
                assistantMsg.IsError = resp.IsError;
                assistantMsg.ActivityState = resp.IsError ? MessageActivityState.Failed : MessageActivityState.Completed;
                assistantMsg.Content = finalText;
                assistantMsg.NotifyBlocksUpdated();
            }
            catch (Exception ex)
            {
                assistantMsg.IsStreaming = false;
                assistantMsg.IsError = true;
                assistantMsg.ActivityState = MessageActivityState.Failed;
                assistantMsg.Content = $"[ERROR] {ex.Message}";
                assistantMsg.NotifyBlocksUpdated();
            }
            finally
            {
                _sideBusy = false;
                BtnSideSend.IsEnabled = true;
                SideChatScroller.ScrollToEnd();
            }
        }

        private void BtnSideCopyAnswer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ChatMessageItem item)
            {
                try { Clipboard.SetText(item.Content ?? string.Empty); } catch { }
            }
        }

        private void BtnSidePromoteAnswer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ChatMessageItem item && !string.IsNullOrWhiteSpace(item.Content))
            {
                SendToMainChatRequested?.Invoke($"[Side Question Insight]\n{item.Content}");
            }
        }

        // =================== TOOL 6: PLUGINS MANAGER ===================
        private void BtnPluginsReload_Click(object sender, RoutedEventArgs e) => _plugins.ReloadCatalog();

        private void RenderPluginsView()
        {
            PluginsListHost.Children.Clear();
            foreach (var rec in _plugins.GetAllPlugins())
            {
                var card = new Border
                {
                    Background = Brush("BrushCardBg", "#111114"),
                    BorderBrush = Brush("BrushBorder", "#27272a"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(10),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var sp = new StackPanel();
                var top = new Grid();
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var titleStack = new StackPanel();
                string ver = rec.HasUpdate ? $"v{rec.Manifest.Version} (Update {rec.Manifest.LatestAvailableVersion})" : $"v{rec.Manifest.Version}";
                titleStack.Children.Add(new TextBlock { Text = $"{rec.Manifest.Name}  [{ver}]", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("BrushTextPrimary", "#fafafa") });
                titleStack.Children.Add(new TextBlock { Text = rec.Manifest.Description, FontSize = 10.5, Foreground = Brush("BrushTextSecondary", "#a1a1aa"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetColumn(titleStack, 0);
                top.Children.Add(titleStack);

                var toggleBtn = new Button
                {
                    Content = rec.State.IsEnabled ? "Enabled ✓" : (rec.State.IsInstalled ? "Enable" : "Install"),
                    Style = TryFindResource(rec.State.IsEnabled ? "PrimaryActionBtn" : "BlackBoxActionBtn") as Style,
                    Padding = new Thickness(9, 3, 9, 3),
                    FontSize = 10,
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Top
                };
                string pid = rec.Manifest.Id;
                toggleBtn.Click += (_, _) =>
                {
                    if (!rec.State.IsInstalled)
                    {
                        _plugins.InstallFromCatalog(pid);
                        return;
                    }
                    if (!_plugins.TrySetEnabled(pid, !rec.State.IsEnabled, out string err))
                    {
                        MessageBox.Show(err, "Plugin Security Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };
                Grid.SetColumn(toggleBtn, 1);
                top.Children.Add(toggleBtn);
                sp.Children.Add(top);

                if (!rec.Manifest.IsBuiltIn)
                {
                    var trustCb = new CheckBox
                    {
                        Content = $"Explicitly Trust External Binary ({rec.Manifest.EntryAssembly})",
                        IsChecked = rec.State.IsTrusted,
                        FontSize = 10,
                        Foreground = Brush("BrushTextSecondary", "#fbbf24"),
                        Margin = new Thickness(0, 5, 0, 0)
                    };
                    trustCb.Checked += (_, _) => _plugins.SetTrust(pid, true);
                    trustCb.Unchecked += (_, _) => _plugins.SetTrust(pid, false);
                    sp.Children.Add(trustCb);
                }

                if (rec.Manifest.RequestedPermissions.Count > 0)
                {
                    var permWrap = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
                    foreach (var p in rec.Manifest.RequestedPermissions)
                    {
                        var perm = p;
                        var cb = new CheckBox
                        {
                            Content = perm.ToString(),
                            IsChecked = rec.State.GrantedPermissions.Contains(perm),
                            FontSize = 10,
                            Foreground = Brush("BrushTextSecondary", "#d4d4d8"),
                            Margin = new Thickness(0, 0, 10, 2)
                        };
                        cb.Checked += (_, _) => _plugins.SetPermission(pid, perm, true);
                        cb.Unchecked += (_, _) => _plugins.SetPermission(pid, perm, false);
                        permWrap.Children.Add(cb);
                    }
                    sp.Children.Add(permWrap);
                }

                if (rec.Manifest.Tools.Count > 0 || rec.Manifest.Commands.Count > 0)
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"Tools: {string.Join(", ", rec.Manifest.Tools)}  |  Commands: {rec.Manifest.Commands.Count}",
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 9.5,
                        Foreground = Brush("BrushTextDim", "#71717a"),
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                }

                card.Child = sp;
                PluginsListHost.Children.Add(card);
            }

            PluginLogList.ItemsSource = _plugins.GetActivityLog()
                .Select(l => $"[{l.TimestampUtc:HH:mm:ss}] [{l.Level}] {l.PluginId}: {l.Message}")
                .ToList();
        }

        // =================== ADDITIONAL UTILITY VIEWS ===================
        private void BtnUtilityRefresh_Click(object sender, RoutedEventArgs e) => RefreshUtilityView();

        private void RefreshUtilityView()
        {
            UtilityContentHost.Children.Clear();
            TxtUtilityViewTitle.Text = _activeTool switch
            {
                "CodeSearch" => "Code Search",
                "Problems" => "Problems & Diagnostics",
                "RunTest" => "Run & Test",
                "SourceControl" => "Source Control (Git)",
                "Memory" => "Memory Inspector",
                "Context" => "Context Inspector",
                "Activity" => "Agent Activity",
                "ModelUsage" => "Model Usage",
                "ProjectInfo" => "Project Info & Telemetry",
                "ExplorerShortcuts" => "Project Explorer Shortcuts",
                _ => _activeTool
            };

            switch (_activeTool)
            {
                case "CodeSearch": BuildCodeSearchView(); break;
                case "Problems": BuildProblemsView(); break;
                case "RunTest": BuildRunAndTestView(); break;
                case "SourceControl": _ = BuildSourceControlViewAsync(); break;
                case "Memory": BuildMemoryInspectorView(); break;
                case "Context": BuildContextInspectorView(); break;
                case "Activity": BuildActivityView(); break;
                case "ModelUsage": BuildModelUsageView(); break;
                case "ProjectInfo": BuildProjectInfoView(); break;
                case "ExplorerShortcuts": BuildExplorerShortcutsView(); break;
            }
        }

        private void BuildCodeSearchView()
        {
            var box = new TextBox { Padding = new Thickness(7, 5, 7, 5), FontSize = 11.5, Background = Brush("BrushCardInnerBg", "#18181b"), Foreground = Brush("BrushTextPrimary", "#fafafa") };
            var btn = new Button { Content = "Search Workspace", Style = TryFindResource("PrimaryActionBtn") as Style, Margin = new Thickness(0, 6, 0, 8), Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand };
            var results = new StackPanel();
            btn.Click += (_, _) =>
            {
                results.Children.Clear();
                string term = (box.Text ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(_workspaceRoot)) return;

                int matches = 0;
                foreach (var rel in _workspaceFiles.Take(250))
                {
                    string full = ResolveFullPath(rel);
                    try
                    {
                        if (!File.Exists(full) || new FileInfo(full).Length > 512_000) continue;
                        int ln = 0;
                        foreach (var line in File.ReadLines(full))
                        {
                            ln++;
                            if (line.Contains(term, StringComparison.OrdinalIgnoreCase))
                            {
                                string captured = full;
                                int capturedLine = ln;
                                var rowBtn = new Button
                                {
                                    Content = $"{rel}:{ln}  {line.Trim()}",
                                    HorizontalContentAlignment = HorizontalAlignment.Left,
                                    Background = Brushes.Transparent,
                                    BorderThickness = new Thickness(0),
                                    FontFamily = new FontFamily("Consolas"),
                                    FontSize = 10.5,
                                    Foreground = Brush("BrushAccentLight", "#93c5fd"),
                                    Cursor = Cursors.Hand,
                                    Margin = new Thickness(0, 2, 0, 2)
                                };
                                rowBtn.Click += (_, _) =>
                                {
                                    OpenWorkspacePath(captured);
                                    OpenFileAtLocationRequested?.Invoke(captured, capturedLine, 1);
                                };
                                results.Children.Add(rowBtn);
                                if (++matches >= 60) break;
                            }
                        }
                    }
                    catch { }
                    if (matches >= 60) break;
                }
                if (matches == 0) results.Children.Add(new TextBlock { Text = "No matches found.", Foreground = Brush("BrushTextDim", "#71717a") });
            };
            UtilityContentHost.Children.Add(box);
            UtilityContentHost.Children.Add(btn);
            UtilityContentHost.Children.Add(results);
        }

        private void BuildProblemsView()
        {
            if (_lastBuildResult != null)
            {
                var diags = LlmodelBox.Infrastructure.Editor.EditorDiagnosticsHelper.ParseBuildOutput(_lastBuildResult.Output);
                BuildDiagnosticsUpdated?.Invoke(diags);
                if (diags.Count > 0)
                {
                    foreach (var d in diags.Take(60))
                    {
                        var captured = d;
                        var diagBtn = new Button
                        {
                            Content = $"{Path.GetFileName(d.FilePath)}({d.Line},{d.Column}): {d.Severity} {d.Code}: {d.Message}",
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Background = Brushes.Transparent,
                            BorderThickness = new Thickness(0),
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 10.5,
                            Foreground = d.Severity == LlmodelBox.Infrastructure.Editor.DiagnosticSeverity.Error ? Brushes.IndianRed : Brushes.Khaki,
                            Cursor = Cursors.Hand,
                            Margin = new Thickness(0, 2, 0, 2)
                        };
                        diagBtn.Click += (_, _) => OpenFileAtLocationRequested?.Invoke(ResolveFullPath(captured.FilePath), captured.Line, captured.Column);
                        UtilityContentHost.Children.Add(diagBtn);
                    }
                    return;
                }

                var rawLines = _lastBuildResult.Output.Replace("\r\n", "\n").Split('\n')
                    .Where(l => l.Contains(": error ", StringComparison.OrdinalIgnoreCase) || l.Contains(": warning ", StringComparison.OrdinalIgnoreCase))
                    .Take(50).ToList();
                if (rawLines.Count > 0)
                {
                    foreach (var l in rawLines)
                        UtilityContentHost.Children.Add(new TextBlock { Text = l, FontFamily = new FontFamily("Consolas"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Foreground = l.Contains("error") ? Brushes.IndianRed : Brushes.Khaki, Margin = new Thickness(0, 2, 0, 2) });
                    return;
                }
            }
            UtilityContentHost.Children.Add(new TextBlock { Text = "No build diagnostics recorded yet. Run a build from Run & Test.", Foreground = Brush("BrushTextSecondary", "#a1a1aa"), TextWrapping = TextWrapping.Wrap });
        }

        private void BuildRunAndTestView()
        {
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            var outBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10.5,
                Background = Brush("BrushCardInnerBg", "#141418"),
                Foreground = Brush("BrushTextPrimary", "#e4e4e7"),
                Padding = new Thickness(8),
                MinHeight = 220,
                Text = _lastBuildResult?.Output ?? "Click Build or Test to execute inside workspace root."
            };
            var bBuild = new Button { Content = "▶ dotnet build", Style = TryFindResource("PrimaryActionBtn") as Style, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(10, 5, 10, 5), Cursor = Cursors.Hand };
            var bTest = new Button { Content = "🧪 dotnet test", Style = TryFindResource("BlackBoxActionBtn") as Style, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(10, 5, 10, 5), Cursor = Cursors.Hand };

            bBuild.Click += async (_, _) =>
            {
                outBox.Text = "Running dotnet build...";
                _lastBuildResult = await _buildRunner.RunCommandAsync(_workspaceRoot, "dotnet", "build");
                outBox.Text = $"Exit: {_lastBuildResult.ExitCode} ({_lastBuildResult.Duration.TotalSeconds:F1}s)\n\n{_lastBuildResult.Output}";
                BuildDiagnosticsUpdated?.Invoke(LlmodelBox.Infrastructure.Editor.EditorDiagnosticsHelper.ParseBuildOutput(_lastBuildResult.Output));
            };
            bTest.Click += async (_, _) =>
            {
                outBox.Text = "Running dotnet test...";
                _lastBuildResult = await _buildRunner.RunCommandAsync(_workspaceRoot, "dotnet", "test");
                outBox.Text = $"Exit: {_lastBuildResult.ExitCode} ({_lastBuildResult.Duration.TotalSeconds:F1}s)\n\n{_lastBuildResult.Output}";
                BuildDiagnosticsUpdated?.Invoke(LlmodelBox.Infrastructure.Editor.EditorDiagnosticsHelper.ParseBuildOutput(_lastBuildResult.Output));
            };

            bar.Children.Add(bBuild);
            bar.Children.Add(bTest);
            UtilityContentHost.Children.Add(bar);
            UtilityContentHost.Children.Add(outBox);
        }

        private async Task BuildSourceControlViewAsync()
        {
            if (string.IsNullOrWhiteSpace(_workspaceRoot)) return;
            var status = await _git.GetStatusAsync(_workspaceRoot);
            if (!status.IsGitRepository)
            {
                UtilityContentHost.Children.Add(new TextBlock { Text = "Current workspace is not a Git repository.", Foreground = Brush("BrushTextDim", "#71717a") });
                return;
            }
            UtilityContentHost.Children.Add(new TextBlock { Text = $"Branch: {status.BranchName}  •  {status.Changes.Count} changed file(s)", FontWeight = FontWeights.SemiBold, Foreground = Brush("BrushAccentLight", "#93c5fd"), Margin = new Thickness(0, 0, 0, 8) });
            foreach (var ch in status.Changes)
            {
                var c = ch;
                var btn = new Button
                {
                    Content = $"[{c.StatusLabel}]  {c.RelativePath}",
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = Brush("BrushTextPrimary", "#fafafa"),
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                btn.Click += (_, _) => OpenWorkspacePath(c.RelativePath);
                UtilityContentHost.Children.Add(btn);
            }
        }

        private void BuildMemoryInspectorView()
        {
            if (string.IsNullOrWhiteSpace(_workspaceRoot)) return;
            var mem = _memoryStore.Load(_workspaceRoot);
            UtilityContentHost.Children.Add(new TextBlock { Text = $"Session Goal: {(string.IsNullOrWhiteSpace(mem.SessionGoal) ? "(none)" : mem.SessionGoal)}", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Foreground = Brush("BrushAccentLight", "#93c5fd"), Margin = new Thickness(0, 0, 0, 6) });
            UtilityContentHost.Children.Add(new TextBlock { Text = $"Facts ({mem.Facts.Count})  |  Turns: {mem.TurnCount}", FontWeight = FontWeights.SemiBold, Foreground = Brush("BrushTextPrimary", "#fafafa"), Margin = new Thickness(0, 0, 0, 6) });
            for (int i = 0; i < mem.Facts.Count; i++)
            {
                int idx = i;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var del = new Button { Content = "✕", Padding = new Thickness(5, 1, 5, 1), FontSize = 9.5 };
                DockPanel.SetDock(del, Dock.Right);
                del.Click += (_, _) => { mem.Facts.RemoveAt(idx); _memoryStore.Save(_workspaceRoot, mem); BuildMemoryInspectorView(); };
                row.Children.Add(del);
                row.Children.Add(new TextBlock { Text = "• " + mem.Facts[i], TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("BrushTextSecondary", "#d4d4d8") });
                UtilityContentHost.Children.Add(row);
            }
        }

        private void BuildContextInspectorView()
        {
            string? openPath = _getOpenFilePath?.Invoke();
            string editorText = _getOpenEditorText?.Invoke() ?? string.Empty;
            var model = _getActiveModel?.Invoke();
            int bufTokens = TokenHelper.CountTokens(editorText);
            int sysTokens = _settings.SystemInstructionEnabled ? TokenHelper.CountTokens(_settings.SystemInstruction) : 0;

            var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 2, Columns = 2 };
            grid.Children.Add(CreateMetricBentoCard("MODEL", model?.Name ?? "None", model?.Context ?? "?", new Thickness(0, 0, 6, 6)));
            grid.Children.Add(CreateMetricBentoCard("OPEN BUFFER", $"{bufTokens:N0} tok", Path.GetFileName(openPath ?? "(none)"), new Thickness(0, 0, 0, 6)));
            grid.Children.Add(CreateMetricBentoCard("SYSTEM PROMPT", $"{sysTokens:N0} tok", _settings.SystemInstructionEnabled ? "Enabled" : "Disabled", new Thickness(0, 0, 6, 0)));
            grid.Children.Add(CreateMetricBentoCard("FILES", _workspaceFiles.Count.ToString(), "Workspace index", new Thickness(0)));
            UtilityContentHost.Children.Add(grid);
        }

        private void BuildActivityView()
        {
            if (_realtimeEvents != null)
            {
                foreach (var ev in _realtimeEvents.GetRecentEvents(25).Reverse())
                    UtilityContentHost.Children.Add(new TextBlock { Text = $"#{ev.Sequence} [{ev.State}] {ev.Detail}", FontFamily = new FontFamily("Consolas"), FontSize = 10, Foreground = Brush("BrushAccentLight", "#93c5fd"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
            }
            lock (_activityEntries)
            {
                foreach (var a in _activityEntries.Take(40))
                    UtilityContentHost.Children.Add(new TextBlock { Text = a, FontFamily = new FontFamily("Consolas"), FontSize = 10, Foreground = Brush("BrushTextSecondary", "#a1a1aa"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
            }
        }

        private void BuildModelUsageView()
        {
            if (_tokenUsageService == null) return;
            var summary = _tokenUsageService.GetSummary();
            var kpiGrid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(0, 0, 0, 8) };
            kpiGrid.Children.Add(CreateMetricBentoCard("TOTAL", summary.TotalTokens.ToString("N0"), $"{summary.CallCount} calls", new Thickness(0, 0, 5, 0)));
            kpiGrid.Children.Add(CreateMetricBentoCard("INPUT", summary.PromptTokens.ToString("N0"), "Prompt tokens", new Thickness(0, 0, 5, 0)));
            kpiGrid.Children.Add(CreateMetricBentoCard("OUTPUT", summary.CompletionTokens.ToString("N0"), "Completion", new Thickness(0)));
            UtilityContentHost.Children.Add(kpiGrid);

            foreach (var r in summary.TopModels)
            {
                UtilityContentHost.Children.Add(CreateMetricBentoCard("MODEL", r.Label, $"{r.Tokens:N0} tokens · {r.Calls} calls", new Thickness(0, 0, 0, 6)));
            }
        }

        private void BuildProjectInfoView()
        {
            UtilityContentHost.Children.Add(CreateMetricBentoCard("WORKSPACE", Path.GetFileName(_workspaceRoot), _workspaceRoot, new Thickness(0, 0, 0, 8)));
            var row = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 2 };
            row.Children.Add(CreateMetricBentoCard("FILES", _workspaceFiles.Count.ToString(), "Indexed files", new Thickness(0, 0, 6, 0)));
            row.Children.Add(CreateMetricBentoCard("PLUGINS", _plugins.GetActiveTools().Count.ToString(), "Active plugin tools", new Thickness(0)));
            UtilityContentHost.Children.Add(row);
        }

        private void BuildExplorerShortcutsView()
        {
            void AddShortcut(string label, string actionKey)
            {
                var btn = new Button { Content = label, Style = TryFindResource("BentoTileBtn") as Style, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
                btn.Click += (_, _) => ExplorerShortcutRequested?.Invoke(actionKey);
                UtilityContentHost.Children.Add(btn);
            }
            AddShortcut("📂 Reveal Workspace in File Explorer", "RevealWorkspace");
            AddShortcut("🔄 Refresh Project File Tree", "RefreshTree");
            AddShortcut("📄 Attach Media / Artifacts", "OpenMedia");
        }

        // =================== HELPERS ===================
        private void OpenWorkspacePath(string relOrFull)
        {
            string full = ResolveFullPath(relOrFull);
            TrackRecentFile(relOrFull);
            OpenFileInEditorRequested?.Invoke(full);
        }

        private string ResolveFullPath(string relOrFull)
        {
            if (string.IsNullOrWhiteSpace(relOrFull)) return string.Empty;
            if (Path.IsPathRooted(relOrFull)) return relOrFull;
            if (!string.IsNullOrWhiteSpace(_workspaceRoot)) return Path.Combine(_workspaceRoot, relOrFull);
            return relOrFull;
        }

        private Brush Brush(string key, string fallback)
        {
            if (TryFindResource(key) is Brush b) return b;
            try
            {
                var sb = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
                sb.Freeze();
                return sb;
            }
            catch { return Brushes.Gray; }
        }

        public static RightPanelDisplayMode ParseMode(string? state) => state switch
        {
            "Collapsed" or "CollapsedRail" => RightPanelDisplayMode.CollapsedRail,
            "Hidden" => RightPanelDisplayMode.Hidden,
            _ => RightPanelDisplayMode.Expanded
        };
    }
}
