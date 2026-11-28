using System;
using System.Collections.Generic;

namespace LlmodelBox.Services
{
    public interface ITokenUsageService
    {
        event Action<Models.TokenUsageRecord>? UsageRecorded;
        void Record(Models.TokenUsageRecord record);
        TokenUsageSummary GetSummary();
        void Clear();
    }

    // Aggregated view of recorded token usage, ready for the dashboard.
    public class TokenUsageSummary
    {
        public long TotalTokens { get; set; }
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public int CallCount { get; set; }
        public List<UsageBucket> ByProvider { get; set; } = new();
        public List<UsageBucket> TopModels { get; set; } = new();
        public List<DayBucket> ByDay { get; set; } = new();

        // Per-model latency/throughput, for the performance graph. Ordered by call count desc.
        public List<ModelPerfBucket> ModelPerf { get; set; } = new();

        // Overall timing across all calls that reported latency.
        public double AvgLatencyMs { get; set; }
        public double AvgTokensPerSecond { get; set; }

        // ---- Advanced analytics ----
        public double AvgTokensPerCall { get; set; }
        public double PromptShare { get; set; }         // prompt / total, 0..1
        public double CompletionShare { get; set; }     // completion / total, 0..1
        public int LiveCallCount { get; set; }          // usage taken from provider response
        public int EstimatedCallCount { get; set; }     // local tokenizer/heuristic fallback
        public long TotalCachedTokens { get; set; }
        public long TotalReasoningTokens { get; set; }
        public double TotalInputCostUsd { get; set; }
        public double TotalOutputCostUsd { get; set; }
        public double TotalEstimatedCostUsd { get; set; }
        public bool HasCostData => TotalEstimatedCostUsd > 0 || TotalInputCostUsd > 0 || TotalOutputCostUsd > 0;
        public double PeakTokensPerSecond { get; set; }
        public long TokensToday { get; set; }
        public long Tokens7Days { get; set; }
        public long PeakDayTokens { get; set; }
        public DateTime? PeakDay { get; set; }
        public string TopModelLabel { get; set; } = string.Empty;
        public List<HourBucket> ByHour { get; set; } = new();

        public bool HasData => CallCount > 0;
    }

    // Tokens aggregated by hour-of-day (0..23, UTC).
    public class HourBucket
    {
        public int Hour { get; set; }
        public long Tokens { get; set; }
        public int Calls { get; set; }
    }

    // One model's aggregated timing: average round-trip latency and output throughput.
    public class ModelPerfBucket
    {
        public string Label { get; set; } = string.Empty;
        public int Calls { get; set; }
        public long Tokens { get; set; }
        public double AvgLatencyMs { get; set; }
        public double AvgTokensPerSecond { get; set; }
    }

    public class UsageBucket
    {
        public string Label { get; set; } = string.Empty;
        public long Tokens { get; set; }
        public int Calls { get; set; }
    }

    public class DayBucket
    {
        public DateTime Day { get; set; }
        public long Tokens { get; set; }
    }
}
