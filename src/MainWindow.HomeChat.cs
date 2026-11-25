using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Context;
using LlmodelBox.Services;

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
        private HomeChatThread? _activeHomeThread = null;
        private ProjectItem? _homeChatProject = null;
        private bool _homeChatBusy = false;

        private sealed class HomeWorkspaceOption
        {
            public string Name { get; set; } = "General Chat (No Workspace)";
            public ProjectItem? Project { get; set; }
        }

        private string HomeThreadsFilePath => Path.Combine(AppConstants.ChatHistoryDir, "home_conversations.json");

        private void InitHomeChat()
        {
            LoadHomeThreads();
            if (_activeHomeThread == null)
            {
                _activeHomeThread = new HomeChatThread();
            }
            UpdateHomeGreetingAndLabels();
            RefreshHomeConversationsUi();
            RefreshHomeChatThreadUi();
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

        private void UpdateHomeGreetingAndLabels()
        {
            if (TxtHomeGreeting != null)
            {
                string who = string.IsNullOrWhiteSpace(_settings?.ProfileName) ? "" : $", {_settings.ProfileName.Trim()}";
                TxtHomeGreeting.Text = $"Welcome back{who}. Where should we pick up?";
            }
            if (TxtHomeModelLabel != null)
            {
                TxtHomeModelLabel.Text = _activeModel != null ? _activeModel.Name : "Select Model";
            }
            if (TxtHomeWorkspaceCtx != null)
            {
                TxtHomeWorkspaceCtx.Text = _homeChatProject != null
                    ? $"This Computer · {_homeChatProject.Name}"
                    : "This Computer · General Chat";
            }
        }

        private void RefreshHomeConversationsUi()
        {
            if (HomeConversationsList == null) return;
            foreach (var t in _homeThreads)
            {
                t.IsSelected = _activeHomeThread != null && string.Equals(t.Id, _activeHomeThread.Id, StringComparison.Ordinal);
            }
            HomeConversationsList.ItemsSource = null;
            HomeConversationsList.ItemsSource = _homeThreads.ToList();
            if (TxtNoConversationsHint != null)
            {
                TxtNoConversationsHint.Visibility = _homeThreads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                Dispatcher.BeginInvoke(new Action(() => HomeChatThreadScroller.ScrollToEnd()),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void BtnNavChat_Click(object sender, RoutedEventArgs e)
        {
            UpdateHomeGreetingAndLabels();
            ShowView(ViewChatHome, ChatHomeTranslate);
        }

        private void BtnNewConversation_Click(object sender, RoutedEventArgs e)
        {
            _activeHomeThread = new HomeChatThread
            {
                ProjectPath = _homeChatProject?.Path ?? string.Empty,
                ProjectName = _homeChatProject?.Name ?? string.Empty
            };
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
                    _activeHomeThread = new HomeChatThread();
                    RefreshHomeChatThreadUi();
                }
                RefreshHomeConversationsUi();
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
                if (_activeHomeThread != null)
                {
                    _activeHomeThread.ProjectPath = _homeChatProject?.Path ?? string.Empty;
                    _activeHomeThread.ProjectName = _homeChatProject?.Name ?? string.Empty;
                }
                UpdateHomeGreetingAndLabels();
                HomeWorkspacePopup.IsOpen = false;
            }
        }

        private void BtnHomeModelPicker_Click(object sender, RoutedEventArgs e)
        {
            if (HomeModelPopup == null || HomeModelPickerList == null) return;
            var sorted = _allModels
                .OrderByDescending(m => !string.IsNullOrWhiteSpace(_cloudApiService.GetApiKeyForProvider(m.Provider, _settings)) || (m.IsCustom && !string.IsNullOrWhiteSpace(m.CustomApiKey)))
                .ThenBy(m => m.Provider)
                .ThenBy(m => m.Name)
                .Take(120)
                .ToList();
            HomeModelPickerList.ItemsSource = sorted;
            HomeModelPopup.IsOpen = !HomeModelPopup.IsOpen;
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

            if (_activeHomeThread == null) _activeHomeThread = new HomeChatThread();

            // Persist thread in list on first message
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

            var requestModel = _activeModel!;
            var history = new List<AiMessage>();
            for (int i = 0; i < _activeHomeThread.Messages.Count - 1; i++)
            {
                var cm = _activeHomeThread.Messages[i];
                if (string.IsNullOrWhiteSpace(cm.Content)) continue;
                if (cm.IsUser) history.Add(AiMessage.User(cm.Content));
                else if (cm.IsAssistant && !cm.IsError) history.Add(AiMessage.Assistant(cm.Content));
            }

            var ctxRequest = new ContextRequest
            {
                Model = requestModel,
                Settings = _settings,
                UserPrompt = prompt,
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

            // Keep most recently active thread near the top
            _homeThreads.RemoveAll(t => t.Id == _activeHomeThread.Id);
            _homeThreads.Insert(0, _activeHomeThread);
            SaveHomeThreads();
            RefreshHomeConversationsUi();
            RefreshHomeChatThreadUi();
        }
    }
}
