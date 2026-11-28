using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LlmodelBox.Controls.UtilityPanel;
using LlmodelBox.Infrastructure;
using LlmodelBox.Infrastructure.RightPanel;
using LlmodelBox.Models;
using LlmodelBox.Runtime.Tools;
using LlmodelBox.Services;
using Xunit;

namespace LlmodelBox.Tests
{
    public class RightUtilityPanelTests
    {
        [Fact]
        public void OmniboxService_ReturnsCategorizedResultsForFilesRecentCommandsToolsPluginsAndUrls()
        {
            var registry = new ToolRegistry();
            var plugins = new PluginManagerService(
                Path.Combine(Path.GetTempPath(), "LlmodelBox_OmniPlug_" + Guid.NewGuid().ToString("N")),
                Path.Combine(Path.GetTempPath(), "LlmodelBox_OmniState_" + Guid.NewGuid().ToString("N") + ".json"));

            // Grant permissions & enable built-in git-lens plugin
            plugins.SetPermission("llmodelbox.git-lens", PluginPermission.ReadFiles, true);
            plugins.SetPermission("llmodelbox.git-lens", PluginPermission.RunCommands, true);
            Assert.True(plugins.TrySetEnabled("llmodelbox.git-lens", true, out _));

            var omni = new OmniboxService(registry, plugins);

            var urlResults = omni.Search(
                "https://learn.microsoft.com/en-us/dotnet",
                new[] { "src/Program.cs" },
                new[] { "src/App.cs" },
                new[] { ("tool:Review", "View: Review", "Diff viewer") },
                new[] { "dotnet test" });
            Assert.Contains(urlResults, r => r.Kind == OmniboxResultKind.Url && r.TargetKey.StartsWith("https://learn.microsoft.com"));

            var fuzzyFiles = omni.Search(
                "pgm",
                new[] { "src/Program.cs", "src/Models/UserSettings.cs" },
                Array.Empty<string>(),
                new[] { ("tool:Review", "View: Review", "Diff") },
                Array.Empty<string>());
            Assert.Contains(fuzzyFiles, r => r.Kind == OmniboxResultKind.File && r.Subtitle == "src/Program.cs");

            var pluginResults = omni.Search(
                "Git",
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<(string, string, string)>(),
                Array.Empty<string>());
            Assert.Contains(pluginResults, r => r.Kind == OmniboxResultKind.PluginCommand);
        }

        [Fact]
        public async Task WorkspaceReviewService_TracksAllChangedFilesBuildsSideBySideAndRejectsHunkOrReverts()
        {
            string root = Path.Combine(Path.GetTempPath(), "LlmodelBox_ReviewTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string fileA = Path.Combine(root, "Alpha.cs");
            string fileB = Path.Combine(root, "Beta.cs");

            string beforeA = "line1\nline2\nline3\nline4\n";
            string afterA = "line1\nline2_changed\nline3\nline4\nline5_added\n";
            string beforeB = "old_b\n";
            string afterB = "new_b\n";

            await File.WriteAllTextAsync(fileA, afterA);
            await File.WriteAllTextAsync(fileB, afterB);

            try
            {
                var svc = new WorkspaceReviewService(new WorkspaceService(), new GitService());
                svc.RecordAiDiffs(root, new[]
                {
                    new ChangedFileEntry { FilePath = "Alpha.cs", BeforeContent = beforeA, AfterContent = afterA, Additions = 2, Deletions = 1 }
                });
                svc.RecordManualUnsavedEdit(root, fileB, beforeB, afterB);

                // Must surface BOTH files, not only the currently open editor file
                var changes = await svc.CollectAllChangesAsync(root);
                Assert.Equal(2, changes.Count);

                var alpha = Assert.Single(changes, c => c.RelativePath == "Alpha.cs");
                Assert.Equal(ChangeOriginKind.AiGenerated, alpha.Origin);
                Assert.NotEmpty(alpha.Hunks);

                var sbs = svc.BuildSideBySide(beforeA, afterA);
                Assert.Contains(sbs, r => r.LeftType == "Removed" || r.RightType == "Added");

                // Accept first hunk then revert whole Beta.cs
                svc.AcceptHunk(alpha.RelativePath, alpha.Hunks[0].Id);
                Assert.True(alpha.Hunks[0].IsAccepted);

                var beta = Assert.Single(changes, c => c.RelativePath == "Beta.cs");
                Assert.True(svc.RevertFile(root, beta));
                Assert.Equal(beforeB, await File.ReadAllTextAsync(fileB));
                Assert.NotEmpty(svc.History);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void TerminalSessionManager_StripsSplitConPtyOscAndAnsiSequencesCleanly()
        {
            string carry = string.Empty;
            string part1 = "PS C:\\proj>\x1B]0;C:\\Windows\\System32\\WindowsPowerShell";
            string part2 = "\\v1.0\\powershell.exe\x07\x1B[?25l\x1B[32mdir\x1B[0m\r\nproject.json\rPS C:\\proj>";

            string out1 = TerminalSessionManager.SanitizeTerminalChunk(part1, ref carry);
            string out2 = TerminalSessionManager.SanitizeTerminalChunk(part2, ref carry);
            string combined = out1 + out2;

            Assert.DoesNotContain("WindowsPowerShell", combined, StringComparison.Ordinal);
            Assert.False(combined.Contains('\x1B'), "Cleaned output must not contain ESC control characters.");
            Assert.DoesNotContain("0;C:", combined, StringComparison.Ordinal);
            Assert.Contains("dir", combined, StringComparison.Ordinal);
            Assert.Contains("project.json", combined, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TerminalSessionManager_SupportsPersistentSessionsHistoryClearAndClose()
        {
            string root = Path.Combine(Path.GetTempPath(), "LlmodelBox_TermTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                using var mgr = new TerminalSessionManager();
                var env = new Dictionary<string, string> { ["LLMODELBOX_TEST_VAR"] = "PART2_OK" };
                // Use pipe mode in headless xUnit worker where desktop console handles may not exist
                var s1 = mgr.CreateSession(TerminalShellKind.Cmd, root, env, preferConPty: false);
                Assert.True(s1.IsRunning);
                Assert.Single(mgr.Sessions);

                await mgr.SendInputAsync(s1.Id, "echo %LLMODELBOX_TEST_VAR%");
                var deadline = DateTime.UtcNow.AddSeconds(4);
                string transcript = mgr.GetTranscript(s1.Id);
                while (!transcript.Contains("PART2_OK", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50);
                    transcript = mgr.GetTranscript(s1.Id);
                }

                Assert.Contains("PART2_OK", transcript);
                Assert.Single(s1.CommandHistory);

                mgr.ClearOutput(s1.Id);
                Assert.Equal(string.Empty, mgr.GetTranscript(s1.Id));

                mgr.CloseSession(s1.Id);
                Assert.Empty(mgr.Sessions);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void BrowserSessionService_ManagesTabsHistoryAndSeparatePermissions()
        {
            var browser = new BrowserSessionService();
            Assert.False(browser.AllowAiAutomation);
            Assert.False(browser.CanAiAutomate(out string deniedReason));
            Assert.Contains("disabled", deniedReason, StringComparison.OrdinalIgnoreCase);

            var tab1 = browser.OpenTab("learn.microsoft.com");
            Assert.Equal("https://learn.microsoft.com/", tab1.Url);

            browser.RecordNavigation(tab1, "https://github.com/R2Ksanu/LLMBOX-FT", "LLMBOX-FT");
            Assert.True(tab1.CanGoBack);
            Assert.False(tab1.CanGoForward);

            string? backUrl = browser.GoBack(tab1);
            Assert.Equal("https://learn.microsoft.com/", backUrl);
            Assert.True(tab1.CanGoForward);

            var tab2 = browser.OpenTab("http://localhost:8080");
            Assert.Equal(2, browser.Tabs.Count);
            Assert.True(browser.CloseTab(tab2.Id));
            Assert.Single(browser.Tabs);

            browser.AllowAiAutomation = true;
            Assert.True(browser.CanAiAutomate(out _));
        }

        [Fact]
        public void PluginManagerService_EnforcesExplicitTrustAndPermissionsBeforeEnablingExternalBinary()
        {
            string root = Path.Combine(Path.GetTempPath(), "LlmodelBox_PluginTest_" + Guid.NewGuid().ToString("N"));
            string pluginsDir = Path.Combine(root, "plugins");
            string statePath = Path.Combine(root, "plugins_state.json");
            Directory.CreateDirectory(pluginsDir);

            try
            {
                string manifestJson = """
                {
                  "Id": "thirdparty.sample-ext",
                  "Name": "Untrusted Sample Plugin",
                  "Version": "1.0.0",
                  "EntryAssembly": "SampleExt.dll",
                  "IsBuiltIn": false,
                  "RequestedPermissions": [ 0, 2 ]
                }
                """;
                File.WriteAllText(Path.Combine(pluginsDir, "sample.manifest.json"), manifestJson);

                var svc = new PluginManagerService(pluginsDir, statePath);
                var rec = Assert.Single(svc.GetAllPlugins(), p => p.Manifest.Id == "thirdparty.sample-ext");
                Assert.False(rec.State.IsTrusted);
                Assert.False(rec.CanExecuteCode);

                // Must fail closed while untrusted
                Assert.False(svc.TrySetEnabled("thirdparty.sample-ext", true, out string err1));
                Assert.Contains("untrusted", err1, StringComparison.OrdinalIgnoreCase);

                // Still fails if trusted but permissions not yet granted
                svc.SetTrust("thirdparty.sample-ext", true);
                Assert.False(svc.TrySetEnabled("thirdparty.sample-ext", true, out string err2));
                Assert.Contains("permissions", err2, StringComparison.OrdinalIgnoreCase);

                // Succeeds only when BOTH trusted and all requested permissions are granted
                svc.SetPermission("thirdparty.sample-ext", PluginPermission.ReadFiles, true);
                svc.SetPermission("thirdparty.sample-ext", PluginPermission.Network, true);
                Assert.True(svc.TrySetEnabled("thirdparty.sample-ext", true, out _));

                // Revoking a required permission automatically disables the plugin
                svc.SetPermission("thirdparty.sample-ext", PluginPermission.Network, false);
                var updated = Assert.Single(svc.GetAllPlugins(), p => p.Manifest.Id == "thirdparty.sample-ext");
                Assert.False(updated.State.IsEnabled);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void PanelLayoutState_RoundTripsAcrossSettingsSaveAndLoad()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "LlmodelBox_PanelPersist_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string settingsPath = Path.Combine(tempDir, "settings.json");

            try
            {
                var svc = new SettingsService(settingsPath);
                var s = new UserSettings
                {
                    RightPanelState = "Collapsed",
                    RightPanelWidth = 465,
                    RightPanelActiveTool = "Terminal",
                    BrowserAllowAutomation = true,
                    BrowserAllowDownloads = false
                };
                Assert.True(svc.SaveSettings(s));

                var loaded = new SettingsService(settingsPath).LoadSettings();
                Assert.Equal("Collapsed", loaded.RightPanelState);
                Assert.Equal(465, loaded.RightPanelWidth);
                Assert.Equal("Terminal", loaded.RightPanelActiveTool);
                Assert.True(loaded.BrowserAllowAutomation);
                Assert.False(loaded.BrowserAllowDownloads);
                Assert.Equal(RightPanelDisplayMode.CollapsedRail, RightUtilityPanel.ParseMode(loaded.RightPanelState));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
