using System.Collections.Generic;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Runtime.Context
{
    // Raw materials the UI/session hands to the Context Engine. The engine — not the UI — decides how
    // these are assembled and budgeted into the final provider-independent AiRequest. Keeping assembly
    // here (out of the UI) makes context construction testable, reusable and provider-agnostic.
    public sealed class ContextRequest
    {
        public ModelItem Model { get; set; } = new();
        public UserSettings Settings { get; set; } = new();

        // The live user turn.
        public string UserPrompt { get; set; } = string.Empty;

        // Optional persistent system instruction to account for in the budget. Delivery of the system
        // instruction itself is handled by the transport (CloudApiService resolves it from settings),
        // so the engine only reserves its token cost — it does not duplicate it into the message list.
        public string? SystemInstruction { get; set; }

        // Prior conversation turns, oldest-first. The engine drops the OLDEST turns first to fit budget.
        public List<AiMessage> History { get; set; } = new();

        // Optional open-file context (truncated to fit the remaining budget).
        public string? OpenFilePath { get; set; }
        public string? OpenFileText { get; set; }

        // Display names of attachments referenced by the turn.
        public List<string> Attachments { get; set; } = new();

        // Stable per-project key used by the Memory Engine to recall/persist durable memory.
        public string? ProjectKey { get; set; }

        // Optional preamble (durable facts + session goal) the Memory Engine wants injected ahead of
        // the live turn. The Context Engine reserves budget for it and prepends it to the user turn.
        public string? MemoryPreamble { get; set; }

        // Knowledge Engine snippets and Memory Engine facts.
        public string? KnowledgeBlock { get; set; }

        // Optional summary of compressed earlier turns & advertised tool schemas for budget tracking.
        public string? ConversationSummary { get; set; }
        public List<AiToolDefinition> ToolSchemas { get; set; } = new();

        public string? SessionId { get; set; }
        public string? TaskId { get; set; }
    }
}
