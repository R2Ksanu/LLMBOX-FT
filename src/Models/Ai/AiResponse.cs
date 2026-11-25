using System.Collections.Generic;
using LlmodelBox.Models;

namespace LlmodelBox.Models.Ai
{
    // Normalized, provider-independent generation result. Provider adapters produce this so no UI
    // or runtime code needs to parse provider-specific response shapes.
    public sealed class AiResponse
    {
        public string Text { get; set; } = string.Empty;
        public string? Reasoning { get; set; }
        public bool IsError { get; set; }
        public string? ErrorMessage { get; set; }
        public string? FinishReason { get; set; }
        public TokenUsageRecord? Usage { get; set; }
        public long LatencyMs { get; set; }
        public string ModelId { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public List<AiToolCall> ToolCalls { get; set; } = new();

        // Context Engine (Phase 3) estimate of the assembled input token count. Used for token
        // accounting when the provider omits authoritative live usage. 0 when not assembled by the engine.
        public int PromptTokensEstimate { get; set; }

        public List<ChatContentBlock> StructuredBlocks { get; set; } = new();

        public static AiResponse Error(string message) =>
            new() { IsError = true, ErrorMessage = message, Text = message };
    }
}
