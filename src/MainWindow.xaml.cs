using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
using LlmodelBox.Services;
using LlmodelBox.Controls;

namespace LlmodelBox
{
    public partial class MainWindow : Window
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        private readonly ILogoService _logoService;
        private readonly ISettingsService _settingsService;
        private readonly IProjectService _projectService;
        private readonly IModelLibraryService _modelService;
        private readonly ICloudApiService _cloudApiService;
        private readonly ITokenUsageService _tokenUsageService;
        private readonly LlmodelBox.Runtime.IAiRuntime _aiRuntime;

        private bool _isFullscreen = false;
        private bool _isAnimatingFullscreen = false;
        private Rect _normalWindowRect = new Rect(100, 100, 1100, 700);

        private List<ProjectItem> _projects = new();
        private List<ModelItem> _models = new();
        private List<ModelItem> _allModels = new();
        private List<CompanyCategoryItem> _allCompanyCategories = new();
        private List<CompanyCategoryItem> _companyCategories = new();
        private string _currentCompanyFilter = "All";
        // Live-fetched model catalogs for aggregator providers (OpenRouter / Hugging Face), keyed by
        // category id. _aggregatorFetchedForKey records the key fingerprint last fetched so a changed
        // key re-fetches while a search/filter re-render does not hammer the provider.
        private readonly Dictionary<string, List<ModelItem>> _aggregatorModels = new();
        private readonly Dictionary<string, int> _aggregatorFetchedForKey = new();
        private readonly HashSet<string> _aggregatorFetching = new();
        private static readonly string[] _aggregatorProviderIds = {
            "openrouter", "openai", "groq", "mistral", "deepseek", "google",
            "xai", "cohere", "nvidia", "sambanova", "huggingface", "anthropic"
        };
        private System.Threading.CancellationTokenSource? _loadingCts = null;
        private CompanyCategoryItem? _pendingCategory = null;
        private ModelItem? _pendingModelToLoad = null;
        private ModelItem? _activeModel = null;
        private UserSettings _settings = new();
        private ProjectItem? _activeProject = null;
        private string _pendingCustomLogoPath = string.Empty;
        private readonly Dictionary<string, IdeWindow> _ideWindows = new(StringComparer.OrdinalIgnoreCase);
        // Shared tool registry: project-scoped file tools are (re)registered here by each open
        // WorkspaceControl as its project loads, so the runtime's agent loop can execute them.
        private readonly LlmodelBox.Runtime.Tools.ToolRegistry _toolRegistry = new();
        private readonly DiscordRichPresenceService _discord = new();
        private string _discordActivity = "idle";

        private IEnumerable<LlmodelBox.Controls.WorkspaceControl> OpenWorkspaces()
            => _ideWindows.Values.Select(w => w.Workspace);


        // Raised during MainWindow_Loaded after each real load stage so the splash can reflect
        // actual startup progress. Additive only; does not alter existing load order.
        public event Action<double, string>? StartupProgress;

        // Raised once the (now async) startup load has fully finished. App uses this — not Loaded — to
        // hand off from the splash, because an async-void Loaded returns at its first await and would
        // otherwise trigger the handoff before the stages complete.
        public event Action? StartupCompleted;

        public MainWindow()
        {
            InitializeComponent();
            _logoService = new LogoService(_httpClient);
            _settingsService = new SettingsService();
            _projectService = new ProjectService();
            _modelService = new ModelLibraryService();
            _cloudApiService = new CloudApiService(_httpClient);
            // Build the tool stack: a UI approver gates Sensitive/Dangerous tools behind a modal
            // Yes/No prompt (marshalled to the UI thread); Safe tools auto-run; no approver would fail
            // closed. The agent loop executes approved calls and feeds results back to the model.
            var permissions = new LlmodelBox.Runtime.Tools.ToolPermissionEngine(ApproveToolCall);
            var toolExecutor = new LlmodelBox.Runtime.Tools.ToolExecutor(_toolRegistry, permissions);
            var agentLoop = new LlmodelBox.Runtime.Tools.AgentLoop(toolExecutor);
            _aiRuntime = new LlmodelBox.Runtime.AiRuntime(
                new LlmodelBox.Providers.ModelRouter(new LlmodelBox.Providers.IModelProvider[] { new LlmodelBox.Providers.CloudApiProvider(_cloudApiService) }),
                new LlmodelBox.Runtime.Context.ContextEngine(),
                new LlmodelBox.Runtime.Memory.MemoryEngine(),
                _toolRegistry,
                agentLoop,
                new LlmodelBox.Runtime.Knowledge.KnowledgeEngine(),
                new LlmodelBox.Runtime.Intent.IntentEngine(),
                new LlmodelBox.Runtime.TaskManager());
            _tokenUsageService = new TokenUsageService();
            // Persist token usage from every successful call; refresh dashboard and inspector telemetry.
            _cloudApiService.UsageRecorded += record => _tokenUsageService.Record(record);
            _tokenUsageService.UsageRecorded += OnUsageRecorded;
            AgentActivityBus.SignalEmitted += OnAgentSignalForPresence;
            Loaded += MainWindow_Loaded;
        }

        // UI approver for Sensitive/Dangerous tool calls. Runs synchronously on the UI thread (the
        // agent loop awaits the decision on a background thread, so we marshal via the Dispatcher).
        // Returns true only on an explicit Yes. Any failure falls through to a denial (fail closed).
        private bool ApproveToolCall(LlmodelBox.Runtime.Tools.ToolPermissionRequest req)
        {
            try
            {
                bool Prompt()
                {
                    string action = LlmodelBox.Runtime.Tools.ToolCallFormatter.Summary(req.ToolName, req.ArgumentsJson);
                    string details = LlmodelBox.Runtime.Tools.ToolCallFormatter.Details(req.ArgumentsJson, 120);
                    bool danger = string.Equals(req.Risk.ToString(), "Dangerous", StringComparison.OrdinalIgnoreCase);
                    return ConfirmDialog(
                        "Tool approval",
                        $"Allow the AI to {action.ToLowerInvariant()}?",
                        details,
                        confirmLabel: "Allow",
                        danger: danger);
                }
                return Dispatcher.CheckAccess() ? Prompt() : Dispatcher.Invoke(Prompt);
            }
            catch { return false; }
        }

        // Mirror live agent activity into the Discord presence state line ("Neural Net · <stage>").
        private void OnAgentSignalForPresence(AgentSignal sig)
        {
            _discordActivity = sig.Node.ToString().ToLowerInvariant();
            if (_discord.IsConnected)
                _discord.Update(null, $"Neural Net · {_discordActivity}");
        }

        // Starts (or stops) Discord Rich Presence to match the current settings.
        private void ApplyDiscordPresence()
        {
            try
            {
                if (_settings.DiscordRichPresenceEnabled && !string.IsNullOrWhiteSpace(_settings.DiscordClientId))
                {
                    _discord.Start(_settings.DiscordClientId, "LlmodelBox — AI workspace", $"Neural Net · {_discordActivity}");
                }
                else
                {
                    _discord.Stop();
                }
            }
            catch { /* presence is best-effort; never block the app */ }
        }

        protected override void OnClosed(EventArgs e)
        {
            AgentActivityBus.SignalEmitted -= OnAgentSignalForPresence;
            try { _discord.Dispose(); } catch { }
            base.OnClosed(e);
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Each stage: publish progress FIRST (starts the 300ms bar animation), yield so the
            // dispatcher paints a frame and the animation clock ticks, THEN run the blocking work.
            // Without the yields the whole handler ran in one UI pass, so the bar snapped instead of
            // animating and the splash looked frozen.
            await StageAsync(30, "Settings loaded", LoadSettings);
            await StageAsync(50, "Workspaces ready", LoadProjects);
            await StageAsync(70, "Model registry ready", LoadModels);
            await StageAsync(85, "Providers ready", RefreshCompanyCategoriesUi);
            _ = EnsureCompanyLogosAsync();

            // The animated splash (App.xaml.cs) is the only loading screen now; go straight to the
            // clean Chat Home with a subtle entrance.
            LoadingOverlay.Visibility = Visibility.Collapsed;
            InitHomeChat();
            ViewChatHome.Opacity = 0.0;
            ChatHomeTranslate.Y = 18.0;
            ViewChatHome.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            ChatHomeTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(18.0, 0.0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            StartupProgress?.Invoke(95, "Finalizing");
            RefreshLiveData();
            StartupCompleted?.Invoke();
        }

        // Publishes a progress stage, lets the splash render a frame of the bar animation, then runs
        // the (UI-thread) stage work. The short yield/delay is what makes the load look smooth instead
        // of snapping; total added time is bounded and hidden by App's minimum splash show-time.
        private async Task StageAsync(double percent, string stage, Action work)
        {
            StartupProgress?.Invoke(percent, stage);
            await Dispatcher.Yield(DispatcherPriority.Render);
            await Task.Delay(140);
            try { work(); }
            catch (Exception ex) { Debug.WriteLine($"[Startup] stage '{stage}' failed: {ex.Message}"); }
        }

        // Company brand-logo domains — must match the cache filenames used in ModelLibraryService.
        private static readonly string[] _companyLogoDomains =
        {
            "openai.com", "anthropic.com", "google.com", "x.ai", "deepseek.com", "groq.com",
            "openrouter.ai", "cloudflare.com", "mistral.ai", "huggingface.co", "cohere.com",
            "nvidia.com", "sambanova.ai", "bigmodel.cn"
        };

        // Fetch real brand logos (Clearbit CDN) for every company in the background, then refresh the
        // library so the icons swap in. Runs once per launch; cached files are reused on later runs.
        private async Task EnsureCompanyLogosAsync()
        {
            // One-time upgrade: earlier builds cached low-res favicons. Clear those once so the new
            // brand-logo source (unavatar) repopulates them. A sentinel file prevents re-clearing.
            try
            {
                string marker = Path.Combine(Common.AppConstants.CacheDir, "logos.v2");
                if (!File.Exists(marker))
                {
                    foreach (var domain in _companyLogoDomains)
                    {
                        string p = Path.Combine(Common.AppConstants.CacheDir, $"{domain}.png");
                        try { if (File.Exists(p)) File.Delete(p); } catch { }
                    }
                    try { File.WriteAllText(marker, DateTime.UtcNow.ToString("o")); } catch { }
                }
            }
            catch { }

            bool any = false;
            foreach (var domain in _companyLogoDomains)
            {
                try
                {
                    string path = await _logoService.FetchAndCacheLogoAsync(domain);
                    if (!string.IsNullOrEmpty(path)) any = true;
                }
                catch { /* offline or blocked — glyph fallback stays */ }
            }
            if (any)
            {
                Dispatcher.Invoke(RefreshCompanyCategoriesUi);
            }
        }

        #region Settings and Profile Management
        private void LoadSettings()
        {
            _settings = _settingsService.LoadSettings();
            ApplyIconShape(_settings.IconShape);
            ApplyThemeUi(_settings.SelectedTheme);
            BuildAccentSwatches();
            UpdateProfileCardUi();
            ApplyDiscordPresence();
        }

        private void SaveSettings()
        {
            if (!_settingsService.SaveSettings(_settings))
            {
                MessageBox.Show("Could not save settings to settings.json.", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ApplyThemeUi(string? theme)
        {
            theme = string.IsNullOrWhiteSpace(theme) ? ThemeManager.ThemeMidnight : theme;
            ThemeManager.ApplyTheme(theme, Resources, _settings.ThemeAccent);
            UpdateThemePillSelection(theme);
        }

        private Color ThemeColor(string brushKey, string fallbackHex)
        {
            if (TryFindResource(brushKey) is SolidColorBrush b) return b.Color;
            return (Color)ColorConverter.ConvertFromString(fallbackHex);
        }

        private void UpdateThemePillSelection(string theme)
        {
            bool isDaylight = string.Equals(theme, "Daylight", StringComparison.OrdinalIgnoreCase);
            bool isSlate = string.Equals(theme, "Slate", StringComparison.OrdinalIgnoreCase);
            bool isMidnight = !isDaylight && !isSlate;

            UpdateThemeButton(BtnThemeOled, isMidnight);
            UpdateThemeButton(BtnThemeMidnight, isSlate);
            UpdateThemeButton(BtnThemeCarbon, isDaylight);

            UpdateThemeButton(BtnSettingsThemeOled, isMidnight);
            UpdateThemeButton(BtnSettingsThemeMidnight, isSlate);
            UpdateThemeButton(BtnSettingsThemeCarbon, isDaylight);
        }

        private void UpdateThemeButton(Button? btn, bool active)
        {
            if (btn == null) return;
            if (active)
            {
                btn.Background = (Brush)FindResource("BrushAccent");
                btn.Foreground = (Brush)FindResource("BrushAccentText");
                btn.BorderBrush = (Brush)FindResource("BrushAccentLight");
                btn.FontWeight = FontWeights.Bold;
            }
            else
            {
                btn.Background = Brushes.Transparent;
                btn.Foreground = (Brush)FindResource("BrushTextMuted");
                btn.BorderBrush = Brushes.Transparent;
                btn.FontWeight = FontWeights.SemiBold;
            }
        }

        private void BtnThemeSelect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string themeTag)
            {
                _settings.SelectedTheme = themeTag;
                ApplyThemeUi(themeTag);
                SaveSettings();
            }
        }

        // Renders one clickable color swatch per accent into the Settings accent row.
        private void BuildAccentSwatches()
        {
            if (AccentSwatchRow == null) return;
            AccentSwatchRow.Children.Clear();
            foreach (var name in ThemeManager.AccentNames)
            {
                var swatchDict = new ResourceDictionary();
                ThemeManager.ApplyAccent(name, swatchDict);
                var fill = (Brush)swatchDict["BrushAccent"];

                bool active = string.Equals(name, _settings.ThemeAccent, StringComparison.OrdinalIgnoreCase);
                var dot = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Background = fill,
                    Margin = new Thickness(0, 0, 10, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    BorderThickness = new Thickness(active ? 2 : 0),
                    BorderBrush = (Brush)FindResource("BrushTextPrimary"),
                    ToolTip = name,
                    Tag = name
                };
                dot.MouseLeftButtonUp += BtnAccentSelect_Click;
                AccentSwatchRow.Children.Add(dot);
            }
        }

        private void BtnAccentSelect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string accent)
            {
                _settings.ThemeAccent = accent;
                ThemeManager.ApplyAccent(accent, Resources);
                SaveSettings();
                BuildAccentSwatches();
            }
        }

        private void ApplyIconShape(string shape)
        {
            bool isCircle = string.Equals(shape, "Circle", StringComparison.OrdinalIgnoreCase);
            Resources["ProviderLogoCornerRadius"] = new CornerRadius(isCircle ? 22 : 10);
            Resources["ProviderLogoInnerCornerRadius"] = new CornerRadius(isCircle ? 19 : 8);
            Resources["ProviderLogoSmallCornerRadius"] = new CornerRadius(isCircle ? 18 : 8);
            Resources["ProviderLogoSmallInnerCornerRadius"] = new CornerRadius(isCircle ? 15 : 6);
        }

        private void RbIconShape_Checked(object sender, RoutedEventArgs e)
        {
            if (RbIconCircle != null && RbIconCircle.IsChecked == true)
            {
                ApplyIconShape("Circle");
            }
            else
            {
                ApplyIconShape("Squircle");
            }
        }

        private void UpdateProfileCardUi()
        {
            TxtProfileName.Text = _settings.ProfileName;
            TxtProfileRole.Text = _settings.ProfileRole;
            string initials = "AX";
            if (!string.IsNullOrWhiteSpace(_settings.ProfileName))
            {
                var parts = _settings.ProfileName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    initials = $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant();
                }
                else
                {
                    initials = _settings.ProfileName.Length >= 2 ? _settings.ProfileName.Substring(0, 2).ToUpperInvariant() : _settings.ProfileName.ToUpperInvariant();
                }
            }
            TxtProfileInitials.Text = initials;
            UpdateHomeGreetingAndLabels();
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsModal();
        }

        private void OpenSettingsModal()
        {
            TxtSettingsProfileName.Text = _settings.ProfileName;
            TxtSettingsProfileRole.Text = _settings.ProfileRole;
            TxtSettingsProjectsDir.Text = _settings.DefaultProjectsDir;
            ChkHardwareAccel.IsChecked = _settings.HardwareAcceleration;
            ChkDiscordPresence.IsChecked = _settings.DiscordRichPresenceEnabled;
            TxtDiscordClientId.Text = _settings.DiscordClientId;

            // Standard API Keys
            TxtApiKeyOpenAI.Text = _settings.ApiKeys.OpenAI;
            TxtApiKeyAnthropic.Text = _settings.ApiKeys.Anthropic;
            TxtApiKeyGrok.Text = _settings.ApiKeys.Grok;
            TxtApiKeyGemini.Text = _settings.ApiKeys.Gemini;
            TxtApiKeyDeepSeek.Text = _settings.ApiKeys.DeepSeek;

            if (string.Equals(_settings.IconShape, "Circle", StringComparison.OrdinalIgnoreCase))
            {
                RbIconCircle.IsChecked = true;
            }
            else
            {
                RbIconSquircle.IsChecked = true;
            }

            SwitchSettingsTab(0);
            RefreshCustomProvidersUi();
            PopulateStoragePathsUi();
            LoadSettingsProviderLogos();

            ModalSettings.Visibility = Visibility.Visible;
            ModalSettings.Opacity = 0.0;
            SettingsModalScale.ScaleX = 0.88;
            SettingsModalScale.ScaleY = 0.88;

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220));
            var scaleAnim = new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
            };

            ModalSettings.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            SettingsModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            SettingsModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            TxtApiKeyOpenAI.Focus();
        }

        private void CloseSettingsModal()
        {
            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(160));
            var scaleAnim = new DoubleAnimation(1.0, 0.92, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            fadeOut.Completed += (s, e) =>
            {
                ModalSettings.Visibility = Visibility.Collapsed;
            };

            ModalSettings.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            SettingsModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            SettingsModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        }

        private void BtnCloseSettings_Click(object sender, RoutedEventArgs e)
        {
            CloseSettingsModal();
        }

        private void BtnBrowseSettingsFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose Default Projects Directory",
                Multiselect = false
            };
            if (dialog.ShowDialog() == true)
            {
                TxtSettingsProjectsDir.Text = dialog.FolderName;
            }
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            string newName = TxtSettingsProfileName.Text.Trim();
            string newRole = TxtSettingsProfileRole.Text.Trim();
            string newDir = TxtSettingsProjectsDir.Text.Trim();

            if (string.IsNullOrEmpty(newName))
            {
                MessageBox.Show("Profile name cannot be empty.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.ProfileName = newName;
            _settings.ProfileRole = string.IsNullOrEmpty(newRole) ? "Operator // Online" : newRole;
            if (!string.IsNullOrEmpty(newDir)) _settings.DefaultProjectsDir = newDir;
            _settings.HardwareAcceleration = ChkHardwareAccel.IsChecked ?? true;
            _settings.DiscordRichPresenceEnabled = ChkDiscordPresence.IsChecked ?? false;
            _settings.DiscordClientId = TxtDiscordClientId.Text.Trim();

            // Save Cloud API Keys
            _settings.ApiKeys.OpenAI = TxtApiKeyOpenAI.Text.Trim();
            _settings.ApiKeys.Anthropic = TxtApiKeyAnthropic.Text.Trim();
            _settings.ApiKeys.Grok = TxtApiKeyGrok.Text.Trim();
            _settings.ApiKeys.Gemini = TxtApiKeyGemini.Text.Trim();
            _settings.ApiKeys.DeepSeek = TxtApiKeyDeepSeek.Text.Trim();

            _settings.IconShape = RbIconCircle.IsChecked == true ? "Circle" : "Squircle";
            ApplyIconShape(_settings.IconShape);

            _aggregatorFetchedForKey.Clear();
            SaveSettings();
            ApplyDiscordPresence();
            UpdateProfileCardUi();
            PopulateStoragePathsUi();
            RefreshCompanyCategoriesUi();
            CloseSettingsModal();
            AppendLog("[SETTINGS] Profile card updated and cloud API credentials saved to settings.json.");
        }

        private async void BtnTestApiKeys_Click(object sender, RoutedEventArgs e)
        {
            CloseSettingsModal();
            await TestApiConnectionsAsync();
        }

        private void BtnOpenApiKeysModal_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsModal();
        }

        private void BtnNavApiKeys_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsModal();
        }

        #region Tab Navigation inside Settings
        private void TabBtnStandardApis_Click(object sender, RoutedEventArgs e)
        {
            SwitchSettingsTab(0);
        }

        private void TabBtnCustomProviders_Click(object sender, RoutedEventArgs e)
        {
            SwitchSettingsTab(1);
        }

        private void TabBtnOperatorProfile_Click(object sender, RoutedEventArgs e)
        {
            SwitchSettingsTab(2);
        }

        private void SwitchSettingsTab(int tabIndex)
        {
            var activeStyle = (Style)FindResource("SettingsTabBtnActive");
            var inactiveStyle = (Style)FindResource("SettingsTabBtn");

            TabBtnStandardApis.Style = tabIndex == 0 ? activeStyle : inactiveStyle;
            TabBtnCustomProviders.Style = tabIndex == 1 ? activeStyle : inactiveStyle;
            TabBtnOperatorProfile.Style = tabIndex == 2 ? activeStyle : inactiveStyle;

            TabContentStandardApis.Visibility = tabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            TabContentCustomProviders.Visibility = tabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            TabContentOperatorProfile.Visibility = tabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

            if (tabIndex == 1)
            {
                RefreshCustomProvidersUi();
            }
        }

        private void RefreshCustomProvidersUi()
        {
            CustomProvidersItemsControl.ItemsSource = null;
            CustomProvidersItemsControl.ItemsSource = _settings.CustomProviders;
        }
        #endregion

        #region Custom Providers Management
        private async void TxtCustomWebUrl_LostFocus(object sender, RoutedEventArgs e)
        {
            await TriggerAutoLogoFetchAsync();
        }

        private async void TxtCustomBaseUrl_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtCustomWebUrl.Text))
            {
                await TriggerAutoLogoFetchAsync();
            }
        }

        private async void BtnFetchCustomLogo_Click(object sender, RoutedEventArgs e)
        {
            await TriggerAutoLogoFetchAsync();
        }

        private async Task TriggerAutoLogoFetchAsync()
        {
            string url = TxtCustomWebUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                url = TxtCustomBaseUrl.Text.Trim();
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                TxtCustomLogoStatus.Text = "Please enter a Web URL or Base URL first";
                return;
            }

            TxtCustomLogoStatus.Text = "Fetching official logo online...";
            string localPath = await _logoService.FetchAndCacheLogoAsync(url);

            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
            {
                _pendingCustomLogoPath = localPath;
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(localPath, UriKind.Absolute);
                    bmp.EndInit();

                    ImgCustomLogoPreview.Source = bmp;
                    ImgCustomLogoPreview.Visibility = Visibility.Visible;
                    TxtCustomLogoFallback.Visibility = Visibility.Collapsed;
                    TxtCustomLogoStatus.Text = $"Logo cached: {Path.GetFileName(localPath)}";
                }
                catch
                {
                    ImgCustomLogoPreview.Visibility = Visibility.Collapsed;
                    TxtCustomLogoFallback.Visibility = Visibility.Visible;
                    TxtCustomLogoStatus.Text = "Cached (vector icon active)";
                }
            }
            else
            {
                ImgCustomLogoPreview.Visibility = Visibility.Collapsed;
                TxtCustomLogoFallback.Visibility = Visibility.Visible;
                TxtCustomLogoStatus.Text = "Using default âš¡ provider badge";
            }
        }

        private async void BtnAddCustomProvider_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtCustomProviderName.Text.Trim();
            string modelId = TxtCustomModelId.Text.Trim();
            string webUrl = TxtCustomWebUrl.Text.Trim();
            string baseUrl = TxtCustomBaseUrl.Text.Trim();
            string apiKey = TxtCustomApiKey.Text.Trim();

            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Please enter a provider name (e.g. OpenRouter, Groq, Together).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtCustomProviderName.Focus();
                return;
            }

            if (string.IsNullOrEmpty(modelId))
            {
                MessageBox.Show("Please enter a model ID / slug (e.g. anthropic/claude-3.5-sonnet, llama-3.3-70b).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtCustomModelId.Focus();
                return;
            }

            if (string.IsNullOrEmpty(baseUrl))
            {
                MessageBox.Show("Please enter the provider base URL / chat completions endpoint.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtCustomBaseUrl.Focus();
                return;
            }

            // If logo hasn't been fetched yet, fetch it online now
            if (string.IsNullOrEmpty(_pendingCustomLogoPath) || !File.Exists(_pendingCustomLogoPath))
            {
                string urlToFetch = !string.IsNullOrEmpty(webUrl) ? webUrl : baseUrl;
                _pendingCustomLogoPath = await _logoService.FetchAndCacheLogoAsync(urlToFetch);
            }

            var provider = new CustomProviderItem
            {
                Id = $"custom-{Guid.NewGuid().ToString().Substring(0, 8)}",
                Name = name,
                Model = modelId,
                WebUrl = webUrl,
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                LocalLogoPath = _pendingCustomLogoPath,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            _settings.CustomProviders.Add(provider);
            SaveSettings();
            RefreshCustomProvidersUi();

            // Register into Model Library
            _modelService.RegisterCustomProviderModel(provider, _allModels, _models);
            RefreshModelsUi();

            // Reset inputs for next provider
            TxtCustomProviderName.Text = "";
            TxtCustomModelId.Text = "";
            TxtCustomWebUrl.Text = "";
            TxtCustomBaseUrl.Text = "";
            TxtCustomApiKey.Text = "";
            _pendingCustomLogoPath = "";
            ImgCustomLogoPreview.Source = null;
            ImgCustomLogoPreview.Visibility = Visibility.Collapsed;
            TxtCustomLogoFallback.Visibility = Visibility.Visible;
            TxtCustomLogoStatus.Text = "Auto-fetches from Web URL";

            AppendLog($"[CUSTOM PROVIDER REGISTERED] {provider.Name} // Model: {provider.Model}");
            AppendLog($"[ENDPOINT] {provider.BaseUrl}");
            if (!string.IsNullOrEmpty(provider.LocalLogoPath))
            {
                AppendLog($"[LOGO] Cached online icon to: {provider.LocalLogoPath}");
            }
            MessageBox.Show($"Custom provider '{provider.Name}' ({provider.Model}) registered successfully!\r\nIt is now available in the Model Library under 'Custom Providers'.", "Provider Added", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void BtnTestCustomProvider_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CustomProviderItem cp)
            {
                CloseSettingsModal();
                await _cloudApiService.TestCustomProviderAsync(cp, _settings, AppendLog);
            }
        }

        private void BtnActivateCustomProvider_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CustomProviderItem cp)
            {
                var model = _allModels.FirstOrDefault(m => m.Id == cp.Model && m.IsCustom);
                if (model == null)
                {
                    _modelService.RegisterCustomProviderModel(cp, _allModels, _models);
                    model = _allModels.FirstOrDefault(m => m.Id == cp.Model && m.IsCustom);
                }

                if (model != null)
                {
                    CloseSettingsModal();
                    TryLoadModel(model);
                }
            }
        }

        private void BtnDeleteCustomProvider_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CustomProviderItem cp)
            {
                var confirm = ConfirmDialog("Remove provider", $"Remove custom provider '{cp.Name}' ({cp.Model})?", null, "Remove", danger: true);
                if (confirm)
                {
                    _settings.CustomProviders.Remove(cp);
                    SaveSettings();
                    RefreshCustomProvidersUi();

                    _allModels.RemoveAll(m => m.Id == cp.Model && m.IsCustom);
                    _models.RemoveAll(m => m.Id == cp.Model && m.IsCustom);
                    RefreshCompanyCategoriesUi();

                    AppendLog($"[SETTINGS] Removed custom provider: {cp.Name} ({cp.Model})");
                }
            }
        }

        private async void BtnTestSingleKey_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string providerName)
            {
                string key = "";
                string endpoint = "";
                string headerName = "";

                if (providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    key = TxtApiKeyOpenAI.Text.Trim();
                    endpoint = "https://api.openai.com/v1/models";
                }
                else if (providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
                {
                    key = TxtApiKeyAnthropic.Text.Trim();
                    endpoint = "https://api.anthropic.com/v1/messages";
                    headerName = "x-api-key";
                }
                else if (providerName.Equals("Grok", StringComparison.OrdinalIgnoreCase))
                {
                    key = TxtApiKeyGrok.Text.Trim();
                    endpoint = "https://api.x.ai/v1/models";
                }
                else if (providerName.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
                {
                    key = TxtApiKeyGemini.Text.Trim();
                    endpoint = "https://generativelanguage.googleapis.com/v1beta/models";
                }
                else if (providerName.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase))
                {
                    key = TxtApiKeyDeepSeek.Text.Trim();
                    endpoint = "https://api.deepseek.com/models";
                }

                if (string.IsNullOrWhiteSpace(key))
                {
                    MessageBox.Show($"Please enter an API key for {providerName} first.", "Missing Key", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                btn.Content = "...";
                btn.IsEnabled = false;

                var sw = Stopwatch.StartNew();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    if (headerName == "x-api-key")
                    {
                        req.Headers.Add("x-api-key", key);
                        req.Headers.Add("anthropic-version", "2023-06-01");
                    }
                    else if (providerName.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
                    {
                        req.RequestUri = new Uri($"{endpoint}?key={key}");
                    }
                    else
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    }

                    var resp = await _httpClient.SendAsync(req);
                    sw.Stop();

                    if (resp.IsSuccessStatusCode)
                    {
                        MessageBox.Show($"âœ” {providerName} API Key Verified!\r\nHTTP {(int)resp.StatusCode} OK in {sw.ElapsedMilliseconds}ms.", "Key Validated", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        MessageBox.Show($"â–² {providerName} Authentication Failed!\r\nHTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}). Please check if the key is correct or has expired.", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        MessageBox.Show($"â— {providerName} Endpoint Connected (HTTP {(int)resp.StatusCode}, {sw.ElapsedMilliseconds}ms).", "Connection Status", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    MessageBox.Show($"âœ• Connection Failed: {ex.Message}", "Network Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    btn.Content = "âš¡ Test";
                    btn.IsEnabled = true;
                }
            }
        }

        private void BtnOpenLogoCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Directory.Exists(AppConstants.CacheDir))
                {
                    Directory.CreateDirectory(AppConstants.CacheDir);
                }
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppConstants.CacheDir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open cache directory: {ex.Message}", "Explorer Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        #endregion
        #endregion

        #region Smooth Loading Animations (20-Second Cinematic Boot)
        private void StartContinuousLoadingAnimations()
        {
            var outerAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(3.5))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            OuterRingRotate.BeginAnimation(RotateTransform.AngleProperty, outerAnim);

            var midAnim = new DoubleAnimation(360, 0, TimeSpan.FromSeconds(4.5))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            MidDiamondRotate.BeginAnimation(RotateTransform.AngleProperty, midAnim);

            var breatheAnim = new DoubleAnimation(0.95, 1.07, TimeSpan.FromSeconds(1.4))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            CoreCubeScale.BeginAnimation(ScaleTransform.ScaleXProperty, breatheAnim);
            CoreCubeScale.BeginAnimation(ScaleTransform.ScaleYProperty, breatheAnim);

            var glowAnim = new DoubleAnimation(15, 35, TimeSpan.FromSeconds(1.4))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            CoreCubeGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, glowAnim);

            var scanAnim = new DoubleAnimation(-85, 85, TimeSpan.FromSeconds(2.0))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            ScanBeamTranslate.BeginAnimation(TranslateTransform.YProperty, scanAnim);
        }

        private void BtnSkipLoading_Click(object sender, RoutedEventArgs e)
        {
            _loadingCts?.Cancel();
        }

        private void UpdatePhaseChips(int currentPhase)
        {
            var chips = new[] { PhaseChip1, PhaseChip2, PhaseChip3, PhaseChip4, PhaseChip5, PhaseChip6 };
            var texts = new[] { TxtPhase1, TxtPhase2, TxtPhase3, TxtPhase4, TxtPhase5, TxtPhase6 };
            var names = new[] { "KERNEL", "TLS 1.3", "GATEWAYS", "MODELS", "VAULT", "READY" };

            for (int i = 0; i < 6; i++)
            {
                int phaseNum = i + 1;
                if (chips[i] == null || texts[i] == null) continue;

                if (phaseNum < currentPhase)
                {
                    chips[i].Background = new SolidColorBrush(ThemeColor("BrushSuccessGlow", "#064e3b"));
                    chips[i].BorderBrush = new SolidColorBrush(ThemeColor("BrushSuccess", "#22c55e"));
                    texts[i].Foreground = new SolidColorBrush(ThemeColor("BrushSuccess", "#22c55e"));
                    texts[i].Text = $"âœ” 0{phaseNum} {names[i]}";
                }
                else if (phaseNum == currentPhase)
                {
                    chips[i].Background = new SolidColorBrush(ThemeColor("BrushAccentMuted", "#1e293b"));
                    chips[i].BorderBrush = new SolidColorBrush(ThemeColor("BrushAccent", "#00f2fe"));
                    texts[i].Foreground = new SolidColorBrush(ThemeColor("BrushAccent", "#00f2fe"));
                    texts[i].Text = $"â— 0{phaseNum} {names[i]}";
                }
                else
                {
                    chips[i].Background = new SolidColorBrush(ThemeColor("BrushCardInnerBg", "#141420"));
                    chips[i].BorderBrush = new SolidColorBrush(ThemeColor("BrushBorder", "#252538"));
                    texts[i].Foreground = new SolidColorBrush(ThemeColor("BrushTextMuted", "#71717a"));
                    texts[i].Text = $"0{phaseNum} {names[i]}";
                }
            }
        }

        private async Task RunSmoothLoadingSequenceAsync()
        {
            _loadingCts = new System.Threading.CancellationTokenSource();
            var token = _loadingCts.Token;

            var telemetrySchedule = new[]
            {
                new { Time = 0.0, Phase = 1, Status = "Initializing BlackBox quantum hypervisor...", Telemetry = "[00.0s] [SYS] Quantum boot protocol initiated." },
                new { Time = 0.5, Phase = 1, Status = "Mapping Direct3D 11 hardware swapchain...", Telemetry = "[00.5s] [KERNEL] BlackBox micro-hypervisor online. D3D11 device mapped." },
                new { Time = 1.0, Phase = 1, Status = "Verifying AVX2 / SSE4.2 SIMD vector pipelines...", Telemetry = "[01.0s] [KERNEL] CPU SIMD vector pipelines mapped. Zero page-fault allocators active." },
                new { Time = 1.5, Phase = 2, Status = "Negotiating TLS 1.3 cryptographic cipher suites...", Telemetry = "[01.5s] [CRYPTO] Initializing TLS 1.3 cipher suites (ECDHE-ECDSA-AES256-GCM-SHA384)." },
                new { Time = 2.1, Phase = 2, Status = "Verifying certificate pinning & entropy pools...", Telemetry = "[02.1s] [CRYPTO] Hardware entropy verified. Certificate pinning active." },
                new { Time = 2.8, Phase = 2, Status = "Unlocking AES-256 local credentials vault...", Telemetry = "[02.8s] [VAULT] Local settings.json decrypted and memory-pinned." },
                new { Time = 3.3, Phase = 3, Status = "Probing OpenAI REST gateway (api.openai.com)...", Telemetry = "[03.3s] [GATEWAY] Probing OpenAI REST gateway (api.openai.com)..." },
                new { Time = 3.9, Phase = 3, Status = "Probing Anthropic Claude 3.5 gateway (api.anthropic.com)...", Telemetry = "[03.9s] [GATEWAY] Probing Anthropic Claude 3.5 gateway (api.anthropic.com)..." },
                new { Time = 4.5, Phase = 3, Status = "Probing Google Gemini & xAI Grok endpoints...", Telemetry = "[04.5s] [GATEWAY] Probing Google Gemini & xAI Grok endpoints..." },
                new { Time = 5.1, Phase = 3, Status = "Probing DeepSeek MoE & Groq ultra-fast LPU inference...", Telemetry = "[05.1s] [GATEWAY] Probing DeepSeek MoE & Groq ultra-fast LPU inference..." },
                new { Time = 5.7, Phase = 3, Status = "Probing OpenRouter, Cloudflare, Mistral, HuggingFace...", Telemetry = "[05.7s] [GATEWAY] Probing OpenRouter, Cloudflare, Mistral, HuggingFace gateways..." },
                new { Time = 6.3, Phase = 4, Status = "Cataloging company model registries & architectures...", Telemetry = "[06.3s] [MODELS] Indexing 14+ company registries & foundation architectures." },
                new { Time = 6.9, Phase = 4, Status = "Parsing parameter scales: 671B MoE, 405B, 70B, 8B...", Telemetry = "[06.9s] [MODELS] Parsing parameter scales: 671B MoE, 405B, 70B, 8B, 3B quantized." },
                new { Time = 7.5, Phase = 4, Status = "Validating direct REST payloads & token streams...", Telemetry = "[07.5s] [MODELS] Direct API schemas validated for 40+ models." },
                new { Time = 8.0, Phase = 5, Status = "Mounting isolated VFS project sandboxes...", Telemetry = "[08.0s] [VFS] Local sandbox filesystem mounted at isolated VFS." },
                new { Time = 8.8, Phase = 5, Status = "Hardening zero-trust runtime memory boundaries...", Telemetry = "[08.8s] [SECURITY] Zero-trust memory sandbox verified. System hardened." },
                new { Time = 9.3, Phase = 5, Status = "Configuring outbound HTTPS firewall policies...", Telemetry = "[09.3s] [SECURITY] Network firewall rules active for direct outbound API calls." },
                new { Time = 9.6, Phase = 6, Status = "Finalizing quantum core initialization...", Telemetry = "[09.6s] [CORE] All diagnostics passed. 0 warnings, 0 faults." },
                new { Time = 10.0, Phase = 6, Status = "BlackBox core operational. Welcome, Operator.", Telemetry = "[10.0s] [SYS] BlackBox workspace active." }
            };

            var emittedIndices = new HashSet<int>();
            var sw = Stopwatch.StartNew();

            UpdatePhaseChips(1);

            try
            {
                while (sw.Elapsed.TotalSeconds < 10.0 && !token.IsCancellationRequested)
                {
                    double elapsed = sw.Elapsed.TotalSeconds;
                    double pct = Math.Min(100.0, (elapsed / 10.0) * 100.0);
                    LoadingProgressBar.Value = pct;

                    int min = sw.Elapsed.Minutes;
                    int sec = sw.Elapsed.Seconds;
                    int tenths = sw.Elapsed.Milliseconds / 100;
                    TxtLoadingTimer.Text = $"{min:D2}:{sec:D2}.{tenths:D1} / 00:10.0 ({(int)pct}%)";

                    for (int i = 0; i < telemetrySchedule.Length; i++)
                    {
                        if (!emittedIndices.Contains(i) && elapsed >= telemetrySchedule[i].Time)
                        {
                            emittedIndices.Add(i);
                            TxtTelemetryFeed.Text += $"{Environment.NewLine}{telemetrySchedule[i].Telemetry}";
                            TxtLoadingStatus.Text = telemetrySchedule[i].Status;
                            UpdatePhaseChips(telemetrySchedule[i].Phase);
                            LoadingTelemetryScroll.ScrollToEnd();
                        }
                    }

                    await Task.Delay(40);
                }

                if (token.IsCancellationRequested)
                {
                    TxtTelemetryFeed.Text += $"{Environment.NewLine}[00:10.0] [FAST-BOOT] Diagnostic sequence fast-forwarded by operator.";
                    LoadingTelemetryScroll.ScrollToEnd();
                }

                LoadingProgressBar.Value = 100;
                TxtLoadingTimer.Text = "00:10.0 / 00:10.0 (100%)";
                UpdatePhaseChips(6);
                TxtLoadingStatus.Text = "BlackBox core operational. Welcome, Operator.";
                await Task.Delay(250);

                var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(420))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                };
                var scaleOut = new DoubleAnimation(1.0, 1.09, TimeSpan.FromMilliseconds(420))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                };

                fadeOut.Completed += (s, ev) =>
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                };

                LoadingOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                LoadingOverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleOut);
                LoadingOverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleOut);

                ViewHomepage.Opacity = 0.0;
                HomepageTranslate.Y = 24.0;
                var hpFadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(450))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var hpSlideIn = new DoubleAnimation(24.0, 0.0, TimeSpan.FromMilliseconds(450))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                ViewHomepage.BeginAnimation(UIElement.OpacityProperty, hpFadeIn);
                HomepageTranslate.BeginAnimation(TranslateTransform.YProperty, hpSlideIn);
            }
            catch
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }
        #endregion

        #region Project Registry Management
        private void LoadProjects()
        {
            _projects = _projectService.LoadProjects();

            if (_projects.Count == 0)
            {
                _projects = new List<ProjectItem>
                {
                    _projectService.CreateProject("DeepSeek-Local-Engine", Path.Combine(_settings.DefaultProjectsDir, "DeepSeek-Local-Engine"), "Local LLM Inference & Fine-tuning Sandbox"),
                    _projectService.CreateProject("Llama3-Vision-Sandbox", Path.Combine(_settings.DefaultProjectsDir, "Llama3-Vision-Sandbox"), "Multimodal Vision & Audio Model Workspace")
                };
                _projectService.SaveProjects(_projects);
            }

            RefreshProjectsUi();
        }

        private void SaveProjects()
        {
            if (!_projectService.SaveProjects(_projects))
            {
                MessageBox.Show("Error saving projects to projects.json.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RefreshProjectsUi()
        {
            foreach (var p in _projects)
            {
                p.ConversationCount = GetConversationCountForProject(p);
            }
            ProjectsItemsControl.ItemsSource = null;
            ProjectsItemsControl.ItemsSource = _projects;
            TxtProjectCountBadge.Text = _projects.Count == 1 ? "1 Project" : $"{_projects.Count} Projects";
        }
        #endregion

        #region Navigation & Smooth Modals
        private void CardNewProject_Click(object sender, MouseButtonEventArgs e)
        {
            OpenNewProjectModal();
        }

        private void BtnOpenNewProjectModal_Click(object sender, RoutedEventArgs e)
        {
            OpenNewProjectModal();
        }

        private void OpenNewProjectModal()
        {
            string suggestedName = _projectService.GetRandomSuggestedName(_projects);
            TxtNewProjectName.Text = suggestedName;
            TxtNewProjectPath.Text = Path.Combine(_settings.DefaultProjectsDir, suggestedName);
            TxtNewProjectDesc.Text = "Custom LLM & Neural Pipeline Sandbox";
            
            ModalNewProject.Visibility = Visibility.Visible;
            ModalNewProject.Opacity = 0.0;
            ModalScale.ScaleX = 0.88;
            ModalScale.ScaleY = 0.88;

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220));
            var scaleAnim = new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
            };

            ModalNewProject.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            ModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            ModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            TxtNewProjectName.Focus();
            TxtNewProjectName.SelectAll();
        }

        private void CloseNewProjectModal()
        {
            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(160));
            var scaleAnim = new DoubleAnimation(1.0, 0.92, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            fadeOut.Completed += (s, e) =>
            {
                ModalNewProject.Visibility = Visibility.Collapsed;
            };

            ModalNewProject.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            ModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            ModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        }

        private void BtnGenerateSuggestion_Click(object sender, RoutedEventArgs e)
        {
            string suggestedName = _projectService.GetRandomSuggestedName(_projects);
            TxtNewProjectName.Text = suggestedName;
            TxtNewProjectPath.Text = Path.Combine(_settings.DefaultProjectsDir, suggestedName);
        }

        // Ask the currently loaded cloud model for fresh project ideas and render them as pickable cards.
        private async void BtnAiIdea_Click(object sender, RoutedEventArgs e)
        {
            if (_activeModel == null)
            {
                MessageBox.Show(
                    "Load a model first: open Cloud Model APIs, enter a key for a provider, and Load & Chat a model. Then generate ideas here.",
                    "No Active Model", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (BtnAiIdea != null) BtnAiIdea.IsEnabled = false;
            IdeasPanel.Visibility = Visibility.Visible;
            IdeasList.ItemsSource = null;
            TxtIdeasStatus.Text = $"Asking {_activeModel.Name}...";

            try
            {
                const string prompt =
                    "You are a software project idea generator. Return ONLY a compact JSON array of 5 original, " +
                    "buildable software project ideas. No prose, no markdown code fences. Each array element must be an " +
                    "object with exactly these keys: \"name\" (2-4 word project title), \"description\" (one concise sentence), " +
                    "\"stack\" (suggested tech stack in a few words). Output JSON array only.";

                string response = await _cloudApiService.CallCloudApiAsync(_activeModel, prompt, _settings);
                var ideas = ParseProjectIdeas(response);
                if (ideas.Count == 0)
                {
                    TxtIdeasStatus.Text = "Model did not return usable ideas — try again.";
                    return;
                }

                IdeasList.ItemsSource = ideas;
                TxtIdeasStatus.Text = $"{ideas.Count} ideas from {_activeModel.Name}";
            }
            catch (Exception ex)
            {
                TxtIdeasStatus.Text = $"Failed: {ex.Message}";
            }
            finally
            {
                if (BtnAiIdea != null) BtnAiIdea.IsEnabled = true;
            }
        }

        private List<ProjectIdea> ParseProjectIdeas(string response)
        {
            var list = new List<ProjectIdea>();
            if (string.IsNullOrWhiteSpace(response)) return list;

            // Tolerate models that wrap the array in prose or ```json fences by slicing to the array.
            int start = response.IndexOf('[');
            int end = response.LastIndexOf(']');
            if (start < 0 || end <= start) return list;
            string json = response.Substring(start, end - start + 1);

            try
            {
                var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<ProjectIdea>>(json, opts);
                if (parsed != null)
                {
                    foreach (var p in parsed)
                    {
                        if (p != null && !string.IsNullOrWhiteSpace(p.Name)) list.Add(p);
                    }
                }
            }
            catch { /* malformed JSON from the model — leave list empty, caller shows a retry hint */ }

            return list;
        }

        private void BtnUseIdea_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ProjectIdea idea)
            {
                string safeName = idea.Name.Trim();
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    safeName = safeName.Replace(c, ' ');
                }
                safeName = safeName.Trim();
                if (safeName.Length == 0) safeName = "AI-Project";

                TxtNewProjectName.Text = safeName;
                TxtNewProjectPath.Text = Path.Combine(_settings.DefaultProjectsDir, safeName);
                if (!string.IsNullOrWhiteSpace(idea.Description))
                {
                    TxtNewProjectDesc.Text = idea.Description;
                }
            }
        }

        private void BtnBrowseCustomFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose Project Directory",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                TxtNewProjectPath.Text = dialog.FolderName;
                if (string.IsNullOrWhiteSpace(TxtNewProjectName.Text) || AppConstants.SuggestedProjectNames.Any(n => TxtNewProjectName.Text.StartsWith(n)))
                {
                    string folderName = Path.GetFileName(dialog.FolderName);
                    if (!string.IsNullOrEmpty(folderName))
                    {
                        TxtNewProjectName.Text = folderName;
                    }
                }
            }
        }

        private void BtnCancelNewProject_Click(object sender, RoutedEventArgs e)
        {
            CloseNewProjectModal();
        }

        private void BtnCreateProjectConfirm_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtNewProjectName.Text.Trim();
            string path = TxtNewProjectPath.Text.Trim();
            string desc = TxtNewProjectDesc.Text.Trim();

            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Please enter a project name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show("Please specify a project folder path.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var project = _projectService.CreateProject(name, path, string.IsNullOrEmpty(desc) ? "BlackBox Model Workspace" : desc);
                _projects.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
                _projects.Insert(0, project);
                SaveProjects();
                RefreshProjectsUi();

                CloseNewProjectModal();
                OpenProjectWorkspace(project);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize project: {ex.Message}", "Creation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CardBrowseFolder_Click(object sender, MouseButtonEventArgs e)
        {
            BrowseExistingFolder();
        }

        private void BtnBrowseExistingFolder_Click(object sender, RoutedEventArgs e)
        {
            BrowseExistingFolder();
        }

        private void BrowseExistingFolder()
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Open Existing Project Folder",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                string path = dialog.FolderName;
                string name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name)) name = "Project-" + DateTime.Now.ToString("yyyyMMdd");

                var existing = _projects.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.LastOpened = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    OpenProjectWorkspace(existing);
                }
                else
                {
                    var newProj = new ProjectItem
                    {
                        Name = name,
                        Path = path,
                        Description = "Imported Local Project Directory",
                        CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                        LastOpened = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                    };
                    _projects.Insert(0, newProj);
                    SaveProjects();
                    RefreshProjectsUi();
                    OpenProjectWorkspace(newProj);
                }
            }
        }

        private void BtnOpenProjectCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ProjectItem project)
            {
                OpenProjectWorkspace(project);
            }
        }

        #region 3-Column IDE Workspace Implementation
        private void OpenProjectWorkspace(ProjectItem project)
        {
            _activeProject = project;
            project.LastOpened = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            SaveProjects();

            if (_ideWindows.TryGetValue(project.Path, out var existing))
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }

            var workspace = new LlmodelBox.Controls.WorkspaceControl(_cloudApiService, _tokenUsageService, _settings, _httpClient,
                () => _activeModel, () => _allModels, m => SetActiveModel(m), _aiRuntime, _toolRegistry);

            var win = new IdeWindow(project, workspace, _settings.SelectedTheme, _settings.ThemeAccent) { Owner = this };
            workspace.BackRequested += () => win.Close();
            workspace.SwitchModelRequested += () =>
            {
                Activate();
                ShowView(ViewModelLibrary, ModelLibraryTranslate);
            };

            _ideWindows[project.Path] = win;
            win.Closed += (_, __) => _ideWindows.Remove(project.Path);
            win.Show();
            win.Activate();
        }
        #endregion

        private void BtnBackToProjects_Click(object sender, RoutedEventArgs e)
        {
            if (ViewChatHome != null) ViewChatHome.Visibility = Visibility.Collapsed;
            ViewProjectWorkspace.Visibility = Visibility.Collapsed;
            ViewModelLibrary.Visibility = Visibility.Collapsed;
            ViewHomepage.Visibility = Visibility.Visible;

            ViewHomepage.Opacity = 0.0;
            HomepageTranslate.Y = 20.0;
            var hpFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var hpSlide = new DoubleAnimation(20.0, 0.0, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            ViewHomepage.BeginAnimation(UIElement.OpacityProperty, hpFade);
            HomepageTranslate.BeginAnimation(TranslateTransform.YProperty, hpSlide);

            RefreshProjectsUi();
        }

        private void BtnOpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ProjectItem project)
            {
                OpenPathInExplorer(project.Path);
            }
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
                Owner = this,
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
                Text = danger ? "âš " : "â„¹",
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

        private void BtnRemoveProject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ProjectItem project)
            {
                var result = ConfirmDialog("Remove project", $"Remove '{project.Name}' from the project homepage?", "This only removes it from the list — files on disk are kept.", "Remove", danger: true);
                if (result)
                {
                    _projects.Remove(project);
                    SaveProjects();
                    RefreshProjectsUi();
                }
            }
        }
        #endregion

        #region Model Library and Sidebar Navigation
        private void LoadModels()
        {
            _models = _modelService.LoadModels();
            _allModels = new List<ModelItem>(_models);

            // Register custom providers configured in settings
            if (_settings.CustomProviders != null)
            {
                foreach (var cp in _settings.CustomProviders)
                {
                    _modelService.RegisterCustomProviderModel(cp, _allModels, _models);
                }
            }

            if (!string.IsNullOrEmpty(_settings.ActiveModelId))
            {
                _activeModel = _allModels.FirstOrDefault(m => string.Equals(m.Id, _settings.ActiveModelId, StringComparison.OrdinalIgnoreCase));
            }
            if (_activeModel == null && _allModels.Count > 0)
            {
                _activeModel = _allModels[0];
            }

            UpdateActiveModelUi();
            RefreshCompanyCategoriesUi();
        }

        private void RefreshCompanyCategoriesUi()
        {
            _allCompanyCategories = _modelService.GetCompanyCategories(_allModels, _settings, _cloudApiService);
            InjectAggregatorModels();
            string search = TxtSearchModels?.Text ?? "";
            _companyCategories = _modelService.FilterCompanyCategories(_allCompanyCategories, _currentCompanyFilter, search);

            if (CompanyCategoriesControl != null)
            {
                CompanyCategoriesControl.ItemsSource = null;
                CompanyCategoriesControl.ItemsSource = _companyCategories;
            }

            int totalModels = _allCompanyCategories.Sum(c => c.Models?.Count ?? 0);
            if (TxtModelCountBadge != null)
            {
                TxtModelCountBadge.Text = $"{totalModels}";
            }
        }

        // Populate aggregator categories (OpenRouter / Hugging Face) with their live catalog. Uses the
        // Populate company categories with their live model catalog from their /models endpoint.
        // Uses the cache when available; otherwise kicks off a one-time async fetch per key.
        // For OpenRouter, fetches public catalog even if key is not yet entered so all 450+ models are visible.
        private void InjectAggregatorModels()
        {
            foreach (var cat in _allCompanyCategories)
            {
                if (Array.IndexOf(_aggregatorProviderIds, cat.Id) < 0) continue;

                if (_aggregatorModels.TryGetValue(cat.Id, out var cached) && cached.Count > 0)
                {
                    foreach (var m in cached) m.HasApiKey = cat.HasApiKey;

                    // The model list is virtualized in XAML, so the full catalog renders lazily without freezing.
                    string search = TxtSearchModels?.Text?.Trim() ?? "";
                    IEnumerable<ModelItem> pool = cached;
                    if (search.Length > 0)
                    {
                        pool = cached.Where(m =>
                            m.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            m.Id.Contains(search, StringComparison.OrdinalIgnoreCase));
                    }

                    var display = pool.ToList();
                    cat.Models = display;
                    cat.Tagline = search.Length > 0
                        ? $"{display.Count} of {cached.Count} models match â€œ{search}â€"
                        : $"{cached.Count} live models available";
                    continue;
                }

                // If OpenRouter, fetch even without key (public endpoint!). For others, fetch once key is set.
                bool canFetch = (cat.Id == "openrouter") || cat.HasApiKey;
                if (!canFetch || _aggregatorFetching.Contains(cat.Id)) continue;

                string key = _cloudApiService.GetApiKeyForProvider(cat.Name, _settings);
                int keyFingerprint = string.IsNullOrEmpty(key) ? 1 : key.GetHashCode();
                if (_aggregatorFetchedForKey.TryGetValue(cat.Id, out int fetchedFor) && fetchedFor == keyFingerprint)
                {
                    continue; // already attempted for this exact key (success or failure)
                }

                _aggregatorFetching.Add(cat.Id);
                FetchAggregatorModelsAsync(cat.Id, cat.Name, key, keyFingerprint);
            }
        }

        private async void FetchAggregatorModelsAsync(string providerId, string providerName, string apiKey, int keyFingerprint)
        {
            try
            {
                AppendLog($"[MODELS] Fetching live model catalog from {providerName} endpoint...");
                var models = await _cloudApiService.FetchProviderModelsAsync(providerId, providerName, apiKey);
                if (models != null && models.Count > 0)
                {
                    _aggregatorModels[providerId] = models;
                    _allModels.RemoveAll(m => !m.IsCustom && (m.Provider.Equals(providerName, StringComparison.OrdinalIgnoreCase) || m.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase)));
                    _allModels.AddRange(models);
                    _modelService.SaveModels(_allModels);
                    AppendLog($"[MODELS] {providerName}: {models.Count} live models loaded and synchronized.");
                }
                else
                {
                    AppendLog($"[MODELS] {providerName}: provider returned no models.");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[MODELS] {providerName} catalog fetch failed: {ex.Message}");
            }
            finally
            {
                _aggregatorFetchedForKey[providerId] = keyFingerprint;
                _aggregatorFetching.Remove(providerId);
                RefreshCompanyCategoriesUi();
            }
        }

        private void RefreshModelsUi()
        {
            RefreshCompanyCategoriesUi();
        }

        private void TxtSearchModels_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshCompanyCategoriesUi();
        }

        private void SetActiveModel(ModelItem model)
        {
            _activeModel = model;
            _settings.ActiveModelId = model.Id;
            SaveSettings();
            UpdateActiveModelUi();
            RefreshLiveData();
        }

        private void UpdateActiveModelUi()
        {
            if (_activeModel == null) return;
            TxtActiveGatewayModel.Text = $"Active: {_activeModel.Name} ({_activeModel.Provider})";
            foreach (var ws in OpenWorkspaces()) ws.UpdateActiveModelBadge(_activeModel);
            TxtHeaderInfo.Text = $"{_activeModel.Name} · {_activeModel.Provider}";
            UpdateHomeGreetingAndLabels();
        }

        private void BtnFilterModels_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                _currentCompanyFilter = tag;
                RefreshCompanyCategoriesUi();
            }
        }

        private void OnUsageRecorded(Models.TokenUsageRecord record)
        {
            // Event may arrive on a threadpool thread; marshal any UI refresh to the dispatcher.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewTokenUsage != null && ViewTokenUsage.Visibility == Visibility.Visible)
                {
                    RenderTokenUsageDashboard();
                }
                foreach (var ws in OpenWorkspaces()) ws.RefreshTelemetry(_activeModel);
                RefreshLiveData();
            }));
        }

        private void BtnNavTokenAnalysis_Click(object sender, RoutedEventArgs e)
        {
            ShowView(ViewTokenUsage, TokenUsageTranslate);
            RenderTokenUsageDashboard();
        }

        private void BtnNavNeuralNet_Click(object sender, RoutedEventArgs e)
        {
            ShowView(ViewNeuralNet, NeuralNetTranslate);
            NeuralNetView?.ReplayRecent();
        }

        private void RefreshLiveData()
        {
        }

        private static string FormatTokenCount(long n)
        {
            if (n >= 1_000_000_000) return $"{n / 1_000_000_000.0:0.##}B";
            if (n >= 1_000_000) return $"{n / 1_000_000.0:0.##}M";
            if (n >= 1_000) return $"{n / 1_000.0:0.##}K";
            return n.ToString("N0");
        }

        private void RenderTokenUsageDashboard()
        {
            if (UsageKpiPanel == null) return;
            var s = _tokenUsageService.GetSummary();

            UsageEmptyState.Visibility = s.HasData ? Visibility.Collapsed : Visibility.Visible;
            UsageContentPanel.Visibility = s.HasData ? Visibility.Visible : Visibility.Collapsed;
            if (!s.HasData) return;

            UsageKpiTotal.Text = FormatTokenCount(s.TotalTokens);
            UsageKpiCalls.Text = s.CallCount.ToString("N0");
            UsageKpiPrompt.Text = FormatTokenCount(s.PromptTokens);
            UsageKpiCompletion.Text = FormatTokenCount(s.CompletionTokens);
            UsageKpiLatency.Text = s.AvgLatencyMs > 0 ? FormatLatency(s.AvgLatencyMs) : "—";
            UsageKpiThroughput.Text = s.AvgTokensPerSecond > 0 ? $"{s.AvgTokensPerSecond:0.#} tok/s" : "—";

            var accent = ThemeColor("BrushAccent", "#3f6fd1");
            var accentBright = ThemeColor("BrushAccentLight", "#7fa8f0");
            var track = ThemeColor("BrushCardInnerBg", "#14141c");
            var muted = ThemeColor("BrushTextMuted", "#8b93a7");
            var value = ThemeColor("BrushTextSecondary", "#c9ced9");
            var good = ThemeColor("BrushSuccess", "#5fd08a");
            var warn = ThemeColor("BrushWarning", "#e0b354");

            // ----- By provider (proportional bars) -----
            UsageProviderBars.Children.Clear();
            long provMax = s.ByProvider.Count > 0 ? s.ByProvider.Max(b => b.Tokens) : 0;
            foreach (var b in s.ByProvider)
            {
                UsageProviderBars.Children.Add(BuildUsageBarRow(b.Label, b.Tokens, b.Calls, provMax, accent, track, muted, value));
            }

            // ----- Top models -----
            UsageModelBars.Children.Clear();
            long modelMax = s.TopModels.Count > 0 ? s.TopModels.Max(b => b.Tokens) : 0;
            foreach (var b in s.TopModels)
            {
                UsageModelBars.Children.Add(BuildUsageBarRow(b.Label, b.Tokens, b.Calls, modelMax, accentBright, track, muted, value));
            }

            // ----- 14-day activity (vertical columns) -----
            UsageDayBars.Children.Clear();
            long dayMax = s.ByDay.Count > 0 ? s.ByDay.Max(d => d.Tokens) : 0;
            foreach (var d in s.ByDay)
            {
                double frac = dayMax > 0 ? (double)d.Tokens / dayMax : 0;
                var col = new StackPanel { Width = 26, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 2, 0) };
                var barHost = new Grid { Height = 120, VerticalAlignment = VerticalAlignment.Bottom };
                var bar = new Border
                {
                    Height = Math.Max(2, frac * 116),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(3, 3, 0, 0),
                    Background = new SolidColorBrush(d.Tokens > 0 ? accent : track),
                    ToolTip = $"{d.Day:MMM d}: {d.Tokens:N0} tokens"
                };
                barHost.Children.Add(bar);
                col.Children.Add(barHost);
                col.Children.Add(new TextBlock
                {
                    Text = d.Day.ToString("d/M"),
                    FontSize = 8,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(muted),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0)
                });
                UsageDayBars.Children.Add(col);
            }

            // ----- Per-model performance (latency + throughput graph) -----
            UsagePerfBars.Children.Clear();
            double perfLatMax = s.ModelPerf.Count > 0 ? s.ModelPerf.Max(m => m.AvgLatencyMs) : 0;
            double perfTpsMax = s.ModelPerf.Count > 0 ? s.ModelPerf.Max(m => m.AvgTokensPerSecond) : 0;
            if (s.ModelPerf.All(m => m.AvgLatencyMs <= 0))
            {
                UsagePerfBars.Children.Add(new TextBlock
                {
                    Text = "No timing captured yet — send a chat and latency/throughput will appear here.",
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(muted),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            else
            {
                foreach (var m in s.ModelPerf)
                {
                    UsagePerfBars.Children.Add(BuildPerfRow(m, perfLatMax, perfTpsMax, warn, good, track, muted, value));
                }
            }

            RenderUsageInsights(s, accent, accentBright, good, warn, track, muted, value);
        }

        // Builds the "Advanced Insights" key/value rows and the hour-of-day activity columns.
        private void RenderUsageInsights(TokenUsageSummary s, Color accent, Color accentBright, Color good, Color warn, Color track, Color muted, Color value)
        {
            if (UsageInsightsPanel == null) return;
            UsageInsightsPanel.Children.Clear();

            void Row(string label, string val)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 9) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = new SolidColorBrush(muted), VerticalAlignment = VerticalAlignment.Center });
                var v = new TextBlock { Text = val, FontSize = 12.5, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(value), HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(v, 1);
                g.Children.Add(v);
                UsageInsightsPanel.Children.Add(g);
            }

            Row("Avg tokens / call", s.AvgTokensPerCall > 0 ? $"{s.AvgTokensPerCall:N0}" : "—");
            Row("Prompt / completion split", s.TotalTokens > 0 ? $"{s.PromptShare * 100:0}% / {s.CompletionShare * 100:0}%" : "—");
            Row("Live vs estimated calls", $"{s.LiveCallCount} live / {s.EstimatedCallCount} est.");
            Row("Peak throughput", s.PeakTokensPerSecond > 0 ? $"{s.PeakTokensPerSecond:0.#} tok/s" : "—");
            Row("Tokens today", FormatTokenCount(s.TokensToday));
            Row("Tokens (7 days)", FormatTokenCount(s.Tokens7Days));
            Row("Busiest day", s.PeakDay.HasValue ? $"{s.PeakDay:MMM d} · {FormatTokenCount(s.PeakDayTokens)}" : "—");
            Row("Most-used model", string.IsNullOrEmpty(s.TopModelLabel) ? "—" : s.TopModelLabel);

            // Prompt/completion proportion bar
            if (s.TotalTokens > 0)
            {
                var barHost = new Grid { Height = 10, Margin = new Thickness(0, 4, 0, 0), ClipToBounds = true };
                barHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(s.PromptShare, GridUnitType.Star) });
                barHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, s.CompletionShare), GridUnitType.Star) });
                var pb = new Border { Background = new SolidColorBrush(accent), CornerRadius = new CornerRadius(4, 0, 0, 4), ToolTip = $"Prompt {s.PromptTokens:N0}" };
                var cb = new Border { Background = new SolidColorBrush(good), CornerRadius = new CornerRadius(0, 4, 4, 0), ToolTip = $"Completion {s.CompletionTokens:N0}" };
                Grid.SetColumn(cb, 1);
                barHost.Children.Add(pb);
                barHost.Children.Add(cb);
                UsageInsightsPanel.Children.Add(barHost);
            }

            // Hour-of-day columns
            if (UsageHourBars != null)
            {
                UsageHourBars.Children.Clear();
                long hourMax = s.ByHour.Count > 0 ? s.ByHour.Max(h => h.Tokens) : 0;
                foreach (var h in s.ByHour)
                {
                    double frac = hourMax > 0 ? (double)h.Tokens / hourMax : 0;
                    var col = new StackPanel { Width = 15, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 0) };
                    var host = new Grid { Height = 90, VerticalAlignment = VerticalAlignment.Bottom };
                    host.Children.Add(new Border
                    {
                        Height = Math.Max(2, frac * 86),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        CornerRadius = new CornerRadius(2, 2, 0, 0),
                        Background = new SolidColorBrush(h.Tokens > 0 ? accentBright : track),
                        ToolTip = $"{h.Hour:00}:00 UTC — {h.Tokens:N0} tokens, {h.Calls} calls"
                    });
                    col.Children.Add(host);
                    col.Children.Add(new TextBlock
                    {
                        Text = h.Hour % 6 == 0 ? h.Hour.ToString("00") : "",
                        FontSize = 8,
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = new SolidColorBrush(muted),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 3, 0, 0)
                    });
                    UsageHourBars.Children.Add(col);
                }
            }
        }

        // Formats a latency in milliseconds as ms under 1s, else seconds.
        private static string FormatLatency(double ms)
        {
            if (ms <= 0) return "—";
            return ms >= 1000 ? $"{ms / 1000.0:0.00}s" : $"{ms:0}ms";
        }

        // Builds one per-model performance row: model name, call count, an amber latency bar
        // (shorter = faster) and a green throughput bar (longer = faster tokens/sec).
        private FrameworkElement BuildPerfRow(ModelPerfBucket m, double latMax, double tpsMax, Color latColor, Color tpsColor, Color track, Color muted, Color value)
        {
            var root = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = m.Label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(value), TextTrimming = TextTrimming.CharacterEllipsis });
            var meta = new TextBlock { Text = $"{m.Calls} call{(m.Calls == 1 ? "" : "s")}", FontSize = 10.5, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(muted), HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(meta, 1);
            header.Children.Add(meta);
            root.Children.Add(header);

            root.Children.Add(BuildMetricBar("latency", FormatLatency(m.AvgLatencyMs), latMax > 0 ? m.AvgLatencyMs / latMax : 0, latColor, track, muted));
            root.Children.Add(BuildMetricBar("throughput", m.AvgTokensPerSecond > 0 ? $"{m.AvgTokensPerSecond:0.#} tok/s" : "—", tpsMax > 0 ? m.AvgTokensPerSecond / tpsMax : 0, tpsColor, track, muted));
            return root;
        }

        // A single small labeled metric bar used inside a performance row.
        private FrameworkElement BuildMetricBar(string caption, string valueText, double frac, Color fill, Color track, Color muted)
        {
            frac = Math.Max(0, Math.Min(1, frac));
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });

            row.Children.Add(new TextBlock { Text = caption, FontSize = 9.5, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(muted), VerticalAlignment = VerticalAlignment.Center });

            var trackBar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(track), VerticalAlignment = VerticalAlignment.Center };
            var fillBar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(fill), HorizontalAlignment = HorizontalAlignment.Left };
            trackBar.Loaded += (s, e) => { if (trackBar.ActualWidth > 0) fillBar.Width = Math.Max(3, trackBar.ActualWidth * frac); };
            trackBar.SizeChanged += (s, e) => { if (trackBar.ActualWidth > 0) fillBar.Width = Math.Max(3, trackBar.ActualWidth * frac); };
            var overlay = new Grid();
            overlay.Children.Add(trackBar);
            overlay.Children.Add(fillBar);
            Grid.SetColumn(overlay, 1);
            row.Children.Add(overlay);

            var val = new TextBlock { Text = valueText, FontSize = 10, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(muted), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(val, 2);
            row.Children.Add(val);
            return row;
        }

        // Builds one labeled horizontal usage bar (label • token count • proportional fill).
        private FrameworkElement BuildUsageBarRow(string label, long tokens, int calls, long max, Color fill, Color track, Color muted, Color value)
        {
            double frac = max > 0 ? (double)tokens / max : 0;
            var root = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(value), TextTrimming = TextTrimming.CharacterEllipsis };
            var amt = new TextBlock { Text = $"{FormatTokenCount(tokens)} · {calls} call{(calls == 1 ? "" : "s")}", FontSize = 10.5, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(muted), HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(amt, 1);
            header.Children.Add(lbl);
            header.Children.Add(amt);
            root.Children.Add(header);

            var trackBar = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(track), Margin = new Thickness(0, 6, 0, 0) };
            var fillBar = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(fill), HorizontalAlignment = HorizontalAlignment.Left };
            trackBar.Loaded += (s, e) =>
            {
                double w = trackBar.ActualWidth;
                if (w > 0) fillBar.Width = Math.Max(6, w * frac);
            };
            trackBar.SizeChanged += (s, e) =>
            {
                if (trackBar.ActualWidth > 0) fillBar.Width = Math.Max(6, trackBar.ActualWidth * frac);
            };
            var overlay = new Grid();
            overlay.Children.Add(trackBar);
            overlay.Children.Add(fillBar);
            root.Children.Add(overlay);
            return root;
        }

        private void BtnClearTokenUsage_Click(object sender, RoutedEventArgs e)
        {
            var r = ConfirmDialog("Clear usage data", "Clear all recorded token usage history? This cannot be undone.", null, "Clear", danger: true);
            if (r)
            {
                _tokenUsageService.Clear();
                RenderTokenUsageDashboard();
            }
        }

        private void BtnConfigureCompanyKey_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CompanyCategoryItem cat)
            {
                OpenApiKeyPromptModalForCategory(cat);
            }
        }

        private async void BtnTestCompanyConnection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CompanyCategoryItem cat)
            {
                string key = _cloudApiService.GetApiKeyForProvider(cat.Name, _settings);
                if (string.IsNullOrWhiteSpace(key))
                {
                    MessageBox.Show($"No API key configured for {cat.Name}. Please enter an API key first.", "API Key Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    OpenApiKeyPromptModalForCategory(cat);
                    return;
                }

                btn.Content = "...";
                btn.IsEnabled = false;

                try
                {
                    var sw = Stopwatch.StartNew();
                    using var req = new HttpRequestMessage(HttpMethod.Get, cat.BaseUrl);
                    if (cat.Name.Contains("Anthropic", StringComparison.OrdinalIgnoreCase))
                    {
                        req.Headers.Add("x-api-key", key);
                        req.Headers.Add("anthropic-version", "2023-06-01");
                    }
                    else if (cat.Name.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
                    {
                        req.RequestUri = new Uri($"{cat.BaseUrl}?key={key}");
                    }
                    else
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    }

                    var resp = await _httpClient.SendAsync(req);
                    sw.Stop();

                    if (resp.IsSuccessStatusCode)
                    {
                        MessageBox.Show($"âœ” {cat.Name} Gateway Connected!\r\nHTTP {(int)resp.StatusCode} OK in {sw.ElapsedMilliseconds}ms.", "Gateway Verified", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        MessageBox.Show($"â–² {cat.Name} Auth Error: HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}). Please check your API key.", "Authentication Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        MessageBox.Show($"â— {cat.Name} Endpoint Responded: HTTP {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms.", "Endpoint Status", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"âœ• Connection test failed for {cat.Name}: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    btn.Content = "âš¡ Test";
                    btn.IsEnabled = true;
                }
            }
        }

        private void BtnChatModel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ModelItem model)
            {
                TryLoadModel(model);
            }
        }

        private void TryLoadModel(ModelItem model)
        {
            string key = model.IsCustom ? model.CustomApiKey : _cloudApiService.GetApiKeyForProvider(model.Provider, _settings);
            if (string.IsNullOrWhiteSpace(key))
            {
                // Key required to load model into workspace!
                OpenApiKeyPromptModal(model);
                return;
            }

            ExecuteLoadModel(model);
        }

        private void ExecuteLoadModel(ModelItem model)
        {
            SetActiveModel(model);

            string key = model.IsCustom ? model.CustomApiKey : _cloudApiService.GetApiKeyForProvider(model.Provider, _settings);
            AppendLog($"[GATEWAY] Switched active cloud model to: {model.Name} ({model.Provider})");
            AppendLog($"[ENDPOINT] {model.Endpoint} | Context: {model.Context} | Pricing: {model.Pricing}");
            AppendLog($"[SECURITY] Key validated: {MaskHelper.MaskKey(key)} | TLS 1.3 Active");
            AppendLog("Ready for live API prompts. Enter a message below and click 'Send to API'.");
            TxtInputPrompt.Focus();
        }

        #region API Key Prompt Modal Flow
        private void OpenApiKeyPromptModal(ModelItem model)
        {
            _pendingModelToLoad = model;
            _pendingCategory = _allCompanyCategories.FirstOrDefault(c =>
                c.Name.Contains(model.Provider, StringComparison.OrdinalIgnoreCase) ||
                model.Provider.Contains(c.Name, StringComparison.OrdinalIgnoreCase));

            TxtPromptSubtitle.Text = $"To load and chat with '{model.Name}', configure your {model.Provider} API key below.";
            TxtPromptTargetModel.Text = $"Target Model: {model.Name} ({model.Id})";
            TxtPromptTargetCompany.Text = $"Company: {model.Provider}";
            TxtPromptEndpoint.Text = $"Endpoint: {model.Endpoint}";
            TxtPromptKeyLabel.Text = $"{model.Provider.ToUpperInvariant()} API KEY";
            TxtPromptKeyInput.Text = "";

            if (model.HasLocalLogo && File.Exists(model.LocalLogoPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(model.LocalLogoPath, UriKind.Absolute);
                    bmp.EndInit();

                    ImgPromptLogo.Source = bmp;
                    ImgPromptLogo.Visibility = Visibility.Visible;
                    TxtPromptFallbackIcon.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    ImgPromptLogo.Visibility = Visibility.Collapsed;
                    TxtPromptFallbackIcon.Text = model.Icon;
                    TxtPromptFallbackIcon.Visibility = Visibility.Visible;
                }
            }
            else if (_pendingCategory != null && _pendingCategory.HasLocalLogo && File.Exists(_pendingCategory.LocalLogoPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(_pendingCategory.LocalLogoPath, UriKind.Absolute);
                    bmp.EndInit();

                    ImgPromptLogo.Source = bmp;
                    ImgPromptLogo.Visibility = Visibility.Visible;
                    TxtPromptFallbackIcon.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    ImgPromptLogo.Visibility = Visibility.Collapsed;
                    TxtPromptFallbackIcon.Text = _pendingCategory.FallbackIcon;
                    TxtPromptFallbackIcon.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ImgPromptLogo.Visibility = Visibility.Collapsed;
                TxtPromptFallbackIcon.Text = model.Icon;
                TxtPromptFallbackIcon.Visibility = Visibility.Visible;
            }

            ShowApiKeyPromptModal();
        }

        private void OpenApiKeyPromptModalForCategory(CompanyCategoryItem cat)
        {
            _pendingCategory = cat;
            _pendingModelToLoad = cat.Models?.FirstOrDefault();

            TxtPromptSubtitle.Text = $"Configure credentials for {cat.Name} to unlock all {cat.ModelCount} models in this category.";
            TxtPromptTargetModel.Text = $"Category: {cat.Name}";
            TxtPromptTargetCompany.Text = $"Tagline: {cat.Tagline}";
            TxtPromptEndpoint.Text = $"Console / Docs: {cat.WebUrl}";
            TxtPromptKeyLabel.Text = $"{cat.Name.ToUpperInvariant()} API KEY";

            string existingKey = _cloudApiService.GetApiKeyForProvider(cat.Name, _settings);
            TxtPromptKeyInput.Text = existingKey;

            if (cat.HasLocalLogo && File.Exists(cat.LocalLogoPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(cat.LocalLogoPath, UriKind.Absolute);
                    bmp.EndInit();

                    ImgPromptLogo.Source = bmp;
                    ImgPromptLogo.Visibility = Visibility.Visible;
                    TxtPromptFallbackIcon.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    ImgPromptLogo.Visibility = Visibility.Collapsed;
                    TxtPromptFallbackIcon.Text = cat.FallbackIcon;
                    TxtPromptFallbackIcon.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ImgPromptLogo.Visibility = Visibility.Collapsed;
                TxtPromptFallbackIcon.Text = cat.FallbackIcon;
                TxtPromptFallbackIcon.Visibility = Visibility.Visible;
            }

            ShowApiKeyPromptModal();
        }

        private void ShowApiKeyPromptModal()
        {
            ModalApiKeyPrompt.Visibility = Visibility.Visible;
            ModalApiKeyPrompt.Opacity = 0.0;
            ApiKeyPromptModalScale.ScaleX = 0.88;
            ApiKeyPromptModalScale.ScaleY = 0.88;

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220));
            var scaleAnim = new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
            };

            ModalApiKeyPrompt.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            ApiKeyPromptModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            ApiKeyPromptModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            TxtPromptKeyInput.Focus();
            TxtPromptKeyInput.SelectAll();
        }

        private void CloseApiKeyPromptModal()
        {
            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(160));
            var scaleAnim = new DoubleAnimation(1.0, 0.92, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            fadeOut.Completed += (s, e) =>
            {
                ModalApiKeyPrompt.Visibility = Visibility.Collapsed;
            };

            ModalApiKeyPrompt.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            ApiKeyPromptModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            ApiKeyPromptModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        }

        private void BtnCloseApiKeyPrompt_Click(object sender, RoutedEventArgs e)
        {
            CloseApiKeyPromptModal();
        }

        private void BtnPromptOpenPortal_Click(object sender, RoutedEventArgs e)
        {
            string url = "";
            if (_pendingCategory != null && !string.IsNullOrWhiteSpace(_pendingCategory.KeyDocUrl))
            {
                url = _pendingCategory.KeyDocUrl;
            }
            else if (_pendingCategory != null && !string.IsNullOrWhiteSpace(_pendingCategory.WebUrl))
            {
                url = _pendingCategory.WebUrl;
            }
            else if (_pendingModelToLoad != null && !string.IsNullOrWhiteSpace(_pendingModelToLoad.WebUrl))
            {
                url = _pendingModelToLoad.WebUrl;
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open portal URL: {ex.Message}", "Browser Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void BtnPromptSaveAndLoad_Click(object sender, RoutedEventArgs e)
        {
            string enteredKey = TxtPromptKeyInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(enteredKey))
            {
                MessageBox.Show("Please enter a valid API key to proceed.", "Key Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtPromptKeyInput.Focus();
                return;
            }

            string providerName = _pendingModelToLoad?.Provider ?? _pendingCategory?.Name ?? "OpenAI";
            _settingsService.SetApiKeyForProvider(providerName, enteredKey, _settings);
            SaveSettings();

            if (_pendingCategory != null)
            {
                _aggregatorFetchedForKey.Remove(_pendingCategory.Id);
                _aggregatorModels.Remove(_pendingCategory.Id);
            }
            RefreshCompanyCategoriesUi();
            CloseApiKeyPromptModal();

            if (_pendingModelToLoad != null)
            {
                ExecuteLoadModel(_pendingModelToLoad);
            }
            else
            {
                MessageBox.Show($"API Key for {providerName} saved successfully.", "Credentials Stored", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void TxtPromptKeyInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnPromptSaveAndLoad_Click(sender, new RoutedEventArgs());
            }
            else if (e.Key == Key.Escape)
            {
                CloseApiKeyPromptModal();
            }
        }
        #endregion

        private void BtnModelApiKey_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsModal();
        }

        private void BtnSwitchModel_Click(object sender, RoutedEventArgs e)
        {
            ShowView(ViewModelLibrary, ModelLibraryTranslate);
        }

        private async void BtnNavTestEndpoints_Click(object sender, RoutedEventArgs e)
        {
            await TestApiConnectionsAsync();
        }

        private void SaveModels()
        {
            if (!_modelService.SaveModels(_models))
            {
                MessageBox.Show("Could not save models configuration.", "Model Save Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowView(FrameworkElement viewToShow, TranslateTransform? translate = null)
        {
            if (ViewChatHome != null) ViewChatHome.Visibility = Visibility.Collapsed;
            ViewHomepage.Visibility = Visibility.Collapsed;
            ViewModelLibrary.Visibility = Visibility.Collapsed;
            ViewProjectWorkspace.Visibility = Visibility.Collapsed;
            if (ViewTokenUsage != null) ViewTokenUsage.Visibility = Visibility.Collapsed;
            if (ViewNeuralNet != null) ViewNeuralNet.Visibility = Visibility.Collapsed;

            viewToShow.Visibility = Visibility.Visible;
            viewToShow.Opacity = 0.0;
            SyncActivityRail(viewToShow);

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            viewToShow.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            if (translate != null)
            {
                translate.Y = 18.0;
                var slideUp = new DoubleAnimation(18.0, 0.0, TimeSpan.FromMilliseconds(320))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                translate.BeginAnimation(TranslateTransform.YProperty, slideUp);
            }
        }

        private void BtnNavHome_Click(object sender, RoutedEventArgs e)
        {
            RefreshProjectsUi();
            ShowView(ViewHomepage, HomepageTranslate);
        }

        private void SyncActivityRail(FrameworkElement view)
        {
        }

        private void BtnToggleOutput_Click(object sender, RoutedEventArgs e)
        {
            if (OutputPanel == null || OutputPanelRow == null) return;
            bool show = OutputPanel.Visibility != Visibility.Visible;
            OutputPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (OutputSplitter != null) OutputSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            OutputPanelRow.Height = show ? new GridLength(180) : new GridLength(0);
        }

        private void BtnNavModelLibrary_Click(object sender, RoutedEventArgs e)
        {
            _currentCompanyFilter = "All";
            // Show the view first so the click feels instant, then rebuild the (heavy) card list at
            // Background priority. This stops the whole window from freezing on the first open.
            ShowView(ViewModelLibrary, ModelLibraryTranslate);
            Dispatcher.BeginInvoke(new Action(RefreshCompanyCategoriesUi), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void BtnNavTerminal_Click(object sender, RoutedEventArgs e)
        {
            if (_activeProject != null)
            {
                OpenProjectWorkspace(_activeProject);
            }
            else if (_projects.Count > 0)
            {
                OpenProjectWorkspace(_projects[0]);
            }
            else
            {
                string defaultPath = Path.Combine(_settings.DefaultProjectsDir, "BlackBox-Core");
                var defaultProj = _projectService.CreateProject("BlackBox-Core", defaultPath, "Primary System BlackBox Workspace");
                _projects.Insert(0, defaultProj);
                SaveProjects();
                OpenProjectWorkspace(defaultProj);
            }
        }

        private void BtnNavStorage_Click(object sender, RoutedEventArgs e)
        {
            string dir = _settings.DefaultProjectsDir;
            if (!Directory.Exists(dir))
            {
                dir = AppConstants.DataDirectory;
            }
            OpenPathInExplorer(dir);
        }
        #endregion

        #region Window Controls & Fullscreen
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var slide = new DoubleAnimation(0.0, 35.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            fade.Completed += (s, ev) =>
            {
                WindowState = WindowState.Minimized;
                this.Opacity = 1.0;
                RootTranslate.Y = 0;
            };

            this.BeginAnimation(UIElement.OpacityProperty, fade);
            RootTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullscreen();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (s, ev) => Application.Current.Shutdown();
            this.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void ToggleFullscreen()
        {
            if (_isAnimatingFullscreen) return;
            _isAnimatingFullscreen = true;

            Duration duration = new Duration(TimeSpan.FromMilliseconds(300));

            if (!_isFullscreen)
            {
                _normalWindowRect = new Rect(this.Left, this.Top, this.Width, this.Height);

                double targetLeft = 0;
                double targetTop = 0;
                double targetWidth = SystemParameters.PrimaryScreenWidth;
                double targetHeight = SystemParameters.PrimaryScreenHeight;

                IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

                var leftAnim = new DoubleAnimation(this.Left, targetLeft, duration) { EasingFunction = ease };
                var topAnim = new DoubleAnimation(this.Top, targetTop, duration) { EasingFunction = ease };
                var widthAnim = new DoubleAnimation(this.Width, targetWidth, duration) { EasingFunction = ease };
                var heightAnim = new DoubleAnimation(this.Height, targetHeight, duration) { EasingFunction = ease };

                OuterBorder.BorderThickness = new Thickness(0);

                heightAnim.Completed += (s, e) =>
                {
                    this.BeginAnimation(Window.LeftProperty, null);
                    this.BeginAnimation(Window.TopProperty, null);
                    this.BeginAnimation(Window.WidthProperty, null);
                    this.BeginAnimation(Window.HeightProperty, null);

                    this.Left = targetLeft;
                    this.Top = targetTop;
                    this.Width = targetWidth;
                    this.Height = targetHeight;

                    _isFullscreen = true;
                    _isAnimatingFullscreen = false;
                    UpdateUiForWindowState();
                    AppendLog("[SYSTEM] Smooth fullscreen expansion complete.");
                };

                this.BeginAnimation(Window.LeftProperty, leftAnim);
                this.BeginAnimation(Window.TopProperty, topAnim);
                this.BeginAnimation(Window.WidthProperty, widthAnim);
                this.BeginAnimation(Window.HeightProperty, heightAnim);
            }
            else
            {
                double targetLeft = _normalWindowRect.Left;
                double targetTop = _normalWindowRect.Top;
                double targetWidth = _normalWindowRect.Width > 300 ? _normalWindowRect.Width : 1100;
                double targetHeight = _normalWindowRect.Height > 300 ? _normalWindowRect.Height : 700;

                IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                var leftAnim = new DoubleAnimation(this.Left, targetLeft, duration) { EasingFunction = ease };
                var topAnim = new DoubleAnimation(this.Top, targetTop, duration) { EasingFunction = ease };
                var widthAnim = new DoubleAnimation(this.Width, targetWidth, duration) { EasingFunction = ease };
                var heightAnim = new DoubleAnimation(this.Height, targetHeight, duration) { EasingFunction = ease };

                heightAnim.Completed += (s, e) =>
                {
                    this.BeginAnimation(Window.LeftProperty, null);
                    this.BeginAnimation(Window.TopProperty, null);
                    this.BeginAnimation(Window.WidthProperty, null);
                    this.BeginAnimation(Window.HeightProperty, null);

                    this.Left = targetLeft;
                    this.Top = targetTop;
                    this.Width = targetWidth;
                    this.Height = targetHeight;

                    OuterBorder.BorderThickness = new Thickness(1);
                    _isFullscreen = false;
                    _isAnimatingFullscreen = false;
                    UpdateUiForWindowState();
                    AppendLog("[SYSTEM] Smooth restoration to normal window complete.");
                };

                this.BeginAnimation(Window.LeftProperty, leftAnim);
                this.BeginAnimation(Window.TopProperty, topAnim);
                this.BeginAnimation(Window.WidthProperty, widthAnim);
                this.BeginAnimation(Window.HeightProperty, heightAnim);
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Normal)
            {
                this.Opacity = 0.0;
                RootTranslate.Y = -25.0;
                var restoreFade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var restoreSlide = new DoubleAnimation(-25.0, 0.0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                this.BeginAnimation(UIElement.OpacityProperty, restoreFade);
                RootTranslate.BeginAnimation(TranslateTransform.YProperty, restoreSlide);
            }
        }

        private void UpdateUiForWindowState()
        {
            if (_isFullscreen)
            {
                TxtWindowMode.Text = " | Fullscreen / Maximized";
                IconMaximize.Visibility = Visibility.Collapsed;
                IconRestore.Visibility = Visibility.Visible;
                BtnFullscreen.ToolTip = "Restore Window (Esc / F11)";
            }
            else
            {
                TxtWindowMode.Text = " | Windowed";
                IconMaximize.Visibility = Visibility.Visible;
                IconRestore.Visibility = Visibility.Collapsed;
                BtnFullscreen.ToolTip = "Toggle Fullscreen / Maximize (F11)";
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                OpenCommandPalette();
                e.Handled = true;
            }
            else if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (CommandPalette != null && CommandPalette.Visibility == Visibility.Visible)
                {
                    CloseCommandPalette();
                    e.Handled = true;
                }
                else if (LoadingOverlay.Visibility == Visibility.Visible)
                {
                    _loadingCts?.Cancel();
                    e.Handled = true;
                }
                else if (ModalApiKeyPrompt.Visibility == Visibility.Visible)
                {
                    CloseApiKeyPromptModal();
                    e.Handled = true;
                }
                else if (ModalSettings.Visibility == Visibility.Visible)
                {
                    CloseSettingsModal();
                    e.Handled = true;
                }
                else if (ModalNewProject.Visibility == Visibility.Visible)
                {
                    CloseNewProjectModal();
                    e.Handled = true;
                }
                else if (_isFullscreen)
                {
                    ToggleFullscreen();
                    e.Handled = true;
                }
            }
        }
        #endregion

        #region Workspace Terminal & Cloud API Interactions
        private async Task TestApiConnectionsAsync()
        {
            await _cloudApiService.TestApiConnectionsAsync(_settings, AppendLog);
        }

        private async Task SendPromptToApiAsync(string prompt)
        {
            if (_activeModel == null)
            {
                if (_allModels.Count > 0) SetActiveModel(_allModels[0]);
                else
                {
                    AppendLog("[ERROR] No active model selected. Please select a model from the Cloud Model Library.");
                    return;
                }
            }
            if (_activeModel == null) return;
            var requestModel = _activeModel;

            AppendLog("");
            AppendLog($"[API CALL -> {requestModel.Name} ({requestModel.Provider})]");
            AppendLog($"[ENDPOINT] {requestModel.Endpoint}");
            AppendLog($"[STATUS] Streaming HTTPS POST payload... awaiting response...");

            DateTime callStartTime = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();
            string response = await _cloudApiService.CallCloudApiAsync(requestModel, prompt, _settings);
            sw.Stop();

            bool isErr = response.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase);

            if (!isErr)
            {
                if (_cloudApiService.LastLiveUsage != null && _cloudApiService.LastLiveUsage.TimestampUtc >= callStartTime)
                {
                    var live = _cloudApiService.LastLiveUsage;
                    AppendLog($"[LIVE API VERIFIED] Tokens: {live.PromptTokens} in + {live.CompletionTokens} out = {live.TotalTokens} total ({live.TokensPerSecond:F1} tok/s, {sw.ElapsedMilliseconds}ms)");
                }
                else
                {
                    int promptTokens = TokenHelper.CountTokens(prompt);
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
                }
                foreach (var ws in OpenWorkspaces()) ws.RefreshTelemetry(requestModel);
            }

            AppendLog("");
            AppendLog($"--------------------------- [{requestModel.Name} RESPONSE ({sw.ElapsedMilliseconds}ms)] ---------------------------");
            AppendLog(response);
            AppendLog("-------------------------------------------------------------------------------");
            AppendLog("");
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            TxtTerminalOutput.Clear();
            AppendLog("[SYSTEM] BlackBox terminal buffer cleared.");
        }

        private async void BtnPing_Click(object sender, RoutedEventArgs e)
        {
            if (_activeModel == null)
            {
                if (_allModels.Count > 0) SetActiveModel(_allModels[0]);
                else return;
            }
            if (_activeModel == null) return;

            AppendLog($"[PING] Pinging cloud endpoint: {_activeModel.Endpoint} ({_activeModel.Provider})...");
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, _activeModel.Endpoint);
                string key = _activeModel.IsCustom ? _activeModel.CustomApiKey : _cloudApiService.GetApiKeyForProvider(_activeModel.Provider, _settings);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    if (_activeModel.Provider.Contains("Anthropic", StringComparison.OrdinalIgnoreCase))
                    {
                        req.Headers.Add("x-api-key", key);
                        req.Headers.Add("anthropic-version", "2023-06-01");
                    }
                    else if (_activeModel.Provider.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
                    {
                        req.RequestUri = new Uri($"{_activeModel.Endpoint}?key={key}");
                    }
                    else
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    }
                }

                var resp = await _httpClient.SendAsync(req);
                sw.Stop();
                AppendLog($"[PING RESPONSE] {_activeModel.Name} | Host: {req.RequestUri?.Host} | Latency: {sw.ElapsedMilliseconds}ms | Status: {(int)resp.StatusCode} {resp.ReasonPhrase} | TLS 1.3 Active");
            }
            catch (Exception ex)
            {
                sw.Stop();
                AppendLog($"[PING FAILED] Error reaching {_activeModel.Endpoint} ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            }
        }

        private void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            ProcessCommand();
        }

        private void TxtInputPrompt_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ProcessCommand();
            }
        }

        private async void ProcessCommand()
        {
            string cmd = TxtInputPrompt.Text.Trim();
            if (string.IsNullOrEmpty(cmd)) return;

            AppendLog($"> {cmd}");
            TxtInputPrompt.Clear();

            string lower = cmd.ToLowerInvariant();
            if (lower == "help")
            {
                AppendLog("  Available commands:");
                AppendLog("    models              - List all cloud AI models & active model status");
                AppendLog("    model <id>          - Switch active model (e.g. 'model gpt-4o', 'model gemini-1.5-pro')");
                AppendLog("    providers           - List all configured custom providers");
                AppendLog("    keys                - Show status of configured provider API keys");
                AppendLog("    key <prov> <val>    - Set API key (e.g. 'key openai sk-...', 'key anthropic sk-ant-...')");
                AppendLog("    test                - Test live HTTPS connectivity to all API endpoints");
                AppendLog("    ping                - Test live HTTPS ping latency to active model endpoint");
                AppendLog("    dir                 - List files inside this project directory");
                AppendLog("    explorer            - Open Windows Explorer for this project");
                AppendLog("    settings            - Open LlmodelBox Settings dialog");
                AppendLog("    home                - Return to Homepage");
                AppendLog("    fullscreen          - Toggle fullscreen mode");
                AppendLog("    clear               - Clear terminal output");
                AppendLog("    exit                - Close application");
                AppendLog("");
                AppendLog("  Prompting:");
                AppendLog("    Any text that is not a command is sent directly to the active Cloud API.");
            }
            else if (lower == "models")
            {
                AppendLog("  Available Cloud Model APIs:");
                foreach (var m in _allModels)
                {
                    string marker = (_activeModel != null && m.Id == _activeModel.Id) ? "â— [ACTIVE] " : "  ";
                    string customBadge = m.IsCustom ? " [CUSTOM]" : "";
                    AppendLog($"  {marker}{m.Icon} {m.Name.PadRight(24)}{customBadge} | ID: {m.Id.PadRight(28)} | {m.Provider}");
                }
                AppendLog("");
                AppendLog("  Use 'model <id>' or click 'ðŸ”„ Switch Model' to change.");
            }
            else if (lower == "providers")
            {
                AppendLog("  Configured Custom Providers:");
                if (_settings.CustomProviders == null || _settings.CustomProviders.Count == 0)
                {
                    AppendLog("    No custom providers configured yet. Open 'âš™ Settings' -> 'Custom Providers' to add one.");
                }
                else
                {
                    foreach (var cp in _settings.CustomProviders)
                    {
                        AppendLog($"    - {cp.Name} | Model: {cp.Model} | Endpoint: {cp.BaseUrl} | Key: {MaskHelper.MaskKey(cp.ApiKey)}");
                    }
                }
                AppendLog("");
            }
            else if (lower.StartsWith("model "))
            {
                string target = cmd.Substring(6).Trim();
                var found = _allModels.FirstOrDefault(m => 
                    string.Equals(m.Id, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.Name, target, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    SetActiveModel(found);
                    AppendLog($"[SUCCESS] Switched active model to: {found.Name} ({found.Provider})");
                }
                else
                {
                    AppendLog($"[ERROR] Model '{target}' not found. Type 'models' to see all available IDs.");
                }
            }
            else if (lower == "keys")
            {
                AppendLog("  Configured Cloud LLM API Keys:");
                AppendLog($"    OpenAI:    {MaskHelper.MaskKey(_settings.ApiKeys.OpenAI)}");
                AppendLog($"    Anthropic: {MaskHelper.MaskKey(_settings.ApiKeys.Anthropic)}");
                AppendLog($"    xAI Grok:  {MaskHelper.MaskKey(_settings.ApiKeys.Grok)}");
                AppendLog($"    Gemini:    {MaskHelper.MaskKey(_settings.ApiKeys.Gemini)}");
                AppendLog($"    DeepSeek:  {MaskHelper.MaskKey(_settings.ApiKeys.DeepSeek)}");
                if (_settings.CustomProviders != null && _settings.CustomProviders.Count > 0)
                {
                    AppendLog("  Custom Provider Keys:");
                    foreach (var cp in _settings.CustomProviders)
                    {
                        AppendLog($"    {cp.Name} ({cp.Model}): {MaskHelper.MaskKey(cp.ApiKey)}");
                    }
                }
                AppendLog("  Use 'key <provider> <secret>' or click 'âš™ Settings' to update.");
            }
            else if (lower.StartsWith("key "))
            {
                var parts = cmd.Substring(4).Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    string prov = parts[0].ToLowerInvariant();
                    string val = parts[1].Trim();
                    if (prov.Contains("openai")) _settings.ApiKeys.OpenAI = val;
                    else if (prov.Contains("anthropic") || prov.Contains("claude")) _settings.ApiKeys.Anthropic = val;
                    else if (prov.Contains("grok") || prov.Contains("xai")) _settings.ApiKeys.Grok = val;
                    else if (prov.Contains("gemini") || prov.Contains("google")) _settings.ApiKeys.Gemini = val;
                    else if (prov.Contains("deepseek")) _settings.ApiKeys.DeepSeek = val;
                    else
                    {
                        AppendLog($"[ERROR] Unknown provider '{parts[0]}'. Allowed: openai, anthropic, grok, gemini, deepseek.");
                        return;
                    }
                    SaveSettings();
                    AppendLog($"[SUCCESS] API key for {parts[0]} updated and saved to settings.json ({MaskHelper.MaskKey(val)}).");
                }
                else
                {
                    AppendLog("Usage: key <provider> <api_key>");
                }
            }
            else if (lower == "test")
            {
                await TestApiConnectionsAsync();
            }
            else if (lower == "settings")
            {
                OpenSettingsModal();
            }
            else if (lower == "dir" || lower == "ls")
            {
                if (_activeProject != null && Directory.Exists(_activeProject.Path))
                {
                    var files = Directory.GetFileSystemEntries(_activeProject.Path);
                    AppendLog($"  Directory listing for: {_activeProject.Path}");
                    if (files.Length == 0)
                    {
                        AppendLog("  (Empty project directory)");
                    }
                    else
                    {
                        foreach (var f in files)
                        {
                            AppendLog($"   - {Path.GetFileName(f)}");
                        }
                    }
                }
            }
            else if (lower == "home" || lower == "back")
            {
                BtnBackToProjects_Click(this, new RoutedEventArgs());
            }
            else if (lower == "explorer")
            {
                if (_activeProject != null) OpenPathInExplorer(_activeProject.Path);
            }
            else if (lower == "fullscreen" || lower == "max")
            {
                ToggleFullscreen();
            }
            else if (lower == "minimize" || lower == "min")
            {
                WindowState = WindowState.Minimized;
            }
            else if (lower == "close" || lower == "exit" || lower == "quit")
            {
                BtnClose_Click(this, new RoutedEventArgs());
            }
            else if (lower == "clear" || lower == "cls")
            {
                BtnClear_Click(this, new RoutedEventArgs());
            }
            else if (lower == "ping")
            {
                BtnPing_Click(this, new RoutedEventArgs());
            }
            else
            {
                await SendPromptToApiAsync(cmd);
            }
        }

        private void AppendLog(string message)
        {
            foreach (var ws in OpenWorkspaces()) ws.Log(message);
            if (TxtTerminalOutput != null)
            {
                TxtTerminalOutput.AppendText($"{message}{Environment.NewLine}");
            }
        }
        #endregion

        #region Command Palette (Ctrl+K)
        private sealed class PaletteCommand
        {
            public string Label { get; set; } = "";
            public Action Run { get; set; } = () => { };
        }

        private List<PaletteCommand> _paletteCommands = new();

        private void BuildPaletteCommands()
        {
            _paletteCommands = new List<PaletteCommand>
            {
                new PaletteCommand { Label = "Go to Workspaces", Run = () => BtnNavHome_Click(this, new RoutedEventArgs()) },
                new PaletteCommand { Label = "Go to Cloud Model APIs", Run = () => BtnNavModelLibrary_Click(this, new RoutedEventArgs()) },
                new PaletteCommand { Label = "Token Analysis", Run = () => BtnNavTokenAnalysis_Click(this, new RoutedEventArgs()) },
                new PaletteCommand { Label = "Test Endpoints", Run = () => BtnNavTestEndpoints_Click(this, new RoutedEventArgs()) },
                new PaletteCommand { Label = "Storage", Run = () => BtnNavStorage_Click(this, new RoutedEventArgs()) },
                new PaletteCommand { Label = "Open Settings", Run = () => BtnSettings_Click(this, new RoutedEventArgs()) },
                new PaletteCommand { Label = "Toggle Output Panel", Run = () => BtnToggleOutput_Click(this, new RoutedEventArgs()) },
                new PaletteCommand { Label = "New Project", Run = () => BtnOpenNewProjectModal_Click(this, new RoutedEventArgs()) }
            };
        }

        private void FilterPaletteCommands(string? query)
        {
            if (PaletteList == null) return;
            if (_paletteCommands == null || _paletteCommands.Count == 0) BuildPaletteCommands();

            IEnumerable<PaletteCommand> filtered = _paletteCommands ?? new List<PaletteCommand>();
            if (!string.IsNullOrWhiteSpace(query))
            {
                string q = query.Trim();
                filtered = filtered.Where(c => c.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            PaletteList.ItemsSource = null;
            PaletteList.ItemsSource = filtered.ToList();
            if (PaletteList.Items.Count > 0) PaletteList.SelectedIndex = 0;
        }

        private void OpenCommandPalette()
        {
            if (CommandPalette == null) return;
            BuildPaletteCommands();
            if (TxtPaletteSearch != null) TxtPaletteSearch.Text = "";
            FilterPaletteCommands("");
            CommandPalette.Visibility = Visibility.Visible;
            if (TxtPaletteSearch != null)
            {
                TxtPaletteSearch.Focus();
                Dispatcher.BeginInvoke(new Action(() => TxtPaletteSearch.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void CloseCommandPalette()
        {
            if (CommandPalette == null) return;
            CommandPalette.Visibility = Visibility.Collapsed;
        }

        private void RunPaletteCommand(PaletteCommand? cmd)
        {
            if (cmd == null) return;
            CloseCommandPalette();
            try { cmd.Run?.Invoke(); } catch { }
        }

        private void TxtPaletteSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtPaletteSearch == null) return;
            FilterPaletteCommands(TxtPaletteSearch.Text);
        }

        private void TxtPaletteSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (PaletteList != null && PaletteList.Items.Count > 0)
                {
                    var top = (PaletteList.SelectedItem ?? PaletteList.Items[0]) as PaletteCommand;
                    RunPaletteCommand(top);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseCommandPalette();
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (PaletteList != null && PaletteList.Items.Count > 0)
                {
                    PaletteList.SelectedIndex = Math.Min(PaletteList.SelectedIndex + 1, PaletteList.Items.Count - 1);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (PaletteList != null && PaletteList.Items.Count > 0)
                {
                    PaletteList.SelectedIndex = Math.Max(PaletteList.SelectedIndex - 1, 0);
                }
                e.Handled = true;
            }
        }

        private void PaletteRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PaletteCommand cmd)
            {
                RunPaletteCommand(cmd);
            }
        }

        private void PaletteScrim_Click(object sender, MouseButtonEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, CommandPalette))
            {
                CloseCommandPalette();
            }
        }
        #endregion
    }
}
