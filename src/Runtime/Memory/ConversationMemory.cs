using System;
using System.Collections.Generic;
using System.Linq;

namespace LlmodelBox.Runtime.Memory
{
    /// <summary>
    /// Scoped memory tiers ordered from shortest-lived (single turn) to longest-lived (approved global).
    /// </summary>
    public enum MemoryScope
    {
        CurrentTurn = 0,
        CurrentConversation = 1,
        CurrentSession = 2,
        CurrentProject = 3,
        UserGlobal = 4
    }

    /// <summary>
    /// Structured, user-inspectable memory entry with lifecycle, provenance, and confidence metadata.
    /// </summary>
    public sealed class MemoryRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public string Content { get; set; } = string.Empty;
        public MemoryScope Scope { get; set; } = MemoryScope.CurrentProject;
        public string Source { get; set; } = "user_manual";
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public double Confidence { get; set; } = 1.0;
        public DateTime? ExpiresUtc { get; set; }
        public bool IsUserEditable { get; set; } = true;

        public string? ProjectKey { get; set; }
        public string? ConversationId { get; set; }
        public string? TurnId { get; set; }

        public bool IsExpired(DateTime? nowUtc = null)
            => ExpiresUtc.HasValue && ExpiresUtc.Value <= (nowUtc ?? DateTime.UtcNow);

        public MemoryRecord Clone() => new()
        {
            Id = Id,
            Content = Content,
            Scope = Scope,
            Source = Source,
            TimestampUtc = TimestampUtc,
            UpdatedUtc = UpdatedUtc,
            Confidence = Confidence,
            ExpiresUtc = ExpiresUtc,
            IsUserEditable = IsUserEditable,
            ProjectKey = ProjectKey,
            ConversationId = ConversationId,
            TurnId = TurnId
        };
    }

    // Durable, per-project memory the runtime accumulates across turns and sessions. Unlike the raw
    // chat transcript (IChatHistoryStore), this is the *distilled* state that survives history
    // compaction: the original session goal, structured MemoryRecords, and backward-compatible Facts.
    public sealed class ConversationMemory
    {
        // The opening objective of the session — preserved even after old turns are dropped.
        public string? SessionGoal { get; set; }

        // Salient, de-duplicated facts (kept synced for backward compatibility & legacy UI readers).
        public List<string> Facts { get; set; } = new();

        // Part 5 structured memory records for this project.
        public List<MemoryRecord> Records { get; set; } = new();

        public int TurnCount { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Ensures legacy Facts are represented as structured MemoryRecords and removes expired entries.
        /// </summary>
        public void SynchronizeRecords(string? projectKey = null, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            Records ??= new List<MemoryRecord>();
            Facts ??= new List<string>();

            // Migrate legacy string facts into structured MemoryRecords once.
            foreach (var f in Facts)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                if (!Records.Any(r => string.Equals(r.Content, f, StringComparison.OrdinalIgnoreCase)))
                {
                    Records.Add(new MemoryRecord
                    {
                        Id = DeterministicId("proj", f),
                        Content = f.Trim(),
                        Scope = MemoryScope.CurrentProject,
                        Source = "legacy_fact",
                        TimestampUtc = UpdatedUtc == default ? now : UpdatedUtc,
                        UpdatedUtc = now,
                        Confidence = 0.78,
                        IsUserEditable = true,
                        ProjectKey = projectKey
                    });
                }
            }

            Records.RemoveAll(r => string.IsNullOrWhiteSpace(r.Content) || r.IsExpired(now));

            // Keep legacy Facts projected from non-expired project records so callers reading Facts stay accurate.
            Facts.Clear();
            foreach (var rec in Records.Where(r => r.Scope == MemoryScope.CurrentProject))
            {
                if (!Facts.Any(x => string.Equals(x, rec.Content, StringComparison.OrdinalIgnoreCase)))
                    Facts.Add(rec.Content);
            }
        }

        internal static string DeterministicId(string prefix, string content)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in content.Trim().ToLowerInvariant())
                {
                    h ^= c;
                    h *= 16777619;
                }
                return $"{prefix}_{h:x8}";
            }
        }
    }

    // Read-only view handed to callers so they cannot mutate persisted state directly.
    public sealed class MemorySnapshot
    {
        public string? SessionGoal { get; init; }
        public IReadOnlyList<string> Facts { get; init; } = Array.Empty<string>();
        public IReadOnlyList<MemoryRecord> ScopedRecords { get; init; } = Array.Empty<MemoryRecord>();

        public bool IsEmpty => string.IsNullOrWhiteSpace(SessionGoal)
                            && Facts.Count == 0
                            && ScopedRecords.Count == 0;
    }
}
