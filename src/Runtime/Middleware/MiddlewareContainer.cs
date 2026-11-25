using LlmodelBox.Runtime.Context;
using LlmodelBox.Runtime.Intent;
using LlmodelBox.Runtime.Knowledge;
using LlmodelBox.Runtime.Memory;
using LlmodelBox.Runtime.Tools;

namespace LlmodelBox.Runtime.Middleware
{
    /// <summary>
    /// Aggregates all 15 Layer 2 AI Intelligence Middleware engines into one injectable composition unit.
    /// </summary>
    public sealed class MiddlewareContainer
    {
        public required IAiRuntime Orchestrator { get; init; }
        public required IConversationEngine Conversation { get; init; }
        public required IIntentEngine Intent { get; init; }
        public required ITaskPlanningEngine TaskPlanner { get; init; }
        public required IMemoryEngine Memory { get; init; }
        public required IContextEngine Context { get; init; }
        public required ITokenIntelligenceEngine TokenIntelligence { get; init; }
        public required IContextCompressionEngine ContextCompression { get; init; }
        public required IKnowledgeEngine Knowledge { get; init; }
        public required ToolRegistry ToolRegistry { get; init; }
        public required IToolExecutor ToolExecutor { get; init; }
        public required IToolPermissionEngine Permissions { get; init; }
        public required IResponseNormalizationEngine ResponseNormalization { get; init; }
        public required IVerificationEngine Verification { get; init; }
        public required ICheckpointEngine Checkpoints { get; init; }
        public required IRealtimeEventEngine RealtimeEvents { get; init; }
    }
}
