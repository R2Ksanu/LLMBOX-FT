using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using LlmodelBox.Common;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    /// <summary>
    /// Encrypted per-user credential vault backed by Windows DPAPI (DataProtectionScope.CurrentUser)
    /// and strict NTFS owner-only permissions. Stored under %LOCALAPPDATA%\LlmodelBox\secrets.dpapi
    /// so API keys survive deleting/reinstalling LlmodelBox.exe and never sit as plaintext on disk.
    /// </summary>
    public sealed class SecureKeyVault
    {
        private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("LLMBXV1\0");
        private static readonly byte[] VaultEntropy = Encoding.UTF8.GetBytes("LlmodelBox.SecretVault.v1::DPAPI.CurrentUser");

        private readonly string _vaultFilePath;

        public sealed class VaultPayload
        {
            public ApiConfig ApiKeys { get; set; } = new();
            public Dictionary<string, string> CustomProviderKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public string DiscordClientId { get; set; } = string.Empty;
        }

        public SecureKeyVault(string? vaultFilePath = null)
        {
            _vaultFilePath = string.IsNullOrWhiteSpace(vaultFilePath)
                ? AppConstants.SecretVaultFilePath
                : vaultFilePath;
        }

        public string VaultFilePath => _vaultFilePath;

        public VaultPayload Load()
        {
            try
            {
                if (!File.Exists(_vaultFilePath)) return new VaultPayload();

                byte[] raw = File.ReadAllBytes(_vaultFilePath);
                if (raw.Length <= MagicHeader.Length) return new VaultPayload();
                for (int i = 0; i < MagicHeader.Length; i++)
                {
                    if (raw[i] != MagicHeader[i]) return new VaultPayload();
                }

                byte[] encrypted = new byte[raw.Length - MagicHeader.Length];
                Buffer.BlockCopy(raw, MagicHeader.Length, encrypted, 0, encrypted.Length);

                byte[] plainBytes = ProtectedData.Unprotect(encrypted, VaultEntropy, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(plainBytes);
                Array.Clear(plainBytes, 0, plainBytes.Length);

                var payload = JsonSerializer.Deserialize<VaultPayload>(json);
                if (payload == null) return new VaultPayload();
                payload.ApiKeys ??= new ApiConfig();
                payload.CustomProviderKeys ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return payload;
            }
            catch
            {
                // Wrong Windows user, different machine, or corrupt file -> fail safely with empty secrets.
                return new VaultPayload();
            }
        }

        public bool SaveFromSettings(UserSettings settings)
        {
            if (settings == null) return false;

            var existing = Load();
            var payload = new VaultPayload
            {
                ApiKeys = CloneApiKeys(settings.ApiKeys ?? new ApiConfig()),
                CustomProviderKeys = new Dictionary<string, string>(existing.CustomProviderKeys, StringComparer.OrdinalIgnoreCase),
                DiscordClientId = settings.DiscordClientId ?? string.Empty
            };

            if (settings.CustomProviders != null)
            {
                foreach (var cp in settings.CustomProviders)
                {
                    if (cp == null) continue;
                    string keyId = !string.IsNullOrWhiteSpace(cp.Id) ? cp.Id : cp.Name;
                    if (string.IsNullOrWhiteSpace(keyId)) continue;

                    if (!string.IsNullOrWhiteSpace(cp.ApiKey))
                        payload.CustomProviderKeys[keyId] = cp.ApiKey.Trim();
                    else if (payload.CustomProviderKeys.ContainsKey(keyId) && settings.CustomProviders.Any(x => x == cp))
                        payload.CustomProviderKeys.Remove(keyId);
                }
            }

            return SavePayload(payload);
        }

        public bool SavePayload(VaultPayload payload)
        {
            if (payload == null) return false;
            try
            {
                string json = JsonSerializer.Serialize(payload);
                byte[] plainBytes = Encoding.UTF8.GetBytes(json);
                byte[] encrypted = ProtectedData.Protect(plainBytes, VaultEntropy, DataProtectionScope.CurrentUser);
                Array.Clear(plainBytes, 0, plainBytes.Length);

                byte[] finalBytes = new byte[MagicHeader.Length + encrypted.Length];
                Buffer.BlockCopy(MagicHeader, 0, finalBytes, 0, MagicHeader.Length);
                Buffer.BlockCopy(encrypted, 0, finalBytes, MagicHeader.Length, encrypted.Length);

                EnsureRestrictedDirectory(_vaultFilePath);
                AtomicFile.WriteAllBytes(_vaultFilePath, finalBytes);
                ApplyOwnerOnlyFileAcl(_vaultFilePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool ContainsAnyPlaintextSecrets(UserSettings settings)
        {
            if (settings == null) return false;
            if (!string.IsNullOrWhiteSpace(settings.DiscordClientId)) return true;

            var k = settings.ApiKeys;
            if (k != null && (
                !string.IsNullOrWhiteSpace(k.OpenAI) ||
                !string.IsNullOrWhiteSpace(k.Anthropic) ||
                !string.IsNullOrWhiteSpace(k.Grok) ||
                !string.IsNullOrWhiteSpace(k.Gemini) ||
                !string.IsNullOrWhiteSpace(k.DeepSeek) ||
                !string.IsNullOrWhiteSpace(k.Groq) ||
                !string.IsNullOrWhiteSpace(k.OpenRouter) ||
                !string.IsNullOrWhiteSpace(k.Cloudflare) ||
                !string.IsNullOrWhiteSpace(k.Mistral) ||
                !string.IsNullOrWhiteSpace(k.HuggingFace) ||
                !string.IsNullOrWhiteSpace(k.Cohere) ||
                !string.IsNullOrWhiteSpace(k.Nvidia) ||
                !string.IsNullOrWhiteSpace(k.SambaNova) ||
                !string.IsNullOrWhiteSpace(k.Zhipu)))
            {
                return true;
            }

            return settings.CustomProviders?.Any(cp => cp != null && !string.IsNullOrWhiteSpace(cp.ApiKey)) == true;
        }

        public static void MergeVaultIntoSettings(UserSettings settings, VaultPayload vault)
        {
            if (settings == null || vault == null) return;
            settings.ApiKeys ??= new ApiConfig();
            vault.ApiKeys ??= new ApiConfig();

            settings.ApiKeys.OpenAI = PickSecret(settings.ApiKeys.OpenAI, vault.ApiKeys.OpenAI);
            settings.ApiKeys.Anthropic = PickSecret(settings.ApiKeys.Anthropic, vault.ApiKeys.Anthropic);
            settings.ApiKeys.Grok = PickSecret(settings.ApiKeys.Grok, vault.ApiKeys.Grok);
            settings.ApiKeys.Gemini = PickSecret(settings.ApiKeys.Gemini, vault.ApiKeys.Gemini);
            settings.ApiKeys.DeepSeek = PickSecret(settings.ApiKeys.DeepSeek, vault.ApiKeys.DeepSeek);
            settings.ApiKeys.Groq = PickSecret(settings.ApiKeys.Groq, vault.ApiKeys.Groq);
            settings.ApiKeys.OpenRouter = PickSecret(settings.ApiKeys.OpenRouter, vault.ApiKeys.OpenRouter);
            settings.ApiKeys.Cloudflare = PickSecret(settings.ApiKeys.Cloudflare, vault.ApiKeys.Cloudflare);
            settings.ApiKeys.Mistral = PickSecret(settings.ApiKeys.Mistral, vault.ApiKeys.Mistral);
            settings.ApiKeys.HuggingFace = PickSecret(settings.ApiKeys.HuggingFace, vault.ApiKeys.HuggingFace);
            settings.ApiKeys.Cohere = PickSecret(settings.ApiKeys.Cohere, vault.ApiKeys.Cohere);
            settings.ApiKeys.Nvidia = PickSecret(settings.ApiKeys.Nvidia, vault.ApiKeys.Nvidia);
            settings.ApiKeys.SambaNova = PickSecret(settings.ApiKeys.SambaNova, vault.ApiKeys.SambaNova);
            settings.ApiKeys.Zhipu = PickSecret(settings.ApiKeys.Zhipu, vault.ApiKeys.Zhipu);

            if (string.IsNullOrWhiteSpace(settings.DiscordClientId) && !string.IsNullOrWhiteSpace(vault.DiscordClientId))
            {
                settings.DiscordClientId = vault.DiscordClientId;
            }

            if (settings.CustomProviders != null && vault.CustomProviderKeys != null)
            {
                foreach (var cp in settings.CustomProviders)
                {
                    if (cp == null || !string.IsNullOrWhiteSpace(cp.ApiKey)) continue;
                    string keyId = !string.IsNullOrWhiteSpace(cp.Id) ? cp.Id : cp.Name;
                    if (!string.IsNullOrWhiteSpace(keyId) && vault.CustomProviderKeys.TryGetValue(keyId, out string? savedKey))
                    {
                        cp.ApiKey = savedKey;
                    }
                    else if (!string.IsNullOrWhiteSpace(cp.Name) && vault.CustomProviderKeys.TryGetValue(cp.Name, out string? namedKey))
                    {
                        cp.ApiKey = namedKey;
                    }
                }
            }
        }

        public static UserSettings CreateScrubbedCopyForDisk(UserSettings settings)
        {
            string json = JsonSerializer.Serialize(settings);
            var copy = JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
            copy.ApiKeys = new ApiConfig();
            copy.DiscordClientId = string.Empty;
            if (copy.CustomProviders != null)
            {
                foreach (var cp in copy.CustomProviders)
                {
                    if (cp != null) cp.ApiKey = string.Empty;
                }
            }
            return copy;
        }

        private static string PickSecret(string existing, string fromVault)
            => !string.IsNullOrWhiteSpace(existing) ? existing : (fromVault ?? string.Empty);

        private static ApiConfig CloneApiKeys(ApiConfig src) => new()
        {
            OpenAI = src.OpenAI?.Trim() ?? string.Empty,
            Anthropic = src.Anthropic?.Trim() ?? string.Empty,
            Grok = src.Grok?.Trim() ?? string.Empty,
            Gemini = src.Gemini?.Trim() ?? string.Empty,
            DeepSeek = src.DeepSeek?.Trim() ?? string.Empty,
            Groq = src.Groq?.Trim() ?? string.Empty,
            OpenRouter = src.OpenRouter?.Trim() ?? string.Empty,
            Cloudflare = src.Cloudflare?.Trim() ?? string.Empty,
            Mistral = src.Mistral?.Trim() ?? string.Empty,
            HuggingFace = src.HuggingFace?.Trim() ?? string.Empty,
            Cohere = src.Cohere?.Trim() ?? string.Empty,
            Nvidia = src.Nvidia?.Trim() ?? string.Empty,
            SambaNova = src.SambaNova?.Trim() ?? string.Empty,
            Zhipu = src.Zhipu?.Trim() ?? string.Empty
        };

        private static void EnsureRestrictedDirectory(string filePath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);
            }
            catch { }
        }

        private static void ApplyOwnerOnlyFileAcl(string filePath)
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.User == null) return;

                var info = new FileInfo(filePath);
                var security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(
                    identity.User,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                security.AddAccessRule(new FileSystemAccessRule(
                    systemSid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                info.SetAccessControl(security);
            }
            catch { }
        }
    }
}
