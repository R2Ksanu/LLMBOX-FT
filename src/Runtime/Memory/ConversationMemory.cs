using System;
using System.Collections.Generic;
using System.Linq;
using LlmodelBox.Runtime.Continuity;

namespace LlmodelBox.Runtime.Memory
{
    public enum MemoryScope
    {
        CurrentTurn = 0,
        CurrentConversation = 1,
        CurrentSession = 2,
        CurrentProject = 3,
        UserGlobal = 4
    }

    public sealed class MemoryRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public string Content { get; set; } = string.Empty;
        public MemoryScope Scope { get; set; } = MemoryScope.CurrentProject;
        public MemoryCategory Category { get; set; } = MemoryCategory.General;
        public MemoryLifecycleStatus Status { get; set; } = MemoryLifecycleStatus.Active;
        public MemoryProvenance Provenance { get; set; } = MemoryProvenance.UserExplicit;
        public string Source { get; set; } = "user_manual";
        public string? SourceMessageId { get; set; }
        public string? SourceEventId { get; set; }
        public string? SourceFilePath { get; set; }
        public string CanonicalKey { get; set; } = string.Empty;
        public string? SupersededById { get; set; }
        public bool IsPinned { get; set; }
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastConfirmedUtc { get; set; } = DateTime.UtcNow;
        public double Confidence { get; set; } = 1.0;
        public DateTime? ExpiresUtc { get; set; }
        public bool IsUserEditable { get; set; } = true;

        public string? ProjectKey { get; set; }
        public string? ConversationId { get; set; }
        public string? TurnId { get; set; }

        public bool IsExpired(DateTime? nowUtc = null)
            => Status == MemoryLifecycleStatus.Expired ||
               (ExpiresUtc.HasValue && ExpiresUtc.Value <= (nowUtc ?? DateTime.UtcNow));

        public bool IsActiveAt(DateTime? nowUtc = null)
            => Status == MemoryLifecycleStatus.Active && !IsExpired(nowUtc);

        public MemoryRecord Clone() => new()
        {
            Id = Id,
            Content = Content,
            Scope = Scope,
            Category = Category,
            Status = Status,
            Provenance = Provenance,
            Source = Source,
            SourceMessageId = SourceMessageId,
            SourceEventId = SourceEventId,
            SourceFilePath = SourceFilePath,
            CanonicalKey = CanonicalKey,
            SupersededById = SupersededById,
            IsPinned = IsPinned,
            TimestampUtc = TimestampUtc,
            UpdatedUtc = UpdatedUtc,
            LastConfirmedUtc = LastConfirmedUtc,
            Confidence = Confidence,
            ExpiresUtc = ExpiresUtc,
            IsUserEditable = IsUserEditable,
            ProjectKey = ProjectKey,
            ConversationId = ConversationId,
            TurnId = TurnId
        };
    }

    public sealed class ConversationMemory
    {
        public string? SessionGoal { get; set; }
        public List<string> Facts { get; set; } = new();
        public List<MemoryRecord> Records { get; set; } = new();
        public int TurnCount { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public void SynchronizeRecords(string? projectKey = null, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            Records ??= new List<MemoryRecord>();
            Facts ??= new List<string>();

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
                        Category = MemoryCategory.General,
                        Status = MemoryLifecycleStatus.Active,
                        Provenance = MemoryProvenance.UserExplicit,
                        Source = "legacy_fact",
                        TimestampUtc = UpdatedUtc == default ? now : UpdatedUtc,
                        UpdatedUtc = now,
                        LastConfirmedUtc = now,
                        Confidence = 0.78,
                        IsUserEditable = true,
                        ProjectKey = projectKey
                    });
                }
            }

            Records.RemoveAll(r => string.IsNullOrWhiteSpace(r.Content) || r.IsExpired(now) || r.Status == MemoryLifecycleStatus.Deleted);

            Facts.Clear();
            foreach (var rec in Records.Where(r => r.Scope == MemoryScope.CurrentProject && r.IsActiveAt(now)))
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
