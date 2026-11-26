using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmodelBox.Common;

namespace LlmodelBox.Infrastructure.RightPanel
{
    public enum PluginPermission
    {
        ReadFiles,
        WriteFiles,
        Network,
        RunCommands,
        BrowserAutomation
    }

    public sealed class PluginCommandSpec
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public sealed class PluginManifest
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0.0";
        public string MinHostVersion { get; set; } = "1.0.0";
        public string Author { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string EntryAssembly { get; set; } = string.Empty;
        public string LatestAvailableVersion { get; set; } = string.Empty;
        public bool IsBuiltIn { get; set; }
        public List<PluginPermission> RequestedPermissions { get; set; } = new();
        public List<string> Tools { get; set; } = new();
        public List<PluginCommandSpec> Commands { get; set; } = new();
        public Dictionary<string, string> DefaultSettings { get; set; } = new();
    }

    public sealed class PluginInstallState
    {
        public string PluginId { get; set; } = string.Empty;
        public bool IsInstalled { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsTrusted { get; set; }
        public List<PluginPermission> GrantedPermissions { get; set; } = new();
        public Dictionary<string, string> Settings { get; set; } = new();
    }

    public sealed class PluginViewRecord
    {
        public required PluginManifest Manifest { get; init; }
        public required PluginInstallState State { get; init; }
        public bool HasUpdate =>
            !string.IsNullOrWhiteSpace(Manifest.LatestAvailableVersion) &&
            !string.Equals(Manifest.LatestAvailableVersion, Manifest.Version, StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool CanExecuteCode => State.IsInstalled && State.IsEnabled && (Manifest.IsBuiltIn || State.IsTrusted);
    }

    public sealed class PluginLogEntry
    {
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
        public string PluginId { get; init; } = string.Empty;
        public string Level { get; init; } = "INFO";
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// Manifest-based Plugin Manager enforcing explicit user trust & permission grants
    /// before any plugin binary/tools/commands can be activated.
    /// </summary>
    public sealed class PluginManagerService
    {
        private readonly string _pluginsDir;
        private readonly string _stateFilePath;
        private readonly object _gate = new();
        private readonly List<PluginLogEntry> _activityLog = new();
        private Dictionary<string, PluginInstallState> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PluginManifest> _manifests = new();

        public event Action? PluginsChanged;

        public PluginManagerService(string? pluginsDir = null, string? stateFilePath = null)
        {
            _pluginsDir = string.IsNullOrWhiteSpace(pluginsDir)
                ? Path.Combine(AppConstants.PersistentUserRoot, "plugins")
                : pluginsDir;
            _stateFilePath = string.IsNullOrWhiteSpace(stateFilePath)
                ? Path.Combine(AppConstants.PersistentUserRoot, "plugins_state.json")
                : stateFilePath;

            LoadState();
            ReloadCatalog();
        }

        public string PluginsDirectory => _pluginsDir;

        public void ReloadCatalog()
        {
            lock (_gate)
            {
                _manifests.Clear();
                _manifests.AddRange(BuildAvailableCatalog());

                try
                {
                    Directory.CreateDirectory(_pluginsDir);
                    foreach (var file in Directory.GetFiles(_pluginsDir, "*.manifest.json", SearchOption.AllDirectories))
                    {
                        try
                        {
                            string json = File.ReadAllText(file);
                            var m = JsonSerializer.Deserialize<PluginManifest>(json);
                            if (m != null && !string.IsNullOrWhiteSpace(m.Id) &&
                                !_manifests.Any(x => string.Equals(x.Id, m.Id, StringComparison.OrdinalIgnoreCase)))
                            {
                                _manifests.Add(m);
                                EnsureStateUnsafe(m).IsInstalled = true;
                                LogUnsafe(m.Id, "INFO", $"Discovered external plugin manifest v{m.Version} (awaiting explicit trust check).");
                            }
                        }
                        catch (Exception ex)
                        {
                            LogUnsafe("loader", "ERROR", $"Failed to parse plugin manifest '{Path.GetFileName(file)}': {ex.Message}");
                        }
                    }
                }
                catch { }
            }
            PluginsChanged?.Invoke();
        }

        public IReadOnlyList<PluginViewRecord> GetAllPlugins()
        {
            lock (_gate)
            {
                return _manifests.Select(m => new PluginViewRecord
                {
                    Manifest = m,
                    State = EnsureStateUnsafe(m)
                }).ToList();
            }
        }

        public IReadOnlyList<PluginLogEntry> GetActivityLog()
        {
            lock (_gate) return _activityLog.AsReadOnly();
        }

        public bool TrySetEnabled(string pluginId, bool enabled, out string error)
        {
            error = string.Empty;
            lock (_gate)
            {
                var manifest = _manifests.FirstOrDefault(m => string.Equals(m.Id, pluginId, StringComparison.OrdinalIgnoreCase));
                if (manifest == null) { error = "Plugin not found."; return false; }

                var st = EnsureStateUnsafe(manifest);
                if (!st.IsInstalled)
                {
                    st.IsInstalled = true;
                }

                // Security boundary: external plugin binaries require explicit user trust AND granted permissions.
                if (enabled && !manifest.IsBuiltIn && !string.IsNullOrWhiteSpace(manifest.EntryAssembly) && !st.IsTrusted)
                {
                    error = $"External binary '{manifest.EntryAssembly}' is untrusted. Mark the plugin as Trusted first.";
                    LogUnsafe(pluginId, "WARN", error);
                    return false;
                }

                if (enabled && manifest.RequestedPermissions.Any(p => !st.GrantedPermissions.Contains(p)))
                {
                    var missing = manifest.RequestedPermissions.Where(p => !st.GrantedPermissions.Contains(p));
                    error = "Missing required permissions: " + string.Join(", ", missing);
                    LogUnsafe(pluginId, "WARN", error);
                    return false;
                }

                st.IsEnabled = enabled;
                SaveStateUnsafe();
                LogUnsafe(pluginId, "INFO", enabled ? "Plugin enabled." : "Plugin disabled.");
            }
            PluginsChanged?.Invoke();
            return true;
        }

        public void SetTrust(string pluginId, bool trusted)
        {
            lock (_gate)
            {
                var manifest = _manifests.FirstOrDefault(m => string.Equals(m.Id, pluginId, StringComparison.OrdinalIgnoreCase));
                if (manifest == null) return;
                var st = EnsureStateUnsafe(manifest);
                st.IsTrusted = trusted;
                if (!trusted) st.IsEnabled = false;
                SaveStateUnsafe();
                LogUnsafe(pluginId, "INFO", trusted ? "Marked plugin binary as explicitly trusted." : "Revoked trust and disabled plugin.");
            }
            PluginsChanged?.Invoke();
        }

        public void SetPermission(string pluginId, PluginPermission perm, bool granted)
        {
            lock (_gate)
            {
                var manifest = _manifests.FirstOrDefault(m => string.Equals(m.Id, pluginId, StringComparison.OrdinalIgnoreCase));
                if (manifest == null) return;
                var st = EnsureStateUnsafe(manifest);
                if (granted && !st.GrantedPermissions.Contains(perm)) st.GrantedPermissions.Add(perm);
                else if (!granted)
                {
                    st.GrantedPermissions.Remove(perm);
                    if (manifest.RequestedPermissions.Contains(perm)) st.IsEnabled = false;
                }
                SaveStateUnsafe();
                LogUnsafe(pluginId, "INFO", $"Permission {perm}: {(granted ? "Granted" : "Revoked")}.");
            }
            PluginsChanged?.Invoke();
        }

        public void UpdateSetting(string pluginId, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            lock (_gate)
            {
                var manifest = _manifests.FirstOrDefault(m => string.Equals(m.Id, pluginId, StringComparison.OrdinalIgnoreCase));
                if (manifest == null) return;
                var st = EnsureStateUnsafe(manifest);
                st.Settings[key.Trim()] = value ?? string.Empty;
                SaveStateUnsafe();
                LogUnsafe(pluginId, "INFO", $"Updated setting '{key.Trim()}'.");
            }
        }

        public void InstallFromCatalog(string pluginId)
        {
            lock (_gate)
            {
                var manifest = _manifests.FirstOrDefault(m => string.Equals(m.Id, pluginId, StringComparison.OrdinalIgnoreCase));
                if (manifest == null) return;
                var st = EnsureStateUnsafe(manifest);
                st.IsInstalled = true;
                SaveStateUnsafe();
                LogUnsafe(pluginId, "INFO", $"Installed '{manifest.Name}' v{manifest.Version}. Review permissions before enabling.");
            }
            PluginsChanged?.Invoke();
        }

        public List<PluginCommandSpec> GetActiveCommands()
        {
            lock (_gate)
            {
                return _manifests
                    .Where(m => EnsureStateUnsafe(m).IsEnabled)
                    .SelectMany(m => m.Commands)
                    .ToList();
            }
        }

        public List<string> GetActiveTools()
        {
            lock (_gate)
            {
                return _manifests
                    .Where(m => EnsureStateUnsafe(m).IsEnabled)
                    .SelectMany(m => m.Tools)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        private PluginInstallState EnsureStateUnsafe(PluginManifest manifest)
        {
            if (!_states.TryGetValue(manifest.Id, out var st))
            {
                st = new PluginInstallState
                {
                    PluginId = manifest.Id,
                    IsInstalled = manifest.IsBuiltIn,
                    IsEnabled = false,
                    IsTrusted = manifest.IsBuiltIn,
                    Settings = new Dictionary<string, string>(manifest.DefaultSettings, StringComparer.OrdinalIgnoreCase)
                };
                _states[manifest.Id] = st;
            }
            return st;
        }

        private void LoadState()
        {
            try
            {
                if (!File.Exists(_stateFilePath)) return;
                var list = JsonSerializer.Deserialize<List<PluginInstallState>>(File.ReadAllText(_stateFilePath));
                if (list == null) return;
                _states = list.Where(x => !string.IsNullOrWhiteSpace(x.PluginId))
                    .ToDictionary(x => x.PluginId, x => x, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        private void SaveStateUnsafe()
        {
            try
            {
                string json = JsonSerializer.Serialize(_states.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
                AtomicFile.WriteAllText(_stateFilePath, json);
            }
            catch { }
        }

        private void LogUnsafe(string pluginId, string level, string message)
        {
            _activityLog.Insert(0, new PluginLogEntry { PluginId = pluginId, Level = level, Message = message });
            if (_activityLog.Count > 150) _activityLog.RemoveAt(_activityLog.Count - 1);
        }

        private static List<PluginManifest> BuildAvailableCatalog() => new()
        {
            new PluginManifest
            {
                Id = "llmodelbox.git-lens",
                Name = "Git Inspector Pro",
                Version = "1.2.0",
                LatestAvailableVersion = "1.2.0",
                Author = "LlmodelBox Core",
                Description = "Exposes workspace git status, branch summaries, and quick diff commands to the utility rail.",
                IsBuiltIn = true,
                RequestedPermissions = { PluginPermission.ReadFiles, PluginPermission.RunCommands },
                Tools = { "git_status_summary", "git_diff_inspect" },
                Commands =
                {
                    new PluginCommandSpec { Id = "plugin.git.refresh", Title = "Plugin: Refresh Git Working Tree", Description = "Scans changed files in Source Control." }
                },
                DefaultSettings = { ["maxDiffLines"] = "400" }
            },
            new PluginManifest
            {
                Id = "llmodelbox.dotnet-diagnostics",
                Name = ".NET Build & Test Analyzer",
                Version = "1.0.0",
                LatestAvailableVersion = "1.1.0",
                Author = "LlmodelBox Core",
                Description = "Parses MSBuild and xUnit compiler diagnostics directly into the Problems view.",
                IsBuiltIn = true,
                RequestedPermissions = { PluginPermission.ReadFiles, PluginPermission.RunCommands },
                Tools = { "parse_msbuild_problems" },
                Commands =
                {
                    new PluginCommandSpec { Id = "plugin.dotnet.build", Title = "Plugin: Build & Collect Problems", Description = "Runs dotnet build and populates Problems." }
                },
                DefaultSettings = { ["configuration"] = "Debug" }
            },
            new PluginManifest
            {
                Id = "llmodelbox.web-researcher",
                Name = "WebView Docs Assistant",
                Version = "0.9.4",
                LatestAvailableVersion = "0.9.4",
                Author = "LlmodelBox Core",
                Description = "Integrates embedded WebView2 documentation shortcuts with explicit browser automation permission gating.",
                IsBuiltIn = true,
                RequestedPermissions = { PluginPermission.Network, PluginPermission.BrowserAutomation },
                Tools = { "open_docs_url" },
                Commands =
                {
                    new PluginCommandSpec { Id = "plugin.docs.mslearn", Title = "Plugin: Open Microsoft Learn (.NET 8)", Description = "Opens official .NET 8 docs in Browser." }
                },
                DefaultSettings = { ["defaultSearchEngine"] = "https://learn.microsoft.com/search/?terms=" }
            }
        };
    }
}
