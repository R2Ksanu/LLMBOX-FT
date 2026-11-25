using System.Collections.Generic;
using LlmodelBox.Models;

namespace LlmodelBox.Runtime.Memory
{
    // Phase 4 (Memory Engine) foundation: durable per-project chat history. The runtime persists the
    // conversation so reopening a project — or restarting the app — restores the session instead of
    // starting from an empty chat. Keyed by a stable project identifier (its path).
    public interface IChatHistoryStore
    {
        List<ChatMessageItem> Load(string projectKey);
        void Save(string projectKey, IEnumerable<ChatMessageItem> messages);
        void Clear(string projectKey);
    }
}
