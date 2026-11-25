using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Providers;
using LlmodelBox.Runtime.Context;
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
        public List<ChatMessageItem> Messages { get; set; } = new();

        [JsonIgnore]
        public bool IsSelected { get; set; }

        [JsonIgnore]
        public string Subtitle => string.IsNullOrWhiteSpace(ProjectName) ? "General Chat" : ProjectName;

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
        private readonly JsonChatHistoryStore _ideChatHistoryStore = new();
        private readonly List<HomeAttachedContextItem> _attachedHomeFiles = new();
        private HomeChatThread? _activeHomeThread = null;
        private ProjectItem? _homeChatProject = null;
        private ProjectItem? _sidebarFilterProject = null;
        private bool _sidebarFilterGeneralOnly = false;
        private string _homeModelFilter = "All";
        private bool _homeChatBusy = false;

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
            LoadHomeThreads();
            foreach (var p in _projects)
            {
                EnsureProjectIdeHistoryImported(p);
            }
            if (_activeHomeThread == null)
            {
                _activeHomeThread = new HomeChatThread();
            }
            UpdateHomeGreetingAndLabels();
            RefreshHomeConversationsUi();
            RefreshHomeChatThreadUi();
            RefreshProjectsUi();
            UpdateHomeContextRing();
        }

        private void LoadHomeThreads()
        {
            _homeThreads.Clear();
            try
            {
                if (File.Exists(HomeThreadsFilePath))
                {
                    string json = File.ReadAllText(HomeThreadsFilePath);
                    var loaded = JsonSerializer.Deserialize<List<HomeChatThread>>(json);
                    if (loaded != null)
                    {
                        _homeThreads.AddRange(loaded.OrderByDescending(t => t.UpdatedAtUtc));
                    }
                }
            }
            catch { }
        }

        private void SaveHomeThreads()
        {
            try
            {
                Directory.CreateDirectory(AppConstants.ChatHistoryDir);
                string json = JsonSerializer.Serialize(_homeThreads.Take(100).ToList(), new JsonSerializerOptions { WriteIndented = false });
                AtomicFile.WriteAllText(HomeThreadsFilePath, json);
            }
            catch { }
        }

        // Surfaces existing per-project IDE history (chat/<hash>.json) inside the project's
        // conversation list if no HomeChatThread has been created for that project yet.
        private void EnsureProjectIdeHistoryImported(ProjectItem project)
        {
            if (project == null || string.IsNullOrWhiteSpace(project.Path)) return;
            bool hasAnyForProject = _homeThreads.Any(t => string.Equals(t.ProjectPath, project.Path, StringComparison.OrdinalIgnoreCase));
            if (hasAnyForProject) return;

            var legacy = _ideChatHistoryStore.Load(project.Path);
            if (legacy.Count == 0) return;

            var firstUser = legacy.FirstOrDefault(m => m.IsUser && !string.IsNullOrWhiteSpace(m.Content));
            string title = firstUser != null
                ? firstUser.Content.Replace("\r", " ").Replace("\n", " ").Trim()
                : $"{project.Name} History";
            if (title.Length > 48) title = title.Substring(0, 48).TrimEnd() + "...";

            _homeThreads.Add(new HomeChatThread
            {
                Title = title,
                ProjectPath = project.Path,
                ProjectName = project.Name,
                UpdatedAtUtc = DateTime.UtcNow,
                Messages = legacy
            });
            SaveHomeThreads();
        }

        internal int GetConversationCountForProject(ProjectItem project)
        {
            if (project == null || string.IsNullOrWhiteSpace(project.Path)) return 0;
            int count = _homeThreads.Count(t => string.Equals(t.ProjectPath, project.Path, StringComparison.OrdinalIgnoreCase));
            if (count == 0 && _ideChatHistoryStore.Load(project.Path).Count > 0) count = 1;
            return count;
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
            if (HomeChatMessagesList == null || HomeChatEmptyState == null || HomeChatThreadScroller == null) return;

            var msgs = _activeHomeThread?.Messages ?? new List<ChatMessageItem>();
            bool hasMessages = msgs.Count > 0;
            HomeChatEmptyState.Visibility = hasMessages ? Visibility.Collapsed : Visibility.Visible;
            HomeChatThreadScroller.Visibility = hasMessages ? Visibility.Visible : Visibility.Collapsed;

            HomeChatMessagesList.ItemsSource = null;
            HomeChatMessagesList.ItemsSource = msgs.ToList();

            if (hasMessages)
            {
                _ = Dispatcher.BeginInvoke(new Action(() => HomeChatThreadScroller.ScrollToEnd()),
                    System.Windows.Threading.DispatcherPriority.Background);
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
                    _activeHomeThread = projectThreads[0];
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
            TxtHomePrompt?.Focus();
        }

        private void HomeConversation_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is HomeChatThread thread)
            {
                _activeHomeThread = thread;
                _homeChatProject = !string.IsNullOrWhiteSpace(thread.ProjectPath)
                    ? _projects.FirstOrDefault(p => string.Equals(p.Path, thread.ProjectPath, StringComparison.OrdinalIgnoreCase))
                    : null;
                UpdateHomeGreetingAndLabels();
                ShowView(ViewChatHome, ChatHomeTranslate);
                RefreshHomeConversationsUi();
                RefreshHomeChatThreadUi();
            }
        }

        private void BtnDeleteHomeConversation_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button btn && btn.Tag is HomeChatThread thread)
            {
                _homeThreads.RemoveAll(t => t.Id == thread.Id);
                SaveHomeThreads();
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

            TxtHomePrompt.Text = string.Empty;
            ShowView(ViewChatHome, null);

            string userInitials = string.IsNullOrWhiteSpace(_settings.ProfileName) ? "Operator" : _settings.ProfileName;
            var userMsg = new ChatMessageItem
            {
                SenderRole = "User",
                SenderName = userInitials,
                Content = prompt,
                Timestamp = DateTime.Now.ToString("HH:mm")
            };
            _activeHomeThread.Messages.Add(userMsg);
            _activeHomeThread.UpdatedAtUtc = DateTime.UtcNow;
            SaveHomeThreads();
            RefreshHomeConversationsUi();
            RefreshHomeChatThreadUi();
            RefreshProjectsUi();

            var requestModel = _activeModel!;
            var history = new List<AiMessage>();
            for (int i = 0; i < _activeHomeThread.Messages.Count - 1; i++)
            {
                var cm = _activeHomeThread.Messages[i];
                if (string.IsNullOrWhiteSpace(cm.Content)) continue;
                if (cm.IsUser) history.Add(AiMessage.User(cm.Content));
                else if (cm.IsAssistant && !cm.IsError) history.Add(AiMessage.Assistant(cm.Content));
            }

            string effectivePrompt = prompt;
            if (_attachedHomeFiles.Count > 0)
            {
                var sb = new System.Text.StringBuilder(prompt);
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

            var ctxRequest = new ContextRequest
            {
                Model = requestModel,
                Settings = _settings,
                UserPrompt = effectivePrompt,
                SystemInstruction = (_settings.SystemInstructionEnabled && !string.IsNullOrWhiteSpace(_settings.SystemInstruction))
                    ? _settings.SystemInstruction : null,
                History = history,
                ProjectKey = _homeChatProject?.Path
            };

            _homeChatBusy = true;
            if (BtnHomeSend != null) BtnHomeSend.IsEnabled = false;
            if (HomeThinkingBar != null) HomeThinkingBar.Visibility = Visibility.Visible;
            if (TxtHomeThinking != null) TxtHomeThinking.Text = $"{requestModel.Name} is thinking...";
            _ = Dispatcher.BeginInvoke(new Action(() => HomeChatThreadScroller?.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Background);

            AgentActivityBus.Emit(AgentNode.Prompt, $"[Home] \"{prompt}\"");
            DateTime callStartTime = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();
            string responseText;
            bool runtimeError;
            int promptTokenEstimate = 0;

            try
            {
                var aiResp = await _aiRuntime.RunAsync(ctxRequest, CancellationToken.None);
                runtimeError = aiResp.IsError;
                promptTokenEstimate = aiResp.PromptTokensEstimate;
                responseText = aiResp.IsError ? (aiResp.ErrorMessage ?? aiResp.Text) : aiResp.Text;
            }
            catch (Exception ex)
            {
                runtimeError = true;
                responseText = $"[ERROR] {ex.Message}";
            }
            finally
            {
                sw.Stop();
                _homeChatBusy = false;
                if (BtnHomeSend != null) BtnHomeSend.IsEnabled = true;
                if (HomeThinkingBar != null) HomeThinkingBar.Visibility = Visibility.Collapsed;
            }

            responseText = ReasoningFilter.Split(responseText, out string reasoning);
            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                AgentActivityBus.Emit(AgentNode.Reasoning, reasoning.Length > 160 ? reasoning.Substring(0, 160) + "..." : reasoning);
            }

            bool isErr = runtimeError || responseText.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase);
            if (!isErr && (_cloudApiService.LastLiveUsage == null || _cloudApiService.LastLiveUsage.TimestampUtc < callStartTime))
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

            _activeHomeThread.Messages.Add(new ChatMessageItem
            {
                SenderRole = "Assistant",
                SenderName = requestModel.Name,
                ModelName = requestModel.Name,
                Provider = requestModel.Provider,
                Content = responseText,
                IsError = isErr,
                Timestamp = DateTime.Now.ToString("HH:mm")
            });
            _activeHomeThread.UpdatedAtUtc = DateTime.UtcNow;

            _homeThreads.RemoveAll(t => t.Id == _activeHomeThread.Id);
            _homeThreads.Insert(0, _activeHomeThread);
            SaveHomeThreads();
            RefreshHomeConversationsUi();
            RefreshHomeChatThreadUi();
            RefreshProjectsUi();
            UpdateHomeContextRing();
        }

        // ===== Settings Storage Paths & Dynamic Provider Logos =====
        private void PopulateStoragePathsUi()
        {
            if (TxtPathChatHistory != null)
                TxtPathChatHistory.Text = AppConstants.ChatHistoryDir;
            if (TxtPathAgentMemory != null)
                TxtPathAgentMemory.Text = Path.Combine(AppConstants.ChatHistoryDir, "<project-hash>.mem.json");
            if (TxtPathDataRoot != null)
                TxtPathDataRoot.Text = AppConstants.SettingsFilePath;
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
