using System;
using System.Text.RegularExpressions;

namespace LlmodelBox.Runtime.Continuity
{
    /// <summary>
    /// Redacts API keys, bearer tokens, passwords, and private key blocks before persisting
    /// observations, events, turn checkpoints, or persistence diagnostics.
    /// </summary>
    public static class SecretRedactor
    {
        private static readonly (Regex Pattern, string Replacement)[] Rules =
        {
            (new Regex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.Compiled), "[REDACTED_PRIVATE_KEY]"),
            (new Regex(@"\b(sk-ant-[A-Za-z0-9\-_]{12,}|sk-[A-Za-z0-9\-_]{16,}|gsk_[A-Za-z0-9]{16,}|hf_[A-Za-z0-9]{16,}|xai-[A-Za-z0-9\-_]{16,}|AIza[0-9A-Za-z\-_]{24,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|nvapi-[A-Za-z0-9\-_]{16,})\b", RegexOptions.Compiled), "[REDACTED_KEY]"),
            (new Regex(@"(?i)(Bearer\s+)[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled), "$1[REDACTED_TOKEN]"),
            (new Regex(@"(?i)((?:api[_-]?key|secret|token|password|passwd|pwd|authorization)\s*[:=]\s*[""']?)([^\s""',;}\]]{6,})", RegexOptions.Compiled), "$1[REDACTED]")
        };

        public static string Redact(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string result = input;
            foreach (var (pattern, replacement) in Rules)
            {
                result = pattern.Replace(result, replacement);
            }
            return result;
        }
    }
}
