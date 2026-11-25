using System.Collections.Generic;

namespace LlmodelBox.Runtime.Knowledge
{
    // Phase 6 — the Knowledge Engine. Indexes a project's text/code files and retrieves the fragments
    // most relevant to the live user turn, so the Context Engine can fold real project knowledge into
    // the prompt (retrieval-augmented context) WITHOUT a model round-trip or any network access.
    public interface IKnowledgeEngine
    {
        // Build (or return a cached) index for the given project root. Missing/invalid roots yield an
        // empty index rather than throwing — callers can always index unconditionally.
        KnowledgeIndex Index(string? projectRoot);

        // Rank the index against a query and return the top snippets, highest score first.
        IReadOnlyList<KnowledgeSnippet> Retrieve(KnowledgeIndex index, string? query, int maxSnippets);

        // Assemble a budgeted, human-readable knowledge block to prepend to the live turn, or null when
        // nothing relevant was found. maxChars caps the block so it cannot dominate the context budget.
        string? BuildKnowledgeBlock(IReadOnlyList<KnowledgeSnippet> snippets, int maxChars);
    }
}
