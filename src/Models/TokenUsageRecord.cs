using System;

namespace LlmodelBox.Models
{
    // One recorded model call's token accounting. Persisted so usage analytics
    // survive restarts.
    public class TokenUsageRecord
    {
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string Provider { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }

        // Wall-clock time for the HTTP round-trip of this call, in milliseconds.
        // 0 when timing was not captured (e.g. legacy records).
        public long LatencyMs { get; set; }

        // Output throughput: completion tokens produced per second of wall-clock time.
        // Non-streaming, so this is an average over the whole response, not a live rate.
        public double TokensPerSecond =>
            LatencyMs > 0 && CompletionTokens > 0 ? CompletionTokens / (LatencyMs / 1000.0) : 0.0;

        // True when token count was extracted directly from provider HTTP API response
        public bool IsLiveApi { get; set; } = true;
    }
}
