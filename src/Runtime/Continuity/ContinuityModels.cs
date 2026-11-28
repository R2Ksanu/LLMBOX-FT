using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmodelBox.Models;

namespace LlmodelBox.Runtime.Continuity
{
    public enum ConversationStatus
    {
        Active = 0,
        Interrupted = 1,
        Archived = 2,
        Deleted = 3
    }

    public enum TurnLifecycleStatus
    {
        Pending = 0,
        Streaming = 1,
        Completed = 2,
        Cancelled = 3,
        Failed = 4,
        Interrupted = 5
    }

    public enum PlanStepStatus
    {
        Pending = 0,
        InProgress = 1,
        Done = 2,
        Blocked = 3,
        Skipped = 4
    }

    public enum TestExecutionOutcome
    {
        Unknown = 0,
        NotRun = 1,
        Passed = 2,
        Failed = 3
    }

    public enum MemoryProvenance
    {
        UserExplicit = 0,
        ToolVerified = 1,
        FileDerived = 2,
        AssistantInferred = 3,
        ManualEdit = 4
    }

    public enum MemoryLifecycleStatus
    {
        Active = 0,
        Superseded = 1,
        Expired = 2,
        Rejected = 3,
        Deleted = 4
    }

    public enum MemoryCategory
    {
        General = 0,
        Preference = 1,
        Architecture = 2,
        Decision = 3,
        BugFix = 4,
        Constraint = 5,
        Workflow = 6,
        TaskState = 7
    }

    // Layer A: Conversation header
    public sealed class ConversationRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "New Conversation";
        public string ProjectKey { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public ConversationStatus Status { get; set; } = ConversationStatus.Active;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
        public string ActiveModelId { get; set; } = string.Empty;
        public string ActiveProvider { get; set; } = string.Empty;
        public string? ParentConversationId { get; set; }
        public bool IsArchived { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime? DeletedAtUtc { get; set; }
        public int MessageCount { get; set; }
        public string? LatestCheckpointSummary { get; set; }
    }

    // Layer A: Ordered message record linked to conversation and turn
    public sealed class ConversationMessageRecord
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ConversationId { get; set; } = string.Empty;
        public string TurnId { get; set; } = string.Empty;
        public int SequenceNumber { get; set; }
        public string SenderRole { get; set; } = "User";
        public string SenderName { get; set; } = "Operator";
        public string Content { get; set; } = string.Empty;
        public string StructuredBlocksJson { get; set; } = "[]";
        public string AttachmentsJson { get; set; } = "[]";
        public string ToolStepsJson { get; set; } = "[]";
        public string ModelName { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public TurnLifecycleStatus TurnStatus { get; set; } = TurnLifecycleStatus.Completed;
        public bool IsError { get; set; }
        public string ActiveFilePath { get; set; } = string.Empty;
        public int FilesModifiedCount { get; set; }
        public int AddedLinesCount { get; set; }
        public int RemovedLinesCount { get; set; }
        public double ElapsedSeconds { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public string DisplayTimestamp { get; set; } = string.Empty;

        public ChatMessageItem ToChatMessageItem()
        {
            List<ChatContentBlock>? blocks = null;
            List<string>? attachments = null;
            List<string>? toolSteps = null;
            try { blocks = JsonSerializer.Deserialize<List<ChatContentBlock>>(StructuredBlocksJson, JsonOpts); } catch { }
            try { attachments = JsonSerializer.Deserialize<List<string>>(AttachmentsJson, JsonOpts); } catch { }
            try { toolSteps = JsonSerializer.Deserialize<List<string>>(ToolStepsJson, JsonOpts); } catch { }

            var localTime = CreatedAtUtc.Kind == DateTimeKind.Utc ? CreatedAtUtc.ToLocalTime() : CreatedAtUtc;
            var item = new ChatMessageItem
            {
                Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
                ConversationId = ConversationId,
                TurnId = TurnId,
                SequenceNumber = SequenceNumber,
                SenderRole = SenderRole,
                SenderName = SenderName,
                Content = Content,
                ModelName = ModelName,
                Provider = Provider,
                IsError = IsError,
                ActiveFilePath = ActiveFilePath,
                FilesModifiedCount = FilesModifiedCount,
                AddedLinesCount = AddedLinesCount,
                RemovedLinesCount = RemovedLinesCount,
                ElapsedSeconds = ElapsedSeconds,
                CreatedAtUtc = CreatedAtUtc,
                UpdatedAtUtc = UpdatedAtUtc,
                Timestamp = !string.IsNullOrWhiteSpace(DisplayTimestamp) ? DisplayTimestamp : localTime.ToString("HH:mm:ss"),
                ImageAttachments = attachments ?? new List<string>(),
                ToolSteps = toolSteps ?? new List<string>(),
                Blocks = blocks ?? new List<ChatContentBlock>(),
                ActivityState = TurnStatus switch
                {
                    TurnLifecycleStatus.Pending => MessageActivityState.Queued,
                    TurnLifecycleStatus.Streaming => MessageActivityState.Streaming,
                    TurnLifecycleStatus.Cancelled => MessageActivityState.Cancelled,
                    TurnLifecycleStatus.Failed => MessageActivityState.Failed,
                    TurnLifecycleStatus.Interrupted => MessageActivityState.Failed,
                    _ => MessageActivityState.Completed
                },
                ActivityDetail = TurnStatus == TurnLifecycleStatus.Interrupted
                    ? "Interrupted before completion (recovered partial output)"
                    : string.Empty
            };
            return item;
        }

        public static ConversationMessageRecord FromChatMessageItem(
            ChatMessageItem msg,
            string conversationId,
            int sequenceNumber,
            TurnLifecycleStatus? overrideStatus = null)
        {
            var created = msg.CreatedAtUtc != default ? msg.CreatedAtUtc : DateTime.UtcNow;
            var updated = msg.UpdatedAtUtc != default ? msg.UpdatedAtUtc : DateTime.UtcNow;
            var status = overrideStatus ?? msg.ActivityState switch
            {
                MessageActivityState.Queued => TurnLifecycleStatus.Pending,
                MessageActivityState.Connecting or MessageActivityState.Thinking or MessageActivityState.Generating or MessageActivityState.Streaming or MessageActivityState.ExecutingTool or MessageActivityState.EditingFiles => TurnLifecycleStatus.Streaming,
                MessageActivityState.Cancelled => TurnLifecycleStatus.Cancelled,
                MessageActivityState.Failed => msg.Content.Contains("[Interrupted", StringComparison.OrdinalIgnoreCase)
                    ? TurnLifecycleStatus.Interrupted
                    : TurnLifecycleStatus.Failed,
                _ => TurnLifecycleStatus.Completed
            };

            string blocksJson = "[]";
            string attachJson = "[]";
            string stepsJson = "[]";
            try { blocksJson = JsonSerializer.Serialize(msg.Blocks ?? new List<ChatContentBlock>(), JsonOpts); } catch { }
            try { attachJson = JsonSerializer.Serialize(msg.ImageAttachments ?? new List<string>(), JsonOpts); } catch { }
            try { stepsJson = JsonSerializer.Serialize(msg.ToolSteps ?? new List<string>(), JsonOpts); } catch { }

            return new ConversationMessageRecord
            {
                Id = string.IsNullOrWhiteSpace(msg.Id) ? Guid.NewGuid().ToString("N") : msg.Id,
                ConversationId = conversationId,
                TurnId = string.IsNullOrWhiteSpace(msg.TurnId) ? msg.Id : msg.TurnId,
                SequenceNumber = sequenceNumber,
                SenderRole = msg.SenderRole ?? "User",
                SenderName = msg.SenderName ?? "Operator",
                Content = msg.Content ?? string.Empty,
                StructuredBlocksJson = blocksJson,
                AttachmentsJson = attachJson,
                ToolStepsJson = stepsJson,
                ModelName = msg.ModelName ?? string.Empty,
                Provider = msg.Provider ?? string.Empty,
                TurnStatus = status,
                IsError = msg.IsError,
                ActiveFilePath = msg.ActiveFilePath ?? string.Empty,
                FilesModifiedCount = msg.FilesModifiedCount,
                AddedLinesCount = msg.AddedLinesCount,
                RemovedLinesCount = msg.RemovedLinesCount,
                ElapsedSeconds = msg.ElapsedSeconds,
                CreatedAtUtc = created,
                UpdatedAtUtc = updated,
                DisplayTimestamp = msg.Timestamp ?? string.Empty
            };
        }
    }

    // Layer A: Execution Turn lifecycle
    public sealed class TurnRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ConversationId { get; set; } = string.Empty;
        public string ProjectKey { get; set; } = string.Empty;
        public string UserMessageId { get; set; } = string.Empty;
        public string AssistantMessageId { get; set; } = string.Empty;
        public string UserPrompt { get; set; } = string.Empty;
        public string AssistantOutput { get; set; } = string.Empty;
        public TurnLifecycleStatus Status { get; set; } = TurnLifecycleStatus.Pending;
        public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? EndedAtUtc { get; set; }
        public int CheckpointSequence { get; set; }
        public int PromptTokensEstimate { get; set; }
        public int CompletionTokensEstimate { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    // Layer A: Idempotent captured tool/file/test/lifecycle work event
    public sealed class WorkEventRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
        public string ConversationId { get; set; } = string.Empty;
        public string TurnId { get; set; } = string.Empty;
        public string ProjectKey { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty; // Prompt, ToolCompleted, FileChanged, TestResult, Compaction, Lifecycle
        public string ToolName { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // Succeeded, Failed, Cancelled, Passed
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed class PlanStepItem
    {
        public string Title { get; set; } = string.Empty;
        public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;
    }

    public sealed class TestOutcomeRecord
    {
        public string Command { get; set; } = string.Empty;
        public TestExecutionOutcome Outcome { get; set; } = TestExecutionOutcome.Unknown;
        public string Summary { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    }

    // Layer B: Durable structured handoff checkpoint ("where we stopped")
    public sealed class HandoffCheckpoint
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ConversationId { get; set; } = string.Empty;
        public string ProjectKey { get; set; } = string.Empty;
        public int CheckpointVersion { get; set; } = 1;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public string SourceTurnId { get; set; } = string.Empty;
        public ConversationStatus Status { get; set; } = ConversationStatus.Active;
        public string CurrentGoal { get; set; } = string.Empty;
        public string UserIntent { get; set; } = string.Empty;
        public List<PlanStepItem> Plan { get; set; } = new();
        public List<string> Completed { get; set; } = new();
        public List<string> Decisions { get; set; } = new();
        public List<string> FilesChanged { get; set; } = new();
        public List<TestOutcomeRecord> Tests { get; set; } = new();
        public List<string> Blockers { get; set; } = new();
        public string NextStep { get; set; } = string.Empty;
        public List<string> RelevantMemoryIds { get; set; } = new();
        public List<string> SourceMessageIds { get; set; } = new();
        public List<string> SourceEventIds { get; set; } = new();
        public double Confidence { get; set; } = 0.85;
        public MemoryProvenance EvidenceLevel { get; set; } = MemoryProvenance.ToolVerified;
        public string SummaryText { get; set; } = string.Empty;
    }

    // Compact progressive-disclosure search index entry
    public sealed class ConversationSearchHit
    {
        public string Kind { get; init; } = string.Empty; // Conversation, Message, Checkpoint, Memory, WorkEvent
        public string Id { get; init; } = string.Empty;
        public string ConversationId { get; init; } = string.Empty;
        public string ProjectKey { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Snippet { get; init; } = string.Empty;
        public double Score { get; init; }
        public DateTime TimestampUtc { get; init; }
    }

    public sealed class ProjectPreviousWorkPointer
    {
        public string ConversationId { get; init; } = string.Empty;
        public string ConversationTitle { get; init; } = string.Empty;
        public string ProjectKey { get; init; } = string.Empty;
        public ConversationStatus Status { get; init; }
        public string CurrentGoal { get; init; } = string.Empty;
        public string CompletedSummary { get; init; } = string.Empty;
        public string Blocker { get; init; } = string.Empty;
        public string NextStep { get; init; } = string.Empty;
        public DateTime UpdatedAtUtc { get; init; }
    }
}
