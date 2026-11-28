using System.Collections.Generic;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Tokens;

namespace LlmodelBox.Runtime.Context
{
    // Outcome of a context assembly pass: ready-to-route request, token budget telemetry,
    // auto-compaction handoff state, file relevance scores, deduplication count, and source report.
    public sealed class ContextBuildResult
    {
        public AiRequest Request { get; set; } = new();

        public int InputTokenEstimate { get; set; }
        public int InputBudget { get; set; }

        public int IncludedHistoryTurns { get; set; }
        public int DroppedHistoryTurns { get; set; }
        public bool OpenFileIncluded { get; set; }
        public bool OpenFileTruncated { get; set; }

        // Part 4 Token Budget breakdown & confidence metadata.
        public TokenBudgetReport? BudgetReport { get; set; }

        // Part 5 Context Intelligence & Auto-Compact telemetry.
        public bool AutoCompacted { get; set; }
        public string CompactionReason { get; set; } = "None";
        public string? UpdatedConversationSummary { get; set; }
        public int DeduplicatedItemsCount { get; set; }
        public List<ScoredFileResult> ScoredFiles { get; set; } = new();
        public List<ContextSourceEntry> SourceReport { get; set; } = new();
    }
}
