using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlmodelBox.Common;

namespace LlmodelBox.Runtime.Memory
{
    // JSON-file implementation of IMemoryStore. One "<hash>.mem.json" per project under
    // AppConstants.ChatHistoryDir, alongside the raw transcript. Best-effort: a missing/corrupt file
    // yields fresh, empty memory rather than throwing into the runtime.
    public sealed class JsonMemoryStore : IMemoryStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

        public ConversationMemory Load(string projectKey)
        {
            try
            {
                string path = PathFor(projectKey);
                if (!File.Exists(path)) return new ConversationMemory();
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return new ConversationMemory();
                return JsonSerializer.Deserialize<ConversationMemory>(json, Options) ?? new ConversationMemory();
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
                Directory.CreateDirectory(AppConstants.ChatHistoryDir);
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

        private static string PathFor(string projectKey)
            => Path.Combine(AppConstants.ChatHistoryDir, Hash(projectKey ?? string.Empty) + ".mem.json");

        private static string Hash(string value)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
