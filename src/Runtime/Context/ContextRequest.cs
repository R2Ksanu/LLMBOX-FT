using System.Collections.Generic;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Runtime.Context
{
    // Raw materials the UI/session hands to the Context Engine. The engine — not the UI — decides how
    // these are prioritized, deduplicated, auto-compacted, and budgeted into the final AiRequest.
    public sealed class ContextRequest
    {
        public ModelItem Model { get; set; } = new();
        public UserSettings Settings { get; set; } = new();

        public string UserPrompt { get; set; } = string.Empty;
        public string? SystemInstruction { get; set; }

        // Prior conversation turns, oldest-first.
        public List<AiMessage> History { get; set; } = new();

        // Active open-file context and optional scored secondary candidate files.
        public string? OpenFilePath { get; set; }
        public string? OpenFileText { get; set; }
        public List<CandidateFileContext> CandidateFiles { get; set; } = new();

        public List<string> Attachments { get; set; } = new();

        public string? ProjectKey { get; set; }
        public string? ConversationId { get; set; }

        public string? MemoryPreamble { get; set; }
        public string? KnowledgeBlock { get; set; }

        public string? ConversationSummary { get; set; }
        public List<AiToolDefinition> ToolSchemas { get; set; } = new();

        // Part 5 Auto-Compact configuration & manual steering focus.
        public bool EnableAutoCompact { get; set; } = true;
        public bool ForceCompact { get; set; } = false;
        public double? AutoCompactThresholdRatio { get; set; }
        public string? CompactFocusInstruction { get; set; }

        public string? SessionId { get; set; }
        public string? TaskId { get; set; }
    }
}
