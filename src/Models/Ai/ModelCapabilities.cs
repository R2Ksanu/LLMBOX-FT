namespace LlmodelBox.Models.Ai
{
    // Normalized, provider-independent view of what a model can do. Provider adapters populate
    // this (Phase 2) so the runtime can ask "can this model stream / call tools / see images / fit
    // this context?" without knowing provider-specific request formats.
    public sealed class ModelCapabilities
    {
        public int ContextWindow { get; set; } = 8192;
        public int InputTokenLimit { get; set; } = 8192;
        public int OutputTokenLimit { get; set; } = 4096;
        public bool Streaming { get; set; }
        public bool ToolCalling { get; set; }
        public bool Vision { get; set; }
        public bool Audio { get; set; }
        public bool StructuredOutput { get; set; }
        public bool Reasoning { get; set; }
        public bool Embeddings { get; set; }

        public static ModelCapabilities Default() => new();
    }
}
