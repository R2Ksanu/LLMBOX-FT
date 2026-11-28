using System;
using System.Collections.Generic;
using LlmodelBox.Runtime.Memory;

namespace LlmodelBox.Runtime.Continuity
{
    public interface IConversationRepository : IDisposable
    {
        PersistenceDiagnosticReport GetDiagnostics();
        event Action<PersistenceDiagnosticReport>? DiagnosticsChanged;
        bool RetryPendingWrites();
        string? CreateBackup(string? destinationDirectory = null);

        // Layer A: Conversations, Messages, Turns, Work Events
        ConversationRecord UpsertConversation(ConversationRecord conversation);
        ConversationRecord? GetConversation(string conversationId);
        IReadOnlyList<ConversationRecord> ListConversations(
            string? projectKey = null,
            bool includeArchived = false,
            bool includeDeleted = false,
            int offset = 0,
            int limit = int.MaxValue);

        void ArchiveConversation(string conversationId, bool archived);
        void SoftDeleteConversation(string conversationId, bool deleteDerivedMemories = false);
        void RestoreConversation(string conversationId);
        void DeleteConversationPermanently(string conversationId, bool deleteDerivedMemories = false);

        void ReplaceMessages(string conversationId, IReadOnlyList<ConversationMessageRecord> messages);
        void UpsertMessage(ConversationMessageRecord message);
        IReadOnlyList<ConversationMessageRecord> GetMessages(string conversationId, int offset = 0, int limit = int.MaxValue);
        IReadOnlyList<ConversationMessageRecord> GetMessagesAround(string messageId, int beforeCount = 4, int afterCount = 4);

        void UpsertTurn(TurnRecord turn);
        IReadOnlyList<TurnRecord> GetTurns(string conversationId);
        int RecoverInterruptedTurns();

        bool RecordWorkEvent(WorkEventRecord workEvent);
        IReadOnlyList<WorkEventRecord> GetWorkEvents(string conversationId, int limit = 100);

        // Layer B: Durable Handoff Checkpoints & Previous-Work Pointers
        void SaveCheckpoint(HandoffCheckpoint checkpoint);
        HandoffCheckpoint? GetLatestCheckpoint(string conversationId);
        ProjectPreviousWorkPointer? GetLatestProjectWorkPointer(string projectKey, string? excludeConversationId = null);

        // Layer C: Scoped Long-Term Memory
        MemoryRecord UpsertMemory(MemoryRecord record);
        MemoryRecord? GetMemory(string id);
        IReadOnlyList<MemoryRecord> QueryMemories(
            MemoryScope? scope = null,
            string? projectKey = null,
            string? conversationId = null,
            string? turnId = null,
            bool includeInactive = false);
        bool DeleteMemory(string id);
        int ForgetProjectMemory(string projectKey);
        int DeleteAllLocalContinuityData();

        // Layer E: Progressive Disclosure FTS Search
        IReadOnlyList<ConversationSearchHit> Search(
            string query,
            string? projectKey = null,
            bool crossProject = false,
            int limit = 20);

        // Idempotent Migration Ledger
        bool IsMigrationRecorded(string sourceKey);
        void RecordMigration(string sourceKey, string sourceKind);
    }
}
