using System;
using System.Collections.Generic;

namespace LlmodelBox.Runtime.Memory
{
    public interface IGlobalMemoryStore
    {
        List<MemoryRecord> LoadGlobal();
        void SaveGlobal(IReadOnlyList<MemoryRecord> records);
        void ClearGlobal();
    }

    // Part 5 Scoped Memory Engine. Manages 5 scopes (CurrentTurn, CurrentConversation,
    // CurrentSession, CurrentProject, UserGlobal) with explicit user-approval gating for global
    // promotion and full inspect / update / delete lifecycle operations.
    public interface IMemoryEngine
    {
        MemorySnapshot Recall(string? projectKey);
        MemorySnapshot Recall(string? projectKey, string? conversationId, string? turnId = null);

        string? BuildPreamble(MemorySnapshot snapshot);

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
