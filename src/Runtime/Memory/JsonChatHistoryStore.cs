using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlmodelBox.Common;
using LlmodelBox.Models;

namespace LlmodelBox.Runtime.Memory
{
    // JSON-file implementation of the chat history store. One file per project under
    // AppConstants.ChatHistoryDir, named by a stable hash of the project key so arbitrary project
    // paths map to safe filenames. All operations are best-effort: a corrupt or missing file yields
    // an empty history rather than throwing into the UI.
    public sealed class JsonChatHistoryStore : IChatHistoryStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

        public List<ChatMessageItem> Load(string projectKey)
        {
            try
            {
                string path = PathFor(projectKey);
                if (!File.Exists(path)) return new List<ChatMessageItem>();
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return new List<ChatMessageItem>();
                var list = JsonSerializer.Deserialize<List<ChatMessageItem>>(json, Options);
                return list ?? new List<ChatMessageItem>();
            }
            catch
            {
                return new List<ChatMessageItem>();
            }
        }

        public void Save(string projectKey, IEnumerable<ChatMessageItem> messages)
        {
            try
            {
                Directory.CreateDirectory(AppConstants.ChatHistoryDir);
                string json = JsonSerializer.Serialize(messages.ToList(), Options);
                LlmodelBox.Common.AtomicFile.WriteAllText(PathFor(projectKey), json);
            }
            catch
            {
                // Persistence is best-effort; never break the chat flow on a disk error.
            }
        }

        public void Clear(string projectKey)
        {
            try
            {
                string path = PathFor(projectKey);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Ignore — nothing to restore is an acceptable outcome.
            }
        }

        private static string PathFor(string projectKey)
        {
            string safe = Hash(projectKey ?? string.Empty);
            return Path.Combine(AppConstants.ChatHistoryDir, safe + ".json");
        }

        private static string Hash(string value)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
