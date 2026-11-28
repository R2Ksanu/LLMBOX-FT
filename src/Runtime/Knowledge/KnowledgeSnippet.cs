namespace LlmodelBox.Runtime.Knowledge
{
    public enum KnowledgeRetrievalMode
    {
        Deterministic = 0,
        Hybrid = 1,
        DeterministicFallback = 2
    }

    // A retrieved fragment of project knowledge: path, line span, text, relevance score, and mode.
    public sealed class KnowledgeSnippet
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public double Score { get; set; }
        public int StartLine { get; set; } = 1;
        public int EndLine { get; set; } = 1;
        public string ChunkSha256 { get; set; } = string.Empty;
        public KnowledgeRetrievalMode RetrievalMode { get; set; } = KnowledgeRetrievalMode.Deterministic;
    }
}
