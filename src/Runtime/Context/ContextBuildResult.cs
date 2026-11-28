using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Tokens;

namespace LlmodelBox.Runtime.Context
{
    // Outcome of a context assembly pass: the ready-to-route request plus what the engine had to do
    // to fit the model's budget (surfaced for telemetry / the Neural Net view).
    public sealed class ContextBuildResult
    {
        public AiRequest Request { get; set; } = new();

        // Estimated input tokens actually assembled (system + history + open file + user turn).
        public int InputTokenEstimate { get; set; }

        // Token budget the engine targeted for the input (context window minus an output reserve).
        public int InputBudget { get; set; }

        public int IncludedHistoryTurns { get; set; }
        public int DroppedHistoryTurns { get; set; }
        public bool OpenFileIncluded { get; set; }
        public bool OpenFileTruncated { get; set; }

        // Part 4 Token Budget breakdown & confidence metadata.
        public TokenBudgetReport? BudgetReport { get; set; }
    }
}
