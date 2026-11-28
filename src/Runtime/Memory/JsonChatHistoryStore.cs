using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Runtime.Continuity;

namespace LlmodelBox.Runtime.Memory
{
    /// <summary>
    /// Compatibility JSON chat history store hardened against silent data loss:
    ///   - Distinguishes missing file from unreadable/corrupt JSON
    ///   - Preserves corrupt files in quarantine instead of overwriting as empty
    ///   - Tracks failed/pending writes cleanly and exposes RetryPendingWrites()
    /// </summary>
    public sealed class JsonChatHistoryStore : IChatHistoryStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
        private readonly string _directory;
        private readonly string _backupDir;
        private readonly Dictionary<string, List<ChatMessageItem>> _pendingDirty = new(StringComparer.OrdinalIgnoreCase);

        public PersistenceDiagnosticReport LastDiagnostics { get; private set; } = new();

        public JsonChatHistoryStore()
            : this(AppConstants.ChatHistoryDir, Path.Combine(AppConstants.DataDirectory, "backups"))
        {
        }

        public JsonChatHistoryStore(string directory, string? backupDir = null)
        {
            _directory = string.IsNullOrWhiteSpace(directory) ? AppConstants.ChatHistoryDir : directory;
            _backupDir = string.IsNullOrWhiteSpace(backupDir) ? Path.Combine(_directory, "backups") : backupDir;
        }

        public List<ChatMessageItem> Load(string projectKey)
        {
            string path = PathFor(projectKey);
            if (!File.Exists(path))
                return new List<ChatMessageItem>();

            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<ChatMessageItem>();

                var list = JsonSerializer.Deserialize<List<ChatMessageItem>>(json, Options);
                if (list == null)
                    throw new InvalidDataException("Deserialized null chat message array.");
                return list;
            }
            catch (Exception ex)
            {
                string? q = CorruptFileQuarantine.PreserveFile(path, _backupDir);
                LastDiagnostics.State = PersistenceState.DegradedError;
                LastDiagnostics.LastError = $"Corrupt or unreadable chat history '{Path.GetFileName(path)}': {SecretRedactor.Redact(ex.Message)}";
                if (!string.IsNullOrEmpty(q)) LastDiagnostics.QuarantineBackups.Add(q);
                return new List<ChatMessageItem>();
            }
        }

        public void Save(string projectKey, IEnumerable<ChatMessageItem> messages)
        {
            var snapshot = messages.ToList();
            try
            {
                Directory.CreateDirectory(_directory);
                string json = JsonSerializer.Serialize(snapshot, Options);
                AtomicFile.WriteAllText(PathFor(projectKey), json);
                _pendingDirty.Remove(projectKey ?? string.Empty);
                LastDiagnostics.State = PersistenceState.Healthy;
                LastDiagnostics.LastError = string.Empty;
                LastDiagnostics.LastSavedUtc = DateTime.UtcNow;
                LastDiagnostics.PendingRetryCount = _pendingDirty.Count;
            }
            catch (Exception ex)
            {
                _pendingDirty[projectKey ?? string.Empty] = snapshot;
                LastDiagnostics.State = PersistenceState.DegradedError;
                LastDiagnostics.LastError = SecretRedactor.Redact(ex.Message);
                LastDiagnostics.PendingRetryCount = _pendingDirty.Count;
            }
        }

        public bool RetryPendingWrites()
        {
            if (_pendingDirty.Count == 0) return true;
            foreach (var key in _pendingDirty.Keys.ToList())
            {
                Save(key, _pendingDirty[key]);
            }
            return _pendingDirty.Count == 0;
        }

        public void Clear(string projectKey)
        {
            try
            {
                _pendingDirty.Remove(projectKey ?? string.Empty);
                string path = PathFor(projectKey);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                LastDiagnostics.State = PersistenceState.DegradedError;
                LastDiagnostics.LastError = SecretRedactor.Redact(ex.Message);
            }
        }

        private string PathFor(string? projectKey)
            => Path.Combine(_directory, Hash(projectKey ?? string.Empty) + ".json");

        private static string Hash(string value)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
