using System;
using System.Collections.Generic;

namespace LlmodelBox.Runtime.Memory
{
    // Durable, per-project memory the runtime accumulates across turns and sessions. Unlike the raw
    // chat transcript (IChatHistoryStore), this is the *distilled* state that survives history
    // compaction: the original session goal and a small set of salient facts about the user/project.
    public sealed class ConversationMemory
    {
        // The opening objective of the session — preserved even after old turns are dropped.
        public string? SessionGoal { get; set; }

        // Salient, de-duplicated facts (e.g. "User's name is Alex", "Prefers Rust").
        public List<string> Facts { get; set; } = new();

        public int TurnCount { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    // Read-only view handed to callers so they cannot mutate persisted state directly.
    public sealed class MemorySnapshot
    {
        public string? SessionGoal { get; init; }
        public IReadOnlyList<string> Facts { get; init; } = Array.Empty<string>();

        public bool IsEmpty => string.IsNullOrWhiteSpace(SessionGoal) && Facts.Count == 0;
    }
}
