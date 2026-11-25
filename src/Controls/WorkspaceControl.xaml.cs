using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime;
using LlmodelBox.Runtime.Content;
using LlmodelBox.Runtime.Context;
using LlmodelBox.Runtime.Memory;
using LlmodelBox.Services;

namespace LlmodelBox.Controls
{
    public partial class WorkspaceControl : UserControl
    {
        private readonly ICloudApiService _cloudApiService;
        private readonly ITokenUsageService _tokenUsageService;
        private readonly UserSettings _settings;
        private readonly HttpClient _httpClient;
        private readonly Func<ModelItem?> _getActiveModel;
        private readonly Func<List<ModelItem>> _getAllModels;
        private readonly Action<ModelItem> _setActiveModel;
        private readonly IAiRuntime _runtime;
        // Optional shared registry; when supplied, project-scoped file tools are registered here on
        // each project load so the runtime's agent loop can read/list/write within the project root.
        private readonly LlmodelBox.Runtime.Tools.IToolRegistry? _toolRegistry;

        private readonly IWorkspaceService _workspaceService = new WorkspaceService();
        private ProjectItem? _activeProject;
        private List<ProjectFileItem> _projectFiles = new();
        private readonly ObservableCollection<ProjectFileItem> _visibleFileRows = new();
        private ProjectFileItem? _selectedFileRow;
        private ProjectFileItem? _selectedFolder;
        private List<ChatMessageItem> _chatMessages = new();
        private readonly ObservableCollection<ChatMessageItem> _displayedChatMessages = new();
        private readonly IChatHistoryStore _historyStore = new JsonChatHistoryStore();
        private readonly IMemoryStore _memoryStore = new JsonMemoryStore();
        private List<string> _pendingChatImages = new();
        private List<ArtifactItem> _artifacts = new();
        private List<DiffLineItem> _diffLines = new();
        private string? _currentOpenFilePath = null;
        private string _originalDiskContent = string.Empty;
        private CancellationTokenSource? _cliCts = null;
        private CancellationTokenSource? _chatCts = null;
        private bool _workspaceChatBusy = false;
        private int _activeInspectorTab = 0;

        private ModelItem? _activeModel => _getActiveModel();
        private List<ModelItem> _allModels => _getAllModels();

        public event Action? BackRequested;
        public event Action? SwitchModelRequested;

        public WorkspaceControl(ICloudApiService cloudApiService, ITokenUsageService tokenUsageService, UserSettings settings, HttpClient httpClient, Func<ModelItem?> getActiveModel, Func<List<ModelItem>> getAllModels, Action<ModelItem> setActiveModel, LlmodelBox.Runtime.IAiRuntime runtime, LlmodelBox.Runtime.Tools.IToolRegistry? toolRegistry = null)
        {
            _cloudApiService = cloudApiService;
            _tokenUsageService = tokenUsageService;
            _settings = settings;
            _httpClient = httpClient;
            _getActiveModel = getActiveModel;
            _getAllModels = getAllModels;
            _setActiveModel = setActiveModel;
            _runtime = runtime;
            _toolRegistry = toolRegistry;
            InitializeComponent();
        }

        private void SetActiveModel(ModelItem model) => _setActiveModel(model);

        public void Log(string message) => AppendLog(message);

        public void RefreshTelemetry(ModelItem? m) => UpdateContextAndTokenTelemetry(m);

        public void UpdateActiveModelBadge(ModelItem? m)
        {
            if (m == null) return;
            TxtTerminalActiveModel.Text = $"[API: {m.Name}]";
            TxtChatActiveModelBadge.Text = $"{m.Name} // Direct Cloud API";
        }

        public void LoadProject(ProjectItem project)
        {
            _activeProject = project;
            RegisterProjectTools(project);
            _chatMessages = _historyStore.Load(project.Path);
            TxtActiveProjectName.Text = project.Name;
            TxtActiveProjectPath.Text = project.Path;
            string modelName = _activeModel?.Name ?? "GPT-4o";
            TxtTerminalActiveModel.Text = $"[API: {modelName}]";
            TxtChatActiveModelBadge.Text = $"{modelName} // Direct Cloud API";
            RefreshProjectFilesUi();
            LoadInitialProjectFile();
            RefreshArtifactsList();
            InitializeProjectChat();
            UpdateInspectorInfo();
            SwitchInspectorTab(0);
            AppendLog($"[WORKSPACE] Loaded project '{project.Name}' at {project.Path}");
            AppendLog($"[ENGINE] Active AI Model: {modelName} | Gateway TLS 1.3 Active");
        }

        // (Re)registers the built-in file tools rooted at the active project directory. Register
        // replaces any prior same-named tool, so switching projects re-scopes the tools' root. Skipped
        // when no registry was injected or the project path is not a usable directory.
        private void RegisterProjectTools(ProjectItem project)
        {
            if (_toolRegistry == null) return;
            string root = project?.Path ?? string.Empty;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            _toolRegistry.Register(new LlmodelBox.Runtime.Tools.Builtin.ReadFileTool(root));
            _toolRegistry.Register(new LlmodelBox.Runtime.Tools.Builtin.ListDirectoryTool(root));
            _toolRegistry.Register(new LlmodelBox.Runtime.Tools.Builtin.WriteFileTool(root));
        }

        private void AppendLog(string message)
        {
            if (TxtCliOutput != null)
            {
                TxtCliOutput.AppendText($"{message}{Environment.NewLine}");
                CliOutputScrollViewer?.ScrollToEnd();
            }
        }

        private Color ThemeColor(string brushKey, string fallbackHex)
        {
            if (TryFindResource(brushKey) is SolidColorBrush b) return b.Color;
            return (Color)ColorConverter.ConvertFromString(fallbackHex);
        }

        private void OpenPathInExplorer(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open explorer: {ex.Message}", "Explorer Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnBackToProjects_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke();
        }

        private void BtnSwitchModel_Click(object sender, RoutedEventArgs e)
        {
            var models = _allModels;
            if (models == null || models.Count == 0)
            {
                SwitchModelRequested?.Invoke();
                return;
            }
            _suppressModelPick = true;
            ModelPickerList.ItemsSource = models;
            ModelPickerList.SelectedItem = _activeModel;
            _suppressModelPick = false;
            ModelPickerPopup.IsOpen = true;
        }

        private bool _suppressModelPick;

        private void ModelPickerList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressModelPick) return;
            if (ModelPickerList.SelectedItem is ModelItem m)
            {
                SetActiveModel(m);
                UpdateActiveModelBadge(m);
                AppendLog($"[ENGINE] Active AI Model switched to {m.Name} ({m.Provider})");
            }
            ModelPickerPopup.IsOpen = false;
        }

        // ================= SYSTEM INSTRUCTION EDITOR =================
        private void BtnSystemInstruction_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Window
            {
                Title = "System Instruction",
                Width = 620,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent
            };

            var shell = new Border
            {
                Background = new SolidColorBrush(ThemeColor("BrushCardBg", "#0a0a0d")),
                BorderBrush = new SolidColorBrush(ThemeColor("BrushBorder", "#26263a")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(22)
            };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            grid.Children.Add(BuildSysHeader(out var enabledCheck));

            // Preset selector row
            var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 8) };
            presetRow.Children.Add(new TextBlock
            {
                Text = "Preset:",
                Foreground = new SolidColorBrush(ThemeColor("BrushTextSecondary", "#a1a1aa")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            var presetCombo = new ComboBox { Width = 240, VerticalAlignment = VerticalAlignment.Center };
            if (TryFindResource("IdeComboBox") is Style cbs) presetCombo.Style = cbs;
            foreach (var p in _settings.SystemInstructionPresets) presetCombo.Items.Add(p.Name);
            presetRow.Children.Add(presetCombo);
            Grid.SetRow(presetRow, 1);
            grid.Children.Add(presetRow);

            var editorBorder = new Border
            {
                Background = new SolidColorBrush(ThemeColor("BrushCardInnerBg", "#101013")),
                BorderBrush = new SolidColorBrush(ThemeColor("BrushBorderSubtle", "#161619")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2)
            };
            var editor = new TextBox
            {
                Text = _settings.SystemInstruction,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = new SolidColorBrush(ThemeColor("BrushTextPrimary", "#fafafa")),
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 12.5,
                Padding = new Thickness(10)
            };
            editorBorder.Child = editor;
            Grid.SetRow(editorBorder, 2);
            grid.Children.Add(editorBorder);

            presetCombo.SelectionChanged += (_, __) =>
            {
                var pick = _settings.SystemInstructionPresets.FirstOrDefault(p => p.Name == (presetCombo.SelectedItem as string));
                if (pick != null) editor.Text = pick.Text;
            };

            grid.Children.Add(BuildSysButtons(dlg, editor, enabledCheck));

            shell.Child = grid;
            dlg.Content = shell;
            dlg.ShowDialog();
        }

        private UIElement BuildSysHeader(out CheckBox enabledCheck)
        {
            var header = new Grid();
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = "⚡ System Instruction",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ThemeColor("BrushTextPrimary", "#fafafa")),
                VerticalAlignment = VerticalAlignment.Center
            });
            enabledCheck = new CheckBox
            {
                Content = "Enabled",
                IsChecked = _settings.SystemInstructionEnabled,
                Foreground = new SolidColorBrush(ThemeColor("BrushTextSecondary", "#a1a1aa")),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(left);
            header.Children.Add(enabledCheck);
            Grid.SetRow(header, 0);
            return header;
        }

        private UIElement BuildSysButtons(Window dlg, TextBox editor, CheckBox enabledCheck)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var save = new Button { Content = "Save", Style = (Style)FindResource("PrimaryActionBtn"), Margin = new Thickness(0, 0, 8, 0), MinWidth = 96 };
            var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("BlackBoxActionBtn"), MinWidth = 84 };
            save.Click += (_, __) =>
            {
                _settings.SystemInstruction = editor.Text?.Trim() ?? "";
                _settings.SystemInstructionEnabled = enabledCheck.IsChecked == true;
                try { new SettingsService().SaveSettings(_settings); } catch { /* persistence best-effort */ }
                AppendLog($"[ENGINE] System instruction updated ({(_settings.SystemInstructionEnabled ? "enabled" : "disabled")}, {TokenHelper.CountTokens(_settings.SystemInstruction)} tokens).");
                dlg.DialogResult = true;
            };
            cancel.Click += (_, __) => dlg.DialogResult = false;
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 3);
            return buttons;
        }

        private void RefreshProjectFilesUi()
        {
            if (_activeProject == null || !Directory.Exists(_activeProject.Path)) return;
            try
            {
                _projectFiles = _workspaceService.GetProjectFileTree(_activeProject.Path);
                RebuildVisibleFileRows();

                long totalBytes = 0;
                int fileCount = 0;
                CountFilesRecursive(_projectFiles, ref fileCount, ref totalBytes);
                string sizeStr = totalBytes switch
                {
                    < 1024 => $"{totalBytes} B",
                    < 1024 * 1024 => $"{totalBytes / 1024.0:F1} KB",
                    _ => $"{totalBytes / (1024.0 * 1024.0):F2} MB"
                };
                TxtExplorerStats.Text = $"{fileCount} files | {sizeStr}";
                TxtInfoFilesCount.Text = $"{fileCount} files";
                TxtInfoProjectSize.Text = sizeStr;
            }
            catch (Exception ex)
            {
                TxtExplorerStats.Text = $"Error scanning files: {ex.Message}";
            }
        }

        private void LoadInitialProjectFile()
        {
            if (_projectFiles == null || _projectFiles.Count == 0) return;
            var flat = new List<ProjectFileItem>();
            void Collect(List<ProjectFileItem> nodes)
            {
                foreach (var n in nodes)
                {
                    if (n.IsDirectory) Collect(n.Children);
                    else flat.Add(n);
                }
            }
            Collect(_projectFiles);
            var target = flat.FirstOrDefault(f => f.Name.Equals("main.py", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                      ?? flat.FirstOrDefault();
            if (target != null)
            {
                LoadFileIntoEditor(target.FullPath);
            }
            else
            {
                TxtWorkspaceEditor.Text = "# Project Workspace ready.\n# Create a file or ask the AI agent to generate code.";
                TxtEditorFileName.Text = "📄 (no file selected)";
                TxtEditorStatus.Text = "Ready";
            }
        }

        private void LoadFileIntoEditor(string filePath)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                _currentOpenFilePath = filePath;
                string content = _workspaceService.ReadFileContent(filePath);
                _originalDiskContent = content;
                TxtWorkspaceEditor.Text = content;
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                string icon = ext switch
                {
                    ".cs" => "🔷",
                    ".py" => "🐍",
                    ".js" or ".ts" => "🟨",
                    ".html" => "🌐",
                    ".css" => "🎨",
                    ".json" => "⚙️",
                    ".md" or ".txt" => "📄",
                    ".bat" or ".ps1" => "⚡",
                    _ => "📄"
                };
                TxtEditorFileName.Text = $"{icon} {Path.GetFileName(filePath)}";
                TxtEditorStatus.Text = "✓ Saved";
                TxtEditorStatus.Foreground = new SolidColorBrush(ThemeColor("BrushSuccess", "#22c55e"));

                UpdateEditorMetrics();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open file: {ex.Message}", "File Open Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void UpdateEditorMetrics()
        {
            if (TxtWorkspaceEditor == null) return;
            var m = _activeModel ?? (_allModels.Count > 0 ? _allModels[0] : null);
            UpdateContextAndTokenTelemetry(m);
        }

        private void UpdateContextAndTokenTelemetry(ModelItem? m)
        {
            try
            {
                int editorChars = TxtWorkspaceEditor?.Text?.Length ?? 0;
                int editorTokens = TokenHelper.EstimateTokens(TxtWorkspaceEditor?.Text ?? "");
                int chatTokens = _chatMessages.Sum(c => TokenHelper.EstimateTokens(c.Content ?? ""));
                int totalActiveTokens = editorTokens + chatTokens;
                int contextLimit = TokenHelper.ParseContextWindowTokens(m?.Context);

                if (PbContextGauge != null && contextLimit > 0)
                {
                    double pct = Math.Min(100.0, ((double)totalActiveTokens / contextLimit) * 100.0);
                    PbContextGauge.Value = pct;
                    if (TxtInfoContextGaugeText != null)
                    {
                        TxtInfoContextGaugeText.Text = $"{TokenHelper.FormatTokens(totalActiveTokens)} / {TokenHelper.FormatTokens(contextLimit)} ({pct:0.0}%)";
                    }
                }
                else if (TxtInfoContextGaugeText != null)
                {
                    TxtInfoContextGaugeText.Text = $"{TokenHelper.FormatTokens(totalActiveTokens)} tokens";
                }

                if (TxtInfoEditorChars != null)
                {
                    TxtInfoEditorChars.Text = $"{editorChars:N0} chars";
                }
                if (TxtInfoEditorTokens != null)
                {
                    TxtInfoEditorTokens.Text = $"{TokenHelper.FormatTokens(editorTokens)} tokens";
                }
                if (TxtInfoChatTokens != null)
                {
                    TxtInfoChatTokens.Text = $"{TokenHelper.FormatTokens(chatTokens)} tokens";
                }
                if (TxtInfoChatTurns != null)
                {
                    TxtInfoChatTurns.Text = $"{_chatMessages.Count} messages";
                }

                double estCost = TokenHelper.CalculateCost(totalActiveTokens, 0, m?.Pricing);
                if (TxtInfoEstCost != null)
                {
                    TxtInfoEstCost.Text = TokenHelper.FormatCost(estCost);
                }

                var summary = _tokenUsageService.GetSummary();
                if (TxtInfoSessionTokens != null)
                {
                    TxtInfoSessionTokens.Text = $"{TokenHelper.FormatTokens(summary.TotalTokens)} tokens";
                }
                if (TxtInfoThroughput != null)
                {
                    TxtInfoThroughput.Text = summary.CallCount > 0 ? $"{summary.AvgTokensPerSecond:0} tok/s" : "—";
                }
                if (TxtInfoLastLiveResponse != null)
                {
                    var last = _cloudApiService.LastLiveUsage;
                    if (last != null)
                    {
                        TxtInfoLastLiveResponse.Text = $"{last.ModelName}: {last.PromptTokens} in / {last.CompletionTokens} out ({last.TotalTokens} total) • {last.LatencyMs}ms ({last.TokensPerSecond:F1} tok/s)";
                    }
                    else
                    {
                        TxtInfoLastLiveResponse.Text = "— Awaiting first live API call";
                    }
                }
            }
            catch { }
        }

        private void TxtWorkspaceEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtEditorStatus == null) return;
            if (TxtWorkspaceEditor.Text != _originalDiskContent)
            {
                TxtEditorStatus.Text = "● Modified";
                TxtEditorStatus.Foreground = new SolidColorBrush(ThemeColor("BrushAccentLight", "#38bdf8"));
            }
            else
            {
                TxtEditorStatus.Text = "✓ Saved";
                TxtEditorStatus.Foreground = new SolidColorBrush(ThemeColor("BrushSuccess", "#22c55e"));
            }
            UpdateEditorMetrics();
        }

        // Walks the project tree and produces the flat list of rows that should currently be
        // visible, tagging each with its Depth. Collapsed folders hide their descendants.
        private void RebuildVisibleFileRows()
        {
            _visibleFileRows.Clear();
            void Walk(List<ProjectFileItem> nodes, int depth)
            {
                foreach (var node in nodes)
                {
                    node.Depth = depth;
                    _visibleFileRows.Add(node);
                    if (node.IsDirectory && node.IsExpanded && node.Children.Count > 0)
                        Walk(node.Children, depth + 1);
                }
            }
            Walk(_projectFiles, 0);
            if (ProjectFilesList.ItemsSource != _visibleFileRows)
                ProjectFilesList.ItemsSource = _visibleFileRows;
        }

        private void CountFilesRecursive(List<ProjectFileItem> nodes, ref int fileCount, ref long totalBytes)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirectory) CountFilesRecursive(node.Children, ref fileCount, ref totalBytes);
                else { fileCount++; totalBytes += node.Size; }
            }
        }

        private void BtnSelectFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ProjectFileItem item)
            {
                if (item.IsDirectory)
                {
                    // Mark the folder as the active creation target and highlight it.
                    if (_selectedFileRow != null) _selectedFileRow.IsSelected = false;
                    item.IsSelected = true;
                    _selectedFileRow = item;
                    _selectedFolder = item;

                    item.IsExpanded = !item.IsExpanded;
                    RebuildVisibleFileRows();
                }
                else
                {
                    if (_selectedFileRow != null) _selectedFileRow.IsSelected = false;
                    item.IsSelected = true;
                    _selectedFileRow = item;
                    // New files land next to the selected file (its parent dir).
                    _selectedFolder = FindParentFolder(item);
                    LoadFileIntoEditor(item.FullPath);
                }
            }
        }

        // Resolves the tree parent directory of a file item (null = project root).
        private ProjectFileItem? FindParentFolder(ProjectFileItem target)
        {
            // Use a sentinel so a legitimate null (root-level parent) isn't mistaken for "not found".
            _foundParentSentinel = false;
            ProjectFileItem? FindWithSentinel(List<ProjectFileItem> nodes, ProjectFileItem? parent)
            {
                foreach (var n in nodes)
                {
                    if (ReferenceEquals(n, target)) { _foundParentSentinel = true; return parent; }
                    if (n.Children.Count > 0)
                    {
                        var hit = FindWithSentinel(n.Children, n);
                        if (_foundParentSentinel) return hit;
                    }
                }
                return null;
            }
            return FindWithSentinel(_projectFiles, null);
        }
        private bool _foundParentSentinel;

        // Base relative directory for new-file/new-folder creation, from the selected folder.
        private string CreationBaseRelative()
        {
            var f = _selectedFolder;
            if (f != null && f.IsDirectory && !string.IsNullOrEmpty(f.RelativePath))
                return f.RelativePath.Replace('\\', '/').Trim('/');
            return "";
        }

        private void BtnSaveFile_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentOpenFilePath))
            {
                if (_activeProject != null)
                {
                    _currentOpenFilePath = Path.Combine(_activeProject.Path, "main.py");
                }
                else return;
            }

            try
            {
                bool ok = _workspaceService.SaveFileContent(_currentOpenFilePath, TxtWorkspaceEditor.Text);
                if (ok)
                {
                    _originalDiskContent = TxtWorkspaceEditor.Text;
                    TxtEditorStatus.Text = "✓ Saved";
                    TxtEditorStatus.Foreground = new SolidColorBrush(ThemeColor("BrushSuccess", "#22c55e"));
                    RefreshProjectFilesUi();
                    if (_activeInspectorTab == 1)
                    {
                        GenerateAndDisplayDiff();
                    }
                    AppendLog($"[EDITOR] File saved: {Path.GetFileName(_currentOpenFilePath)}");
                }
                else
                {
                    MessageBox.Show("Failed to save file.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save file: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnViewEditorDiff_Click(object sender, RoutedEventArgs e)
        {
            SwitchInspectorTab(1);
            GenerateAndDisplayDiff();
        }

        private void BtnCopyEditorCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TxtWorkspaceEditor.Text);
                AppendLog("[EDITOR] Code copied to clipboard.");
            }
            catch { }
        }

        private void BtnNewFile_Click(object sender, RoutedEventArgs e)
        {
            CreateFileUnder(CreationBaseRelative());
        }

        private void BtnNewFolder_Click(object sender, RoutedEventArgs e)
        {
            CreateFolderUnder(CreationBaseRelative());
        }

        // Context-menu "New File" on a row → create inside that row's folder (or its parent, for files).
        private void BtnNewFileHere_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe && fe.Tag is ProjectFileItem item)) return;
            CreateFileUnder(BaseDirFor(item));
        }

        private void BtnNewFolderHere_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe && fe.Tag is ProjectFileItem item)) return;
            CreateFolderUnder(BaseDirFor(item));
        }

        // Base relative dir for an item: the item itself if it's a folder, else its parent folder.
        private string BaseDirFor(ProjectFileItem item)
        {
            if (item.IsDirectory) return item.RelativePath.Replace('\\', '/').Trim('/');
            var parent = FindParentFolder(item);
            return parent?.RelativePath.Replace('\\', '/').Trim('/') ?? "";
        }

        private void CreateFileUnder(string baseRel)
        {
            if (_activeProject == null) return;
            string hint = string.IsNullOrEmpty(baseRel) ? "" : baseRel + "/";
            string? fileName = PromptForText("New File", $"File name (created in /{hint}):", "", "Create");
            if (string.IsNullOrWhiteSpace(fileName)) return;
            string rel = CombineRel(baseRel, fileName.Trim());
            if (_workspaceService.CreateNewFile(_activeProject.Path, rel))
            {
                RefreshProjectFilesUi();
                LoadFileIntoEditor(Path.Combine(_activeProject.Path, rel));
                AppendLog($"[EXPLORER] Created new file: {rel}");
            }
            else
            {
                MessageBox.Show("Could not create the file — the name may be invalid or already exist.", "New File", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CreateFolderUnder(string baseRel)
        {
            if (_activeProject == null) return;
            string hint = string.IsNullOrEmpty(baseRel) ? "" : baseRel + "/";
            string? folderName = PromptForText("New Folder", $"Folder name (created in /{hint}):", "", "Create");
            if (string.IsNullOrWhiteSpace(folderName)) return;
            string rel = CombineRel(baseRel, folderName.Trim());
            if (_workspaceService.CreateNewFolder(_activeProject.Path, rel))
            {
                RefreshProjectFilesUi();
                AppendLog($"[EXPLORER] Created new directory: {rel}");
            }
            else
            {
                MessageBox.Show("Could not create the folder — the name may be invalid or already exist.", "New Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string CombineRel(string baseRel, string leaf)
        {
            leaf = leaf.Replace('\\', '/').Trim('/');
            return string.IsNullOrEmpty(baseRel) ? leaf : baseRel.TrimEnd('/') + "/" + leaf;
        }

        private void BtnRefreshFiles_Click(object sender, RoutedEventArgs e)
        {
            RefreshProjectFilesUi();
            RefreshArtifactsList();
        }

        private void BtnFileMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.DataContext = btn.DataContext;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void BtnRenameFile_Click(object sender, RoutedEventArgs e)
        {
            if (_activeProject == null) return;
            if (!(sender is FrameworkElement fe && fe.Tag is ProjectFileItem item)) return;

            string? newName = PromptForText("Rename", $"New name for “{item.Name}”:", item.Name, "Rename");
            if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == item.Name) return;

            if (_workspaceService.RenamePath(_activeProject.Path, item.FullPath, newName.Trim(), out string newFull))
            {
                bool wasOpen = string.Equals(_currentOpenFilePath, item.FullPath, StringComparison.OrdinalIgnoreCase);
                RefreshProjectFilesUi();
                if (wasOpen && !item.IsDirectory) LoadFileIntoEditor(newFull);
                AppendLog($"[EXPLORER] Renamed {item.Name} → {newName.Trim()}");
            }
            else
            {
                MessageBox.Show("Rename failed — the name may be invalid or already exist.", "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnMoveFile_Click(object sender, RoutedEventArgs e)
        {
            if (_activeProject == null) return;
            if (!(sender is FrameworkElement fe && fe.Tag is ProjectFileItem item)) return;

            string? dest = PromptForText("Move", $"Move “{item.Name}” to which folder? (relative path, blank = project root)", "", "Move");
            if (dest == null) return; // cancelled

            if (_workspaceService.MovePath(_activeProject.Path, item.FullPath, dest.Trim(), out string newFull))
            {
                bool wasOpen = string.Equals(_currentOpenFilePath, item.FullPath, StringComparison.OrdinalIgnoreCase);
                RefreshProjectFilesUi();
                if (wasOpen && !item.IsDirectory) LoadFileIntoEditor(newFull);
                AppendLog($"[EXPLORER] Moved {item.Name} → {dest.Trim()}");
            }
            else
            {
                MessageBox.Show("Move failed — the destination may be invalid, already contain that name, or be inside the item itself.", "Move", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private System.Windows.Point _dragStartPoint;
        private ProjectFileItem? _dragItem;

        private void FileRow_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _dragItem = (sender as FrameworkElement)?.Tag as ProjectFileItem;
        }

        private void FileRow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _dragItem == null) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            if (sender is Button b)
                DragDrop.DoDragDrop(b, _dragItem, System.Windows.DragDropEffects.Move);
        }

        private void FileRow_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(ProjectFileItem))
                ? System.Windows.DragDropEffects.Move
                : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void FileRow_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (_activeProject == null) return;
            if (!(e.Data.GetData(typeof(ProjectFileItem)) is ProjectFileItem source)) return;
            var target = (sender as FrameworkElement)?.Tag as ProjectFileItem;
            if (target == null || ReferenceEquals(source, target)) { _dragItem = null; return; }

            // Drop onto a folder → move into it; drop onto a file → move into that file's folder.
            string destRel = target.IsDirectory
                ? target.RelativePath
                : (System.IO.Path.GetDirectoryName(target.RelativePath) ?? "");

            if (_workspaceService.MovePath(_activeProject.Path, source.FullPath, destRel, out string newFull))
            {
                bool wasOpen = string.Equals(_currentOpenFilePath, source.FullPath, StringComparison.OrdinalIgnoreCase);
                RefreshProjectFilesUi();
                if (wasOpen && !source.IsDirectory) LoadFileIntoEditor(newFull);
                AppendLog($"[EXPLORER] Moved {source.Name} → {(string.IsNullOrEmpty(destRel) ? "project root" : destRel)}");
            }
            else
            {
                MessageBox.Show("Move failed — the destination may be invalid, already contain that name, or be inside the item itself.", "Move", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            _dragItem = null;
            e.Handled = true;
        }

        private void BtnDeleteFile_Click(object sender, RoutedEventArgs e)
        {
            if (_activeProject == null) return;
            if (!(sender is FrameworkElement fe && fe.Tag is ProjectFileItem item)) return;

            string kind = item.IsDirectory ? "folder and everything inside it" : "file";
            bool confirmed = ConfirmDialog(
                "Delete " + (item.IsDirectory ? "folder" : "file"),
                $"Delete this {kind}? This cannot be undone.",
                item.RelativePath,
                "Delete",
                danger: true);
            if (!confirmed) return;

            if (_workspaceService.DeletePath(_activeProject.Path, item.FullPath))
            {
                bool wasOpen = string.Equals(_currentOpenFilePath, item.FullPath, StringComparison.OrdinalIgnoreCase);
                RefreshProjectFilesUi();
                if (wasOpen)
                {
                    TxtWorkspaceEditor.Text = string.Empty;
                    TxtEditorFileName.Text = "📄 (no file selected)";
                    TxtEditorStatus.Text = "Ready";
                    _currentOpenFilePath = null;
                }
                AppendLog($"[EXPLORER] Deleted {item.RelativePath}");
            }
            else
            {
                MessageBox.Show("Delete failed.", "Delete", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Minimal theme-aware modal text prompt (no extra XAML file needed).
        private string? PromptForText(string title, string message, string initial, string okLabel = "OK")
        {
            var dlg = new Window
            {
                Title = title,
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent
            };

            var shell = new Border
            {
                Background = new SolidColorBrush(ThemeColor("BrushCardBg", "#0a0a0d")),
                BorderBrush = new SolidColorBrush(ThemeColor("BrushBorder", "#26263a")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20)
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(ThemeColor("BrushTextPrimary", "#e4e4e7")),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap
            });

            var inputBorder = new Border
            {
                Background = new SolidColorBrush(ThemeColor("BrushInputBg", "#000000")),
                BorderBrush = new SolidColorBrush(ThemeColor("BrushBorder", "#2a2a38")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6)
            };
            var input = new TextBox
            {
                Text = initial,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = new SolidColorBrush(ThemeColor("BrushTextPrimary", "#ffffff")),
                CaretBrush = new SolidColorBrush(ThemeColor("BrushAccentLight", "#7fa8f0")),
                BorderThickness = new Thickness(0),
                FontSize = 13,
                FontFamily = new System.Windows.Media.FontFamily("Consolas, Segoe UI")
            };
            inputBorder.Child = input;
            stack.Children.Add(inputBorder);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var ok = new Button { Content = okLabel, Style = (Style)FindResource("PrimaryActionBtn"), Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
            var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("BlackBoxActionBtn"), MinWidth = 80 };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            stack.Children.Add(buttons);
            shell.Child = stack;
            dlg.Content = shell;

            string? result = null;
            void Commit() { result = input.Text; dlg.DialogResult = true; }
            ok.Click += (_, __) => Commit();
            cancel.Click += (_, __) => { dlg.DialogResult = false; };
            input.KeyDown += (_, ev) =>
            {
                if (ev.Key == Key.Enter) Commit();
                else if (ev.Key == Key.Escape) dlg.DialogResult = false;
            };
            dlg.Loaded += (_, __) => { input.Focus(); input.SelectAll(); };

            return dlg.ShowDialog() == true ? result : null;
        }

        // Theme-aware modal confirmation (replaces the raw Win32 MessageBox). Returns true on confirm.
        private bool ConfirmDialog(string title, string message, string? detail, string confirmLabel = "Confirm", bool danger = false)
        {
            var dlg = new Window
            {
                Title = title,
                Width = 430,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent
            };

            var shell = new Border
            {
                Background = new SolidColorBrush(ThemeColor("BrushCardBg", "#0a0a0d")),
                BorderBrush = new SolidColorBrush(ThemeColor("BrushBorder", "#26263a")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(22)
            };
            var stack = new StackPanel();

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new TextBlock
            {
                Text = danger ? "⚠" : "ℹ",
                FontSize = 18,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(danger ? ThemeColor("BrushDanger", "#f43f5e") : ThemeColor("BrushAccent", "#3b82f6"))
            });
            header.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(ThemeColor("BrushTextPrimary", "#fafafa"))
            });
            stack.Children.Add(header);

            stack.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(ThemeColor("BrushTextSecondary", "#a1a1aa")),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrWhiteSpace(detail))
            {
                var detailBorder = new Border
                {
                    Background = new SolidColorBrush(ThemeColor("BrushCardInnerBg", "#101013")),
                    BorderBrush = new SolidColorBrush(ThemeColor("BrushBorderSubtle", "#161619")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 7, 10, 7),
                    Margin = new Thickness(0, 10, 0, 0)
                };
                detailBorder.Child = new TextBlock
                {
                    Text = detail,
                    Foreground = new SolidColorBrush(ThemeColor("BrushTextPrimary", "#fafafa")),
                    FontSize = 12,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas, Segoe UI"),
                    TextWrapping = TextWrapping.Wrap
                };
                stack.Children.Add(detailBorder);
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var confirm = new Button { Content = confirmLabel, Style = (Style)FindResource("PrimaryActionBtn"), Margin = new Thickness(0, 0, 8, 0), MinWidth = 96 };
            if (danger)
            {
                confirm.Background = new SolidColorBrush(ThemeColor("BrushDanger", "#f43f5e"));
                confirm.BorderBrush = confirm.Background;
            }
            var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("BlackBoxActionBtn"), MinWidth = 84 };
            buttons.Children.Add(confirm);
            buttons.Children.Add(cancel);
            stack.Children.Add(buttons);

            shell.Child = stack;
            dlg.Content = shell;

            confirm.Click += (_, __) => dlg.DialogResult = true;
            cancel.Click += (_, __) => dlg.DialogResult = false;
            dlg.KeyDown += (_, ev) =>
            {
                if (ev.Key == Key.Enter) dlg.DialogResult = true;
                else if (ev.Key == Key.Escape) dlg.DialogResult = false;
            };
            dlg.Loaded += (_, __) => cancel.Focus();

            return dlg.ShowDialog() == true;
        }

        // ================= AI CHAT IMPLEMENTATION =================
        private void InitializeProjectChat()
        {
            if (_chatMessages.Count == 0)
            {
                string modelName = _activeModel?.Name ?? "GPT-4o";
                string prov = _activeModel?.Provider ?? "OpenAI";
                _chatMessages.Add(new ChatMessageItem
                {
                    SenderRole = "Assistant",
                    SenderName = modelName,
                    ModelName = modelName,
                    Provider = prov,
                    Content = $"Welcome to {_activeProject?.Name ?? "Workspace"}!\n\nConnected via cloud API to **{modelName}** ({prov}).\n\n• Left column: Project files & folders (click any file to view/edit).\n• Center: Active editor + multi-turn conversational AI.\n• Right column: Telemetry info, line-by-line code diff, CLI terminal runner, and media attachments.\n\nAsk questions, request code updates, review diffs, or execute project scripts below.",
                    Timestamp = DateTime.Now.ToString("HH:mm:ss")
                });
            }
            RefreshChatListUi();
        }

        private void RefreshChatListUi()
        {
            if (ChatMessagesList == null) return;
            if (ChatMessagesList.ItemsSource != _displayedChatMessages)
            {
                ChatMessagesList.ItemsSource = _displayedChatMessages;
            }
            _displayedChatMessages.Clear();
            foreach (var msg in _chatMessages)
            {
                _displayedChatMessages.Add(msg);
            }
            if (_displayedChatMessages.Count > 0)
            {
                var last = _displayedChatMessages[_displayedChatMessages.Count - 1];
                _ = Dispatcher.BeginInvoke(new Action(() => ChatMessagesList.ScrollIntoView(last)), DispatcherPriority.Background);
            }
            if (TxtInfoChatTurns != null)
            {
                TxtInfoChatTurns.Text = $"{_chatMessages.Count} messages";
            }
        }

        private async void BtnSendChatMessage_Click(object sender, RoutedEventArgs e)
        {
            await DispatchChatMessageAsync();
        }

        private async void TxtChatPromptInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                await DispatchChatMessageAsync();
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private System.Windows.Threading.DispatcherTimer? _thinkTimer;
        private int _thinkPhase;

        // Accumulated reasoning steps for the current turn, shown in the collapsible step log under
        // the thinking pill. Deduped against the last entry so repeated signals don't spam the list.
        private readonly System.Collections.ObjectModel.ObservableCollection<string> _steps = new();

        private void ShowThinking(string label)
        {
            if (ThinkingIndicator == null) return;
            TxtThinking.Text = label;
            _steps.Clear();
            if (StepLog != null && StepLog.ItemsSource == null) StepLog.ItemsSource = _steps;
            ThinkingIndicator.Visibility = Visibility.Visible;
            _thinkPhase = 0;
            _thinkTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            _thinkTimer.Tick -= Think_Tick;
            _thinkTimer.Tick += Think_Tick;
            _thinkTimer.Start();
        }

        private void Think_Tick(object? sender, EventArgs e)
        {
            var dots = new[] { ThinkDot1, ThinkDot2, ThinkDot3 };
            for (int i = 0; i < dots.Length; i++)
            {
                dots[i].Opacity = (i == _thinkPhase % 3) ? 1.0 : 0.28;
                double s = (i == _thinkPhase % 3) ? 1.35 : 1.0;
                dots[i].RenderTransformOrigin = new Point(0.5, 0.5);
                dots[i].RenderTransform = new ScaleTransform(s, s);
            }
            _thinkPhase++;
        }

        private void HideThinking()
        {
            _thinkTimer?.Stop();
            if (ThinkingIndicator != null) ThinkingIndicator.Visibility = Visibility.Collapsed;
        }

        // Live step feedback: map each pipeline signal to a short human label and show it in the
        // thinking pill while a turn is in flight. Signals arrive on threadpool continuations, so we
        // marshal to the UI thread; we only update while the indicator is actually visible.
        private void OnAgentStep(AgentSignal sig)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => OnAgentStep(sig))); return; }
            if (ThinkingIndicator == null || ThinkingIndicator.Visibility != Visibility.Visible) return;
            var label = StepLabel(sig);
            if (string.IsNullOrEmpty(label)) return;
            TxtThinking.Text = label;
            // Append to the collapsible log, skipping consecutive duplicates.
            if (_steps.Count == 0 || !string.Equals(_steps[_steps.Count - 1], label, StringComparison.Ordinal))
            {
                _steps.Add(label);
                if (_steps.Count > 200) _steps.RemoveAt(0);
            }
        }

        // Toggle the reasoning step log open/closed.
        private void ThinkingHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (StepLog == null) return;
            bool open = StepLog.Visibility != Visibility.Visible;
            StepLog.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (TxtStepChevron != null) TxtStepChevron.Text = open ? " ▾" : " ▸";
        }

        private static string StepLabel(AgentSignal sig)
        {
            string d = sig.Detail ?? string.Empty;
            switch (sig.Node)
            {
                case AgentNode.Prompt:
                    if (d.StartsWith("Intent")) return "Understanding request…";
                    if (d.StartsWith("Task")) return "Planning · " + d;
                    return string.Empty;
                case AgentNode.Memory: return "Recalling memory…";
                case AgentNode.Context:
                    return d.StartsWith("Knowledge") ? "Reading project knowledge…" : "Assembling context…";
                case AgentNode.Model: return "Contacting model…";
                case AgentNode.Reasoning:
                    if (d.StartsWith("→")) return "Working · " + d.TrimStart('→', ' ');
                    if (d.StartsWith("Planning")) return d;
                    return "Reasoning…";
                case AgentNode.Compaction: return "Trimming context to fit…";
                case AgentNode.Response:
                    if (d.StartsWith("✓") || d.StartsWith("✗")) return d;
                    return "Reading response…";
                default: return string.Empty;
            }
        }

        private void BtnStopChatGeneration_Click(object sender, RoutedEventArgs e)
        {
            try { _chatCts?.Cancel(); } catch { }
        }

        private async Task DispatchChatMessageAsync(string? overridePrompt = null, bool addUserMessage = true, List<string>? overrideImages = null)
        {
            if (_workspaceChatBusy) return;

            string prompt = (overridePrompt ?? TxtChatPromptInput.Text ?? string.Empty).Trim();
            var pendingImgs = overrideImages ?? _pendingChatImages;
            if (string.IsNullOrEmpty(prompt) && pendingImgs.Count == 0) return;

            if (_activeModel == null)
            {
                if (_allModels.Count > 0) SetActiveModel(_allModels[0]);
                else
                {
                    MessageBox.Show("Please select an active model from the Cloud Model Library first.", "No Active Model", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            if (_activeModel == null) return;
            var requestModel = _activeModel;

            List<string> messageImages;
            if (addUserMessage)
            {
                messageImages = new List<string>(_pendingChatImages);
                var userMsg = new ChatMessageItem
                {
                    SenderRole = "User",
                    SenderName = _settings.ProfileName,
                    Content = prompt,
                    Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                    ImageAttachments = messageImages
                };
                _chatMessages.Add(userMsg);
                if (ChatMessagesList.ItemsSource != _displayedChatMessages) ChatMessagesList.ItemsSource = _displayedChatMessages;
                _displayedChatMessages.Add(userMsg);
                TxtChatPromptInput.Clear();
                _pendingChatImages.Clear();
                PendingImagesList.ItemsSource = null;
                PendingImagesBar.Visibility = Visibility.Collapsed;
                if (TxtInfoChatTurns != null) TxtInfoChatTurns.Text = $"{_chatMessages.Count} messages";
                PersistChat();
            }
            else
            {
                messageImages = new List<string>(pendingImgs);
            }

            AgentActivityBus.Emit(AgentNode.Prompt, $"\"{Truncate(prompt, 90)}\" ({prompt.Length} chars)");

            var history = new List<AiMessage>();
            for (int h = 0; h < _chatMessages.Count - 1; h++)
            {
                var cm = _chatMessages[h];
                if (string.IsNullOrWhiteSpace(cm.Content)) continue;
                if (cm.SenderRole == "User") history.Add(AiMessage.User(cm.Content));
                else if (cm.SenderRole == "Assistant" && !cm.IsError) history.Add(AiMessage.Assistant(cm.Content));
            }

            var ctxRequest = new ContextRequest
            {
                Model = requestModel,
                Settings = _settings,
                UserPrompt = prompt,
                SystemInstruction = (_settings.SystemInstructionEnabled && !string.IsNullOrWhiteSpace(_settings.SystemInstruction))
                    ? _settings.SystemInstruction : null,
                OpenFilePath = _currentOpenFilePath,
                OpenFileText = TxtWorkspaceEditor.Text,
                Attachments = messageImages.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList(),
                History = history,
                ProjectKey = _activeProject?.Path
            };

            var assistantMsg = new ChatMessageItem
            {
                SenderRole = "Assistant",
                SenderName = requestModel.Name,
                ModelName = requestModel.Name,
                Provider = requestModel.Provider,
                ActivityState = MessageActivityState.Connecting,
                ActivityDetail = $"Connecting to {requestModel.Name}...",
                IsStreaming = true,
                Timestamp = DateTime.Now.ToString("HH:mm:ss")
            };
            _chatMessages.Add(assistantMsg);
            if (ChatMessagesList.ItemsSource != _displayedChatMessages) ChatMessagesList.ItemsSource = _displayedChatMessages;
            _displayedChatMessages.Add(assistantMsg);
            _ = Dispatcher.BeginInvoke(new Action(() => ChatMessagesList.ScrollIntoView(assistantMsg)), DispatcherPriority.Background);

            _workspaceChatBusy = true;
            _chatCts = new CancellationTokenSource();
            if (BtnSendChatMessage != null) BtnSendChatMessage.Visibility = Visibility.Collapsed;
            if (BtnStopChatGeneration != null) BtnStopChatGeneration.Visibility = Visibility.Visible;

            AppendLog($"[AI CHAT] Sending prompt to {requestModel.Name} ({requestModel.Provider})...");
            AgentActivityBus.Emit(AgentNode.Model, $"Dispatch → {requestModel.Name} ({requestModel.Provider})", "active");
            AgentActivityBus.Emit(AgentNode.Reasoning, $"{requestModel.Name} is generating…", "active");
            DateTime callStartTime = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();

            var streamBuffer = new StringBuilder();
            var streamLock = new object();
            bool hasPendingDelta = false;
            var flushTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(45)
            };
            flushTimer.Tick += (_, _) =>
            {
                string? snapshot = null;
                lock (streamLock)
                {
                    if (hasPendingDelta)
                    {
                        snapshot = streamBuffer.ToString();
                        hasPendingDelta = false;
                    }
                }
                if (snapshot != null)
                {
                    assistantMsg.Content = snapshot;
                    ChatMessagesList.ScrollIntoView(assistantMsg);
                }
                assistantMsg.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            };
            flushTimer.Start();

            AiResponse? aiResp = null;
            bool runtimeError = false;
            bool cancelled = false;
            string response = string.Empty;
            int promptTokenEstimate = 0;

            void OnProgress(RuntimeProgressEvent evt)
            {
                if (!string.IsNullOrEmpty(evt.TokenDelta))
                {
                    lock (streamLock)
                    {
                        streamBuffer.Append(evt.TokenDelta);
                        hasPendingDelta = true;
                    }
                }

                Dispatcher.BeginInvoke(() =>
                {
                    assistantMsg.ActivityState = evt.State;
                    if (!string.IsNullOrWhiteSpace(evt.Detail))
                    {
                        assistantMsg.ActivityDetail = evt.Detail;
                        if (evt.State == MessageActivityState.ExecutingTool || evt.State == MessageActivityState.EditingFiles)
                        {
                            if (assistantMsg.ToolSteps.Count == 0 || !string.Equals(assistantMsg.ToolSteps[assistantMsg.ToolSteps.Count - 1], evt.Detail, StringComparison.Ordinal))
                            {
                                assistantMsg.ToolSteps.Add(evt.Detail);
                                assistantMsg.ToolSteps = new List<string>(assistantMsg.ToolSteps);
                            }
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(evt.FilePath))
                        assistantMsg.ActiveFilePath = evt.FilePath;
                    if (evt.FilesModified > 0)
                        assistantMsg.FilesModifiedCount = evt.FilesModified;
                    if (evt.Additions > 0)
                        assistantMsg.AddedLinesCount = evt.Additions;
                    if (evt.Deletions > 0)
                        assistantMsg.RemovedLinesCount = evt.Deletions;
                    assistantMsg.ElapsedSeconds = sw.Elapsed.TotalSeconds;
                });
            }

            try
            {
                aiResp = await _runtime.RunWithProgressAsync(ctxRequest, OnProgress, _chatCts.Token);
                runtimeError = aiResp.IsError;
                promptTokenEstimate = aiResp.PromptTokensEstimate;
                response = aiResp.IsError ? (aiResp.ErrorMessage ?? aiResp.Text) : aiResp.Text;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                lock (streamLock) { response = streamBuffer.ToString(); }
            }
            catch (Exception ex)
            {
                runtimeError = true;
                response = $"[ERROR] {ex.Message}";
            }
            finally
            {
                sw.Stop();
                flushTimer.Stop();
                _chatCts?.Dispose();
                _chatCts = null;
                _workspaceChatBusy = false;
                if (BtnStopChatGeneration != null) BtnStopChatGeneration.Visibility = Visibility.Collapsed;
                if (BtnSendChatMessage != null) BtnSendChatMessage.Visibility = Visibility.Visible;
                HideThinking();
            }

            response = ReasoningFilter.Split(response, out string reasoning);
            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                AgentActivityBus.Emit(AgentNode.Reasoning, Truncate(reasoning, 160));
            }

            bool isErr = !cancelled && (runtimeError || response.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase));
            if (isErr)
            {
                AgentActivityBus.Emit(AgentNode.Response, Truncate(response, 120), "error");
            }

            if (!isErr && !cancelled)
            {
                if (_cloudApiService.LastLiveUsage != null && _cloudApiService.LastLiveUsage.TimestampUtc >= callStartTime)
                {
                    var live = _cloudApiService.LastLiveUsage;
                    AppendLog($"[LIVE API VERIFIED] Tokens: {live.PromptTokens} in + {live.CompletionTokens} out = {live.TotalTokens} total ({live.TokensPerSecond:F1} tok/s, {sw.ElapsedMilliseconds}ms)");
                    AgentActivityBus.Emit(AgentNode.Tokens, $"Live usage · {live.PromptTokens:N0} in + {live.CompletionTokens:N0} out · {live.TokensPerSecond:F1} tok/s", "info", live.TotalTokens);
                }
                else
                {
                    int promptTokens = promptTokenEstimate > 0 ? promptTokenEstimate : TokenHelper.CountTokens(prompt);
                    int completionTokens = TokenHelper.CountTokens(response);
                    _tokenUsageService.Record(new Models.TokenUsageRecord
                    {
                        TimestampUtc = DateTime.UtcNow,
                        Provider = requestModel.Provider,
                        ModelId = requestModel.Id,
                        ModelName = requestModel.Name,
                        PromptTokens = promptTokens,
                        CompletionTokens = completionTokens,
                        TotalTokens = promptTokens + completionTokens,
                        LatencyMs = sw.ElapsedMilliseconds,
                        IsLiveApi = false
                    });
                    AppendLog($"[BPE ESTIMATE] Count: {promptTokens} in + {completionTokens} out = {promptTokens + completionTokens} total ({sw.ElapsedMilliseconds}ms)");
                    AgentActivityBus.Emit(AgentNode.Tokens, $"BPE estimate · {promptTokens:N0} in + {completionTokens:N0} out", "info", promptTokens + completionTokens);
                }
                UpdateContextAndTokenTelemetry(requestModel);
                AgentActivityBus.Emit(AgentNode.Response, $"{requestModel.Name} replied ({response.Length:N0} chars, {sw.ElapsedMilliseconds:N0}ms)");
            }

            assistantMsg.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            assistantMsg.IsStreaming = false;
            assistantMsg.IsError = isErr;
            assistantMsg.Content = response;

            if (cancelled)
            {
                assistantMsg.ActivityState = MessageActivityState.Cancelled;
                assistantMsg.ActivityDetail = "Generation cancelled by user.";
                assistantMsg.Blocks = StructuredContentParser.Parse(response, aiResp?.StructuredBlocks);
            }
            else if (isErr)
            {
                assistantMsg.ActivityState = MessageActivityState.Failed;
                assistantMsg.ActivityDetail = "Request failed.";
                assistantMsg.Blocks = new List<ChatContentBlock>
                {
                    new ErrorBlock
                    {
                        Title = $"{requestModel.Name} Error",
                        Message = response,
                        Operation = "Workspace Chat",
                        CanRetry = true
                    }
                };
            }
            else
            {
                assistantMsg.ActivityState = MessageActivityState.Completed;
                assistantMsg.ActivityDetail = string.Empty;
                assistantMsg.Blocks = StructuredContentParser.Parse(response, aiResp?.StructuredBlocks);
            }
            assistantMsg.NotifyBlocksUpdated();

            if (assistantMsg.FilesModifiedCount > 0)
            {
                RefreshProjectFilesUi();
            }

            if (TxtInfoChatTurns != null) TxtInfoChatTurns.Text = $"{_chatMessages.Count} messages";
            PersistChat();
            ChatMessagesList.ScrollIntoView(assistantMsg);
            AppendLog($"[AI CHAT] Received response from {requestModel.Name} ({sw.ElapsedMilliseconds}ms)");
        }

        private void WorkspaceRichMessage_TaskChanged(object? sender, EventArgs e)
        {
            PersistChat();
        }

        private void WorkspaceRichMessage_ReviewChangesRequested(object? sender, DiffSummaryBlock summary)
        {
            if (summary.Files.Count > 0)
            {
                var first = summary.Files[0];
                string fullPath = ResolveProjectFilePath(first.FilePath);
                if (File.Exists(fullPath))
                {
                    LoadFileIntoEditor(fullPath);
                }
                _diffLines = _workspaceService.GenerateDiff(first.BeforeContent, first.AfterContent);
                DiffLinesList.ItemsSource = null;
                DiffLinesList.ItemsSource = _diffLines;
                int added = _diffLines.Count(d => d.Type == "Added");
                int removed = _diffLines.Count(d => d.Type == "Removed");
                TxtDiffFileName.Text = Path.GetFileName(first.FilePath);
                TxtDiffAddedCount.Text = $"+{added}";
                TxtDiffRemovedCount.Text = $"-{removed}";
                DiffEmptyState.Visibility = (added > 0 || removed > 0) ? Visibility.Collapsed : Visibility.Visible;
            }
            BtnInspectorTab_Click(BtnInspectorTabDiff, new RoutedEventArgs());
        }

        private void WorkspaceRichMessage_OpenFileRequested(object? sender, string filePath)
        {
            string full = ResolveProjectFilePath(filePath);
            if (File.Exists(full))
            {
                RefreshProjectFilesUi();
                LoadFileIntoEditor(full);
            }
            else
            {
                try { if (File.Exists(filePath)) Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
                catch { }
            }
        }

        private async void WorkspaceRichMessage_RetryRequested(object? sender, ChatMessageItem failedMsg)
        {
            if (_workspaceChatBusy) return;
            int idx = _chatMessages.IndexOf(failedMsg);
            if (idx <= 0) return;

            ChatMessageItem? prevUser = null;
            for (int i = idx - 1; i >= 0; i--)
            {
                if (_chatMessages[i].IsUser) { prevUser = _chatMessages[i]; break; }
            }
            if (prevUser == null || string.IsNullOrWhiteSpace(prevUser.Content)) return;

            _chatMessages.RemoveAt(idx);
            _displayedChatMessages.Remove(failedMsg);
            await DispatchChatMessageAsync(prevUser.Content, addUserMessage: false, prevUser.ImageAttachments);
        }

        private string ResolveProjectFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            if (Path.IsPathRooted(path)) return path;
            if (_activeProject != null) return Path.Combine(_activeProject.Path, path);
            return path;
        }

        private void BtnAttachImage_Click(object sender, RoutedEventArgs e)
        {
            if (_activeProject == null) return;
            var dlg = new OpenFileDialog
            {
                Title = "Select Image or Media File to Attach",
                Filter = "All Supported Files|*.png;*.jpg;*.jpeg;*.webp;*.svg;*.gif;*.pdf;*.txt;*.json|Image Files|*.png;*.jpg;*.jpeg;*.webp;*.svg;*.gif|All Files|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                string savedPath = _workspaceService.SaveUploadedImage(_activeProject.Path, dlg.FileName);
                _pendingChatImages.Add(savedPath);
                PendingImagesList.ItemsSource = null;
                PendingImagesList.ItemsSource = _pendingChatImages;
                PendingImagesBar.Visibility = Visibility.Visible;
                RefreshArtifactsList();
                AppendLog($"[ATTACH] Staged file for chat: {Path.GetFileName(savedPath)}");
            }
        }

        private void BtnRemovePendingImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path)
            {
                _pendingChatImages.Remove(path);
                PendingImagesList.ItemsSource = null;
                PendingImagesList.ItemsSource = _pendingChatImages;
                if (_pendingChatImages.Count == 0)
                {
                    PendingImagesBar.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void BtnClearChat_Click(object sender, RoutedEventArgs e)
        {
            _chatMessages.Clear();
            if (_activeProject != null)
            {
                _historyStore.Clear(_activeProject.Path);
                _memoryStore.Clear(_activeProject.Path);
            }
            InitializeProjectChat();
            AppendLog("[AI CHAT] Chat history cleared.");
        }

        // Best-effort durable persistence of the current conversation, keyed by project path.
        private void PersistChat()
        {
            if (_activeProject != null) _historyStore.Save(_activeProject.Path, _chatMessages);
        }

        private void BtnCopyChatMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ChatMessageItem msg)
            {
                try
                {
                    Clipboard.SetText(msg.Content);
                    AppendLog("[AI CHAT] Message copied to clipboard.");
                }
                catch { }
            }
        }

        // ================= RIGHT INSPECTOR TABS =================
        private void BtnInspectorTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int tabIndex))
            {
                SwitchInspectorTab(tabIndex);
            }
        }

        private void SwitchInspectorTab(int tabIndex)
        {
            _activeInspectorTab = tabIndex;
            BtnInspectorTabInfo.Style = (Style)Resources[tabIndex == 0 ? "SettingsTabBtnActive" : "SettingsTabBtn"];
            BtnInspectorTabDiff.Style = (Style)Resources[tabIndex == 1 ? "SettingsTabBtnActive" : "SettingsTabBtn"];
            BtnInspectorTabCmd.Style = (Style)Resources[tabIndex == 2 ? "SettingsTabBtnActive" : "SettingsTabBtn"];
            BtnInspectorTabArtifacts.Style = (Style)Resources[tabIndex == 3 ? "SettingsTabBtnActive" : "SettingsTabBtn"];

            TabContentInfo.Visibility = tabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            TabContentDiff.Visibility = tabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            TabContentCmd.Visibility = tabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            TabContentArtifacts.Visibility = tabIndex == 3 ? Visibility.Visible : Visibility.Collapsed;

            if (tabIndex == 0) UpdateInspectorInfo();
            else if (tabIndex == 1) GenerateAndDisplayDiff();
            else if (tabIndex == 3) RefreshArtifactsList();
        }

        private void UpdateInspectorInfo()
        {
            if (_activeProject != null)
            {
                TxtInfoProjectName.Text = _activeProject.Name;
                TxtInfoProjectPath.Text = _activeProject.Path;
            }

            var m = _activeModel ?? (_allModels.Count > 0 ? _allModels[0] : null);
            if (m != null)
            {
                TxtInfoModelName.Text = m.Name;
                TxtInfoModelProvider.Text = m.Provider;
                TxtInfoModelId.Text = m.Id;
                TxtInfoModelContext.Text = m.Context;
                TxtInfoModelPricing.Text = m.Pricing;

                string key = m.IsCustom ? m.CustomApiKey : _cloudApiService.GetApiKeyForProvider(m.Provider, _settings);
                TxtInfoModelKeyStatus.Text = !string.IsNullOrWhiteSpace(key) ? "VALIDATED ✓" : "NOT CONFIGURED ✕";
                TxtInfoModelKeyStatus.Foreground = new SolidColorBrush(!string.IsNullOrWhiteSpace(key) ? ThemeColor("BrushSuccess", "#22c55e") : ThemeColor("BrushDanger", "#f87171"));
            }

            UpdateContextAndTokenTelemetry(m);
        }

        private async void BtnPingActiveModel_Click(object sender, RoutedEventArgs e)
        {
            var m = _activeModel ?? (_allModels.Count > 0 ? _allModels[0] : null);
            if (m == null) return;

            TxtInfoPingResult.Text = $"Testing ping to {m.Name} ({m.Endpoint})...";
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, m.Endpoint);
                string key = m.IsCustom ? m.CustomApiKey : _cloudApiService.GetApiKeyForProvider(m.Provider, _settings);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    if (m.Provider.Contains("Anthropic", StringComparison.OrdinalIgnoreCase))
                    {
                        req.Headers.Add("x-api-key", key);
                        req.Headers.Add("anthropic-version", "2023-06-01");
                    }
                    else if (m.Provider.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
                    {
                        req.RequestUri = new Uri($"{m.Endpoint}?key={key}");
                    }
                    else
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    }
                }

                var resp = await _httpClient.SendAsync(req);
                sw.Stop();
                TxtInfoPingResult.Text = $"Ping: {sw.ElapsedMilliseconds}ms | Status: {(int)resp.StatusCode} {resp.ReasonPhrase} | Host: {req.RequestUri?.Host}";
                AppendLog($"[PING] {m.Name} latency: {sw.ElapsedMilliseconds}ms ({resp.StatusCode})");
            }
            catch (Exception ex)
            {
                sw.Stop();
                TxtInfoPingResult.Text = $"Ping Error ({sw.ElapsedMilliseconds}ms): {ex.Message}";
                AppendLog($"[PING ERROR] {ex.Message}");
            }
        }

        // ================= REVIEW DIFF =================
        private void GenerateAndDisplayDiff()
        {
            string original = _originalDiskContent;
            string current = TxtWorkspaceEditor.Text;
            _diffLines = _workspaceService.GenerateDiff(original, current);
            DiffLinesList.ItemsSource = null;
            DiffLinesList.ItemsSource = _diffLines;

            int added = _diffLines.Count(d => d.Type == "Added");
            int removed = _diffLines.Count(d => d.Type == "Removed");
            TxtDiffFileName.Text = Path.GetFileName(_currentOpenFilePath ?? "active_file");
            TxtDiffAddedCount.Text = $"+{added}";
            TxtDiffRemovedCount.Text = $"-{removed}";

            bool hasChanges = added > 0 || removed > 0;
            DiffEmptyState.Visibility = hasChanges ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnRefreshDiff_Click(object sender, RoutedEventArgs e)
        {
            GenerateAndDisplayDiff();
        }

        // ================= CMD RUNS =================
        private async void BtnRunCliCommand_Click(object sender, RoutedEventArgs e)
        {
            string cmd = TxtCliCommandInput.Text.Trim();
            if (string.IsNullOrEmpty(cmd)) return;
            await ExecuteCliCommandAsync(cmd);
        }

        private async void TxtCliCommandInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string cmd = TxtCliCommandInput.Text.Trim();
                if (string.IsNullOrEmpty(cmd)) return;
                await ExecuteCliCommandAsync(cmd);
            }
        }

        private async void BtnCmdPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string cmd)
            {
                TxtCliCommandInput.Text = cmd;
                await ExecuteCliCommandAsync(cmd);
            }
        }

        private async Task ExecuteCliCommandAsync(string command)
        {
            if (_activeProject == null || !Directory.Exists(_activeProject.Path))
            {
                AppendLog("[CLI ERROR] Project path does not exist.");
                return;
            }

            _cliCts?.Cancel();
            _cliCts = new System.Threading.CancellationTokenSource();

            AppendLog($"\n$ {command}");
            try
            {
                await _workspaceService.RunCliCommandAsync(command, _activeProject.Path, line =>
                {
                    Dispatcher.Invoke(() => AppendLog(line));
                }, _cliCts.Token);
            }
            catch (OperationCanceledException)
            {
                AppendLog("[CLI] Command cancelled by user.");
            }
            catch (Exception ex)
            {
                AppendLog($"[CLI ERROR] {ex.Message}");
            }
        }

        private void BtnStopCliCommand_Click(object sender, RoutedEventArgs e)
        {
            _cliCts?.Cancel();
            AppendLog("[CLI] Stop requested.");
        }

        private void BtnClearCliOutput_Click(object sender, RoutedEventArgs e)
        {
            TxtCliOutput.Clear();
            AppendLog("[CLI] Console buffer cleared.");
        }

        // ================= ARTIFACTS & UPLOADS =================
        private void RefreshArtifactsList()
        {
            if (_activeProject == null || !Directory.Exists(_activeProject.Path)) return;
            try
            {
                _artifacts = _workspaceService.GetProjectArtifacts(_activeProject.Path);
                ArtifactsList.ItemsSource = null;
                ArtifactsList.ItemsSource = _artifacts;
                TxtArtifactsCount.Text = $"PROJECT ARTIFACTS ({_artifacts.Count})";
            }
            catch (Exception ex)
            {
                AppendLog($"[ARTIFACTS ERROR] {ex.Message}");
            }
        }

        private void BtnUploadArtifact_Click(object sender, RoutedEventArgs e)
        {
            if (_activeProject == null) return;
            var dlg = new OpenFileDialog
            {
                Title = "Upload Image or Artifact to Project",
                Filter = "All Files|*.*|Images|*.png;*.jpg;*.jpeg;*.webp;*.svg;*.gif|Code/Data|*.py;*.cs;*.json;*.txt;*.md",
                Multiselect = true
            };
            if (dlg.ShowDialog() == true)
            {
                foreach (var file in dlg.FileNames)
                {
                    string saved = _workspaceService.SaveUploadedImage(_activeProject.Path, file);
                    AppendLog($"[UPLOAD] Saved: {Path.GetFileName(saved)}");
                }
                RefreshArtifactsList();
                RefreshProjectFilesUi();
            }
        }

        private void ArtifactDropZone_Drop(object sender, DragEventArgs e)
        {
            if (_activeProject == null) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null)
                {
                    foreach (var file in files)
                    {
                        string saved = _workspaceService.SaveUploadedImage(_activeProject.Path, file);
                        AppendLog($"[UPLOAD DROP] Saved: {Path.GetFileName(saved)}");
                    }
                    RefreshArtifactsList();
                    RefreshProjectFilesUi();
                }
            }
        }

        private void ArtifactDropZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void BtnAttachArtifactToChat_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ArtifactItem artifact)
            {
                if (!_pendingChatImages.Contains(artifact.FilePath))
                {
                    _pendingChatImages.Add(artifact.FilePath);
                    PendingImagesList.ItemsSource = null;
                    PendingImagesList.ItemsSource = _pendingChatImages;
                    PendingImagesBar.Visibility = Visibility.Visible;
                    AppendLog($"[ATTACH] Staged '{artifact.Name}' to AI chat prompt.");
                }
            }
        }

        private void BtnOpenArtifact_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ArtifactItem artifact)
            {
                string ext = Path.GetExtension(artifact.FilePath).ToLowerInvariant();
                if (ext is ".txt" or ".py" or ".cs" or ".json" or ".md" or ".html" or ".css" or ".js")
                {
                    LoadFileIntoEditor(artifact.FilePath);
                }
                else
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(artifact.FilePath) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Could not open file: {ex.Message}", "Open Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }

        private void BtnRefreshArtifacts_Click(object sender, RoutedEventArgs e)
        {
            RefreshArtifactsList();
        }
        private void BtnWorkspaceExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (_activeProject != null)
            {
                OpenPathInExplorer(_activeProject.Path);
            }
        }
    }
}
