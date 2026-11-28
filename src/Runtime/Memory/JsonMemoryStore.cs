using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlmodelBox.Common;
using LlmodelBox.Runtime.Continuity;

namespace LlmodelBox.Runtime.Memory
{
    // Hardened JSON-file implementation of IMemoryStore and IGlobalMemoryStore.
    // Distinguishes missing files from corrupt/unreadable files, quarantines damaged data,
    // and retains in-memory dirty copies on transient save errors rather than losing writes.
    public sealed class JsonMemoryStore : IMemoryStore, IGlobalMemoryStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
        private readonly string _directory;
        private readonly string _globalFilePath;
        private readonly string _backupDir;
        private readonly Dictionary<string, ConversationMemory> _dirtyProjects = new(StringComparer.OrdinalIgnoreCase);
        private List<MemoryRecord>? _dirtyGlobal;

        public PersistenceDiagnosticReport LastDiagnostics { get; private set; } = new();

        public JsonMemoryStore()
            : this(AppConstants.ChatHistoryDir,
                   Path.Combine(AppConstants.ChatHistoryDir, "global-preferences.mem.json"),
                   Path.Combine(AppConstants.DataDirectory, "backups"))
        {
        }

        public JsonMemoryStore(string directory, string? globalFilePath = null, string? backupDir = null)
        {
            _directory = string.IsNullOrWhiteSpace(directory) ? AppConstants.ChatHistoryDir : directory;
            _globalFilePath = string.IsNullOrWhiteSpace(globalFilePath)
                ? Path.Combine(_directory, "global-preferences.mem.json")
                : globalFilePath;
            _backupDir = string.IsNullOrWhiteSpace(backupDir)
                ? Path.Combine(_directory, "backups")
                : backupDir;
        }

        public ConversationMemory Load(string projectKey)
        {
            if (_dirtyProjects.TryGetValue(projectKey ?? string.Empty, out var dirtyCopy))
                return dirtyCopy;

            string path = PathFor(projectKey);
            if (!File.Exists(path)) return new ConversationMemory();

            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return new ConversationMemory();
                var mem = JsonSerializer.Deserialize<ConversationMemory>(json, Options);
                if (mem == null) throw new InvalidDataException("Null memory JSON.");
                mem.SynchronizeRecords(projectKey);
                return mem;
            }
            catch (Exception ex)
            {
                string? q = CorruptFileQuarantine.PreserveFile(path, _backupDir);
                LastDiagnostics.State = PersistenceState.DegradedError;
                LastDiagnostics.LastError = $"Corrupt memory file '{Path.GetFileName(path)}': {SecretRedactor.Redact(ex.Message)}";
                if (!string.IsNullOrEmpty(q)) LastDiagnostics.QuarantineBackups.Add(q);
                return new ConversationMemory();
            }
        }

        public void Save(string projectKey, ConversationMemory memory)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                memory.SynchronizeRecords(projectKey);
                AtomicFile.WriteAllText(PathFor(projectKey), JsonSerializer.Serialize(memory, Options));
                _dirtyProjects.Remove(projectKey ?? string.Empty);
                LastDiagnostics.State = PersistenceState.Healthy;
                LastDiagnostics.LastError = string.Empty;
                LastDiagnostics.LastSavedUtc = DateTime.UtcNow;
                LastDiagnostics.PendingRetryCount = _dirtyProjects.Count + (_dirtyGlobal != null ? 1 : 0);
            }
            catch (Exception ex)
            {
                _dirtyProjects[projectKey ?? string.Empty] = memory;
                LastDiagnostics.State = PersistenceState.DegradedError;
                LastDiagnostics.LastError = SecretRedactor.Redact(ex.Message);
                LastDiagnostics.PendingRetryCount = _dirtyProjects.Count + (_dirtyGlobal != null ? 1 : 0);
            }
        }

        public void Clear(string projectKey)
        {
            try
            {
                _dirtyProjects.Remove(projectKey ?? string.Empty);
                string path = PathFor(projectKey);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                LastDiagnostics.State = PersistenceState.DegradedError;
                LastDiagnostics.LastError = SecretRedactor.Redact(ex.Message);
            }
        }

        public List<MemoryRecord> LoadGlobal()
        {
            if (_dirtyGlobal != null) return _dirtyGlobal.Select(r => r.Clone()).ToList();
            if (!File.Exists(_globalFilePath)) return new List<MemoryRecord>();

            try
            {
                string json = File.ReadAllText(_globalFilePath);
                if (string.IsNullOrWhiteSpace(json)) return new List<MemoryRecord>();
                var list = JsonSerializer.Deserialize<List<MemoryRecord>>(json, Options);
                if (list == null) throw new InvalidDataException("Null global memory JSON.");
                var now = DateTime.UtcNow;
                list.RemoveAll(r => string.IsNullOrWhiteSpace(r.Content) || r.IsExpired(now));
                foreach (var r in list) r.Scope = MemoryScope.UserGlobal;
                return list;
            }
            catch (Exception ex)
            {
                string? q = CorruptFileQuarantine.PreserveFile(_globalFilePath, _backupDir);
                LastDiagnostics.State = PersistenceState.DegradedError;
                LastDiagnostics.LastError = $"Corrupt global memory file: {SecretRedactor.Redact(ex.Message)}";
                if (!string.IsNullOrEmpty(q)) LastDiagnostics.QuarantineBackups.Add(q);
                return new List<MemoryRecord>();
            }
        }

        public void SaveGlobal(IReadOnlyList<MemoryRecord> records)
        {
            try
            {
                var dir = Path.GetDirectoryName(_globalFilePath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                AtomicFile.WriteAllText(_globalFilePath, JsonSerializer.Serialize(records, Options));
                _dirtyGlobal = null;
                LastDiagnostics.State = PersistenceState.Healthy;
                LastDiagnostics.LastError = string.Empty;
                LastDiagnostics.LastSavedUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _dirtyGlobal = records.Select(r => r.Clone()).ToList();
                LastDiagnostics.State = PersistenceState.DegradedError;
                LastDiagnostics.LastError = SecretRedactor.Redact(ex.Message);
            }
        }

        public void ClearGlobal()
        {
            try
            {
                _dirtyGlobal = null;
                if (File.Exists(_globalFilePath)) File.Delete(_globalFilePath);
            }
            catch (Exception ex)
            {
                LastDiagnostics.State = PersistenceState.DegradedError;
                LastDiagnostics.LastError = SecretRedactor.Redact(ex.Message);
            }
        }

        public bool RetryPendingWrites()
        {
            foreach (var k in _dirtyProjects.Keys.ToList())
                Save(k, _dirtyProjects[k]);
            if (_dirtyGlobal != null)
                SaveGlobal(_dirtyGlobal);
            return _dirtyProjects.Count == 0 && _dirtyGlobal == null;
        }

        private string PathFor(string? projectKey)
            => Path.Combine(_directory, Hash(projectKey ?? string.Empty) + ".mem.json");

        private static string Hash(string value)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
