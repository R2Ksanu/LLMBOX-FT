using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Indentation;
using LlmodelBox.Infrastructure.Editor;
using LlmodelBox.Services;

namespace LlmodelBox.Controls.Editor
{
    public partial class CodeEditorWorkspace : UserControl
    {
        private const int LargeSyntaxThresholdChars = 1_500_000;

        private readonly List<EditorTabState> _tabs = new();
        private EditorTabState? _activeTab;
        private bool _suppressDocumentChanged = false;
        private bool _isDarkTheme = true;
        private bool _allFolded = false;

        private FoldingManager? _foldingManager;
        private readonly TextMarkerService _markerService;
        private readonly BracketHighlightRenderer _bracketRenderer;
        private readonly DispatcherTimer _foldingDebounceTimer;
        private readonly DispatcherTimer _diagDebounceTimer;

        private readonly List<EditorDiagnostic> _buildDiagnostics = new();
        private List<EditorDiagnostic> _activeTabDiagnostics = new();
        private int _diagnosticCycleIndex = -1;

        private readonly List<(int Start, int Length)> _findMatches = new();
        private int _currentFindMatchIdx = -1;
        private CancellationTokenSource? _loadCts;
        private ToolTip? _hoverToolTip;
        private IWorkspaceService? _workspaceService;

        public string? ActiveFilePath => _activeTab?.FilePath;
        public string ActiveText => _activeTab?.Document.Text ?? AvalonEditor.Text ?? string.Empty;
        public string ActiveOriginalDiskContent => _activeTab?.OriginalDiskContent ?? string.Empty;
        public IReadOnlyList<EditorTabState> OpenTabs => _tabs;

        public event Action<string, string, string>? ActiveBufferChanged; // filePath, original, current
        public event Action<string>? FileSaved;
        public event Action? DiffRequested;
        public event Action? CopyRequested;

        public CodeEditorWorkspace()
        {
            InitializeComponent();

            // Configure clean AvalonEdit behavior
            AvalonEditor.Options.ConvertTabsToSpaces = true;
            AvalonEditor.Options.IndentationSize = 4;
            AvalonEditor.Options.HighlightCurrentLine = true;
            AvalonEditor.Options.EnableEmailHyperlinks = false;
            AvalonEditor.Options.EnableHyperlinks = false;
            AvalonEditor.Options.ShowTabs = false;

            _markerService = new TextMarkerService(AvalonEditor.Document);
            _bracketRenderer = new BracketHighlightRenderer();
            AvalonEditor.TextArea.TextView.BackgroundRenderers.Add(_markerService);
            AvalonEditor.TextArea.TextView.BackgroundRenderers.Add(_bracketRenderer);
            _markerService.AddToTextView(AvalonEditor.TextArea.TextView);

            _foldingManager = FoldingManager.Install(AvalonEditor.TextArea);

            _foldingDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
            _foldingDebounceTimer.Tick += (_, _) =>
            {
                _foldingDebounceTimer.Stop();
                RefreshFoldings();
            };

            _diagDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _diagDebounceTimer.Tick += (_, _) =>
            {
                _diagDebounceTimer.Stop();
                RefreshDiagnosticsMarkersAndSummary();
            };

            AvalonEditor.TextArea.Caret.PositionChanged += Caret_PositionChanged;
            AvalonEditor.MouseHover += AvalonEditor_MouseHover;
            AvalonEditor.MouseHoverStopped += AvalonEditor_MouseHoverStopped;

            ApplyThemePalette(isDarkTheme: true);
            SetWelcomeState("LlmodelBox IDE ready.\nOpen a project file or ask the AI agent to write code.");
        }

        public void SetWorkspaceService(IWorkspaceService workspaceService)
        {
            _workspaceService = workspaceService;
        }

        public void ApplyThemePalette(bool isDarkTheme)
        {
            _isDarkTheme = isDarkTheme;
            AvalonEditor.TextArea.TextView.CurrentLineBackground = isDarkTheme
                ? new SolidColorBrush(Color.FromArgb(24, 148, 163, 184))
                : new SolidColorBrush(Color.FromArgb(24, 37, 99, 235));
            AvalonEditor.TextArea.TextView.CurrentLineBorder = new Pen(
                new SolidColorBrush(Color.FromArgb(40, 148, 163, 184)), 1);

            _bracketRenderer.SetPalette(
                isDarkTheme ? Color.FromArgb(75, 59, 130, 246) : Color.FromArgb(55, 37, 99, 235),
                isDarkTheme ? Color.FromArgb(180, 96, 165, 250) : Color.FromArgb(170, 37, 99, 235));

            ApplySyntaxHighlightingForActiveTab();
            AvalonEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
        }

        public void SetWelcomeState(string welcomeText)
        {
            _tabs.Clear();
            var welcome = new EditorTabState
            {
                FilePath = string.Empty,
                Language = EditorLanguageService.DetectLanguage("welcome.md"),
                Document = new TextDocument(welcomeText ?? string.Empty),
                OriginalDiskContent = welcomeText ?? string.Empty,
                IsDirty = false
            };
            ActivateTab(welcome, addIfMissing: false);
        }

        public async Task OpenFileAsync(string filePath, int targetLine = 0, int targetCol = 1)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

            var existing = _tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                ActivateTab(existing, addIfMissing: false);
                if (targetLine > 0) GoToLocation(targetLine, targetCol);
                return;
            }

            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            LoadingPillOverlay.Visibility = Visibility.Visible;
            try
            {
                var (content, truncated, encodingName) = await Task.Run(() => ReadFileSafeBackground(filePath, token), token);
                if (token.IsCancellationRequested) return;

                var lang = EditorLanguageService.DetectLanguage(filePath);
                var doc = new TextDocument(content);
                var tab = new EditorTabState
                {
                    FilePath = filePath,
                    Language = lang,
                    Document = doc,
                    OriginalDiskContent = content,
                    IsDirty = false,
                    IsLargeTruncated = truncated,
                    EncodingName = encodingName
                };

                ActivateTab(tab, addIfMissing: true);
                if (targetLine > 0) GoToLocation(targetLine, targetCol);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open file: {ex.Message}", "File Open Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                LoadingPillOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private (string Content, bool Truncated, string EncodingName) ReadFileSafeBackground(string filePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var fi = new FileInfo(filePath);
            if (fi.Length > 4 * 1024 * 1024)
            {
                using var sr = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                char[] buf = new char[120_000];
                int count = sr.ReadBlock(buf, 0, buf.Length);
                return ("[NOTICE: File exceeds 4MB. Showing non-blocking read-only preview of first 120,000 characters]\n\n" + new string(buf, 0, count), true, "UTF-8 (Preview)");
            }

            string text = _workspaceService != null ? _workspaceService.ReadFileContent(filePath) : File.ReadAllText(filePath, Encoding.UTF8);
            return (text, false, "UTF-8");
        }

        private void ActivateTab(EditorTabState tab, bool addIfMissing)
        {
            if (_activeTab != null)
            {
                _activeTab.CaretOffset = AvalonEditor.CaretOffset;
                _activeTab.VerticalScrollOffset = AvalonEditor.VerticalOffset;
                _activeTab.Document.TextChanged -= ActiveDocument_TextChanged;
            }

            if (addIfMissing && !_tabs.Any(t => string.Equals(t.FilePath, tab.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                _tabs.Add(tab);
            }

            _activeTab = tab;
            _suppressDocumentChanged = true;
            try
            {
                if (_foldingManager != null)
                {
                    try { FoldingManager.Uninstall(_foldingManager); } catch { }
                    _foldingManager = null;
                }

                AvalonEditor.Document = tab.Document;
                AvalonEditor.IsReadOnly = tab.IsLargeTruncated;
                AvalonEditor.TextArea.IndentationStrategy = new DefaultIndentationStrategy();
                _markerService.SetDocument(tab.Document);

                _foldingManager = FoldingManager.Install(AvalonEditor.TextArea);

                tab.Document.TextChanged -= ActiveDocument_TextChanged;
                tab.Document.TextChanged += ActiveDocument_TextChanged;

                ApplySyntaxHighlightingForActiveTab();
            }
            finally
            {
                _suppressDocumentChanged = false;
            }

            if (tab.CaretOffset >= 0 && tab.CaretOffset <= tab.Document.TextLength)
                AvalonEditor.CaretOffset = tab.CaretOffset;
            if (tab.VerticalScrollOffset > 0)
                AvalonEditor.ScrollToVerticalOffset(tab.VerticalScrollOffset);

            TxtActiveFileName.Text = tab.FileName;
            TxtEditorEncoding.Text = tab.EncodingName;
            TxtEditorLanguage.Text = tab.Language.DisplayName;
            TxtLargeFileNotice.Visibility = (tab.IsLargeTruncated || tab.Document.TextLength > LargeSyntaxThresholdChars)
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateDirtyVisualState();
            RefreshFoldings();
            RefreshAiProposalOverlay();
            RefreshDiagnosticsMarkersAndSummary();
            RenderTabsStrip();
            UpdateCursorTelemetry();

            if (FindReplaceOverlay.Visibility == Visibility.Visible && !string.IsNullOrEmpty(TxtFindQuery.Text))
                ExecuteFindAll(jumpToClosest: false);
        }

        private void ApplySyntaxHighlightingForActiveTab()
        {
            if (_activeTab == null || _activeTab.Document.TextLength > LargeSyntaxThresholdChars)
            {
                AvalonEditor.SyntaxHighlighting = null;
                return;
            }

            AvalonEditor.SyntaxHighlighting = EditorLanguageService.GetThemedHighlighting(_activeTab.Language, _isDarkTheme);
        }

        private void ActiveDocument_TextChanged(object? sender, EventArgs e)
        {
            if (_suppressDocumentChanged || _activeTab == null) return;

            string currentText = _activeTab.Document.Text;
            _activeTab.IsDirty = !string.Equals(currentText, _activeTab.OriginalDiskContent, StringComparison.Ordinal);
            UpdateDirtyVisualState();
            RenderTabsStrip();

            _foldingDebounceTimer.Stop();
            _foldingDebounceTimer.Start();

            _diagDebounceTimer.Stop();
            _diagDebounceTimer.Start();

            if (FindReplaceOverlay.Visibility == Visibility.Visible && !string.IsNullOrEmpty(TxtFindQuery.Text))
                ExecuteFindAll(jumpToClosest: false);

            ActiveBufferChanged?.Invoke(_activeTab.FilePath, _activeTab.OriginalDiskContent, currentText);
        }

        private void UpdateDirtyVisualState()
        {
            bool dirty = _activeTab?.IsDirty == true;
            TxtEditorStatus.Text = dirty ? "● Modified" : "✓ Saved";
            TxtEditorStatus.Foreground = Brush(dirty ? "BrushAccentLight" : "BrushSuccess", dirty ? "#38bdf8" : "#22c55e");
            DirtyStatusPill.Background = Brush(dirty ? "BrushAccentGlow" : "BrushSuccessGlow", dirty ? "#172554" : "#052e16");
        }

        private void RefreshFoldings()
        {
            if (_foldingManager == null || _activeTab == null) return;
            if (_activeTab.Document.TextLength > LargeSyntaxThresholdChars)
            {
                _foldingManager.UpdateFoldings(Array.Empty<NewFolding>(), -1);
                return;
            }

            var foldings = EditorLanguageService.ComputeFoldings(_activeTab.Document, _activeTab.Language.FoldingKind, out int firstErr);
            _foldingManager.UpdateFoldings(foldings, firstErr);
        }

        public bool SaveActiveFile(string? fallbackPath = null)
        {
            if (_activeTab == null) return false;
            if (_activeTab.IsLargeTruncated)
            {
                MessageBox.Show("Large file safe preview (>4MB) cannot overwrite disk file directly.", "Read-Only Preview", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            string target = !string.IsNullOrEmpty(_activeTab.FilePath) ? _activeTab.FilePath : (fallbackPath ?? string.Empty);
            if (string.IsNullOrEmpty(target)) return false;

            string content = _activeTab.Document.Text;
            bool ok = _workspaceService != null
                ? _workspaceService.SaveFileContent(target, content)
                : WriteAtomicFallback(target, content);

            if (ok)
            {
                _activeTab.FilePath = target;
                _activeTab.Language = EditorLanguageService.DetectLanguage(target);
                _activeTab.OriginalDiskContent = content;
                _activeTab.IsDirty = false;
                TxtEditorLanguage.Text = _activeTab.Language.DisplayName;
                ApplySyntaxHighlightingForActiveTab();
                UpdateDirtyVisualState();
                RenderTabsStrip();
                FileSaved?.Invoke(target);
            }
            return ok;
        }

        private static bool WriteAtomicFallback(string target, string content)
        {
            try
            {
                LlmodelBox.Common.AtomicFile.WriteAllText(target, content);
                return true;
            }
            catch { return false; }
        }

        public void NotifyFileRenamedOrMoved(string oldPath, string newPath)
        {
            var tab = _tabs.FirstOrDefault(t => string.Equals(t.FilePath, oldPath, StringComparison.OrdinalIgnoreCase));
            if (tab == null) return;
            tab.FilePath = newPath;
            tab.Language = EditorLanguageService.DetectLanguage(newPath);
            if (tab == _activeTab)
            {
                TxtActiveFileName.Text = tab.FileName;
                TxtEditorLanguage.Text = tab.Language.DisplayName;
                ApplySyntaxHighlightingForActiveTab();
            }
            RenderTabsStrip();
        }

        public void CloseTabByPath(string filePath)
        {
            var tab = _tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (tab != null) CloseTab(tab, forceDiscard: true);
        }

        private void CloseTab(EditorTabState tab, bool forceDiscard = false)
        {
            if (!forceDiscard && tab.IsDirty)
            {
                var res = MessageBox.Show($"Save unsaved changes to '{tab.FileName}' before closing?", "Unsaved File", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (res == MessageBoxResult.Cancel) return;
                if (res == MessageBoxResult.Yes)
                {
                    if (_activeTab != tab) ActivateTab(tab, addIfMissing: false);
                    if (!SaveActiveFile()) return;
                }
            }

            int idx = _tabs.IndexOf(tab);
            _tabs.Remove(tab);
            if (_activeTab == tab)
            {
                if (_tabs.Count > 0)
                {
                    var next = _tabs[Math.Clamp(idx - 1, 0, _tabs.Count - 1)];
                    ActivateTab(next, addIfMissing: false);
                }
                else
                {
                    _activeTab = null;
                    SetWelcomeState("# Workspace ready. Select a file from Explorer to start editing.");
                }
            }
            else
            {
                RenderTabsStrip();
            }
        }

        public void GoToLocation(int line, int column = 1)
        {
            if (AvalonEditor.Document == null || AvalonEditor.Document.LineCount == 0) return;
            int safeLine = Math.Clamp(line, 1, AvalonEditor.Document.LineCount);
            var docLine = AvalonEditor.Document.GetLineByNumber(safeLine);
            int safeCol = Math.Clamp(column, 1, docLine.Length + 1);
            AvalonEditor.CaretOffset = docLine.Offset + (safeCol - 1);
            AvalonEditor.ScrollToLine(safeLine);
            AvalonEditor.Focus();
            UpdateCursorTelemetry();
        }

        public void SetBuildDiagnostics(IEnumerable<EditorDiagnostic> diagnostics)
        {
            _buildDiagnostics.Clear();
            if (diagnostics != null) _buildDiagnostics.AddRange(diagnostics);
            RefreshDiagnosticsMarkersAndSummary();
        }

        public void ApplyAiInlineChangePreview(string filePath, string beforeContent, string afterContent, int additions, int deletions)
        {
            var proposal = EditorDiagnosticsHelper.BuildInlineProposal(filePath, beforeContent, afterContent, additions, deletions);
            var tab = _tabs.FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (tab != null)
            {
                tab.AiProposal = proposal;
                if (tab == _activeTab) RefreshAiProposalOverlay();
            }
        }

        private void RefreshAiProposalOverlay()
        {
            _markerService.RemoveAll(m => m.MarkerType == TextMarkerType.AiAddedLine);
            var proposal = _activeTab?.AiProposal;
            if (proposal == null || _activeTab == null)
            {
                AiInlinePreviewBanner.Visibility = Visibility.Collapsed;
                return;
            }

            TxtAiProposalDelta.Text = proposal.Summary;
            AiInlinePreviewBanner.Visibility = Visibility.Visible;

            var doc = _activeTab.Document;
            foreach (int ln in proposal.ChangedLineNumbers)
            {
                if (ln < 1 || ln > doc.LineCount) continue;
                var line = doc.GetLineByNumber(ln);
                var marker = _markerService.Create(line.Offset, Math.Max(1, line.Length));
                marker.MarkerType = TextMarkerType.AiAddedLine;
                marker.BackgroundColor = Color.FromArgb(40, 34, 197, 94);
                marker.MarkerColor = Color.FromRgb(34, 197, 94);
                marker.ToolTipText = "AI-modified line (use Accept AI or Reject / Revert above)";
            }
        }

        private void RefreshDiagnosticsMarkersAndSummary()
        {
            _markerService.RemoveAll(m => m.MarkerType == TextMarkerType.Squiggle);
            _activeTabDiagnostics = new List<EditorDiagnostic>();
            if (_activeTab == null || string.IsNullOrEmpty(_activeTab.FilePath))
            {
                TxtDiagnosticsSummary.Text = "0 Errors · 0 Warnings";
                DiagStatusDot.Fill = Brush("BrushSuccess", "#22c55e");
                TxtDiagnosticActiveMsg.Text = string.Empty;
                return;
            }

            string activePath = _activeTab.FilePath;
            foreach (var d in _buildDiagnostics)
            {
                if (string.Equals(d.FilePath, activePath, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(d.FilePath).Equals(Path.GetFileName(activePath), StringComparison.OrdinalIgnoreCase))
                {
                    _activeTabDiagnostics.Add(d);
                }
            }

            var structural = EditorDiagnosticsHelper.AnalyzeLightweight(activePath, _activeTab.Language.Id, _activeTab.Document.Text);
            _activeTabDiagnostics.AddRange(structural);

            var doc = _activeTab.Document;
            foreach (var d in _activeTabDiagnostics)
            {
                if (d.Line < 1 || d.Line > doc.LineCount) continue;
                var line = doc.GetLineByNumber(d.Line);
                int colIdx = Math.Clamp(d.Column - 1, 0, line.Length);
                int offset = line.Offset + colIdx;
                int len = Math.Clamp(d.Length, 1, Math.Max(1, line.Length - colIdx));
                var marker = _markerService.Create(offset, len);
                marker.MarkerType = TextMarkerType.Squiggle;
                marker.MarkerColor = d.Severity switch
                {
                    DiagnosticSeverity.Error => Color.FromRgb(248, 81, 73),
                    DiagnosticSeverity.Warning => Color.FromRgb(251, 191, 36),
                    _ => Color.FromRgb(96, 165, 250)
                };
                marker.ToolTipText = string.IsNullOrEmpty(d.Code) ? d.Message : $"[{d.Code}] {d.Message}";
            }

            int errors = _activeTabDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            int warnings = _activeTabDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
            TxtDiagnosticsSummary.Text = $"{errors} Errors · {warnings} Warnings";
            DiagStatusDot.Fill = errors > 0
                ? Brush("BrushDanger", "#f43f5e")
                : warnings > 0 ? Brush("BrushWarning", "#f59e0b") : Brush("BrushSuccess", "#22c55e");
            TxtDiagnosticActiveMsg.Text = _activeTabDiagnostics.FirstOrDefault()?.Message ?? string.Empty;
        }

        private void RenderTabsStrip()
        {
            EditorTabsPanel.Children.Clear();
            foreach (var tab in _tabs)
            {
                bool isActive = tab == _activeTab;
                var tabBorder = new Border
                {
                    Background = isActive ? Brush("BrushCardBg", "#111721") : Brushes.Transparent,
                    BorderBrush = isActive ? Brush("BrushAccent", "#3b82f6") : Brush("BrushBorderSubtle", "#1a2230"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Margin = new Thickness(0, 2, 5, 2),
                    Padding = new Thickness(8, 3, 6, 3),
                    ToolTip = tab.FilePath
                };

                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var badge = new Border
                {
                    Background = Brush("BrushCardInnerBg", "#18202e"),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock
                    {
                        Text = tab.Language.Badge,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 8.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brush("BrushAccentLight", "#60a5fa")
                    }
                };
                var captured = tab;
                var titleBtn = new Button
                {
                    Content = tab.FileName + (tab.IsDirty ? " ●" : ""),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = isActive ? Brush("BrushTextPrimary", "#f8fafc") : Brush("BrushTextSecondary", "#9ca3af"),
                    FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                    FontSize = 11,
                    Cursor = Cursors.Hand
                };
                titleBtn.Click += (_, _) => ActivateTab(captured, addIfMissing: false);

                var closeBtn = new Button
                {
                    Content = "✕",
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = Brush("BrushTextDim", "#71717a"),
                    FontSize = 9.5,
                    Margin = new Thickness(6, 0, 0, 0),
                    Cursor = Cursors.Hand
                };
                closeBtn.Click += (_, _) => CloseTab(captured);

                tabBorder.MouseDown += (_, me) =>
                {
                    if (me.ChangedButton == MouseButton.Middle) { me.Handled = true; CloseTab(captured); }
                };

                sp.Children.Add(badge);
                sp.Children.Add(titleBtn);
                sp.Children.Add(closeBtn);
                tabBorder.Child = sp;
                EditorTabsPanel.Children.Add(tabBorder);
            }
        }

        // =================== CARET, BRACKET MATCHING & HOVER DIAGNOSTICS ===================
        private void Caret_PositionChanged(object? sender, EventArgs e)
        {
            UpdateCursorTelemetry();
            var (open, close) = BracketHighlightRenderer.FindMatchingBracket(AvalonEditor.Text ?? string.Empty, AvalonEditor.CaretOffset);
            _bracketRenderer.UpdateHighlight(open, close, AvalonEditor.TextArea.TextView);
        }

        private void UpdateCursorTelemetry()
        {
            var loc = AvalonEditor.TextArea.Caret.Location;
            TxtCursorPosition.Text = $"Ln {loc.Line}, Col {loc.Column}";
        }

        private void AvalonEditor_MouseHover(object sender, MouseEventArgs e)
        {
            var pos = AvalonEditor.GetPositionFromPoint(e.GetPosition(AvalonEditor));
            if (!pos.HasValue || AvalonEditor.Document == null) return;
            int offset = AvalonEditor.Document.GetOffset(pos.Value.Location);
            var marker = _markerService.GetMarkersAtOffset(offset).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.ToolTipText));
            if (marker == null) return;

            _hoverToolTip ??= new ToolTip();
            _hoverToolTip.PlacementTarget = AvalonEditor;
            _hoverToolTip.Content = marker.ToolTipText;
            _hoverToolTip.IsOpen = true;
            e.Handled = true;
        }

        private void AvalonEditor_MouseHoverStopped(object sender, MouseEventArgs e)
        {
            if (_hoverToolTip != null) _hoverToolTip.IsOpen = false;
        }

        // =================== FIND & REPLACE + GO TO LINE + FOLDING ===================
        private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (ctrl && e.Key == Key.F)
            {
                OpenFindOverlay(showReplace: false);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.H)
            {
                OpenFindOverlay(showReplace: true);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.G)
            {
                OpenGoToLineOverlay();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.S)
            {
                SaveActiveFile();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (FindReplaceOverlay.Visibility == Visibility.Visible) { BtnCloseFind_Click(this, e); e.Handled = true; }
                else if (GoToLineOverlay.Visibility == Visibility.Visible) { BtnCloseGoToLine_Click(this, e); e.Handled = true; }
            }
        }

        private void OpenFindOverlay(bool showReplace)
        {
            FindReplaceOverlay.Visibility = Visibility.Visible;
            ReplaceRow.Visibility = showReplace ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrEmpty(AvalonEditor.SelectedText) && !AvalonEditor.SelectedText.Contains('\n'))
            {
                TxtFindQuery.Text = AvalonEditor.SelectedText;
            }
            TxtFindQuery.Focus();
            TxtFindQuery.SelectAll();
            ExecuteFindAll(jumpToClosest: false);
        }

        private void OpenGoToLineOverlay()
        {
            GoToLineOverlay.Visibility = Visibility.Visible;
            TxtGoToLineInput.Text = AvalonEditor.TextArea.Caret.Line.ToString();
            TxtGoToLineInput.Focus();
            TxtGoToLineInput.SelectAll();
        }

        private Regex? BuildFindRegex(string query)
        {
            if (string.IsNullOrEmpty(query)) return null;
            try
            {
                string pattern = TglRegex.IsChecked == true ? query : Regex.Escape(query);
                if (TglWholeWord.IsChecked == true) pattern = $@"\b{pattern}\b";
                var opts = TglMatchCase.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
                return new Regex(pattern, opts);
            }
            catch { return null; }
        }

        private void ExecuteFindAll(bool jumpToClosest)
        {
            _markerService.RemoveAll(m => m.Tag as string == "FIND");
            _findMatches.Clear();
            _currentFindMatchIdx = -1;

            string query = TxtFindQuery.Text ?? string.Empty;
            var rx = BuildFindRegex(query);
            if (rx == null)
            {
                TxtFindCount.Text = "0/0";
                return;
            }

            string docText = AvalonEditor.Text ?? string.Empty;
            foreach (Match m in rx.Matches(docText))
            {
                if (m.Length == 0) continue;
                _findMatches.Add((m.Index, m.Length));
                var mk = _markerService.Create(m.Index, m.Length);
                mk.MarkerType = TextMarkerType.Background;
                mk.BackgroundColor = Color.FromArgb(75, 245, 158, 11);
                mk.Tag = "FIND";
            }

            if (_findMatches.Count == 0)
            {
                TxtFindCount.Text = "0/0";
                return;
            }

            if (jumpToClosest)
            {
                int caret = AvalonEditor.CaretOffset;
                _currentFindMatchIdx = _findMatches.FindIndex(x => x.Start >= caret);
                if (_currentFindMatchIdx < 0) _currentFindMatchIdx = 0;
                SelectFindMatch(_currentFindMatchIdx);
            }
            else
            {
                _currentFindMatchIdx = 0;
                TxtFindCount.Text = $"{_findMatches.Count} found";
            }
        }

        private void SelectFindMatch(int index)
        {
            if (index < 0 || index >= _findMatches.Count) return;
            var (start, len) = _findMatches[index];
            AvalonEditor.Select(start, len);
            var loc = AvalonEditor.Document.GetLocation(start);
            AvalonEditor.ScrollToLine(loc.Line);
            TxtFindCount.Text = $"{index + 1}/{_findMatches.Count}";
        }

        private void TxtFindQuery_TextChanged(object sender, TextChangedEventArgs e) => ExecuteFindAll(jumpToClosest: true);
        private void SearchOption_Changed(object sender, RoutedEventArgs e) => ExecuteFindAll(jumpToClosest: true);

        private void TxtFindQuery_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) BtnFindPrev_Click(sender, e);
                else BtnFindNext_Click(sender, e);
            }
        }

        private void BtnFindNext_Click(object sender, RoutedEventArgs e)
        {
            if (_findMatches.Count == 0) { ExecuteFindAll(jumpToClosest: true); return; }
            _currentFindMatchIdx = (_currentFindMatchIdx + 1) % _findMatches.Count;
            SelectFindMatch(_currentFindMatchIdx);
        }

        private void BtnFindPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_findMatches.Count == 0) { ExecuteFindAll(jumpToClosest: true); return; }
            _currentFindMatchIdx = (_currentFindMatchIdx - 1 + _findMatches.Count) % _findMatches.Count;
            SelectFindMatch(_currentFindMatchIdx);
        }

        private void BtnReplaceOne_Click(object sender, RoutedEventArgs e)
        {
            if (_findMatches.Count == 0) return;
            if (_currentFindMatchIdx < 0 || _currentFindMatchIdx >= _findMatches.Count) _currentFindMatchIdx = 0;
            var (start, len) = _findMatches[_currentFindMatchIdx];
            AvalonEditor.Document.Replace(start, len, TxtReplaceWith.Text ?? string.Empty);
            ExecuteFindAll(jumpToClosest: true);
        }

        private void BtnReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            var rx = BuildFindRegex(TxtFindQuery.Text ?? string.Empty);
            if (rx == null) return;
            string replacement = TxtReplaceWith.Text ?? string.Empty;
            string updated = rx.Replace(AvalonEditor.Text ?? string.Empty, replacement);
            AvalonEditor.Document.BeginUpdate();
            AvalonEditor.Document.Replace(0, AvalonEditor.Document.TextLength, updated);
            AvalonEditor.Document.EndUpdate();
            ExecuteFindAll(jumpToClosest: false);
        }

        private void BtnCloseFind_Click(object sender, RoutedEventArgs e)
        {
            FindReplaceOverlay.Visibility = Visibility.Collapsed;
            _markerService.RemoveAll(m => m.Tag as string == "FIND");
            AvalonEditor.Focus();
        }

        private void TxtGoToLineInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; BtnJumpToLine_Click(sender, e); }
        }

        private void BtnJumpToLine_Click(object sender, RoutedEventArgs e)
        {
            string raw = (TxtGoToLineInput.Text ?? string.Empty).Trim();
            var parts = raw.Split(':', ',');
            if (int.TryParse(parts[0], out int ln))
            {
                int col = parts.Length > 1 && int.TryParse(parts[1], out int parsedCol) ? parsedCol : 1;
                GoToLocation(ln, col);
                GoToLineOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnCloseGoToLine_Click(object sender, RoutedEventArgs e)
        {
            GoToLineOverlay.Visibility = Visibility.Collapsed;
            AvalonEditor.Focus();
        }

        // =================== TOOLBAR & INLINE AI PROPOSAL BUTTONS ===================
        private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveActiveFile();
        private void BtnUndo_Click(object sender, RoutedEventArgs e) { if (AvalonEditor.CanUndo) AvalonEditor.Undo(); }
        private void BtnRedo_Click(object sender, RoutedEventArgs e) { if (AvalonEditor.CanRedo) AvalonEditor.Redo(); }
        private void BtnToggleFind_Click(object sender, RoutedEventArgs e) => OpenFindOverlay(showReplace: true);
        private void BtnToggleGoToLine_Click(object sender, RoutedEventArgs e) => OpenGoToLineOverlay();
        private void BtnDiff_Click(object sender, RoutedEventArgs e) => DiffRequested?.Invoke();

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(ActiveText);
                CopyRequested?.Invoke();
            }
            catch { }
        }

        private void BtnFoldToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_foldingManager == null) return;
            _allFolded = !_allFolded;
            foreach (var f in _foldingManager.AllFoldings)
            {
                f.IsFolded = _allFolded;
            }
        }

        private void BtnDiagnosticsBadge_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTabDiagnostics.Count == 0) return;
            _diagnosticCycleIndex = (_diagnosticCycleIndex + 1) % _activeTabDiagnostics.Count;
            var d = _activeTabDiagnostics[_diagnosticCycleIndex];
            GoToLocation(d.Line, d.Column);
            TxtDiagnosticActiveMsg.Text = d.Message;
        }

        private void BtnAcceptAiProposal_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTab == null) return;
            _activeTab.AiProposal = null;
            RefreshAiProposalOverlay();
        }

        private void BtnRejectAiProposal_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTab?.AiProposal == null) return;
            string before = _activeTab.AiProposal.BeforeContent;
            AvalonEditor.Document.Replace(0, AvalonEditor.Document.TextLength, before);
            _activeTab.AiProposal = null;
            RefreshAiProposalOverlay();
            SaveActiveFile();
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
    }
}
