using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Providers;
using LlmodelBox.Runtime;
using LlmodelBox.Runtime.Content;
using LlmodelBox.Runtime.Context;
using LlmodelBox.Runtime.Continuity;
using LlmodelBox.Runtime.Memory;
using LlmodelBox.Services;
using Microsoft.Win32;

namespace LlmodelBox
{
    public sealed class HomeChatThread
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "New Conversation";
        public string ProjectPath { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public ConversationStatus Status { get; set; } = ConversationStatus.Active;
        public bool IsArchived { get; set; }
        public string? LatestCheckpointSummary { get; set; }
        public List<ChatMessageItem> Messages { get; set; } = new();

        [JsonIgnore]
        public bool IsSelected { get; set; }

        [JsonIgnore]
        public string Subtitle => string.IsNullOrWhiteSpace(ProjectName) ? "General Chat" : ProjectName;

        [JsonIgnore]
        public string StatusBadgeText => IsArchived
            ? "ARCHIVED"
            : (Status == ConversationStatus.Interrupted ? "INTERRUPTED" : string.Empty);

        [JsonIgnore]
        public Visibility StatusBadgeVisibility => string.IsNullOrEmpty(StatusBadgeText) ? Visibility.Collapsed : Visibility.Visible;

        [JsonIgnore]
        public string ArchiveActionGlyph => IsArchived ? "↩" : "⊟";

        [JsonIgnore]
        public string ArchiveActionTooltip => IsArchived ? "Restore from archive" : "Archive conversation";

        [JsonIgnore]
        public string RelativeTime
        {
            get
            {
                var delta = DateTime.UtcNow - UpdatedAtUtc;
                if (delta.TotalMinutes < 1) return "now";
                if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes}m";
                if (delta.TotalDays < 1) return $"{(int)delta.TotalHours}h";
                if (delta.TotalDays < 30) return $"{(int)delta.TotalDays}d";
                return $"{Math.Max(1, (int)(delta.TotalDays / 30))}mo";
            }
        }

        [JsonIgnore]
        public Brush RowBackground => IsSelected
            ? (Application.Current?.TryFindResource("BrushCardInnerBg") as Brush ?? Brushes.Transparent)
            : Brushes.Transparent;

        [JsonIgnore]
        public FontWeight TitleWeight => IsSelected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    public partial class MainWindow
    {
        private readonly List<HomeChatThread> _homeThreads = new();
        private readonly ObservableCollection<ChatMessageItem> _homeDisplayedMessages = new();
        private readonly JsonChatHistoryStore _ideChatHistoryStore = new();
        private readonly List<HomeAttachedContextItem> _attachedHomeFiles = new();
        private HomeChatThread? _activeHomeThread = null;
        private ProjectItem? _homeChatProject = null;
        private ProjectItem? _sidebarFilterProject = null;
        private bool _sidebarFilterGeneralOnly = false;
        private string _homeModelFilter = "All";
        private bool _homeChatBusy = false;
        private bool _showArchivedThreads = false;
        private string? _previousWorkConversationId = null;
        private bool _continuityEventsHooked = false;
        private CancellationTokenSource? _homeGenerationCts = null;
        private DiffSummaryBlock? _activeDiffSummary = null;

        private sealed class HomeAttachedContextItem
        {
            public string FullPath { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string CachedContent { get; set; } = string.Empty;
        }

        private sealed class HomeWorkspaceOption
        {
            public string Name { get; set; } = "General Chat (No Workspace)";
            public ProjectItem? Project { get; set; }
            public bool IsAll { get; set; }
            public bool IsGeneralOnly { get; set; }
        }

        private string HomeThreadsFilePath => Path.Combine(AppConstants.ChatHistoryDir, "home_conversations.json");

        private void InitHomeChat()
        {
            if (!_continuityEventsHooked && _services?.Continuity != null)
            {
                _continuityEventsHooked = true;
                _services.Continuity.DiagnosticsChanged += rep => Dispatcher.BeginInvoke(() => UpdatePersistenceStatusUi(rep));
                _services.Continuity.ArchiveUpdated += () => Dispatcher.BeginInvoke(() =>
                {
                    if (!_homeChatBusy)
                    {
                        LoadHomeThreads();
                        RefreshHomeConversationsUi();
                        RefreshProjectsUi();
                        UpdateHomePreviousWorkCard();
                    }
                });
            }

            LoadHomeThreads();
            if (_activeHomeThread == null)
            {
                _activeHomeThread = new HomeChatThread();
            }
            UpdateHomeGreetingAndLabels();
            RefreshHomeConversationsUi();
            RefreshHomeChatThreadUi();
            RefreshProjectsUi();
            UpdateHomeContextRing();
            UpdateHomePreviousWorkCard();
            UpdatePersistenceStatusUi(_services?.Continuity?.GetDiagnostics());
        }

        private void LoadHomeThreads()
        {
            _homeThreads.Clear();
            var records = _services?.Continuity?.ListConversations(
                projectKey: null,
                includeArchived: _showArchivedThreads,
                includeDeleted: false,
                offset: 0,
                limit: int.MaxValue);

            if (records == null) return;

            foreach (var r in records)
            {
                _homeThreads.Add(new HomeChatThread
                {
                    Id = r.Id,
                    Title = r.Title,
                    ProjectPath = r.ProjectKey,
                    ProjectName = r.ProjectName,
                    UpdatedAtUtc = r.UpdatedAtUtc,
                    Status = r.Status,
                    IsArchived = r.IsArchived,
                    LatestCheckpointSummary = r.LatestCheckpointSummary
                });
            }
        }

        private void SaveHomeThreads()
        {
            if (_services?.Continuity == null || _activeHomeThread == null) return;
            if (_activeHomeThread.Messages.Count == 0 && _services.Continuity.GetConversation(_activeHomeThread.Id) == null)
                return;

            _services.Continuity.SaveConversationMessages(
                _activeHomeThread.Id,
                _activeHomeThread.ProjectPath,
                _activeHomeThread.ProjectName,
                _activeHomeThread.Messages,
                _activeHomeThread.Title,
                _activeModel?.Id,
                _activeModel?.Provider);
            UpdatePersistenceStatusUi(_services.Continuity.GetDiagnostics());
        }

        private void EnsureProjectIdeHistoryImported(ProjectItem project)
        {
            // Unified repository supersedes legacy one-time import; reload indexed project conversations.
            if (project == null || string.IsNullOrWhiteSpace(project.Path)) return;
            LoadHomeThreads();
        }

        internal int GetConversationCountForProject(ProjectItem project)
        {
            if (project == null || string.IsNullOrWhiteSpace(project.Path)) return 0;
            if (_services?.Continuity != null)
                return _services.Continuity.ListConversations(project.Path, includeArchived: false, includeDeleted: false).Count;
            return _homeThreads.Count(t => !t.IsArchived && string.Equals(t.ProjectPath, project.Path, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdatePersistenceStatusUi(PersistenceDiagnosticReport? rep)
        {
            if (TxtPersistenceStatus == null || PersistenceStatusDot == null || rep == null) return;
            switch (rep.State)
            {
                case PersistenceState.Healthy:
                    TxtPersistenceStatus.Text = rep.LastSavedUtc.HasValue ? "Storage: Saved" : "Storage: Ready";
                    PersistenceStatusDot.Fill = TryFindResource("BrushSuccess") as Brush ?? Brushes.LimeGreen;
                    break;
                case PersistenceState.PendingSave:
                    TxtPersistenceStatus.Text = $"Storage: Saving ({rep.PendingRetryCount})";
                    PersistenceStatusDot.Fill = TryFindResource("BrushWarning") as Brush ?? Brushes.Orange;
                    break;
                case PersistenceState.RecoveredFromBackup:
                    TxtPersistenceStatus.Text = "Storage: Recovered Backup";
                    PersistenceStatusDot.Fill = TryFindResource("BrushWarning") as Brush ?? Brushes.Orange;
                    break;
                case PersistenceState.DegradedError:
                    TxtPersistenceStatus.Text = $"Storage Error ({rep.PendingRetryCount} queued)";
                    PersistenceStatusDot.Fill = TryFindResource("BrushDanger") as Brush ?? Brushes.Red;
                    break;
            }
        }

        private void UpdateHomePreviousWorkCard()
        {
            if (HomePreviousWorkCard == null || TxtHomePrevGoal == null || TxtHomePrevDetail == null) return;
            string? pk = _homeChatProject?.Path;
            if (string.IsNullOrWhiteSpace(pk) || (_activeHomeThread?.Messages.Count ?? 0) > 0)
            {
                HomePreviousWorkCard.Visibility = Visibility.Collapsed;
                _previousWorkConversationId = null;
                return;
            }

            var ptr = _services?.Continuity?.GetPreviousWorkPointer(pk, _activeHomeThread?.Id);
            if (ptr == null || string.IsNullOrWhiteSpace(ptr.CurrentGoal))
            {
                HomePreviousWorkCard.Visibility = Visibility.Collapsed;
                _previousWorkConversationId = null;
                return;
            }

            _previousWorkConversationId = ptr.ConversationId;
            TxtHomePrevGoal.Text = $"{ptr.ConversationTitle} · {ptr.CurrentGoal}";
            string detail = !string.IsNullOrWhiteSpace(ptr.Blocker)
                ? $"Blocker: {ptr.Blocker}"
                : (!string.IsNullOrWhiteSpace(ptr.NextStep) ? $"Next: {ptr.NextStep}" : ptr.CompletedSummary);
            TxtHomePrevDetail.Text = detail;
            HomePreviousWorkCard.Visibility = Visibility.Visible;
        }

        private void BtnHomeContinuePrev_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_previousWorkConversationId)) return;
            var hit = _homeThreads.FirstOrDefault(t => string.Equals(t.Id, _previousWorkConversationId, StringComparison.Ordinal));
            if (hit == null)
            {
                var conv = _services?.Continuity?.GetConversation(_previousWorkConversationId!);
                if (conv != null)
                {
                    hit = new HomeChatThread
                    {
                        Id = conv.Id,
                        Title = conv.Title,
                        ProjectPath = conv.ProjectKey,
                        ProjectName = conv.ProjectName,
                        UpdatedAtUtc = conv.UpdatedAtUtc,
                        Status = conv.Status,
                        IsArchived = conv.IsArchived
                    };
                }
            }
            if (hit != null) OpenHomeThread(hit);
        }

        private void BtnPersistenceStatus_Click(object sender, RoutedEventArgs e)
        {
            if (_services?.Continuity == null) return;
            bool ok = _services.Continuity.RetryPendingWrites();
            var rep = _services.Continuity.GetDiagnostics();
            UpdatePersistenceStatusUi(rep);
            string msg = $"State: {rep.State}\nPending Writes: {rep.PendingRetryCount}\nRecovered Interrupted Turns: {rep.RecoveredInterruptedTurns}";
            if (!string.IsNullOrWhiteSpace(rep.LastWarning)) msg += $"\nWarning: {rep.LastWarning}";
            if (!string.IsNullOrWhiteSpace(rep.LastError)) msg += $"\nError: {rep.LastError}";
            if (rep.QuarantineBackups.Count > 0) msg += $"\nQuarantine Backups: {string.Join(", ", rep.QuarantineBackups)}";
            MessageBox.Show(msg, ok ? "Continuity Persistence Health" : "Persistence Error Diagnostics", MessageBoxButton.OK,
                rep.State == PersistenceState.DegradedError ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private void BtnToggleArchivedConversations_Click(object sender, RoutedEventArgs e)
        {
            _showArchivedThreads = !_showArchivedThreads;
            if (BtnToggleArchivedConversations != null)
                BtnToggleArchivedConversations.Content = _showArchivedThreads ? "Hide Arch" : "Archived";
            LoadHomeThreads();
            RefreshHomeConversationsUi();
        }

        private void UpdateHomeGreetingAndLabels()
        {
            if (TxtHomeGreeting != null)
            {
                string who = string.IsNullOrWhiteSpace(_settings?.ProfileName) ? "" : $", {_settings.ProfileName.Trim()}";
                if (_homeChatProject != null)
                {
                    TxtHomeGreeting.Text = $"Project: {_homeChatProject.Name}";
                }
                else
                {
                    TxtHomeGreeting.Text = $"Welcome back{who}. Where should we pick up?";
                }
            }
            if (TxtHomeGreetingSub != null)
            {
                TxtHomeGreetingSub.Text = _homeChatProject != null
                    ? "Start a new conversation inside this project or pick a previous one from the sidebar."
                    : "Choose a workspace above or start a general conversation.";
            }
            if (TxtHomeModelLabel != null)
            {
                TxtHomeModelLabel.Text = _activeModel != null ? _activeModel.Name : "Select Model";
            }
            if (TxtHomeModelTier != null && HomeModelTierBadge != null)
            {
                if (_activeModel != null)
                {
                    TxtHomeModelTier.Text = _activeModel.TierBadgeText;
                    try
                    {
                        TxtHomeModelTier.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_activeModel.TierBadgeColor));
                        HomeModelTierBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_activeModel.TierBadgeBg));
                    }
                    catch { }
                    HomeModelTierBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    HomeModelTierBadge.Visibility = Visibility.Collapsed;
                }
            }
            if (TxtHomeModelCap != null)
            {
                TxtHomeModelCap.Text = _activeModel != null ? _activeModel.PrimaryCategoryTag : "TEXT";
            }
            UpdateHomeContextRing();
            if (TxtHomeWorkspaceCtx != null)
            {
                TxtHomeWorkspaceCtx.Text = _homeChatProject != null
                    ? $"This Computer · {_homeChatProject.Name}"
                    : "This Computer · General Chat";
            }
            if (TxtSidebarProjectFilter != null)
            {
                if (_sidebarFilterProject != null)
                    TxtSidebarProjectFilter.Text = _sidebarFilterProject.Name;
                else if (_sidebarFilterGeneralOnly)
                    TxtSidebarProjectFilter.Text = "General Chat";
                else
                    TxtSidebarProjectFilter.Text = "All Projects";
            }
            if (BtnClearProjectFilter != null)
            {
                BtnClearProjectFilter.Visibility = (_sidebarFilterProject != null || _sidebarFilterGeneralOnly)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void RefreshHomeConversationsUi()
        {
            if (HomeConversationsList == null) return;

            IEnumerable<HomeChatThread> filtered = _homeThreads;
            if (_sidebarFilterProject != null)
            {
                filtered = _homeThreads.Where(t => string.Equals(t.ProjectPath, _sidebarFilterProject.Path, StringComparison.OrdinalIgnoreCase));
            }
            else if (_sidebarFilterGeneralOnly)
            {
                filtered = _homeThreads.Where(t => string.IsNullOrWhiteSpace(t.ProjectPath));
            }

            var list = filtered.OrderByDescending(t => t.UpdatedAtUtc).ToList();
            foreach (var t in list)
            {
                t.IsSelected = _activeHomeThread != null && string.Equals(t.Id, _activeHomeThread.Id, StringComparison.Ordinal);
            }
            HomeConversationsList.ItemsSource = null;
            HomeConversationsList.ItemsSource = list;

            if (TxtNoConversationsHint != null)
            {
                TxtNoConversationsHint.Text = _sidebarFilterProject != null
                    ? $"No conversations in {_sidebarFilterProject.Name} yet. Type below to start one."
                    : "No conversations yet. Start typing on the right.";
                TxtNoConversationsHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void RefreshHomeChatThreadUi()
        {
            if (HomeChatMessagesList == null || HomeChatEmptyState == null) return;

            var msgs = _activeHomeThread?.Messages ?? new List<ChatMessageItem>();
            bool hasMessages = msgs.Count > 0;
            HomeChatEmptyState.Visibility = hasMessages ? Visibility.Collapsed : Visibility.Visible;
            HomeChatMessagesList.Visibility = hasMessages ? Visibility.Visible : Visibility.Collapsed;

            if (HomeChatMessagesList.ItemsSource != _homeDisplayedMessages)
            {
                HomeChatMessagesList.ItemsSource = _homeDisplayedMessages;
            }

            _homeDisplayedMessages.Clear();
            foreach (var m in msgs)
            {
                _homeDisplayedMessages.Add(m);
            }

            if (hasMessages)
            {
                var last = _homeDisplayedMessages[_homeDisplayedMessages.Count - 1];
                _ = Dispatcher.BeginInvoke(new Action(() => HomeChatMessagesList.ScrollIntoView(last)),
                    DispatcherPriority.Background);
            }
        }

        private void SelectProjectForConversations(ProjectItem? project, bool openMostRecentThread = true)
        {
            _sidebarFilterGeneralOnly = false;
            _sidebarFilterProject = project;
            _homeChatProject = project;

            if (project != null)
            {
                EnsureProjectIdeHistoryImported(project);
                var projectThreads = _homeThreads
                    .Where(t => string.Equals(t.ProjectPath, project.Path, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.UpdatedAtUtc)
                    .ToList();

                if (openMostRecentThread && projectThreads.Count > 0)
                {
                    OpenHomeThread(projectThreads[0]);
                    return;
                }
                else
                {
                    _activeHomeThread = new HomeChatThread
                    {
                        ProjectPath = project.Path,
                        ProjectName = project.Name
                    };
                }
            }

            UpdateHomeGreetingAndLabels();
            RefreshHomeConversationsUi();
            RefreshHomeChatThreadUi();
            UpdateHomePreviousWorkCard();
            ShowView(ViewChatHome, ChatHomeTranslate);
            TxtHomePrompt?.Focus();
        }

        private void BtnOpenProjectChats_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ProjectItem project)
            {
                SelectProjectForConversations(project, openMostRecentThread: true);
            }
        }

        private void BtnSidebarProjectFilter_Click(object sender, RoutedEventArgs e)
        {
            if (SidebarProjectFilterPopup == null || SidebarProjectFilterList == null) return;
            var options = new List<HomeWorkspaceOption>
            {
                new() { Name = "All Projects", IsAll = true },
                new() { Name = "General Chat (No Project)", IsGeneralOnly = true }
            };
            foreach (var p in _projects)
            {
                options.Add(new HomeWorkspaceOption { Name = p.Name, Project = p });
            }
            SidebarProjectFilterList.ItemsSource = options;
            SidebarProjectFilterPopup.IsOpen = !SidebarProjectFilterPopup.IsOpen;
        }

        private void SidebarProjectFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SidebarProjectFilterList?.SelectedItem is not HomeWorkspaceOption opt) return;
            SidebarProjectFilterPopup.IsOpen = false;

            if (opt.IsAll)
            {
                _sidebarFilterProject = null;
                _sidebarFilterGeneralOnly = false;
                UpdateHomeGreetingAndLabels();
                RefreshHomeConversationsUi();
                return;
            }

            if (opt.IsGeneralOnly)
            {
                _sidebarFilterProject = null;
                _sidebarFilterGeneralOnly = true;
                _homeChatProject = null;
                var generalThreads = _homeThreads
                    .Where(t => string.IsNullOrWhiteSpace(t.ProjectPath))
                    .OrderByDescending(t => t.UpdatedAtUtc)
                    .ToList();
                _activeHomeThread = generalThreads.FirstOrDefault() ?? new HomeChatThread();
                UpdateHomeGreetingAndLabels();
                RefreshHomeConversationsUi();
                RefreshHomeChatThreadUi();
                ShowView(ViewChatHome, ChatHomeTranslate);
                return;
            }

            if (opt.Project != null)
            {
                SelectProjectForConversations(opt.Project, openMostRecentThread: true);
            }
        }

        private void BtnClearProjectFilter_Click(object sender, RoutedEventArgs e)
        {
            _sidebarFilterProject = null;
            _sidebarFilterGeneralOnly = false;
            UpdateHomeGreetingAndLabels();
            RefreshHomeConversationsUi();
        }

        private void BtnNavChat_Click(object sender, RoutedEventArgs e)
        {
            UpdateHomeGreetingAndLabels();
            ShowView(ViewChatHome, ChatHomeTranslate);
        }

        private void BtnNewConversation_Click(object sender, RoutedEventArgs e)
        {
            var scopedProject = _sidebarFilterProject ?? _homeChatProject;
            _homeChatProject = scopedProject;
            _activeHomeThread = new HomeChatThread
            {
                ProjectPath = scopedProject?.Path ?? string.Empty,
                ProjectName = scopedProject?.Name ?? string.Empty
            };
            UpdateHomeGreetingAndLabels();
            ShowView(ViewChatHome, ChatHomeTranslate);
            RefreshHomeConversationsUi();
            RefreshHomeChatThreadUi();
            UpdateHomePreviousWorkCard();
            TxtHomePrompt?.Focus();
        }

        private void OpenHomeThread(HomeChatThread thread)
        {
            if (thread.Messages.Count == 0 && _services?.Continuity != null)
            {
                thread.Messages = _services.Continuity.LoadMessages(thread.Id);
            }
            _activeHomeThread = thread;
            _homeChatProject = !string.IsNullOrWhiteSpace(thread.ProjectPath)
                ? _projects.FirstOrDefault(p => string.Equals(p.Path, thread.ProjectPath, StringComparison.OrdinalIgnoreCase))
                : null;
            UpdateHomeGreetingAndLabels();
            ShowView(ViewChatHome, ChatHomeTranslate);
            RefreshHomeConversationsUi();
            RefreshHomeChatThreadUi();
            UpdateHomePreviousWorkCard();
        }

        private void HomeConversation_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is HomeChatThread thread)
            {
                OpenHomeThread(thread);
            }
        }

        private void BtnArchiveHomeConversation_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button btn && btn.Tag is HomeChatThread thread && _services?.Continuity != null)
            {
                bool targetArchived = !thread.IsArchived;
                _services.Continuity.ArchiveConversation(thread.Id, targetArchived);
                LoadHomeThreads();
                RefreshHomeConversationsUi();
                RefreshProjectsUi();
                UpdateHomePreviousWorkCard();
            }
        }

        private void BtnDeleteHomeConversation_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button btn && btn.Tag is HomeChatThread thread)
            {
                var confirm = MessageBox.Show(
                    $"Delete conversation \"{thread.Title}\"?\n\nThis hides/removes only this conversation and does NOT delete your project memory, global preferences, or other conversations.",
                    "Confirm Conversation Deletion",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes) return;

                _services?.Continuity?.SoftDeleteConversation(thread.Id, deleteDerivedMemories: false);
                _homeThreads.RemoveAll(t => t.Id == thread.Id);
                if (_activeHomeThread?.Id == thread.Id)
                {
                    var scopedProject = _sidebarFilterProject ?? _homeChatProject;
                    _activeHomeThread = new HomeChatThread
                    {
                        ProjectPath = scopedProject?.Path ?? string.Empty,
                        ProjectName = scopedProject?.Name ?? string.Empty
                    };
                    RefreshHomeChatThreadUi();
                }
                RefreshHomeConversationsUi();
                RefreshProjectsUi();
                UpdateHomePreviousWorkCard();
            }
        }

        private void BtnHomeWorkspaceCtx_Click(object sender, RoutedEventArgs e)
        {
            if (HomeWorkspacePopup == null || HomeWorkspacePickerList == null) return;
            var options = new List<HomeWorkspaceOption>
            {
                new() { Name = "General Chat (No Workspace)", Project = null }
            };
            foreach (var p in _projects)
            {
                options.Add(new HomeWorkspaceOption { Name = p.Name, Project = p });
            }
            HomeWorkspacePickerList.ItemsSource = options;
            HomeWorkspacePopup.IsOpen = !HomeWorkspacePopup.IsOpen;
        }

        private void HomeWorkspacePickerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HomeWorkspacePickerList?.SelectedItem is HomeWorkspaceOption opt)
            {
                _homeChatProject = opt.Project;
                if (_activeHomeThread != null && _activeHomeThread.Messages.Count == 0)
                {
                    _activeHomeThread.ProjectPath = _homeChatProject?.Path ?? string.Empty;
                    _activeHomeThread.ProjectName = _homeChatProject?.Name ?? string.Empty;
                }
                UpdateHomeGreetingAndLabels();
                HomeWorkspacePopup.IsOpen = false;
            }
        }

        private IEnumerable<ModelItem> GetAllKnownModels()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cat in _allCompanyCategories)
            {
                if (cat.Models == null) continue;
                foreach (var m in cat.Models)
                {
                    if (seen.Add($"{m.Provider}|{m.Id}")) yield return m;
                }
            }
            foreach (var m in _allModels)
            {
                if (seen.Add($"{m.Provider}|{m.Id}")) yield return m;
            }
        }

        private void RefreshHomeModelPickerList()
        {
            if (HomeModelPickerList == null) return;
            string search = (TxtHomeModelSearch?.Text ?? string.Empty).Trim().ToLowerInvariant();

            var filtered = GetAllKnownModels()
                .Where(m => m.MatchesCapabilityFilter(_homeModelFilter))
                .Where(m => string.IsNullOrEmpty(search) ||
                            m.Name.ToLowerInvariant().Contains(search) ||
                            m.Id.ToLowerInvariant().Contains(search) ||
                            m.Provider.ToLowerInvariant().Contains(search) ||
                            m.CategoryBadgeText.ToLowerInvariant().Contains(search) ||
                            m.TierBadgeText.ToLowerInvariant().Contains(search))
                .OrderByDescending(m => !string.IsNullOrWhiteSpace(_cloudApiService.GetApiKeyForProvider(m.Provider, _settings)) || (m.IsCustom && !string.IsNullOrWhiteSpace(m.CustomApiKey)))
                .ThenByDescending(m => m.IsFree)
                .ThenBy(m => m.Provider)
                .ThenBy(m => m.Name)
                .ToList();

            HomeModelPickerList.ItemsSource = filtered;
            if (TxtHomeModelPopupCount != null)
            {
                TxtHomeModelPopupCount.Text = filtered.Count == 1 ? "1 model" : $"{filtered.Count} models";
            }
        }

        private void BtnHomeModelPicker_Click(object sender, RoutedEventArgs e)
        {
            if (HomeModelPopup == null || HomeModelPickerList == null) return;
            RefreshHomeModelPickerList();
            HomeModelPopup.IsOpen = !HomeModelPopup.IsOpen;
            if (HomeModelPopup.IsOpen) TxtHomeModelSearch?.Focus();
        }

        private void TxtHomeModelSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshHomeModelPickerList();
        }

        private void BtnHomeModelFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                _homeModelFilter = tag;
                RefreshHomeModelPickerList();
            }
        }

        private void HomeModelPickerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HomeModelPickerList?.SelectedItem is ModelItem model)
            {
                SetActiveModel(model);
                UpdateHomeGreetingAndLabels();
                HomeModelPopup.IsOpen = false;
            }
        }

        // ===== + Add Context Menu & Attached Files =====
        private void RefreshHomeAttachedFilesUi()
        {
            if (HomeAttachedFilesList == null) return;
            HomeAttachedFilesList.ItemsSource = null;
            HomeAttachedFilesList.ItemsSource = _attachedHomeFiles.ToList();
            HomeAttachedFilesList.Visibility = _attachedHomeFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateHomeContextRing();
        }

        private void AttachHomeFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            if (_attachedHomeFiles.Any(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase))) return;

            string content = string.Empty;
            try
            {
                const int maxChars = 24000;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                char[] buf = new char[maxChars];
                int read = sr.Read(buf, 0, maxChars);
                content = new string(buf, 0, read);
                if (!sr.EndOfStream) content += "\n... (truncated)";
            }
            catch { }

            _attachedHomeFiles.Add(new HomeAttachedContextItem
            {
                FullPath = path,
                DisplayName = Path.GetFileName(path),
                CachedContent = content
            });
            RefreshHomeAttachedFilesUi();
        }

        private void BtnHomeAddContext_Click(object sender, RoutedEventArgs e)
        {
            if (HomeAddContextPopup == null) return;
            HomeAddContextPopup.IsOpen = !HomeAddContextPopup.IsOpen;
        }

        private void BtnCtxFiles_Click(object sender, RoutedEventArgs e)
        {
            if (HomeAddContextPopup != null) HomeAddContextPopup.IsOpen = false;
            var dlg = new OpenFileDialog
            {
                Title = "Attach Files to Conversation Context",
                Multiselect = true,
                Filter = "All Files (*.*)|*.*|Code & Text (*.cs;*.xaml;*.py;*.js;*.ts;*.json;*.md;*.txt)|*.cs;*.xaml;*.py;*.js;*.ts;*.json;*.md;*.txt"
            };
            if (dlg.ShowDialog() == true)
            {
                foreach (var f in dlg.FileNames) AttachHomeFile(f);
            }
        }

        private void BtnCtxFolder_Click(object sender, RoutedEventArgs e)
        {
            if (HomeAddContextPopup != null) HomeAddContextPopup.IsOpen = false;
            var dlg = new OpenFolderDialog
            {
                Title = "Select Workspace Folder for Context",
                Multiselect = false
            };
            if (dlg.ShowDialog() == true)
            {
                string folder = dlg.FolderName;
                var existing = _projects.FirstOrDefault(p => string.Equals(p.Path, folder, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new ProjectItem
                    {
                        Name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                        Path = folder,
                        Description = "Linked workspace folder",
                        CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        LastOpened = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                    };
                    _projects.Insert(0, existing);
                    SaveProjects();
                    RefreshProjectsUi();
                }
                _homeChatProject = existing;
                if (_activeHomeThread != null && _activeHomeThread.Messages.Count == 0)
                {
                    _activeHomeThread.ProjectPath = existing.Path;
                    _activeHomeThread.ProjectName = existing.Name;
                }
                UpdateHomeGreetingAndLabels();
            }
        }

        private void BtnCtxMentions_Click(object sender, RoutedEventArgs e)
        {
            if (HomeAddContextPopup != null) HomeAddContextPopup.IsOpen = false;
            if (HomeMentionsPopup == null || HomeMentionsList == null) return;

            var items = new List<string>();
            if (_homeChatProject != null && Directory.Exists(_homeChatProject.Path))
            {
                try
                {
                    items.AddRange(Directory.EnumerateFiles(_homeChatProject.Path, "*", SearchOption.AllDirectories)
                        .Where(p => !p.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
                                 && !p.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                                 && !p.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase))
                        .Take(60)
                        .Select(p => Path.GetRelativePath(_homeChatProject.Path, p)));
                }
                catch { }
            }
            if (items.Count == 0)
            {
                items.Add("(Attach a workspace folder first to mention project files)");
            }
            HomeMentionsList.ItemsSource = items;
            HomeMentionsPopup.IsOpen = true;
        }

        private void HomeMentionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HomeMentionsList?.SelectedItem is not string rel) return;
            if (HomeMentionsPopup != null) HomeMentionsPopup.IsOpen = false;
            if (_homeChatProject != null && !rel.StartsWith("("))
            {
                string full = Path.Combine(_homeChatProject.Path, rel);
                AttachHomeFile(full);
                if (TxtHomePrompt != null)
                {
                    string insert = $"@{rel} ";
                    TxtHomePrompt.Text = (TxtHomePrompt.Text ?? string.Empty) + insert;
                    TxtHomePrompt.CaretIndex = TxtHomePrompt.Text.Length;
                    TxtHomePrompt.Focus();
                }
            }
        }

        private void BtnCtxActions_Click(object sender, RoutedEventArgs e)
        {
            if (HomeAddContextPopup != null) HomeAddContextPopup.IsOpen = false;
            if (HomePresetsPopup == null || HomePresetsList == null) return;
            HomePresetsList.ItemsSource = _settings.SystemInstructionPresets;
            HomePresetsPopup.IsOpen = true;
        }

        private void HomePresetsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HomePresetsList?.SelectedItem is SystemInstructionPreset preset)
            {
                _settings.SystemInstructionEnabled = true;
                _settings.SystemInstruction = preset.Text;
                SaveSettings();
                UpdateHomeContextRing();
                if (HomePresetsPopup != null) HomePresetsPopup.IsOpen = false;
            }
        }

        private void BtnCtxClear_Click(object sender, RoutedEventArgs e)
        {
            if (HomeAddContextPopup != null) HomeAddContextPopup.IsOpen = false;
            _attachedHomeFiles.Clear();
            RefreshHomeAttachedFilesUi();
        }

        private void BtnRemoveHomeContextFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is HomeAttachedContextItem item)
            {
                _attachedHomeFiles.Remove(item);
                RefreshHomeAttachedFilesUi();
            }
        }

        // ===== Tiny Context Window Circle Bar =====
        private void TxtHomePrompt_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateHomeContextRing();
        }

        private static string FormatCompactTokens(int tokens)
        {
            if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:0.#}M";
            if (tokens >= 1_000) return $"{tokens / 1_000.0:0.#}k";
            return tokens.ToString();
        }

        private void UpdateHomeContextRing()
        {
            if (HomeContextArcPath == null || TxtHomeContextLabel == null) return;

            var caps = ModelCapabilityResolver.Resolve(_activeModel);
            int maxCtx = Math.Max(4096, caps.ContextWindow);

            int used = 0;
            if (_settings.SystemInstructionEnabled && !string.IsNullOrWhiteSpace(_settings.SystemInstruction))
                used += TokenHelper.CountTokens(_settings.SystemInstruction);
            if (_activeHomeThread != null)
            {
                foreach (var m in _activeHomeThread.Messages)
                    used += TokenHelper.CountTokens(m.Content);
            }
            if (TxtHomePrompt != null && !string.IsNullOrEmpty(TxtHomePrompt.Text))
                used += TokenHelper.CountTokens(TxtHomePrompt.Text);
            foreach (var f in _attachedHomeFiles)
                used += TokenHelper.CountTokens(f.CachedContent);

            double ratio = Math.Clamp((double)used / maxCtx, 0.0, 1.0);
            int pct = (int)Math.Round(ratio * 100.0);

            TxtHomeContextLabel.Text = $"{pct}% · {FormatCompactTokens(maxCtx)}";
            if (HomeContextRingContainer != null)
            {
                HomeContextRingContainer.ToolTip = $"Context window: {used:N0} / {maxCtx:N0} tokens ({pct}%)";
            }

            string brushKey = ratio >= 0.90 ? "BrushDanger" : (ratio >= 0.70 ? "BrushWarning" : "BrushAccentLight");
            if (TryFindResource(brushKey) is Brush strokeBrush)
            {
                HomeContextArcPath.Stroke = strokeBrush;
            }

            if (ratio <= 0.005)
            {
                HomeContextArcPath.Data = null;
                return;
            }

            // Draw arc on 16x16 circle (center 8,8, radius 6.5)
            double angle = Math.Min(359.5, ratio * 360.0);
            double rad = (angle - 90.0) * Math.PI / 180.0;
            const double cx = 8.0, cy = 8.0, r = 6.5;
            var start = new Point(cx, cy - r);
            var end = new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
            bool largeArc = angle > 180.0;

            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, largeArc, SweepDirection.Clockwise, true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            HomeContextArcPath.Data = geo;
        }

        private void TxtHomePrompt_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                BtnHomeSend_Click(sender, new RoutedEventArgs());
            }
        }

        private async void BtnHomeSend_Click(object sender, RoutedEventArgs e)
        {
            if (_homeChatBusy || TxtHomePrompt == null) return;
            string prompt = (TxtHomePrompt.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            await ExecuteHomePromptAsync(prompt, addUserMessage: true);
        }

        private void BtnHomeStop_Click(object sender, RoutedEventArgs e)
        {
            try { _homeGenerationCts?.Cancel(); } catch { }
        }

        private async Task ExecuteHomePromptAsync(string prompt, bool addUserMessage)
        {
            if (_homeChatBusy) return;

            if (_activeModel == null)
            {
                if (_allModels.Count > 0) SetActiveModel(_allModels[0]);
                else
                {
                    MessageBox.Show("No models are loaded. Open Models & Keys first.", "No Active Model", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            if (_activeHomeThread == null)
            {
                _activeHomeThread = new HomeChatThread
                {
                    ProjectPath = _homeChatProject?.Path ?? string.Empty,
                    ProjectName = _homeChatProject?.Name ?? string.Empty
                };
            }

            if (!_homeThreads.Any(t => t.Id == _activeHomeThread.Id))
            {
                string title = prompt.Replace("\r", " ").Replace("\n", " ").Trim();
                if (title.Length > 48) title = title.Substring(0, 48).TrimEnd() + "...";
                _activeHomeThread.Title = title;
                _activeHomeThread.ProjectPath = _homeChatProject?.Path ?? string.Empty;
                _activeHomeThread.ProjectName = _homeChatProject?.Name ?? string.Empty;
                _homeThreads.Insert(0, _activeHomeThread);
            }

            ChatMessageItem? userMsg = null;
            if (addUserMessage)
            {
                if (TxtHomePrompt != null) TxtHomePrompt.Text = string.Empty;
                ShowView(ViewChatHome, null);

                string userInitials = string.IsNullOrWhiteSpace(_settings.ProfileName) ? "Operator" : _settings.ProfileName;
                var nowUtc = DateTime.UtcNow;
                userMsg = new ChatMessageItem
                {
                    ConversationId = _activeHomeThread.Id,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                    SenderRole = "User",
                    SenderName = userInitials,
                    Content = prompt,
                    Timestamp = DateTime.Now.ToString("HH:mm:ss")
                };
                _activeHomeThread.Messages.Add(userMsg);
                _activeHomeThread.UpdatedAtUtc = nowUtc;
                SaveHomeThreads();
                RefreshHomeConversationsUi();
                RefreshHomeChatThreadUi();
                RefreshProjectsUi();
                UpdateHomePreviousWorkCard();
            }
            else
            {
                userMsg = _activeHomeThread.Messages.LastOrDefault(m => m.IsUser);
            }

            var requestModel = _activeModel!;
            var history = new List<AiMessage>();
            int historyEnd = _activeHomeThread.Messages.Count - 1;
            for (int i = 0; i < historyEnd; i++)
            {
                var cm = _activeHomeThread.Messages[i];
                if (string.IsNullOrWhiteSpace(cm.Content)) continue;
                if (cm.IsUser) history.Add(AiMessage.User(cm.Content));
                else if (cm.IsAssistant && !cm.IsError) history.Add(AiMessage.Assistant(cm.Content));
            }

            string effectivePrompt = prompt;
            if (_attachedHomeFiles.Count > 0)
            {
                var sb = new StringBuilder(prompt);
                sb.AppendLine();
                sb.AppendLine();
                foreach (var f in _attachedHomeFiles)
                {
                    sb.AppendLine($"[Attached Context: {f.DisplayName}]");
                    sb.AppendLine(f.CachedContent);
                    sb.AppendLine();
                }
                effectivePrompt = sb.ToString();
                _attachedHomeFiles.Clear();
                RefreshHomeAttachedFilesUi();
            }

            string? priorSummary = _activeHomeThread.LatestCheckpointSummary
                ?? _services?.Continuity?.GetLatestCheckpoint(_activeHomeThread.Id)?.SummaryText;

            var ctxRequest = new ContextRequest
            {
                Model = requestModel,
                Settings = _settings,
                UserPrompt = effectivePrompt,
                SystemInstruction = (_settings.SystemInstructionEnabled && !string.IsNullOrWhiteSpace(_settings.SystemInstruction))
                    ? _settings.SystemInstruction : null,
                History = history,
                ProjectKey = _homeChatProject?.Path,
                ConversationId = _activeHomeThread.Id,
                ConversationSummary = priorSummary,
                EnableAutoCompact = _settings.AutoCompactEnabled,
                AutoCompactThresholdRatio = Math.Clamp(_settings.AutoCompactThresholdPercent / 100.0, 0.40, 0.92)
            };

            var assistantUtc = DateTime.UtcNow;
            var assistantMsg = new ChatMessageItem
            {
                ConversationId = _activeHomeThread.Id,
                SequenceNumber = _activeHomeThread.Messages.Count + 1,
                CreatedAtUtc = assistantUtc,
                UpdatedAtUtc = assistantUtc,
                SenderRole = "Assistant",
                SenderName = requestModel.Name,
                ModelName = requestModel.Name,
                Provider = requestModel.Provider,
                ActivityState = MessageActivityState.Connecting,
                ActivityDetail = $"Connecting to {requestModel.Name}...",
                IsStreaming = true,
                Timestamp = DateTime.Now.ToString("HH:mm:ss")
            };
            _activeHomeThread.Messages.Add(assistantMsg);
            TurnRecord? activeTurn = userMsg != null
                ? _services?.Continuity?.BeginTurn(_activeHomeThread.Id, _homeChatProject?.Path, userMsg, assistantMsg)
                : null;
            if (HomeChatMessagesList != null)
            {
                if (HomeChatMessagesList.ItemsSource != _homeDisplayedMessages)
                    HomeChatMessagesList.ItemsSource = _homeDisplayedMessages;
                HomeChatEmptyState.Visibility = Visibility.Collapsed;
                HomeChatMessagesList.Visibility = Visibility.Visible;
                _homeDisplayedMessages.Add(assistantMsg);
                _ = Dispatcher.BeginInvoke(new Action(() => HomeChatMessagesList.ScrollIntoView(assistantMsg)), DispatcherPriority.Background);
            }

            _homeChatBusy = true;
            _homeGenerationCts = new CancellationTokenSource();
            if (BtnHomeSend != null) BtnHomeSend.Visibility = Visibility.Collapsed;
            if (BtnHomeStop != null) BtnHomeStop.Visibility = Visibility.Visible;

            AgentActivityBus.Emit(AgentNode.Prompt, $"[Home] \"{prompt}\"");
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
                    if (activeTurn != null)
                        _services?.Continuity?.CheckpointStreamingProgress(activeTurn, assistantMsg, snapshot);
                    HomeChatMessagesList?.ScrollIntoView(assistantMsg);
                }
                assistantMsg.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            };
            flushTimer.Start();

            AiResponse? aiResp = null;
            bool runtimeError = false;
            bool cancelled = false;
            string responseText = string.Empty;
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
                aiResp = await _aiRuntime.RunWithProgressAsync(ctxRequest, OnProgress, _homeGenerationCts.Token);
                runtimeError = aiResp.IsError;
                promptTokenEstimate = aiResp.PromptTokensEstimate;
                responseText = aiResp.IsError ? (aiResp.ErrorMessage ?? aiResp.Text) : aiResp.Text;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                lock (streamLock) { responseText = streamBuffer.ToString(); }
            }
            catch (Exception ex)
            {
                runtimeError = true;
                responseText = $"[ERROR] {ex.Message}";
            }
            finally
            {
                sw.Stop();
                flushTimer.Stop();
                _homeGenerationCts?.Dispose();
                _homeGenerationCts = null;
                _homeChatBusy = false;
                if (BtnHomeStop != null) BtnHomeStop.Visibility = Visibility.Collapsed;
                if (BtnHomeSend != null) BtnHomeSend.Visibility = Visibility.Visible;
            }

            responseText = ReasoningFilter.Split(responseText, out string reasoning);
            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                AgentActivityBus.Emit(AgentNode.Reasoning, reasoning.Length > 160 ? reasoning.Substring(0, 160) + "..." : reasoning);
            }

            bool isErr = !cancelled && (runtimeError || responseText.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase));
            if (!isErr && !cancelled && (_cloudApiService.LastLiveUsage == null || _cloudApiService.LastLiveUsage.TimestampUtc < callStartTime))
            {
                int pTok = promptTokenEstimate > 0 ? promptTokenEstimate : TokenHelper.CountTokens(prompt);
                int cTok = TokenHelper.CountTokens(responseText);
                _tokenUsageService.Record(new TokenUsageRecord
                {
                    TimestampUtc = DateTime.UtcNow,
                    Provider = requestModel.Provider,
                    ModelId = requestModel.Id,
                    ModelName = requestModel.Name,
                    PromptTokens = pTok,
                    CompletionTokens = cTok,
                    TotalTokens = pTok + cTok,
                    LatencyMs = sw.ElapsedMilliseconds,
                    IsLiveApi = false
                });
            }

            assistantMsg.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            assistantMsg.IsStreaming = false;
            assistantMsg.IsError = isErr;
            assistantMsg.Content = responseText;

            if (cancelled)
            {
                assistantMsg.ActivityState = MessageActivityState.Cancelled;
                assistantMsg.ActivityDetail = "Generation cancelled by user.";
                assistantMsg.Blocks = StructuredContentParser.Parse(responseText, aiResp?.StructuredBlocks);
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
                        Message = responseText,
                        Operation = "Chat Generation",
                        CanRetry = true
                    }
                };
            }
            else
            {
                assistantMsg.ActivityState = MessageActivityState.Completed;
                assistantMsg.ActivityDetail = string.Empty;
                assistantMsg.Blocks = StructuredContentParser.Parse(responseText, aiResp?.StructuredBlocks);
            }
            assistantMsg.NotifyBlocksUpdated();

            _activeHomeThread.UpdatedAtUtc = DateTime.UtcNow;
            _activeHomeThread.LatestCheckpointSummary = aiResp?.UpdatedConversationSummary ?? _activeHomeThread.LatestCheckpointSummary;
            _homeThreads.RemoveAll(t => t.Id == _activeHomeThread.Id);
            _homeThreads.Insert(0, _activeHomeThread);
            SaveHomeThreads();

            if (activeTurn != null && _services?.Continuity != null)
            {
                var turnStatus = cancelled
                    ? TurnLifecycleStatus.Cancelled
                    : (isErr ? TurnLifecycleStatus.Failed : TurnLifecycleStatus.Completed);
                var cp = _services.Continuity.CompleteTurnAndCheckpoint(
                    activeTurn,
                    assistantMsg,
                    turnStatus,
                    responseText,
                    runtimeWorkEvents: null,
                    memoryEngine: _services.Middleware.Memory,
                    overrideSummary: aiResp?.UpdatedConversationSummary,
                    promptTokens: promptTokenEstimate);
                _activeHomeThread.LatestCheckpointSummary = cp.SummaryText;
                _activeHomeThread.Status = cp.Status;
            }

            RefreshHomeConversationsUi();
            RefreshProjectsUi();
            UpdateHomeContextRing();
            UpdatePersistenceStatusUi(_services?.Continuity?.GetDiagnostics());
            if (HomeChatMessagesList != null)
            {
                _ = Dispatcher.BeginInvoke(new Action(() => HomeChatMessagesList.ScrollIntoView(assistantMsg)), DispatcherPriority.Background);
            }
        }

        private void HomeRichMessage_TaskChanged(object? sender, EventArgs e)
        {
            SaveHomeThreads();
        }

        private void HomeRichMessage_ReviewChangesRequested(object? sender, DiffSummaryBlock summary)
        {
            _activeDiffSummary = summary;
            if (ModalDiffReview == null || DiffReviewFilesList == null || DiffReviewContentPanel == null) return;

            TxtDiffReviewTitle.Text = string.IsNullOrWhiteSpace(summary.Title) ? "Review Code Changes" : summary.Title;
            TxtDiffReviewStats.Text = $"{summary.Files.Count} file(s)  +{summary.Additions} / -{summary.Deletions}";
            DiffReviewFilesList.ItemsSource = null;
            DiffReviewFilesList.ItemsSource = summary.Files;
            if (summary.Files.Count > 0)
            {
                DiffReviewFilesList.SelectedIndex = 0;
            }
            else
            {
                DiffReviewContentPanel.Children.Clear();
            }
            ModalDiffReview.Visibility = Visibility.Visible;
        }

        private void DiffReviewFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DiffReviewContentPanel == null) return;
            DiffReviewContentPanel.Children.Clear();

            if (DiffReviewFilesList?.SelectedItem is not ChangedFileEntry file) return;

            var diffLines = new WorkspaceService().GenerateDiff(file.BeforeContent, file.AfterContent);
            var addBg = new SolidColorBrush(Color.FromArgb(26, 34, 197, 94));
            var delBg = new SolidColorBrush(Color.FromArgb(26, 239, 68, 68));
            var addFg = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80));
            var delFg = new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71));

            foreach (var dl in diffLines)
            {
                var row = new Border
                {
                    Background = dl.Type == "Added" ? addBg : dl.Type == "Removed" ? delBg : Brushes.Transparent,
                    Padding = new Thickness(8, 2, 8, 2)
                };
                var rg = new Grid();
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var t1 = new TextBlock { Text = dl.DisplayOldLine, FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = TryFindResource("BrushTextDim") as Brush ?? Brushes.Gray, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
                var t2 = new TextBlock { Text = dl.DisplayNewLine, FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = TryFindResource("BrushTextDim") as Brush ?? Brushes.Gray, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
                var t3 = new TextBlock { Text = dl.Prefix, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = dl.Type == "Added" ? addFg : dl.Type == "Removed" ? delFg : Brushes.Gray };
                var t4 = new TextBlock { Text = dl.Content, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, Foreground = dl.Type == "Added" ? addFg : dl.Type == "Removed" ? delFg : (TryFindResource("BrushTextPrimary") as Brush ?? Brushes.White) };

                Grid.SetColumn(t1, 0); Grid.SetColumn(t2, 1); Grid.SetColumn(t3, 2); Grid.SetColumn(t4, 3);
                rg.Children.Add(t1); rg.Children.Add(t2); rg.Children.Add(t3); rg.Children.Add(t4);
                row.Child = rg;
                DiffReviewContentPanel.Children.Add(row);
            }
        }

        private void BtnCloseDiffReview_Click(object sender, RoutedEventArgs e)
        {
            if (ModalDiffReview != null) ModalDiffReview.Visibility = Visibility.Collapsed;
        }

        private void BtnDiffOpenWorkspace_Click(object sender, RoutedEventArgs e)
        {
            if (ModalDiffReview != null) ModalDiffReview.Visibility = Visibility.Collapsed;
            var targetProject = _homeChatProject ?? _activeProject;
            if (targetProject != null) OpenProjectWorkspace(targetProject);
        }

        private void HomeRichMessage_OpenFileRequested(object? sender, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            if (_homeChatProject != null)
            {
                OpenProjectWorkspace(_homeChatProject);
                return;
            }
            try
            {
                if (File.Exists(filePath))
                    Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            catch { }
        }

        private async void HomeRichMessage_RetryRequested(object? sender, ChatMessageItem failedMsg)
        {
            if (_homeChatBusy || _activeHomeThread == null) return;
            int idx = _activeHomeThread.Messages.IndexOf(failedMsg);
            if (idx <= 0) return;

            string? retryPrompt = null;
            for (int i = idx - 1; i >= 0; i--)
            {
                if (_activeHomeThread.Messages[i].IsUser)
                {
                    retryPrompt = _activeHomeThread.Messages[i].Content;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(retryPrompt)) return;

            _activeHomeThread.Messages.RemoveAt(idx);
            _homeDisplayedMessages.Remove(failedMsg);
            await ExecuteHomePromptAsync(retryPrompt, addUserMessage: false);
        }

        // ===== Settings Storage Paths & Dynamic Provider Logos =====
        private void PopulateStoragePathsUi()
        {
            if (TxtPathChatHistory != null)
                TxtPathChatHistory.Text = AppConstants.ChatHistoryDir;
            if (TxtPathAgentMemory != null)
                TxtPathAgentMemory.Text = Path.Combine(AppConstants.ChatHistoryDir, "<project-hash>.mem.json");
            if (TxtPathDataRoot != null)
            {
                TxtPathDataRoot.Text = AppConstants.SettingsFilePath;
                TxtPathDataRoot.ToolTip = $"Encrypted DPAPI Key Vault: {AppConstants.SecretVaultFilePath}";
            }
        }

        private void LoadSettingsProviderLogos()
        {
            TryBindCachedLogo(ImgLogoOpenAI, "openai.com.png");
            TryBindCachedLogo(ImgLogoAnthropic, "anthropic.com.png");
            TryBindCachedLogo(ImgLogoGrok, "x.ai.png");
            TryBindCachedLogo(ImgLogoGemini, "google.com.png");
            TryBindCachedLogo(ImgLogoDeepSeek, "deepseek.com.png");
        }

        private static void TryBindCachedLogo(Image? target, string fileName)
        {
            if (target == null) return;
            try
            {
                string path = Path.Combine(AppConstants.CacheDir, fileName);
                if (!File.Exists(path))
                {
                    target.Source = null;
                    return;
                }
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                target.Source = bmp;
            }
            catch
            {
                target.Source = null;
            }
        }

        private static void EnsureAndOpenFolder(string folderPath)
        {
            try
            {
                Directory.CreateDirectory(folderPath);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"") { UseShellExecute = true });
            }
            catch { }
        }

        private void BtnOpenChatHistoryPath_Click(object sender, RoutedEventArgs e)
            => EnsureAndOpenFolder(AppConstants.ChatHistoryDir);

        private void BtnCopyChatHistoryPath_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(AppConstants.ChatHistoryDir); } catch { }
        }

        private void BtnOpenAgentMemoryPath_Click(object sender, RoutedEventArgs e)
            => EnsureAndOpenFolder(AppConstants.ChatHistoryDir);

        private void BtnCopyAgentMemoryPath_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(Path.Combine(AppConstants.ChatHistoryDir, "*.mem.json")); } catch { }
        }

        private void BtnOpenDataRootPath_Click(object sender, RoutedEventArgs e)
            => EnsureAndOpenFolder(AppConstants.DataDirectory);

        private void BtnCopyDataRootPath_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(AppConstants.SettingsFilePath); } catch { }
        }
    }
}
