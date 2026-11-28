using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmodelBox.Common;

namespace LlmodelBox.Runtime.Knowledge
{
    public sealed class KnowledgeIndexOptions
    {
        public int ChunkMaxChars { get; set; } = 1400;
        public int ChunkOverlapLines { get; set; } = 4;
        public int MaxFileBytes { get; set; } = 512 * 1024;
        public int MaxFiles { get; set; } = 1200;
        public int MaxChunks { get; set; } = 4000;
        public bool RespectIgnoreFiles { get; set; } = true;
        public bool PersistToDisk { get; set; } = true;
        public List<string> ExcludedGlobs { get; set; } = new();
        public List<string> ExtraExcludedDirectories { get; set; } = new();

        public string ComputeOptionsHash()
        {
            string raw = $"{ChunkMaxChars}|{ChunkOverlapLines}|{MaxFileBytes}|{RespectIgnoreFiles}|{string.Join(",", ExcludedGlobs)}|{string.Join(",", ExtraExcludedDirectories)}";
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes)[..16];
        }
    }

    public sealed class KnowledgeFileManifestEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public string ContentSha256 { get; set; } = string.Empty;
        public long LastWriteUtcTicks { get; set; }
        public long Length { get; set; }
    }

    public sealed class KnowledgeIndexStats
    {
        public int TotalFiles { get; set; }
        public int ReusedFiles { get; set; }
        public int ReindexedFiles { get; set; }
        public int RemovedFiles { get; set; }
        public long DurationMs { get; set; }
        public bool CacheHitExact { get; set; }
    }

    public sealed class KnowledgeChunk
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public int StartLine { get; set; } = 1;
        public int EndLine { get; set; } = 1;
        public string ChunkSha256 { get; set; } = string.Empty;
        public string FileSha256 { get; set; } = string.Empty;
        public Dictionary<string, int> TermFrequency { get; set; } = new();
        public int TokenCount { get; set; }
        public float[]? Embedding { get; set; }
    }

    public sealed class KnowledgeIndex
    {
        public string Root { get; set; } = string.Empty;
        public DateTime BuiltUtc { get; set; }
        public long Signature { get; set; }
        public string OptionsHash { get; set; } = string.Empty;

        public Dictionary<string, KnowledgeFileManifestEntry> FileManifest { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<KnowledgeChunk> Chunks { get; set; } = new();
        public Dictionary<string, int> DocumentFrequency { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        public KnowledgeIndexStats LastStats { get; set; } = new();

        public int DocumentCount => Chunks.Count;
        public bool IsEmpty => Chunks.Count == 0;
        public double AverageChunkTokens
        {
            get
            {
                if (Chunks.Count == 0) return 1.0;
                long total = 0;
                foreach (var c in Chunks) total += c.TokenCount;
                return Math.Max(1.0, (double)total / Chunks.Count);
            }
        }
    }

    public interface IKnowledgeIndexStore
    {
        KnowledgeIndex? Load(string projectRoot);
        void Save(KnowledgeIndex index);
    }

    public sealed class JsonKnowledgeIndexStore : IKnowledgeIndexStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
        private readonly string _cacheDir;

        public JsonKnowledgeIndexStore()
            : this(Path.Combine(AppConstants.DataDirectory, "cache", "knowledge"))
        {
        }

        public JsonKnowledgeIndexStore(string cacheDir)
        {
            _cacheDir = string.IsNullOrWhiteSpace(cacheDir)
                ? Path.Combine(AppConstants.DataDirectory, "cache", "knowledge")
                : cacheDir;
        }

        public KnowledgeIndex? Load(string projectRoot)
        {
            try
            {
                string path = PathFor(projectRoot);
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<KnowledgeIndex>(json, Options);
            }
            catch
            {
                return null;
            }
        }

        public void Save(KnowledgeIndex index)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(index.Root)) return;
                Directory.CreateDirectory(_cacheDir);
                AtomicFile.WriteAllText(PathFor(index.Root), JsonSerializer.Serialize(index, Options));
            }
            catch { }
        }

        private string PathFor(string root)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant()));
            return Path.Combine(_cacheDir, Convert.ToHexString(bytes)[..24].ToLowerInvariant() + ".idx.json");
        }
    }
}
