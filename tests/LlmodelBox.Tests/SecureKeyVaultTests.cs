using System;
using System.IO;
using System.Text.Json;
using LlmodelBox.Models;
using LlmodelBox.Services;
using Xunit;

namespace LlmodelBox.Tests
{
    public class SecureKeyVaultTests
    {
        [Fact]
        public void SaveAndLoad_EncryptsAllKeysInDpapiVault_AndNeverWritesPlaintextToSettingsJson()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "LlmodelBox_VaultTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string settingsPath = Path.Combine(tempDir, "settings.json");
            string vaultPath = Path.Combine(tempDir, "secrets.dpapi");

            try
            {
                var svc = new SettingsService(settingsPath, vaultPath);
                var settings = new UserSettings
                {
                    ProfileName = "Axion",
                    SelectedTheme = "Midnight",
                    DiscordClientId = "1234567890",
                    ApiKeys = new ApiConfig
                    {
                        OpenAI = "sk-secret-openai-998877",
                        Anthropic = "sk-ant-secret-112233",
                        OpenRouter = "sk-or-v1-secret-445566"
                    }
                };
                settings.CustomProviders.Add(new CustomProviderItem
                {
                    Id = "custom-1",
                    Name = "MyCustomLLM",
                    BaseUrl = "https://api.example.com/v1/chat/completions",
                    ApiKey = "custom-secret-token-777"
                });

                Assert.True(svc.SaveSettings(settings));
                Assert.True(File.Exists(settingsPath));
                Assert.True(File.Exists(vaultPath));

                // settings.json must NOT contain ANY plaintext secret
                string diskJson = File.ReadAllText(settingsPath);
                Assert.DoesNotContain("sk-secret-openai-998877", diskJson);
                Assert.DoesNotContain("sk-ant-secret-112233", diskJson);
                Assert.DoesNotContain("sk-or-v1-secret-445566", diskJson);
                Assert.DoesNotContain("custom-secret-token-777", diskJson);
                Assert.DoesNotContain("1234567890", diskJson);

                // Vault binary ciphertext must NOT contain plaintext strings either
                string vaultText = File.ReadAllText(vaultPath);
                Assert.DoesNotContain("sk-secret-openai-998877", vaultText);
                Assert.DoesNotContain("custom-secret-token-777", vaultText);

                // Reloading decrypts all secrets accurately while preserving preferences
                var reloaded = new SettingsService(settingsPath, vaultPath).LoadSettings();
                Assert.Equal("Axion", reloaded.ProfileName);
                Assert.Equal("sk-secret-openai-998877", reloaded.ApiKeys.OpenAI);
                Assert.Equal("sk-ant-secret-112233", reloaded.ApiKeys.Anthropic);
                Assert.Equal("sk-or-v1-secret-445566", reloaded.ApiKeys.OpenRouter);
                Assert.Equal("1234567890", reloaded.DiscordClientId);
                var custom = Assert.Single(reloaded.CustomProviders, p => p.Id == "custom-1");
                Assert.Equal("custom-secret-token-777", custom.ApiKey);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void LoadSettings_AutoMigratesLegacyPlaintextSettingsJsonIntoDpapiVault()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "LlmodelBox_MigrateTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string settingsPath = Path.Combine(tempDir, "settings.json");
            string vaultPath = Path.Combine(tempDir, "secrets.dpapi");

            try
            {
                var legacy = new UserSettings
                {
                    ApiKeys = new ApiConfig { Gemini = "AIzaSyLegacySecretKey999", Nvidia = "nvapi-legacy-123" }
                };
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(legacy));

                var svc = new SettingsService(settingsPath, vaultPath);
                var loaded = svc.LoadSettings();

                Assert.Equal("AIzaSyLegacySecretKey999", loaded.ApiKeys.Gemini);
                Assert.Equal("nvapi-legacy-123", loaded.ApiKeys.Nvidia);
                Assert.True(File.Exists(vaultPath));

                // Verify legacy plaintext keys were scrubbed from settings.json immediately
                string scrubbedJson = File.ReadAllText(settingsPath);
                Assert.DoesNotContain("AIzaSyLegacySecretKey999", scrubbedJson);
                Assert.DoesNotContain("nvapi-legacy-123", scrubbedJson);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void ReinstallWithNewAppFolder_StillPreservesApiKeysFromSharedVault()
        {
            string root = Path.Combine(Path.GetTempPath(), "LlmodelBox_ReinstallTest_" + Guid.NewGuid().ToString("N"));
            string installV1 = Path.Combine(root, "InstallV1");
            string installV2 = Path.Combine(root, "InstallV2");
            string sharedVault = Path.Combine(root, "LocalAppData", "secrets.dpapi");
            Directory.CreateDirectory(installV1);
            Directory.CreateDirectory(installV2);

            try
            {
                var svcV1 = new SettingsService(Path.Combine(installV1, "settings.json"), sharedVault);
                var initial = new UserSettings { ApiKeys = new ApiConfig { DeepSeek = "sk-deepseek-survives-reinstall" } };
                Assert.True(svcV1.SaveSettings(initial));

                // Simulate deleting InstallV1 completely before launching InstallV2
                Directory.Delete(installV1, recursive: true);

                var svcV2 = new SettingsService(Path.Combine(installV2, "settings.json"), sharedVault);
                var restored = svcV2.LoadSettings();
                Assert.Equal("sk-deepseek-survives-reinstall", restored.ApiKeys.DeepSeek);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
