using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Runtime.Content;
using LlmodelBox.Services;
using Microsoft.Win32;

namespace LlmodelBox.Controls.RichChat
{
    public partial class RichMessageView : UserControl
    {
        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register(
                nameof(Message),
                typeof(ChatMessageItem),
                typeof(RichMessageView),
                new PropertyMetadata(null, OnMessageChanged));

        public ChatMessageItem? Message
        {
            get => (ChatMessageItem?)GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }

        public event EventHandler? TaskChanged;
        public event EventHandler<DiffSummaryBlock>? ReviewChangesRequested;
        public event EventHandler<string>? OpenFileRequested;
        public event EventHandler<ChatMessageItem>? RetryRequested;

        private ChatMessageItem? _boundMessage;
        private string _renderedMessageId = string.Empty;
        private int _renderedRevision = -1;
        private RichTextBox? _streamingTrailingRtb;
        private DispatcherTimer? _animTimer;
        private int _animTick = 0;

        public RichMessageView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (Message == null && e.NewValue is ChatMessageItem item)
            {
                AttachMessage(item);
            }
        }

        private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RichMessageView view)
            {
                view.AttachMessage(e.NewValue as ChatMessageItem);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_boundMessage == null && Message != null)
            {
                AttachMessage(Message);
            }
            else if (_boundMessage == null && DataContext is ChatMessageItem dcItem)
            {
                AttachMessage(dcItem);
            }
            UpdateAnimationTimerState();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopAnimationTimer();
            if (_boundMessage != null)
            {
                _boundMessage.PropertyChanged -= OnMessagePropertyChanged;
            }
        }

        private void AttachMessage(ChatMessageItem? item)
        {
            if (ReferenceEquals(_boundMessage, item))
            {
                return;
            }

            if (_boundMessage != null)
            {
                _boundMessage.PropertyChanged -= OnMessagePropertyChanged;
            }

            _boundMessage = item;
            _renderedMessageId = string.Empty;
            _renderedRevision = -1;
            _streamingTrailingRtb = null;

            if (_boundMessage != null)
            {
                _boundMessage.PropertyChanged += OnMessagePropertyChanged;
            }

            UpdateActivityHeader();
            RebuildBlocksIfNeeded(force: true);
            UpdateToolTrace();
            UpdateAnimationTimerState();
        }

        private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => OnMessagePropertyChanged(sender, e));
                return;
            }

            if (_boundMessage == null) return;

            switch (e.PropertyName)
            {
                case nameof(ChatMessageItem.ActivityState):
                case nameof(ChatMessageItem.ActivityDetail):
                case nameof(ChatMessageItem.ActiveFilePath):
                case nameof(ChatMessageItem.FilesModifiedCount):
                case nameof(ChatMessageItem.AddedLinesCount):
                case nameof(ChatMessageItem.RemovedLinesCount):
                case nameof(ChatMessageItem.ElapsedSeconds):
                case nameof(ChatMessageItem.IsActiveInProgress):
                    UpdateActivityHeader();
                    UpdateAnimationTimerState();
                    break;

                case nameof(ChatMessageItem.Content):
                    if (_boundMessage.IsStreaming)
                    {
                        UpdateStreamingContentInPlace();
                    }
                    else
                    {
                        RebuildBlocksIfNeeded(force: true);
                    }
                    break;

                case nameof(ChatMessageItem.IsStreaming):
                    if (!_boundMessage.IsStreaming)
                    {
                        RebuildBlocksIfNeeded(force: true);
                    }
                    UpdateActivityHeader();
                    UpdateAnimationTimerState();
                    break;

                case nameof(ChatMessageItem.Blocks):
                case nameof(ChatMessageItem.BlocksRevision):
                    RebuildBlocksIfNeeded(force: true);
                    break;

                case nameof(ChatMessageItem.ToolSteps):
                    UpdateToolTrace();
                    break;
            }
        }

        private void UpdateAnimationTimerState()
        {
            bool shouldRun = IsLoaded && _boundMessage != null && _boundMessage.IsActiveInProgress;
            if (shouldRun)
            {
                if (_animTimer == null)
                {
                    _animTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(150)
                    };
                    _animTimer.Tick += OnAnimationTick;
                }
                if (!_animTimer.IsEnabled)
                {
                    _animTimer.Start();
                }
            }
            else
            {
                StopAnimationTimer();
            }
        }

        private void StopAnimationTimer()
        {
            if (_animTimer != null)
            {
                _animTimer.Stop();
                _animTimer.Tick -= OnAnimationTick;
                _animTimer = null;
            }
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            if (_boundMessage == null || !_boundMessage.IsActiveInProgress)
            {
                StopAnimationTimer();
                return;
            }

            _animTick++;
            if (ThreeDotsPanel.Visibility == Visibility.Visible)
            {
                int phase = _animTick % 4;
                Dot1.Opacity = phase == 0 ? 1.0 : 0.3;
                Dot2.Opacity = phase == 1 ? 1.0 : 0.3;
                Dot3.Opacity = phase == 2 ? 1.0 : 0.3;
            }
            if (SpinnerPanel.Visibility == Visibility.Visible)
            {
                SpinnerRotateTransform.Angle = (_animTick * 30) % 360;
            }
        }

        private void UpdateActivityHeader()
        {
            var msg = _boundMessage;
            if (msg == null)
            {
                ActivityHeaderBorder.Visibility = Visibility.Collapsed;
                return;
            }

            bool show = msg.IsActiveInProgress ||
                        msg.ActivityState == MessageActivityState.Cancelled ||
                        (msg.ActivityState == MessageActivityState.Failed && msg.Blocks.All(b => b is not ErrorBlock));

            if (!show)
            {
                ActivityHeaderBorder.Visibility = Visibility.Collapsed;
                return;
            }

            ActivityHeaderBorder.Visibility = Visibility.Visible;

            string actor = string.IsNullOrWhiteSpace(msg.ModelName)
                ? (string.IsNullOrWhiteSpace(msg.SenderName) ? "Assistant" : msg.SenderName)
                : msg.ModelName;

            string defaultLabel = msg.ActivityState switch
            {
                MessageActivityState.Queued => $"{actor} queued...",
                MessageActivityState.Connecting => $"Connecting to {actor}...",
                MessageActivityState.Thinking => $"{actor} is thinking...",
                MessageActivityState.Generating => $"{actor} is generating a response...",
                MessageActivityState.Streaming => $"{actor} is writing...",
                MessageActivityState.ExecutingTool => "Running workspace tool...",
                MessageActivityState.EditingFiles => "Editing project files...",
                MessageActivityState.Cancelled => "Generation cancelled by user.",
                MessageActivityState.Failed => "Generation failed.",
                _ => actor
            };

            TxtActivityText.Text = string.IsNullOrWhiteSpace(msg.ActivityDetail) ? defaultLabel : msg.ActivityDetail;
            TxtActivityStatusBadge.Text = string.IsNullOrWhiteSpace(msg.ActivityBadgeText) ? "ACTIVE" : msg.ActivityBadgeText;

            bool useSpinner = msg.ActivityState == MessageActivityState.ExecutingTool ||
                              msg.ActivityState == MessageActivityState.EditingFiles;
            ThreeDotsPanel.Visibility = (!useSpinner && msg.IsActiveInProgress) ? Visibility.Visible : Visibility.Collapsed;
            SpinnerPanel.Visibility = (useSpinner && msg.IsActiveInProgress) ? Visibility.Visible : Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(msg.ActiveFilePath))
            {
                ActiveFileChip.Visibility = Visibility.Visible;
                TxtActiveFile.Text = Path.GetFileName(msg.ActiveFilePath);
                ActiveFileChip.ToolTip = msg.ActiveFilePath;
            }
            else
            {
                ActiveFileChip.Visibility = Visibility.Collapsed;
            }

            if (msg.FilesModifiedCount > 0 || msg.AddedLinesCount > 0 || msg.RemovedLinesCount > 0)
            {
                TxtEditCounts.Visibility = Visibility.Visible;
                TxtEditCounts.Text = $"{msg.FilesModifiedCount} file(s)  +{msg.AddedLinesCount} / -{msg.RemovedLinesCount}";
            }
            else
            {
                TxtEditCounts.Visibility = Visibility.Collapsed;
            }

            if (msg.ElapsedSeconds >= 0.5)
            {
                TxtElapsedTime.Visibility = Visibility.Visible;
                TxtElapsedTime.Text = $"{msg.ElapsedSeconds:F1}s";
            }
            else
            {
                TxtElapsedTime.Visibility = Visibility.Collapsed;
            }

            // Semantic pill & border colors
            if (msg.ActivityState == MessageActivityState.Failed)
            {
                SetActivityPalette("#18ef4444", "#ef4444");
            }
            else if (msg.ActivityState == MessageActivityState.Cancelled)
            {
                SetActivityPalette("#18f59e0b", "#f59e0b");
            }
            else if (msg.ActivityState == MessageActivityState.ExecutingTool)
            {
                SetActivityPalette("#1422c55e", "#22c55e");
            }
            else if (msg.ActivityState == MessageActivityState.EditingFiles)
            {
                SetActivityPalette("#143b82f6", "#60a5fa");
            }
            else
            {
                SetActivityPalette("#1238bdf8", "#38bdf8");
            }
        }

        private void SetActivityPalette(string bgHex, string accentHex)
        {
            try
            {
                var bg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgHex));
                bg.Freeze();
                ActivityHeaderBorder.Background = bg;

                var accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accentHex));
                accent.Freeze();
                TxtActivityStatusBadge.Foreground = accent;
                ActivityStatusPill.BorderBrush = accent;

                var pillBg = new SolidColorBrush(Color.FromArgb(36, accent.Color.R, accent.Color.G, accent.Color.B));
                pillBg.Freeze();
                ActivityStatusPill.Background = pillBg;
            }
            catch { }
        }

        private void UpdateToolTrace()
        {
            if (_boundMessage == null || _boundMessage.ToolSteps == null || _boundMessage.ToolSteps.Count == 0)
            {
                ToolTraceExpander.Visibility = Visibility.Collapsed;
                ToolTraceList.Children.Clear();
                return;
            }

            ToolTraceExpander.Visibility = Visibility.Visible;
            TxtToolTraceHeader.Text = $"Execution steps ({_boundMessage.ToolSteps.Count})";
            ToolTraceList.Children.Clear();

            foreach (var step in _boundMessage.ToolSteps)
            {
                ToolTraceList.Children.Add(new TextBlock
                {
                    Text = $"• {step}",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 1)
                });
            }
        }

        private void UpdateStreamingContentInPlace()
        {
            if (_boundMessage == null) return;

            if (_streamingTrailingRtb == null)
            {
                BlocksHost.Children.Clear();
                _streamingTrailingRtb = CreateMarkdownRtb(_boundMessage.Content, showCursor: true);
                BlocksHost.Children.Add(_streamingTrailingRtb);
                return;
            }

            _streamingTrailingRtb.Document = RichMarkdownView.Build(_boundMessage.Content, this, showCursor: true);
        }

        private void RebuildBlocksIfNeeded(bool force)
        {
            var msg = _boundMessage;
            if (msg == null)
            {
                BlocksHost.Children.Clear();
                _streamingTrailingRtb = null;
                return;
            }

            if (!force &&
                string.Equals(_renderedMessageId, msg.Id, StringComparison.Ordinal) &&
                _renderedRevision == msg.BlocksRevision)
            {
                return;
            }

            _renderedMessageId = msg.Id;
            _renderedRevision = msg.BlocksRevision;
            _streamingTrailingRtb = null;
            BlocksHost.Children.Clear();

            if (msg.IsStreaming && (msg.Blocks == null || msg.Blocks.Count == 0))
            {
                if (!string.IsNullOrEmpty(msg.Content))
                {
                    _streamingTrailingRtb = CreateMarkdownRtb(msg.Content, showCursor: true);
                    BlocksHost.Children.Add(_streamingTrailingRtb);
                }
                return;
            }

            IReadOnlyList<ChatContentBlock> blocks = msg.Blocks;
            if ((blocks == null || blocks.Count == 0) && !string.IsNullOrWhiteSpace(msg.Content))
            {
                blocks = StructuredContentParser.Parse(msg.Content);
            }

            if (blocks == null || blocks.Count == 0)
            {
                return;
            }

            for (int i = 0; i < blocks.Count; i++)
            {
                bool isLast = i == blocks.Count - 1;
                var element = BuildBlockVisual(blocks[i], showCursor: isLast && msg.IsStreaming);
                if (element is FrameworkElement fe)
                {
                    if (i > 0)
                    {
                        fe.Margin = new Thickness(0, 8, 0, 0);
                    }
                    if (isLast && blocks[i] is MarkdownBlock && element is RichTextBox rtb)
                    {
                        _streamingTrailingRtb = rtb;
                    }
                }
                if (element != null)
                {
                    BlocksHost.Children.Add(element);
                }
            }
        }

        private UIElement? BuildBlockVisual(ChatContentBlock block, bool showCursor)
        {
            return block switch
            {
                MarkdownBlock md => CreateMarkdownRtb(md.Text, showCursor),
                CodeBlock code => BuildCodeCard(code),
                TableBlock table => BuildTableCard(table),
                TaskListBlock taskList => BuildTaskListCard(taskList),
                DiffSummaryBlock summary => BuildDiffSummaryCard(summary),
                CodeDiffBlock diff => BuildCodeDiffCard(diff),
                ChartBlock chart => BuildChartCard(chart),
                MermaidBlock mermaid => BuildMermaidCard(mermaid),
                FileAttachmentBlock file => BuildFileCard(file),
                ProgressBlock progress => BuildProgressCard(progress),
                CitationBlock citation => BuildCitationCard(citation),
                ErrorBlock err => BuildErrorCard(err),
                ImageBlock img => BuildImageCard(img),
                _ => null
            };
        }

        private RichTextBox CreateMarkdownRtb(string? text, bool showCursor)
        {
            var rtb = new RichTextBox
            {
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                IsDocumentEnabled = true,
                Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                Document = RichMarkdownView.Build(text, this, showCursor)
            };
            return rtb;
        }

        private UIElement BuildCodeCard(CodeBlock code)
        {
            string normalized = (code.Code ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = normalized.Split('\n');
            const int CollapseThreshold = 18;
            bool isLong = lines.Length > CollapseThreshold;
            bool isExpanded = !isLong;

            var card = CreateCardContainer();
            var root = new StackPanel();
            card.Child = root;

            // Header bar
            var header = new Border
            {
                Background = ResolveBrush("BrushCardInnerBg", "#141418"),
                BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftInfo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string langText = string.IsNullOrWhiteSpace(code.Language) ? "CODE" : code.Language.Trim().ToUpperInvariant();
            leftInfo.Children.Add(CreatePill(langText, "#38bdf8"));
            if (!string.IsNullOrWhiteSpace(code.FilePath))
            {
                leftInfo.Children.Add(new TextBlock
                {
                    Text = code.FilePath,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                });
            }
            leftInfo.Children.Add(new TextBlock
            {
                Text = $"{lines.Length} lines",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = ResolveBrush("BrushTextDim", "#71717a"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            Grid.SetColumn(leftInfo, 0);
            headerGrid.Children.Add(leftInfo);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Button? expandBtn = null;
            if (isLong)
            {
                expandBtn = CreateMiniButton($"Expand ({lines.Length} lines)");
                expandBtn.Margin = new Thickness(0, 0, 6, 0);
                actions.Children.Add(expandBtn);
            }
            var copyBtn = CreateMiniButton("Copy");
            copyBtn.Click += (_, _) => CopyTextWithFeedback(normalized, copyBtn);
            actions.Children.Add(copyBtn);
            Grid.SetColumn(actions, 1);
            headerGrid.Children.Add(actions);

            header.Child = headerGrid;
            root.Children.Add(header);

            // Code viewport
            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0)
            };

            var codeHost = new Grid { Margin = new Thickness(8, 8, 10, 8) };
            codeHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            codeHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var gutter = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = ResolveBrush("BrushTextDim", "#52525b"),
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 12, 0),
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };

            var codeRtb = new RichTextBox
            {
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = ResolveBrush("BrushTextPrimary", "#e4e4e7")
            };

            bool isLightTheme = IsLightThemeHost();
            void RenderCodeLines(bool expanded)
            {
                int count = expanded ? lines.Length : Math.Min(CollapseThreshold, lines.Length);
                var gutterSb = new StringBuilder();
                var doc = new FlowDocument
                {
                    PagePadding = new Thickness(0),
                    PageWidth = 2400,
                    Background = Brushes.Transparent,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12
                };

                var p = new Paragraph
                {
                    Margin = new Thickness(0),
                    LineHeight = 18,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight
                };

                for (int i = 0; i < count; i++)
                {
                    gutterSb.Append(i + 1);
                    if (i < count - 1) gutterSb.Append('\n');
                    CodeSyntaxHighlighter.AppendHighlightedLine(p, lines[i], code.Language, isLightTheme);
                    if (i < count - 1)
                    {
                        p.Inlines.Add(new LineBreak());
                    }
                }

                gutter.Text = gutterSb.ToString();
                doc.Blocks.Add(p);
                codeRtb.Document = doc;
            }

            RenderCodeLines(isExpanded);
            Grid.SetColumn(gutter, 0);
            Grid.SetColumn(codeRtb, 1);
            codeHost.Children.Add(gutter);
            codeHost.Children.Add(codeRtb);
            scroller.Content = codeHost;
            root.Children.Add(scroller);

            if (expandBtn != null)
            {
                expandBtn.Click += (_, _) =>
                {
                    isExpanded = !isExpanded;
                    expandBtn.Content = isExpanded ? "Collapse" : $"Expand ({lines.Length} lines)";
                    RenderCodeLines(isExpanded);
                };
            }

            return card;
        }

        private UIElement BuildTableCard(TableBlock table)
        {
            var card = CreateCardContainer();
            var root = new StackPanel();
            card.Child = root;

            int sortColumn = -1;
            bool sortAscending = true;
            var currentRows = table.Rows.Select(r => new List<string>(r)).ToList();
            const int CollapseRows = 10;
            bool isLong = currentRows.Count > CollapseRows;
            bool isExpanded = !isLong;

            // Header
            var header = new Border
            {
                Background = ResolveBrush("BrushCardInnerBg", "#141418"),
                BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titlePanel.Children.Add(CreatePill("TABLE", "#a78bfa"));
            titlePanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(table.Caption)
                    ? $"{table.Headers.Count} cols • {currentRows.Count} rows"
                    : $"{table.Caption} ({currentRows.Count} rows)",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            Grid.SetColumn(titlePanel, 0);
            headerGrid.Children.Add(titlePanel);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            Button? expandBtn = null;
            if (isLong)
            {
                expandBtn = CreateMiniButton($"Show All ({currentRows.Count})");
                expandBtn.Margin = new Thickness(0, 0, 6, 0);
                actions.Children.Add(expandBtn);
            }
            var csvBtn = CreateMiniButton("Copy CSV");
            csvBtn.Click += (_, _) =>
            {
                var sb = new StringBuilder();
                if (table.Headers.Count > 0)
                {
                    sb.AppendLine(string.Join(",", table.Headers.Select(EscapeCsv)));
                }
                foreach (var row in currentRows)
                {
                    sb.AppendLine(string.Join(",", row.Select(EscapeCsv)));
                }
                CopyTextWithFeedback(sb.ToString().TrimEnd(), csvBtn);
            };
            actions.Children.Add(csvBtn);
            Grid.SetColumn(actions, 1);
            headerGrid.Children.Add(actions);
            header.Child = headerGrid;
            root.Children.Add(header);

            var tableScroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var tableGrid = new Grid();
            tableScroller.Content = tableGrid;
            root.Children.Add(tableScroller);

            void RenderTable()
            {
                tableGrid.Children.Clear();
                tableGrid.ColumnDefinitions.Clear();
                tableGrid.RowDefinitions.Clear();

                int cols = Math.Max(table.Headers.Count, currentRows.Count > 0 ? currentRows.Max(r => r.Count) : 0);
                if (cols == 0) return;

                for (int c = 0; c < cols; c++)
                {
                    tableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                }

                tableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int c = 0; c < cols; c++)
                {
                    int colIdx = c;
                    string colTitle = c < table.Headers.Count ? table.Headers[c] : $"Col {c + 1}";
                    string indicator = sortColumn == c ? (sortAscending ? " ▲" : " ▼") : string.Empty;

                    var hdrBtn = new Button
                    {
                        Content = colTitle + indicator,
                        Background = ResolveBrush("BrushCardInnerBg", "#18181b"),
                        Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                        BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                        BorderThickness = new Thickness(0, 0, 1, 1),
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 11.5,
                        Padding = new Thickness(10, 6, 10, 6),
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Cursor = Cursors.Hand
                    };
                    hdrBtn.Click += (_, _) =>
                    {
                        if (sortColumn == colIdx) sortAscending = !sortAscending;
                        else { sortColumn = colIdx; sortAscending = true; }

                        currentRows.Sort((a, b) =>
                        {
                            string sa = colIdx < a.Count ? a[colIdx] : string.Empty;
                            string sb = colIdx < b.Count ? b[colIdx] : string.Empty;
                            int cmp = CompareCellValues(sa, sb);
                            return sortAscending ? cmp : -cmp;
                        });
                        RenderTable();
                    };
                    Grid.SetRow(hdrBtn, 0);
                    Grid.SetColumn(hdrBtn, c);
                    tableGrid.Children.Add(hdrBtn);
                }

                int rowLimit = isExpanded ? currentRows.Count : Math.Min(CollapseRows, currentRows.Count);
                for (int r = 0; r < rowLimit; r++)
                {
                    tableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var rowBg = r % 2 == 1 ? ResolveBrush("BrushHoverBg", "#08ffffff") : Brushes.Transparent;
                    var rowData = currentRows[r];
                    for (int c = 0; c < cols; c++)
                    {
                        string val = c < rowData.Count ? rowData[c] : string.Empty;
                        bool isNum = double.TryParse(val.Replace(",", "").Replace("%", "").Replace("$", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out _);

                        var cellBorder = new Border
                        {
                            Background = rowBg,
                            BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                            BorderThickness = new Thickness(0, 0, 1, 1),
                            Padding = new Thickness(10, 5, 10, 5)
                        };
                        cellBorder.Child = new TextBlock
                        {
                            Text = val,
                            FontSize = 11.5,
                            FontFamily = isNum ? new FontFamily("Consolas") : new FontFamily("Segoe UI, sans-serif"),
                            TextAlignment = isNum ? TextAlignment.Right : TextAlignment.Left,
                            Foreground = ResolveBrush("BrushTextPrimary", "#e4e4e7")
                        };
                        Grid.SetRow(cellBorder, r + 1);
                        Grid.SetColumn(cellBorder, c);
                        tableGrid.Children.Add(cellBorder);
                    }
                }
            }

            if (expandBtn != null)
            {
                expandBtn.Click += (_, _) =>
                {
                    isExpanded = !isExpanded;
                    expandBtn.Content = isExpanded ? "Collapse" : $"Show All ({currentRows.Count})";
                    RenderTable();
                };
            }

            RenderTable();
            return card;
        }

        private UIElement BuildTaskListCard(TaskListBlock taskList)
        {
            var card = CreateCardContainer();
            var root = new StackPanel();
            card.Child = root;

            bool isExpanded = true;

            var header = new Border
            {
                Background = ResolveBrush("BrushCardInnerBg", "#141418"),
                BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Padding = new Thickness(10, 7, 10, 7)
            };

            var hdrGrid = new Grid();
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(CreatePill("TASKS", "#22c55e"));
            left.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(taskList.Title) ? "Tasks" : taskList.Title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(left, 0);
            hdrGrid.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var progressText = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var toggleBtn = CreateMiniButton("Collapse");
            right.Children.Add(progressText);
            right.Children.Add(toggleBtn);
            Grid.SetColumn(right, 1);
            hdrGrid.Children.Add(right);

            header.Child = hdrGrid;
            root.Children.Add(header);

            var progressBar = new ProgressBar
            {
                Height = 4,
                Minimum = 0,
                Maximum = 100,
                Foreground = ResolveBrush("BrushAccent", "#38bdf8"),
                Background = ResolveBrush("BrushCardInnerBg", "#18181b"),
                BorderThickness = new Thickness(0)
            };
            root.Children.Add(progressBar);

            var itemsPanel = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
            root.Children.Add(itemsPanel);

            void RefreshTasks()
            {
                int total = taskList.Tasks.Count;
                int done = taskList.Tasks.Count(t => t.IsCompleted);
                double pct = total > 0 ? Math.Round(done * 100.0 / total, 0) : 0;
                progressText.Text = $"{done}/{total} ({pct:0}%)";
                progressBar.Value = pct;

                itemsPanel.Children.Clear();
                itemsPanel.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
                if (!isExpanded) return;

                for (int i = 0; i < taskList.Tasks.Count; i++)
                {
                    int idx = i;
                    var task = taskList.Tasks[i];

                    var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var cb = new CheckBox
                    {
                        IsChecked = task.IsCompleted,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 2, 8, 0)
                    };
                    var textStack = new StackPanel();
                    var titleTb = new TextBlock
                    {
                        Text = task.Title,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = task.IsCompleted
                            ? ResolveBrush("BrushTextDim", "#71717a")
                            : ResolveBrush("BrushTextPrimary", "#fafafa"),
                        TextDecorations = task.IsCompleted ? TextDecorations.Strikethrough : null
                    };
                    textStack.Children.Add(titleTb);
                    if (!string.IsNullOrWhiteSpace(task.Description))
                    {
                        textStack.Children.Add(new TextBlock
                        {
                            Text = task.Description,
                            FontSize = 11,
                            Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa"),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 1, 0, 0)
                        });
                    }

                    cb.Checked += (_, _) =>
                    {
                        task.IsCompleted = true;
                        RefreshTasks();
                        TaskChanged?.Invoke(this, EventArgs.Empty);
                    };
                    cb.Unchecked += (_, _) =>
                    {
                        task.IsCompleted = false;
                        RefreshTasks();
                        TaskChanged?.Invoke(this, EventArgs.Empty);
                    };

                    var movePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 0, 0) };
                    var upBtn = CreateMiniButton("↑");
                    upBtn.Padding = new Thickness(4, 1, 4, 1);
                    upBtn.IsEnabled = idx > 0;
                    upBtn.Click += (_, _) =>
                    {
                        (taskList.Tasks[idx - 1], taskList.Tasks[idx]) = (taskList.Tasks[idx], taskList.Tasks[idx - 1]);
                        RefreshTasks();
                        TaskChanged?.Invoke(this, EventArgs.Empty);
                    };
                    var downBtn = CreateMiniButton("↓");
                    downBtn.Padding = new Thickness(4, 1, 4, 1);
                    downBtn.Margin = new Thickness(3, 0, 0, 0);
                    downBtn.IsEnabled = idx < taskList.Tasks.Count - 1;
                    downBtn.Click += (_, _) =>
                    {
                        (taskList.Tasks[idx + 1], taskList.Tasks[idx]) = (taskList.Tasks[idx], taskList.Tasks[idx + 1]);
                        RefreshTasks();
                        TaskChanged?.Invoke(this, EventArgs.Empty);
                    };
                    movePanel.Children.Add(upBtn);
                    movePanel.Children.Add(downBtn);

                    Grid.SetColumn(cb, 0);
                    Grid.SetColumn(textStack, 1);
                    Grid.SetColumn(movePanel, 2);
                    row.Children.Add(cb);
                    row.Children.Add(textStack);
                    row.Children.Add(movePanel);
                    itemsPanel.Children.Add(row);
                }
            }

            toggleBtn.Click += (_, _) =>
            {
                isExpanded = !isExpanded;
                toggleBtn.Content = isExpanded ? "Collapse" : $"Expand ({taskList.Tasks.Count})";
                RefreshTasks();
            };

            RefreshTasks();
            return card;
        }

        private UIElement BuildDiffSummaryCard(DiffSummaryBlock summary)
        {
            var card = CreateCardContainer();
            var root = new StackPanel();
            card.Child = root;

            bool isExpanded = true;

            var header = new Border
            {
                Background = ResolveBrush("BrushCardInnerBg", "#141418"),
                BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Padding = new Thickness(10, 7, 10, 7)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(CreatePill("CHANGES", "#60a5fa"));
            int count = summary.FilesChanged > 0 ? summary.FilesChanged : summary.Files.Count;
            left.Children.Add(new TextBlock
            {
                Text = $"{count} file(s) changed",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            left.Children.Add(new TextBlock
            {
                Text = $"+{summary.Additions}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            left.Children.Add(new TextBlock
            {
                Text = $"-{summary.Deletions}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71)),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            var toggleBtn = CreateMiniButton("Hide");
            toggleBtn.Margin = new Thickness(0, 0, 6, 0);
            var reviewBtn = CreateMiniButton("Review Changes", accent: true);
            reviewBtn.Click += (_, _) => ReviewChangesRequested?.Invoke(this, summary);
            right.Children.Add(toggleBtn);
            right.Children.Add(reviewBtn);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            header.Child = grid;
            root.Children.Add(header);

            var fileList = new StackPanel { Margin = new Thickness(10, 6, 10, 8) };
            foreach (var f in summary.Files)
            {
                var fRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                fRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                fRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var pathBtn = new Button
                {
                    Content = f.FilePath,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = ResolveBrush("BrushAccent", "#38bdf8"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11.5,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(0)
                };
                pathBtn.Click += (_, _) => OpenFileRequested?.Invoke(this, f.FilePath);
                Grid.SetColumn(pathBtn, 0);
                fRow.Children.Add(pathBtn);

                var deltaPanel = new StackPanel { Orientation = Orientation.Horizontal };
                deltaPanel.Children.Add(new TextBlock
                {
                    Text = $"+{f.Additions}",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                    Margin = new Thickness(8, 0, 6, 0)
                });
                deltaPanel.Children.Add(new TextBlock
                {
                    Text = $"-{f.Deletions}",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71))
                });
                Grid.SetColumn(deltaPanel, 1);
                fRow.Children.Add(deltaPanel);

                fileList.Children.Add(fRow);
            }
            root.Children.Add(fileList);

            toggleBtn.Click += (_, _) =>
            {
                isExpanded = !isExpanded;
                fileList.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
                toggleBtn.Content = isExpanded ? "Hide" : $"Files ({summary.Files.Count})";
            };

            return card;
        }

        private UIElement BuildCodeDiffCard(CodeDiffBlock diff)
        {
            var card = CreateCardContainer();
            var root = new StackPanel();
            card.Child = root;

            List<DiffLineItem> diffLines;
            if (!string.IsNullOrEmpty(diff.BeforeContent) || !string.IsNullOrEmpty(diff.AfterContent))
            {
                diffLines = new WorkspaceService().GenerateDiff(diff.BeforeContent, diff.AfterContent);
            }
            else
            {
                diffLines = ParseUnifiedDiffFallback(diff.RawPatch);
            }

            int additions = diff.Additions > 0 ? diff.Additions : diffLines.Count(l => l.Type == "Added");
            int deletions = diff.Deletions > 0 ? diff.Deletions : diffLines.Count(l => l.Type == "Removed");
            const int CollapseThreshold = 24;
            bool isLong = diffLines.Count > CollapseThreshold;
            bool isExpanded = !isLong;

            var header = new Border
            {
                Background = ResolveBrush("BrushCardInnerBg", "#141418"),
                BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };

            var hdrGrid = new Grid();
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(CreatePill("DIFF", "#f59e0b"));
            left.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(diff.FilePath) ? "Patch" : diff.FilePath,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            left.Children.Add(new TextBlock
            {
                Text = $"+{additions} / -{deletions}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10.5,
                Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa"),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(left, 0);
            hdrGrid.Children.Add(left);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            Button? expandBtn = null;
            if (isLong)
            {
                expandBtn = CreateMiniButton($"Expand ({diffLines.Count} lines)");
                expandBtn.Margin = new Thickness(0, 0, 6, 0);
                actions.Children.Add(expandBtn);
            }
            if (!string.IsNullOrWhiteSpace(diff.FilePath))
            {
                var openBtn = CreateMiniButton("Open in Editor");
                openBtn.Margin = new Thickness(0, 0, 6, 0);
                openBtn.Click += (_, _) => OpenFileRequested?.Invoke(this, diff.FilePath);
                actions.Children.Add(openBtn);
            }
            var copyBtn = CreateMiniButton("Copy Patch");
            copyBtn.Click += (_, _) =>
            {
                string patch = !string.IsNullOrWhiteSpace(diff.RawPatch)
                    ? diff.RawPatch
                    : string.Join("\n", diffLines.Select(l => $"{l.Prefix}{l.Content}"));
                CopyTextWithFeedback(patch, copyBtn);
            };
            actions.Children.Add(copyBtn);
            Grid.SetColumn(actions, 1);
            hdrGrid.Children.Add(actions);

            header.Child = hdrGrid;
            root.Children.Add(header);

            var scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var rowsPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            scroller.Content = rowsPanel;
            root.Children.Add(scroller);

            var addBg = new SolidColorBrush(Color.FromArgb(26, 34, 197, 94));
            addBg.Freeze();
            var delBg = new SolidColorBrush(Color.FromArgb(26, 239, 68, 68));
            delBg.Freeze();
            var addFg = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80));
            addFg.Freeze();
            var delFg = new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71));
            delFg.Freeze();

            void RenderDiffRows(bool expanded)
            {
                rowsPanel.Children.Clear();
                int count = expanded ? diffLines.Count : Math.Min(CollapseThreshold, diffLines.Count);
                for (int i = 0; i < count; i++)
                {
                    var dl = diffLines[i];
                    var rowBorder = new Border
                    {
                        Background = dl.Type == "Added" ? addBg : dl.Type == "Removed" ? delBg : Brushes.Transparent,
                        Padding = new Thickness(8, 1, 10, 1)
                    };
                    var rg = new Grid();
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var oldTb = new TextBlock { Text = dl.DisplayOldLine, FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = ResolveBrush("BrushTextDim", "#71717a"), TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
                    var newTb = new TextBlock { Text = dl.DisplayNewLine, FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = ResolveBrush("BrushTextDim", "#71717a"), TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
                    var pfxTb = new TextBlock
                    {
                        Text = dl.Prefix,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = dl.Type == "Added" ? addFg : dl.Type == "Removed" ? delFg : ResolveBrush("BrushTextDim", "#71717a")
                    };
                    var txtTb = new TextBlock
                    {
                        Text = dl.Content,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11.5,
                        Foreground = dl.Type == "Added" ? addFg : dl.Type == "Removed" ? delFg : ResolveBrush("BrushTextPrimary", "#e4e4e7")
                    };

                    Grid.SetColumn(oldTb, 0);
                    Grid.SetColumn(newTb, 1);
                    Grid.SetColumn(pfxTb, 2);
                    Grid.SetColumn(txtTb, 3);
                    rg.Children.Add(oldTb);
                    rg.Children.Add(newTb);
                    rg.Children.Add(pfxTb);
                    rg.Children.Add(txtTb);
                    rowBorder.Child = rg;
                    rowsPanel.Children.Add(rowBorder);
                }
            }

            if (expandBtn != null)
            {
                expandBtn.Click += (_, _) =>
                {
                    isExpanded = !isExpanded;
                    expandBtn.Content = isExpanded ? "Collapse" : $"Expand ({diffLines.Count} lines)";
                    RenderDiffRows(isExpanded);
                };
            }

            RenderDiffRows(isExpanded);
            return card;
        }

        private UIElement BuildChartCard(ChartBlock chart)
        {
            var card = CreateCardContainer();
            var root = new StackPanel();
            card.Child = root;

            var header = new Border
            {
                Background = ResolveBrush("BrushCardInnerBg", "#141418"),
                BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(CreatePill(chart.Kind.ToString().ToUpperInvariant(), "#38bdf8"));
            if (!string.IsNullOrWhiteSpace(chart.Title))
            {
                left.Children.Add(new TextBlock
                {
                    Text = chart.Title,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            header.Child = left;
            root.Children.Add(header);

            var chartHost = new Border { Padding = new Thickness(10, 8, 10, 10) };
            chartHost.Child = ChartRenderer.Build(chart, this);
            root.Children.Add(chartHost);

            return card;
        }

        private UIElement BuildMermaidCard(MermaidBlock mermaid)
        {
            var card = CreateCardContainer();
            var root = new StackPanel();
            card.Child = root;

            var header = new Border
            {
                Background = ResolveBrush("BrushCardInnerBg", "#141418"),
                BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };
            var hdrGrid = new Grid();
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string kind = string.IsNullOrWhiteSpace(mermaid.DiagramType) ? "DIAGRAM" : mermaid.DiagramType.ToUpperInvariant();
            left.Children.Add(CreatePill(kind, "#34d399"));
            left.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(mermaid.Title) ? "Mermaid Diagram" : mermaid.Title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(left, 0);
            hdrGrid.Children.Add(left);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var expandBtn = CreateMiniButton("Expand");
            expandBtn.Margin = new Thickness(0, 0, 6, 0);
            expandBtn.Click += (_, _) =>
            {
                var win = new Window
                {
                    Title = string.IsNullOrWhiteSpace(mermaid.Title) ? "Diagram Viewer" : mermaid.Title,
                    Width = 900,
                    Height = 620,
                    Background = ResolveBrush("BrushCardBg", "#111114"),
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Window.GetWindow(this),
                    Content = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Padding = new Thickness(20),
                        Content = MermaidRenderer.Build(mermaid, this)
                    }
                };
                win.Show();
            };
            var copyBtn = CreateMiniButton("Copy Source");
            copyBtn.Click += (_, _) => CopyTextWithFeedback(mermaid.Source, copyBtn);
            actions.Children.Add(expandBtn);
            actions.Children.Add(copyBtn);
            Grid.SetColumn(actions, 1);
            hdrGrid.Children.Add(actions);

            header.Child = hdrGrid;
            root.Children.Add(header);

            var diagramHost = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(10, 8, 10, 10),
                Content = MermaidRenderer.Build(mermaid, this)
            };
            root.Children.Add(diagramHost);

            return card;
        }

        private UIElement BuildFileCard(FileAttachmentBlock file)
        {
            var card = CreateCardContainer();
            var root = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
            card.Child = root;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var topRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string ext = string.IsNullOrWhiteSpace(file.Extension) ? "FILE" : file.Extension.TrimStart('.').ToUpperInvariant();
            topRow.Children.Add(CreatePill(ext, "#f472b6"));
            topRow.Children.Add(new TextBlock
            {
                Text = file.FileName,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                Margin = new Thickness(8, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            if (file.SizeBytes > 0)
            {
                topRow.Children.Add(new TextBlock
                {
                    Text = FormatSize(file.SizeBytes),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10.5,
                    Foreground = ResolveBrush("BrushTextDim", "#71717a"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            left.Children.Add(topRow);
            if (!string.IsNullOrWhiteSpace(file.Description))
            {
                left.Children.Add(new TextBlock
                {
                    Text = file.Description,
                    FontSize = 11.5,
                    Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string targetPath = !string.IsNullOrWhiteSpace(file.FullPath) ? file.FullPath : file.FileName;
            var openBtn = CreateMiniButton("Open");
            openBtn.Margin = new Thickness(6, 0, 0, 0);
            openBtn.Click += (_, _) => OpenFileRequested?.Invoke(this, targetPath);
            actions.Children.Add(openBtn);

            var exportBtn = CreateMiniButton("Export");
            exportBtn.Margin = new Thickness(6, 0, 0, 0);
            exportBtn.Click += (_, _) => ExportFileAttachment(file);
            actions.Children.Add(exportBtn);

            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            root.Children.Add(grid);

            if (!string.IsNullOrWhiteSpace(file.PreviewText))
            {
                var previewExpander = new Expander
                {
                    Header = "Preview",
                    Margin = new Thickness(0, 6, 0, 0),
                    FontSize = 11,
                    Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa")
                };
                previewExpander.Content = new Border
                {
                    Background = ResolveBrush("BrushCardInnerBg", "#141418"),
                    BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 4, 0, 0),
                    Child = new TextBlock
                    {
                        Text = file.PreviewText,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = ResolveBrush("BrushTextPrimary", "#e4e4e7"),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                root.Children.Add(previewExpander);
            }

            return card;
        }

        private UIElement BuildProgressCard(ProgressBlock progress)
        {
            var card = CreateCardContainer();
            var root = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
            card.Child = root;

            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string st = string.IsNullOrWhiteSpace(progress.Status) ? "RUNNING" : progress.Status.ToUpperInvariant();
            string color = st.Contains("FAIL") || st.Contains("ERROR") ? "#ef4444" : st.Contains("DONE") || st.Contains("COMP") ? "#22c55e" : "#38bdf8";
            left.Children.Add(CreatePill(st, color));
            left.Children.Add(new TextBlock
            {
                Text = progress.Title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(left, 0);
            hdr.Children.Add(left);

            double pct = progress.PercentOverride ?? (progress.TotalOperations > 0 ? Math.Min(100, progress.CompletedOperations * 100.0 / progress.TotalOperations) : 0);
            var countsTb = new TextBlock
            {
                Text = progress.TotalOperations > 0
                    ? $"{progress.CompletedOperations}/{progress.TotalOperations} ({pct:0}%)"
                    : $"{pct:0}%",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(countsTb, 1);
            hdr.Children.Add(countsTb);
            root.Children.Add(hdr);

            root.Children.Add(new ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                Value = pct,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                Background = ResolveBrush("BrushCardInnerBg", "#18181b"),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 7, 0, 5)
            });

            if (!string.IsNullOrWhiteSpace(progress.CurrentOperation))
            {
                root.Children.Add(new TextBlock
                {
                    Text = progress.CurrentOperation,
                    FontSize = 11,
                    Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa"),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            if (progress.FailedOperations > 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = $"Failed operations: {progress.FailedOperations}",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71)),
                    Margin = new Thickness(0, 3, 0, 0)
                });
            }

            return card;
        }

        private UIElement BuildCitationCard(CitationBlock citation)
        {
            var card = CreateCardContainer();
            var root = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
            card.Child = root;

            var hdr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            hdr.Children.Add(CreatePill("SOURCES", "#38bdf8"));
            hdr.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(citation.Title) ? $"References ({citation.Sources.Count})" : citation.Title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            root.Children.Add(hdr);

            foreach (var src in citation.Sources)
            {
                var itemBorder = new Border
                {
                    Background = ResolveBrush("BrushCardInnerBg", "#141418"),
                    BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 3, 0, 0)
                };
                var itemStack = new StackPanel();
                var topLine = new WrapPanel { Orientation = Orientation.Horizontal };
                string domain = !string.IsNullOrWhiteSpace(src.Domain) ? src.Domain : ExtractDomain(src.Url);
                if (!string.IsNullOrWhiteSpace(domain))
                {
                    topLine.Children.Add(new TextBlock
                    {
                        Text = $"[{domain}]",
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 10.5,
                        Foreground = ResolveBrush("BrushTextDim", "#71717a"),
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                if (WebLink.TryParse(src.Url, out var safeUri))
                {
                    var linkBtn = new Button
                    {
                        Content = string.IsNullOrWhiteSpace(src.Title) ? src.Url : src.Title,
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(0),
                        Foreground = ResolveBrush("BrushAccent", "#38bdf8"),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Cursor = Cursors.Hand
                    };
                    linkBtn.Click += (_, _) =>
                    {
                        try { Process.Start(new ProcessStartInfo(safeUri.AbsoluteUri) { UseShellExecute = true }); }
                        catch { }
                    };
                    topLine.Children.Add(linkBtn);
                }
                else
                {
                    topLine.Children.Add(new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(src.Title) ? src.Url : src.Title,
                        Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold
                    });
                }

                itemStack.Children.Add(topLine);
                if (!string.IsNullOrWhiteSpace(src.Description))
                {
                    itemStack.Children.Add(new TextBlock
                    {
                        Text = src.Description,
                        FontSize = 11,
                        Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 3, 0, 0)
                    });
                }
                itemBorder.Child = itemStack;
                root.Children.Add(itemBorder);
            }

            return card;
        }

        private UIElement BuildErrorCard(ErrorBlock err)
        {
            var redBorder = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            redBorder.Freeze();
            var redBg = new SolidColorBrush(Color.FromArgb(24, 239, 68, 68));
            redBg.Freeze();

            var card = new Border
            {
                Background = redBg,
                BorderBrush = redBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8)
            };
            var root = new StackPanel();
            card.Child = root;

            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(CreatePill("ERROR", "#ef4444"));
            left.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(err.Title) ? "Error" : err.Title,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71)),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(left, 0);
            hdr.Children.Add(left);

            if (err.CanRetry && _boundMessage != null)
            {
                var retryBtn = CreateMiniButton("Retry");
                retryBtn.Click += (_, _) => RetryRequested?.Invoke(this, _boundMessage);
                Grid.SetColumn(retryBtn, 1);
                hdr.Children.Add(retryBtn);
            }
            root.Children.Add(hdr);

            if (!string.IsNullOrWhiteSpace(err.Message))
            {
                root.Children.Add(new TextBlock
                {
                    Text = err.Message,
                    FontSize = 12,
                    Foreground = ResolveBrush("BrushTextPrimary", "#fafafa"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }

            if (!string.IsNullOrWhiteSpace(err.Operation) || !string.IsNullOrWhiteSpace(err.FilePath))
            {
                string meta = string.Join("  •  ", new[]
                {
                    string.IsNullOrWhiteSpace(err.Operation) ? null : $"Operation: {err.Operation}",
                    string.IsNullOrWhiteSpace(err.FilePath) ? null : $"File: {err.FilePath}"
                }.Where(s => s != null));
                root.Children.Add(new TextBlock
                {
                    Text = meta,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10.5,
                    Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa"),
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            if (!string.IsNullOrWhiteSpace(err.TechnicalDetails))
            {
                root.Children.Add(new Expander
                {
                    Header = "Technical details",
                    FontSize = 11,
                    Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa"),
                    Margin = new Thickness(0, 6, 0, 0),
                    Content = new TextBox
                    {
                        Text = err.TechnicalDetails,
                        IsReadOnly = true,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = ResolveBrush("BrushTextSecondary", "#d4d4d8"),
                        Background = ResolveBrush("BrushCardInnerBg", "#141418"),
                        BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                        TextWrapping = TextWrapping.Wrap,
                        Padding = new Thickness(6),
                        Margin = new Thickness(0, 4, 0, 0)
                    }
                });
            }

            return card;
        }

        private UIElement BuildImageCard(ImageBlock img)
        {
            var card = CreateCardContainer();
            var root = new StackPanel { Margin = new Thickness(8) };
            card.Child = root;

            bool loaded = false;
            try
            {
                Uri? uri = null;
                if (File.Exists(img.SourcePath))
                {
                    uri = new Uri(img.SourcePath, UriKind.Absolute);
                }
                else if (WebLink.TryParse(img.SourcePath, out var safeWeb))
                {
                    uri = safeWeb;
                }

                if (uri != null)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = uri;
                    bmp.EndInit();

                    root.Children.Add(new Image
                    {
                        Source = bmp,
                        MaxHeight = 280,
                        Stretch = Stretch.Uniform
                    });
                    loaded = true;
                }
            }
            catch { }

            if (!loaded)
            {
                root.Children.Add(new TextBlock
                {
                    Text = $"[Image unavailable: {img.SourcePath}]",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa")
                });
            }

            if (!string.IsNullOrWhiteSpace(img.Caption))
            {
                root.Children.Add(new TextBlock
                {
                    Text = img.Caption,
                    FontSize = 11,
                    Foreground = ResolveBrush("BrushTextSecondary", "#a1a1aa"),
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            return card;
        }

        private Border CreateCardContainer()
        {
            return new Border
            {
                Background = ResolveBrush("BrushCardBg", "#111114"),
                BorderBrush = ResolveBrush("BrushBorder", "#27272a"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };
        }

        private Border CreatePill(string text, string accentHex)
        {
            Color c = (Color)ColorConverter.ConvertFromString(accentHex);
            var fg = new SolidColorBrush(c);
            fg.Freeze();
            var bg = new SolidColorBrush(Color.FromArgb(30, c.R, c.G, c.B));
            bg.Freeze();
            var bdr = new SolidColorBrush(Color.FromArgb(95, c.R, c.G, c.B));
            bdr.Freeze();

            return new Border
            {
                Background = bg,
                BorderBrush = bdr,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Child = new TextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = fg
                }
            };
        }

        private Button CreateMiniButton(string text, bool accent = false)
        {
            return new Button
            {
                Content = text,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(8, 3, 8, 3),
                Cursor = Cursors.Hand,
                Foreground = accent ? Brushes.White : ResolveBrush("BrushTextSecondary", "#d4d4d8"),
                Background = accent ? ResolveBrush("BrushAccent", "#2563eb") : ResolveBrush("BrushCardBg", "#18181b"),
                BorderBrush = accent ? ResolveBrush("BrushAccent", "#3b82f6") : ResolveBrush("BrushBorder", "#3f3f46"),
                BorderThickness = new Thickness(1)
            };
        }

        private void CopyTextWithFeedback(string text, Button btn)
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
                string original = btn.Content?.ToString() ?? "Copy";
                btn.Content = "Copied";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    btn.Content = original;
                };
                timer.Start();
            }
            catch { }
        }

        private void ExportFileAttachment(FileAttachmentBlock file)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    FileName = string.IsNullOrWhiteSpace(file.FileName) ? "export.txt" : file.FileName,
                    Filter = "All files (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;

                if (!string.IsNullOrWhiteSpace(file.FullPath) && File.Exists(file.FullPath))
                {
                    File.Copy(file.FullPath, dlg.FileName, overwrite: true);
                }
                else
                {
                    File.WriteAllText(dlg.FileName, file.PreviewText ?? string.Empty);
                }
            }
            catch { }
        }

        private bool IsLightThemeHost()
        {
            if (TryFindResource("BrushCardBg") is SolidColorBrush sb)
            {
                double lum = 0.2126 * sb.Color.R + 0.7152 * sb.Color.G + 0.0722 * sb.Color.B;
                return lum > 150;
            }
            return false;
        }

        private Brush ResolveBrush(string key, string fallbackHex)
        {
            if (TryFindResource(key) is Brush b) return b;
            try
            {
                var sb = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex));
                sb.Freeze();
                return sb;
            }
            catch { return Brushes.Gray; }
        }

        private static int CompareCellValues(string a, string b)
        {
            string na = (a ?? string.Empty).Replace(",", "").Replace("%", "").Replace("$", "");
            string nb = (b ?? string.Empty).Replace(",", "").Replace("%", "").Replace("$", "");
            if (double.TryParse(na, NumberStyles.Any, CultureInfo.InvariantCulture, out double da) &&
                double.TryParse(nb, NumberStyles.Any, CultureInfo.InvariantCulture, out double db))
            {
                return da.CompareTo(db);
            }
            return StringComparer.OrdinalIgnoreCase.Compare(a, b);
        }

        private static string EscapeCsv(string? val)
        {
            string s = val ?? string.Empty;
            if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
            {
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            }
            return s;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        private static string ExtractDomain(string? url)
        {
            if (WebLink.TryParse(url, out var uri))
            {
                return uri.Host;
            }
            return string.Empty;
        }

        private static List<DiffLineItem> ParseUnifiedDiffFallback(string? rawPatch)
        {
            var list = new List<DiffLineItem>();
            if (string.IsNullOrWhiteSpace(rawPatch)) return list;

            int oldLine = 1, newLine = 1;
            foreach (var raw in rawPatch.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.StartsWith("+++") || raw.StartsWith("---") || raw.StartsWith("@@"))
                {
                    list.Add(new DiffLineItem { Type = "Header", Prefix = "@", Content = raw });
                }
                else if (raw.StartsWith("+"))
                {
                    list.Add(new DiffLineItem { Type = "Added", Prefix = "+", LineNumberNew = newLine++, Content = raw.Substring(1) });
                }
                else if (raw.StartsWith("-"))
                {
                    list.Add(new DiffLineItem { Type = "Removed", Prefix = "-", LineNumberOld = oldLine++, Content = raw.Substring(1) });
                }
                else
                {
                    list.Add(new DiffLineItem { Type = "Unchanged", Prefix = " ", LineNumberOld = oldLine++, LineNumberNew = newLine++, Content = raw.StartsWith(" ") ? raw.Substring(1) : raw });
                }
            }
            return list;
        }
    }
}
