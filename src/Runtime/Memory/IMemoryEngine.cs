using System;
using System.Collections.Generic;
using LlmodelBox.Runtime.Continuity;

namespace LlmodelBox.Runtime.Memory
{
    public interface IGlobalMemoryStore
    {
        List<MemoryRecord> LoadGlobal();
        void SaveGlobal(IReadOnlyList<MemoryRecord> records);
        void ClearGlobal();
    }

    public interface IMemoryEngine
    {
        PersistenceDiagnosticReport GetDiagnostics();

        MemorySnapshot Recall(string? projectKey);
        MemorySnapshot Recall(string? projectKey, string? conversationId, string? turnId = null);

        string? BuildPreamble(MemorySnapshot snapshot);
        string? BuildPreamble(MemorySnapshot snapshot, string? query, int maxChars = 1200, string? activeConversationId = null);

        void Observe(string? projectKey, string userText, string assistantText);
        void Observe(string? projectKey, string? conversationId, string userText, string assistantText);

        void Forget(string? projectKey);

        IReadOnlyList<MemoryRecord> Inspect(
            MemoryScope? scope = null,
            string? projectKey = null,
            string? conversationId = null,
            string? turnId = null,
            bool includeExpired = false);

        MemoryRecord AddOrUpdate(MemoryRecord record, bool explicitUserApproval = false);

        bool UpdateRecord(
            string id,
            string newContent,
            MemoryScope? newScope = null,
            DateTime? expiresUtc = null,
            string? projectKey = null,
            string? conversationId = null,
            bool explicitUserApproval = true);

        bool SetPinned(string id, bool pinned, string? projectKey = null, string? conversationId = null);

        bool DeleteRecord(string id, string? projectKey = null, string? conversationId = null);

        bool Promote(
            string id,
            MemoryScope targetScope,
            string? projectKey = null,
            string? conversationId = null,
            bool explicitUserApproval = true);

        int PruneExpired(string? projectKey = null);
        void ClearTurn(string? turnId = null);
        void ClearConversation(string conversationId);
        void ClearSession();
    }
}
