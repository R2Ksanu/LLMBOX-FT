using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlmodelBox.Common;

namespace LlmodelBox.Runtime.Memory
{
    // JSON-file implementation of IMemoryStore and IGlobalMemoryStore.
    // Project scope: "<hash>.mem.json" under ChatHistoryDir.
    // Global scope: "global-preferences.mem.json" (user-approved only).
    public sealed class JsonMemoryStore : IMemoryStore, IGlobalMemoryStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
        private readonly string _directory;
        private readonly string _globalFilePath;

        public JsonMemoryStore()
            : this(AppConstants.ChatHistoryDir, Path.Combine(AppConstants.ChatHistoryDir, "global-preferences.mem.json"))
        {
        }

        public JsonMemoryStore(string directory, string? globalFilePath = null)
        {
            _directory = string.IsNullOrWhiteSpace(directory) ? AppConstants.ChatHistoryDir : directory;
            _globalFilePath = string.IsNullOrWhiteSpace(globalFilePath)
                ? Path.Combine(_directory, "global-preferences.mem.json")
                : globalFilePath;
        }

        public ConversationMemory Load(string projectKey)
        {
            try
            {
                string path = PathFor(projectKey);
                if (!File.Exists(path)) return new ConversationMemory();
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return new ConversationMemory();
                var mem = JsonSerializer.Deserialize<ConversationMemory>(json, Options) ?? new ConversationMemory();
                mem.SynchronizeRecords(projectKey);
                return mem;
            }
            catch
            {
                return new ConversationMemory();
            }
        }

        public void Save(string projectKey, ConversationMemory memory)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                memory.SynchronizeRecords(projectKey);
                LlmodelBox.Common.AtomicFile.WriteAllText(PathFor(projectKey), JsonSerializer.Serialize(memory, Options));
            }
            catch
            {
                // Best-effort — never break the turn on a disk error.
            }
        }

        public void Clear(string projectKey)
        {
            try
            {
                string path = PathFor(projectKey);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        public List<MemoryRecord> LoadGlobal()
        {
            try
            {
                if (!File.Exists(_globalFilePath)) return new List<MemoryRecord>();
                string json = File.ReadAllText(_globalFilePath);
                if (string.IsNullOrWhiteSpace(json)) return new List<MemoryRecord>();
                var list = JsonSerializer.Deserialize<List<MemoryRecord>>(json, Options) ?? new List<MemoryRecord>();
                var now = DateTime.UtcNow;
                list.RemoveAll(r => string.IsNullOrWhiteSpace(r.Content) || r.IsExpired(now));
                foreach (var r in list) r.Scope = MemoryScope.UserGlobal;
                return list;
            }
            catch
            {
                return new List<MemoryRecord>();
            }
        }

        public void SaveGlobal(IReadOnlyList<MemoryRecord> records)
        {
            try
            {
                var dir = Path.GetDirectoryName(_globalFilePath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                LlmodelBox.Common.AtomicFile.WriteAllText(_globalFilePath, JsonSerializer.Serialize(records, Options));
            }
            catch { }
        }

        public void ClearGlobal()
        {
            try
            {
                if (File.Exists(_globalFilePath)) File.Delete(_globalFilePath);
            }
            catch { }
        }

        private string PathFor(string projectKey)
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
