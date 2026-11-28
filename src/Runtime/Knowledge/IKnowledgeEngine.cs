using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LlmodelBox.Runtime.Knowledge
{
    public interface IKnowledgeEmbeddingProvider
    {
        string ProviderId { get; }
        bool IsConfigured { get; }
        Task<IReadOnlyList<float[]?>> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
    }

    // Part 5 upgraded Knowledge Engine: incremental SHA-256 content hashing, configurable
    // exclusions, overlapping line-tracked code chunks, and optional embedding-based hybrid
    // retrieval with automatic deterministic BM25 fallback.
    public interface IKnowledgeEngine
    {
        KnowledgeIndex Index(string? projectRoot);
        KnowledgeIndex Index(string? projectRoot, KnowledgeIndexOptions? options);

        IReadOnlyList<KnowledgeSnippet> Retrieve(KnowledgeIndex index, string? query, int maxSnippets);

        Task<IReadOnlyList<KnowledgeSnippet>> RetrieveAsync(
            KnowledgeIndex index,
            string? query,
            int maxSnippets,
            IKnowledgeEmbeddingProvider? embeddingProvider = null,
            CancellationToken cancellationToken = default);

        string? BuildKnowledgeBlock(IReadOnlyList<KnowledgeSnippet> snippets, int maxChars);
    }
}
