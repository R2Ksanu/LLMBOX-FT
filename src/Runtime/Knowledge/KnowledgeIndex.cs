using System;
using System.Collections.Generic;

namespace LlmodelBox.Runtime.Knowledge
{
    // A lightweight, in-memory index of a project's text/code files. Deterministic and dependency-free:
    // each file is split into fixed-size chunks, each chunk carries a term-frequency map, and the index
    // keeps document frequencies so retrieval can weight rare terms higher (TF-IDF-lite). Honest scope:
    // this is keyword/term-overlap retrieval, NOT semantic embeddings — it favours precision on shared
    // vocabulary and never reaches the network or a model.
    public sealed class KnowledgeChunk
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        // term -> occurrences within this chunk.
        public Dictionary<string, int> TermFrequency { get; set; } = new();
        // Number of tokens in the chunk (for length-normalised scoring).
        public int TokenCount { get; set; }
    }

    public sealed class KnowledgeIndex
    {
        public string Root { get; set; } = string.Empty;
        public DateTime BuiltUtc { get; set; }
        // Cache-invalidation signature: the newest file write time + file count seen at build time.
        public long Signature { get; set; }

        public List<KnowledgeChunk> Chunks { get; } = new();
        // term -> number of chunks that contain it (document frequency).
        public Dictionary<string, int> DocumentFrequency { get; } = new();

        public int DocumentCount => Chunks.Count;
        public bool IsEmpty => Chunks.Count == 0;
    }
}
