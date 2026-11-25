namespace LlmodelBox.Runtime.Knowledge
{
    // A single retrieved fragment of project knowledge: where it came from, the text itself, and the
    // relevance score the engine assigned it for the current query. Kept tiny and immutable-ish so it
    // is trivial to test and to render on the bus.
    public sealed class KnowledgeSnippet
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public double Score { get; set; }
    }
}
