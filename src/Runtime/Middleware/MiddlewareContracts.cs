using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Context;
using LlmodelBox.Runtime.Intent;
using LlmodelBox.Runtime.Tokens;

namespace LlmodelBox.Runtime.Middleware
{
    // 1. AI Orchestrator (bridges cleanly to existing IAiRuntime)
    public interface IAiOrchestrator : IAiRuntime
    {
    }

    // 2. Conversation Engine
    public interface IConversationEngine
    {
        List<AiMessage> BuildHistoryWindow(IEnumerable<ChatMessageItem> messages, int maxTurns = 24);
        string DeriveThreadTitle(string firstUserPrompt, int maxLength = 48);
    }

    // 4. Task Planning Engine
    public sealed class ExecutionPlanStep
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = string.Empty;
        public List<string> DependsOn { get; set; } = new();
        public AiTaskState State { get; set; } = AiTaskState.Created;
    }

    public sealed class ExecutionPlan
    {
        public string Goal { get; set; } = string.Empty;
        public List<ExecutionPlanStep> Steps { get; set; } = new();
    }

    public interface ITaskPlanningEngine
    {
        ExecutionPlan BuildPlan(string userPrompt, IntentResult intent);
    }

    // 7. Token Intelligence Engine
    public sealed class TokenReconciliationReport
    {
        public int PromptTokens { get; init; }
        public int CompletionTokens { get; init; }
        public int TotalTokens { get; init; }
        public int CachedTokens { get; init; }
        public int ReasoningTokens { get; init; }
        public int EstimatedPromptTokens { get; init; }
        public int ReconciliationDeltaTokens => PromptTokens - EstimatedPromptTokens;
        public bool IsProviderAuthoritative { get; init; }
        public TokenCountConfidence Confidence { get; init; }
        public string SourceLabel { get; init; } = "Estimated";
        public TokenCostBreakdown? CostBreakdown { get; init; }
    }

    public interface ITokenIntelligenceEngine
    {
        int EstimateTokens(string? text, ModelItem? model = null);
        TokenCountResult CountWithConfidence(string? text, ModelItem? model = null);
        IModelTokenCounter ResolveCounter(ModelItem? model);
        TokenBudgetReport AllocateBudget(TokenBudgetRequest request);
        TokenReconciliationReport Reconcile(
            string? promptText,
            string? responseText,
            TokenUsageRecord? liveUsage,
            int contextPromptEstimate = 0,
            ModelItem? model = null);
    }

    // 8. Context Compression Engine
    public sealed class CompressionResult
    {
        public List<AiMessage> KeptMessages { get; init; } = new();
        public string? SummaryNote { get; init; }
        public int DroppedCount { get; init; }
    }

    public interface IContextCompressionEngine
    {
        CompressionResult CompressHistory(IReadOnlyList<AiMessage> history, int maxHistoryTokens);
    }

    // 12. Response Normalization Engine
    public sealed class NormalizedAssistantTurn
    {
        public string VisibleText { get; init; } = string.Empty;
        public string ReasoningSummary { get; init; } = string.Empty;
        public List<ChatContentBlock> Blocks { get; init; } = new();
        public bool IsError { get; init; }
    }

    public interface IResponseNormalizationEngine
    {
        NormalizedAssistantTurn Normalize(string? rawText, bool isError, IEnumerable<ChatContentBlock>? runtimeBlocks = null);
    }

    // 13. Verification Engine
    public sealed class VerificationReport
    {
        public bool Passed { get; init; }
        public List<string> Checks { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
    }

    public interface IVerificationEngine
    {
        VerificationReport VerifyTurn(AiResponse response, string? workspaceRoot);
    }

    // 14. Checkpoint and Recovery Engine
    public sealed class FileCheckpointEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public bool ExistedBefore { get; set; }
        public string? PreviousContent { get; set; }
    }

    public sealed class TurnCheckpoint
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Label { get; set; } = string.Empty;
        public string WorkspaceRoot { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public List<FileCheckpointEntry> Files { get; set; } = new();
    }

    public interface ICheckpointEngine
    {
        TurnCheckpoint CreateCheckpoint(string label, string workspaceRoot);
        void CaptureBeforeEdit(TurnCheckpoint checkpoint, string filePath);
        IReadOnlyList<TurnCheckpoint> GetCheckpoints(string workspaceRoot);
        Task<int> RestoreCheckpointAsync(string checkpointId, CancellationToken ct = default);
    }

    // 15. Real-Time Event Engine
    public sealed class MiddlewareEvent
    {
        public long Sequence { get; init; }
        public string ConversationId { get; init; } = string.Empty;
        public string TurnId { get; init; } = string.Empty;
        public MessageActivityState State { get; init; }
        public string Detail { get; init; } = string.Empty;
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    }

    public interface IRealtimeEventEngine
    {
        event Action<MiddlewareEvent>? EventPublished;
        void Publish(string conversationId, string turnId, MessageActivityState state, string detail);
        IReadOnlyList<MiddlewareEvent> GetRecentEvents(int maxCount = 50);
    }
}
