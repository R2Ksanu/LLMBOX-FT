using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlmodelBox.Common;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly string _settingsFilePath;
        private readonly SecureKeyVault _vault;

        public SettingsService(string? settingsFilePath = null, string? vaultFilePath = null)
        {
            _settingsFilePath = settingsFilePath ?? AppConstants.SettingsFilePath;
            string resolvedVault;
            if (!string.IsNullOrWhiteSpace(vaultFilePath))
            {
                resolvedVault = vaultFilePath;
            }
            else if (settingsFilePath != null)
            {
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(settingsFilePath).ToLowerInvariant()));
                resolvedVault = Path.Combine(Path.GetTempPath(), "LlmodelBox_IsolatedVaults", Convert.ToHexString(hash).Substring(0, 16) + ".dpapi");
            }
            else
            {
                resolvedVault = AppConstants.SecretVaultFilePath;
            }
            _vault = new SecureKeyVault(resolvedVault);
        }

        public UserSettings LoadSettings()
        {
            var settings = new UserSettings();
            bool loadedFromDisk = false;
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<UserSettings>(json);
                    if (loaded != null)
                    {
                        settings = loaded;
                        loadedFromDisk = true;
                    }
                }
            }
            catch { }

            if (settings.ApiKeys == null) settings.ApiKeys = new ApiConfig();
            if (settings.CustomProviders == null) settings.CustomProviders = new List<CustomProviderItem>();

            bool hadLegacyPlaintextOnDisk = loadedFromDisk && SecureKeyVault.ContainsAnyPlaintextSecrets(settings);
            var vaultPayload = _vault.Load();
            SecureKeyVault.MergeVaultIntoSettings(settings, vaultPayload);

            // Auto-migrate any plaintext API keys out of legacy settings.json into the DPAPI vault
            // and scrub settings.json so no plaintext secrets remain on disk.
            if (hadLegacyPlaintextOnDisk)
            {
                _vault.SaveFromSettings(settings);
                WriteScrubbedSettingsFile(settings);
            }

            // Auto-detect environment variables in-memory only (do not overwrite encrypted vault implicitly).
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.OpenAI))
                settings.ApiKeys.OpenAI = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.Anthropic))
                settings.ApiKeys.Anthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.Grok))
                settings.ApiKeys.Grok = Environment.GetEnvironmentVariable("XAI_API_KEY") ?? Environment.GetEnvironmentVariable("GROK_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.Gemini))
                settings.ApiKeys.Gemini = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.DeepSeek))
                settings.ApiKeys.DeepSeek = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.Groq))
                settings.ApiKeys.Groq = Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.OpenRouter))
                settings.ApiKeys.OpenRouter = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.Cloudflare))
                settings.ApiKeys.Cloudflare = Environment.GetEnvironmentVariable("CLOUDFLARE_API_KEY") ?? Environment.GetEnvironmentVariable("CF_API_TOKEN") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.Mistral))
                settings.ApiKeys.Mistral = Environment.GetEnvironmentVariable("MISTRAL_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.HuggingFace))
                settings.ApiKeys.HuggingFace = Environment.GetEnvironmentVariable("HUGGINGFACE_API_KEY") ?? Environment.GetEnvironmentVariable("HF_TOKEN") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.Cohere))
                settings.ApiKeys.Cohere = Environment.GetEnvironmentVariable("COHERE_API_KEY") ?? Environment.GetEnvironmentVariable("CO_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.Nvidia))
                settings.ApiKeys.Nvidia = Environment.GetEnvironmentVariable("NVIDIA_API_KEY") ?? Environment.GetEnvironmentVariable("NIM_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.SambaNova))
                settings.ApiKeys.SambaNova = Environment.GetEnvironmentVariable("SAMBANOVA_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(settings.ApiKeys.Zhipu))
                settings.ApiKeys.Zhipu = Environment.GetEnvironmentVariable("ZHIPU_API_KEY") ?? Environment.GetEnvironmentVariable("BIGMODEL_API_KEY") ?? "";

            if (string.IsNullOrEmpty(settings.IconShape))
                settings.IconShape = "Squircle";

            EnsureDefaultCustomProviders(settings);

            // Sync custom provider keys with ApiKeys if previously set
            foreach (var cp in settings.CustomProviders)
            {
                if (string.IsNullOrWhiteSpace(cp.ApiKey))
                {
                    if (cp.Name.Contains("Groq", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.ApiKeys.Groq))
                        cp.ApiKey = settings.ApiKeys.Groq;
                    else if (cp.Name.Contains("OpenRouter", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.ApiKeys.OpenRouter))
                        cp.ApiKey = settings.ApiKeys.OpenRouter;
                    else if (cp.Name.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.ApiKeys.Cloudflare))
                        cp.ApiKey = settings.ApiKeys.Cloudflare;
                    else if (cp.Name.Contains("Mistral", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.ApiKeys.Mistral))
                        cp.ApiKey = settings.ApiKeys.Mistral;
                    else if (cp.Name.Contains("Hugging", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.ApiKeys.HuggingFace))
                        cp.ApiKey = settings.ApiKeys.HuggingFace;
                    else if (cp.Name.Contains("Cohere", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.ApiKeys.Cohere))
                        cp.ApiKey = settings.ApiKeys.Cohere;
                    else if (cp.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.ApiKeys.Nvidia))
                        cp.ApiKey = settings.ApiKeys.Nvidia;
                    else if (cp.Name.Contains("SambaNova", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.ApiKeys.SambaNova))
                        cp.ApiKey = settings.ApiKeys.SambaNova;
                    else if (cp.Name.Contains("Zhipu", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.ApiKeys.Zhipu))
                        cp.ApiKey = settings.ApiKeys.Zhipu;
                    else if (cp.Name.Contains("Google", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.ApiKeys.Gemini))
                        cp.ApiKey = settings.ApiKeys.Gemini;
                }
            }

            return settings;
        }

        public bool SaveSettings(UserSettings settings)
        {
            if (settings == null) return false;
            try
            {
                // Persist API keys in Windows DPAPI encrypted vault (current-user + NTFS ACL protected)
                // and scrub plaintext secrets out of settings.json before touching disk.
                _vault.SaveFromSettings(settings);
                WriteScrubbedSettingsFile(settings);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void WriteScrubbedSettingsFile(UserSettings settings)
        {
            var scrubbed = SecureKeyVault.CreateScrubbedCopyForDisk(settings);
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(scrubbed, options);
            LlmodelBox.Common.AtomicFile.WriteAllText(_settingsFilePath, json);
        }

        public void SetApiKeyForProvider(string provider, string key, UserSettings settings)
        {
            if (string.IsNullOrWhiteSpace(provider)) return;
            string cleanKey = key.Trim();

            if (provider.Contains("OpenAI", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.OpenAI = cleanKey;
            else if (provider.Contains("Anthropic", StringComparison.OrdinalIgnoreCase) || provider.Contains("Claude", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.Anthropic = cleanKey;
            else if (provider.Contains("Grok", StringComparison.OrdinalIgnoreCase) || provider.Contains("xAI", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.Grok = cleanKey;
            else if (provider.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || provider.Contains("Google", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.Gemini = cleanKey;
            else if (provider.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.DeepSeek = cleanKey;
            else if (provider.Contains("Groq", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.Groq = cleanKey;
            else if (provider.Contains("OpenRouter", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.OpenRouter = cleanKey;
            else if (provider.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.Cloudflare = cleanKey;
            else if (provider.Contains("Mistral", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.Mistral = cleanKey;
            else if (provider.Contains("Hugging", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.HuggingFace = cleanKey;
            else if (provider.Contains("Cohere", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.Cohere = cleanKey;
            else if (provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.Nvidia = cleanKey;
            else if (provider.Contains("SambaNova", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.SambaNova = cleanKey;
            else if (provider.Contains("Zhipu", StringComparison.OrdinalIgnoreCase) || provider.Contains("BigModel", StringComparison.OrdinalIgnoreCase) || provider.Contains("Z.AI", StringComparison.OrdinalIgnoreCase))
                settings.ApiKeys.Zhipu = cleanKey;

            // Also synchronize custom providers list
            if (settings.CustomProviders != null)
            {
                var cp = settings.CustomProviders.FirstOrDefault(p =>
                    p.Name.Contains(provider, StringComparison.OrdinalIgnoreCase) ||
                    provider.Contains(p.Name, StringComparison.OrdinalIgnoreCase));
                if (cp != null)
                {
                    cp.ApiKey = cleanKey;
                }
            }

            SaveSettings(settings);
        }

        public void EnsureDefaultCustomProviders(UserSettings settings)
        {
            if (settings.CustomProviders == null)
            {
                settings.CustomProviders = new List<CustomProviderItem>();
            }

            var defaultList = new List<CustomProviderItem>
            {
                new CustomProviderItem
                {
                    Id = "provider-google-aistudio",
                    Name = "Google AI Studio / Gemini",
                    WebUrl = "https://aistudio.google.com",
                    BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models",
                    ApiKey = settings.ApiKeys?.Gemini ?? "",
                    Model = "gemini-1.5-pro",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "google.com.png"),
                    CreatedAt = "2026-09-20 15:45:00"
                },
                new CustomProviderItem
                {
                    Id = "provider-groq",
                    Name = "Groq",
                    WebUrl = "https://console.groq.com",
                    BaseUrl = "https://api.groq.com/openai/v1/chat/completions",
                    ApiKey = settings.ApiKeys?.Groq ?? "",
                    Model = "llama-3.3-70b-versatile",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "groq.com.png"),
                    CreatedAt = "2026-09-20 15:45:00"
                },
                new CustomProviderItem
                {
                    Id = "provider-openrouter",
                    Name = "OpenRouter",
                    WebUrl = "https://openrouter.ai",
                    BaseUrl = "https://openrouter.ai/api/v1/chat/completions",
                    ApiKey = settings.ApiKeys?.OpenRouter ?? "",
                    Model = "anthropic/claude-3.5-sonnet",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "openrouter.ai.png"),
                    CreatedAt = "2026-09-20 15:45:00"
                },
                new CustomProviderItem
                {
                    Id = "provider-cloudflare",
                    Name = "Cloudflare Workers AI",
                    WebUrl = "https://developers.cloudflare.com/workers-ai",
                    BaseUrl = "https://api.cloudflare.com/client/v4/accounts/{account_id}/ai/v1/chat/completions",
                    ApiKey = settings.ApiKeys?.Cloudflare ?? "",
                    Model = "@cf/meta/llama-3.1-70b-instruct",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "cloudflare.com.png"),
                    CreatedAt = "2026-09-20 15:45:00"
                },
                new CustomProviderItem
                {
                    Id = "provider-mistral",
                    Name = "Mistral AI",
                    WebUrl = "https://console.mistral.ai",
                    BaseUrl = "https://api.mistral.ai/v1/chat/completions",
                    ApiKey = settings.ApiKeys?.Mistral ?? "",
                    Model = "mistral-large-latest",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "mistral.ai.png"),
                    CreatedAt = "2026-09-20 15:45:00"
                },
                new CustomProviderItem
                {
                    Id = "provider-huggingface",
                    Name = "Hugging Face",
                    WebUrl = "https://huggingface.co",
                    BaseUrl = "https://api-inference.huggingface.co/v1/chat/completions",
                    ApiKey = settings.ApiKeys?.HuggingFace ?? "",
                    Model = "meta-llama/Llama-3.2-3B-Instruct",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "huggingface.co.png"),
                    CreatedAt = "2026-09-20 15:45:00"
                },
                new CustomProviderItem
                {
                    Id = "provider-cohere",
                    Name = "Cohere",
                    WebUrl = "https://dashboard.cohere.com",
                    BaseUrl = "https://api.cohere.com/v2/chat",
                    ApiKey = settings.ApiKeys?.Cohere ?? "",
                    Model = "command-r-plus-08-2024",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "cohere.com.png"),
                    CreatedAt = "2026-09-20 15:45:00"
                },
                new CustomProviderItem
                {
                    Id = "provider-nvidia",
                    Name = "NVIDIA NIM",
                    WebUrl = "https://build.nvidia.com",
                    BaseUrl = "https://integrate.api.nvidia.com/v1/chat/completions",
                    ApiKey = settings.ApiKeys?.Nvidia ?? "",
                    Model = "meta/llama-3.1-70b-instruct",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "nvidia.com.png"),
                    CreatedAt = "2026-09-20 15:45:00"
                },
                new CustomProviderItem
                {
                    Id = "provider-sambanova",
                    Name = "SambaNova Cloud",
                    WebUrl = "https://cloud.sambanova.ai",
                    BaseUrl = "https://api.sambanova.ai/v1/chat/completions",
                    ApiKey = settings.ApiKeys?.SambaNova ?? "",
                    Model = "Meta-Llama-3.1-70B-Instruct",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "sambanova.ai.png"),
                    CreatedAt = "2026-09-20 15:45:00"
                },
                new CustomProviderItem
                {
                    Id = "provider-zhipu",
                    Name = "Z.AI / Zhipu",
                    WebUrl = "https://open.bigmodel.cn",
                    BaseUrl = "https://open.bigmodel.cn/api/paas/v4/chat/completions",
                    ApiKey = settings.ApiKeys?.Zhipu ?? "",
                    Model = "glm-4-plus",
                    LocalLogoPath = Path.Combine(AppConstants.CacheDir, "bigmodel.cn.png"),
                    CreatedAt = "2026-09-20 15:45:00"
                }
            };

            foreach (var item in defaultList)
            {
                if (!settings.CustomProviders.Any(p => p.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) || p.WebUrl.Equals(item.WebUrl, StringComparison.OrdinalIgnoreCase)))
                {
                    settings.CustomProviders.Add(item);
                }
            }
        }
    }
}
