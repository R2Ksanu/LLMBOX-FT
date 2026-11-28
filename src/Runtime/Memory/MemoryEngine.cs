using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LlmodelBox.Runtime.Memory
{
    /// <summary>
    /// Part 5 Scoped Memory Engine.
    /// Supports 5 explicit scopes: CurrentTurn, CurrentConversation, CurrentSession,
    /// CurrentProject, and UserGlobal (only written with explicit user approval).
    /// Arbitrary conversation turns are NEVER automatically promoted into permanent memory.
    /// </summary>
    public sealed class MemoryEngine : IMemoryEngine
    {
        private const int MaxFacts = 24;
        private const int MaxConversationFacts = 32;
        private const int MaxSessionFacts = 32;
        private const int MaxGlobalFacts = 48;
        private const int MaxFactLen = 200;
        private const int GoalMaxLen = 300;

        private readonly IMemoryStore _store;
        private readonly IGlobalMemoryStore? _globalStore;
        private readonly object _gate = new();

        private readonly List<MemoryRecord> _turnRecords = new();
        private readonly Dictionary<string, List<MemoryRecord>> _conversationRecords = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<MemoryRecord> _sessionRecords = new();
        private readonly List<MemoryRecord> _inMemoryGlobal = new();

        public MemoryEngine() : this(new JsonMemoryStore()) { }

        public MemoryEngine(IMemoryStore store)
            : this(store, store as IGlobalMemoryStore)
        {
        }

        public MemoryEngine(IMemoryStore store, IGlobalMemoryStore? globalStore)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _globalStore = globalStore;
        }

        private const string Term = @"(?=\s+(?:and|but|or|then)\s+|[.!?,;\n]|$)";

        private static readonly (Regex rx, string label)[] Patterns =
        {
            (new Regex(@"\bmy name is\s+([A-Za-z][\w'\-]{1,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled), "User's name is {0}"),
            (new Regex(@"\bcall me\s+([A-Za-z][\w'\-]{1,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled), "User goes by {0}"),
            (new Regex(@"\bi (?:really )?(?:like|love|prefer|enjoy)\s+(.{2,80}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "User likes {0}"),
            (new Regex(@"\bi(?:\s+am|'m)\s+(?:using|working with|building (?:in|with))\s+(.{2,80}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "User is using {0}"),
            (new Regex(@"\bwe (?:use|are using)\s+(.{2,80}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "Team uses {0}"),
            (new Regex(@"\bi(?:\s+am|'m)?\s+work(?:ing)? on\s+(.{2,80}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "User works on {0}"),
            (new Regex(@"\bremember (?:that )?(.{3,140}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "Remember: {0}"),
        };

        public MemorySnapshot Recall(string? projectKey)
            => Recall(projectKey, conversationId: null, turnId: null);

        public MemorySnapshot Recall(string? projectKey, string? conversationId, string? turnId = null)
        {
            string? goal = null;
            var projectFacts = new List<string>();
            var scoped = new List<MemoryRecord>();

            if (!string.IsNullOrWhiteSpace(projectKey))
            {
                var mem = LoadSynced(projectKey!);
                goal = mem.SessionGoal;
                projectFacts.AddRange(mem.Facts);
                scoped.AddRange(mem.Records.Where(r => !r.IsExpired()).Select(r => r.Clone()));
            }

            lock (_gate)
            {
                var now = DateTime.UtcNow;
                _turnRecords.RemoveAll(r => r.IsExpired(now));
                _sessionRecords.RemoveAll(r => r.IsExpired(now));

                foreach (var r in _turnRecords)
                {
                    if (string.IsNullOrWhiteSpace(turnId) || string.Equals(r.TurnId, turnId, StringComparison.OrdinalIgnoreCase))
                        scoped.Add(r.Clone());
                }

                if (!string.IsNullOrWhiteSpace(conversationId) &&
                    _conversationRecords.TryGetValue(conversationId!, out var convList))
                {
                    convList.RemoveAll(r => r.IsExpired(now));
                    scoped.AddRange(convList.Select(r => r.Clone()));
                }

                scoped.AddRange(_sessionRecords.Select(r => r.Clone()));
            }

            foreach (var g in LoadGlobalList())
            {
                if (!g.IsExpired()) scoped.Add(g.Clone());
            }

            // Order highest-priority/stable scopes clearly while keeping deterministic recency.
            var ordered = scoped
                .OrderByDescending(r => r.Scope)
                .ThenByDescending(r => r.Confidence)
                .ThenByDescending(r => r.UpdatedUtc)
                .ToList();

            return new MemorySnapshot
            {
                SessionGoal = goal,
                Facts = projectFacts,
                ScopedRecords = ordered
            };
        }

        public string? BuildPreamble(MemorySnapshot snapshot)
        {
            if (snapshot == null || snapshot.IsEmpty) return null;
            var sb = new StringBuilder();
            sb.AppendLine("[Memory — scoped durable context]");
            if (!string.IsNullOrWhiteSpace(snapshot.SessionGoal))
                sb.AppendLine($"Session goal: {snapshot.SessionGoal}");

            if (snapshot.ScopedRecords.Count > 0)
            {
                AppendScopeSection(sb, "Approved global preferences", snapshot.ScopedRecords, MemoryScope.UserGlobal);
                AppendScopeSection(sb, "Project memory", snapshot.ScopedRecords, MemoryScope.CurrentProject);
                AppendScopeSection(sb, "Session memory", snapshot.ScopedRecords, MemoryScope.CurrentSession);
                AppendScopeSection(sb, "Conversation notes", snapshot.ScopedRecords, MemoryScope.CurrentConversation);
                AppendScopeSection(sb, "Current turn notes", snapshot.ScopedRecords, MemoryScope.CurrentTurn);
            }
            else if (snapshot.Facts.Count > 0)
            {
                sb.AppendLine("Known facts:");
                foreach (var f in snapshot.Facts) sb.AppendLine($"- {f}");
            }

            return sb.ToString().TrimEnd();
        }

        private static void AppendScopeSection(StringBuilder sb, string heading, IReadOnlyList<MemoryRecord> records, MemoryScope scope)
        {
            var items = records.Where(r => r.Scope == scope).ToList();
            if (items.Count == 0) return;
            sb.AppendLine($"{heading}:");
            foreach (var item in items)
                sb.AppendLine($"- {item.Content}");
        }

        public void Observe(string? projectKey, string userText, string assistantText)
            => Observe(projectKey, null, userText, assistantText);

        /// <summary>
        /// Selectively extracts explicit facts / goals without blindly promoting general dialogue into permanent memory.
        /// </summary>
        public void Observe(string? projectKey, string? conversationId, string userText, string assistantText)
        {
            if (string.IsNullOrWhiteSpace(projectKey) && string.IsNullOrWhiteSpace(conversationId))
                return;

            var extracted = ExtractFacts(userText).ToList();

            if (!string.IsNullOrWhiteSpace(projectKey))
            {
                var mem = LoadSynced(projectKey!);
                mem.TurnCount++;

                // First substantial user objective seeds the session goal for compaction survival.
                if (string.IsNullOrWhiteSpace(mem.SessionGoal) && !string.IsNullOrWhiteSpace(userText))
                    mem.SessionGoal = Clip(userText.Trim(), GoalMaxLen);

                foreach (var fact in extracted)
                {
                    AddProjectRecord(mem, new MemoryRecord
                    {
                        Id = ConversationMemory.DeterministicId("proj", fact),
                        Content = fact,
                        Scope = MemoryScope.CurrentProject,
                        Source = "heuristic_self_disclosure",
                        TimestampUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow,
                        Confidence = 0.78,
                        IsUserEditable = true,
                        ProjectKey = projectKey
                    });
                }

                mem.UpdatedUtc = DateTime.UtcNow;
                mem.SynchronizeRecords(projectKey);
                _store.Save(projectKey!, mem);
            }

            // Record only high-signal explicit reminders into CurrentConversation (never auto-promote to UserGlobal).
            if (!string.IsNullOrWhiteSpace(conversationId) && extracted.Count > 0)
            {
                foreach (var fact in extracted)
                {
                    AddOrUpdate(new MemoryRecord
                    {
                        Id = ConversationMemory.DeterministicId("conv", fact),
                        Content = fact,
                        Scope = MemoryScope.CurrentConversation,
                        Source = "conversation_observation",
                        Confidence = 0.72,
                        ConversationId = conversationId,
                        IsUserEditable = true
                    }, explicitUserApproval: false);
                }
            }
        }

        public void Forget(string? projectKey)
        {
            if (string.IsNullOrWhiteSpace(projectKey)) return;
            _store.Clear(projectKey!);
        }

        public IReadOnlyList<MemoryRecord> Inspect(
            MemoryScope? scope = null,
            string? projectKey = null,
            string? conversationId = null,
            string? turnId = null,
            bool includeExpired = false)
        {
            var now = DateTime.UtcNow;
            var results = new List<MemoryRecord>();

            if (!scope.HasValue || scope.Value == MemoryScope.CurrentTurn)
            {
                lock (_gate)
                {
                    results.AddRange(_turnRecords
                        .Where(r => (includeExpired || !r.IsExpired(now))
                                 && (string.IsNullOrWhiteSpace(turnId) || string.Equals(r.TurnId, turnId, StringComparison.OrdinalIgnoreCase)))
                        .Select(r => r.Clone()));
                }
            }

            if (!scope.HasValue || scope.Value == MemoryScope.CurrentConversation)
            {
                lock (_gate)
                {
                    IEnumerable<MemoryRecord> seq = string.IsNullOrWhiteSpace(conversationId)
                        ? _conversationRecords.Values.SelectMany(x => x)
                        : (_conversationRecords.TryGetValue(conversationId!, out var c) ? c : Enumerable.Empty<MemoryRecord>());
                    results.AddRange(seq.Where(r => includeExpired || !r.IsExpired(now)).Select(r => r.Clone()));
                }
            }

            if (!scope.HasValue || scope.Value == MemoryScope.CurrentSession)
            {
                lock (_gate)
                {
                    results.AddRange(_sessionRecords.Where(r => includeExpired || !r.IsExpired(now)).Select(r => r.Clone()));
                }
            }

            if ((!scope.HasValue || scope.Value == MemoryScope.CurrentProject) && !string.IsNullOrWhiteSpace(projectKey))
            {
                var mem = _store.Load(projectKey!);
                mem.SynchronizeRecords(projectKey, includeExpired ? DateTime.MinValue : now);
                results.AddRange(mem.Records
                    .Where(r => r.Scope == MemoryScope.CurrentProject && (includeExpired || !r.IsExpired(now)))
                    .Select(r => r.Clone()));
            }

            if (!scope.HasValue || scope.Value == MemoryScope.UserGlobal)
            {
                results.AddRange(LoadGlobalList()
                    .Where(r => includeExpired || !r.IsExpired(now))
                    .Select(r => r.Clone()));
            }

            return results
                .OrderByDescending(r => r.Scope)
                .ThenByDescending(r => r.UpdatedUtc)
                .ToList();
        }

        public MemoryRecord AddOrUpdate(MemoryRecord record, bool explicitUserApproval = false)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (string.IsNullOrWhiteSpace(record.Content))
                throw new ArgumentException("Memory record content cannot be empty.", nameof(record));

            if (record.Scope == MemoryScope.UserGlobal && !explicitUserApproval)
                throw new InvalidOperationException("UserGlobal memory requires explicit user approval.");

            var normalized = record.Clone();
            normalized.Content = Clip(normalized.Content.Trim(), MaxFactLen);
            normalized.Confidence = Math.Clamp(normalized.Confidence, 0.0, 1.0);
            if (string.IsNullOrWhiteSpace(normalized.Id))
                normalized.Id = Guid.NewGuid().ToString("N")[..12];
            if (normalized.TimestampUtc == default)
                normalized.TimestampUtc = DateTime.UtcNow;
            normalized.UpdatedUtc = DateTime.UtcNow;

            switch (normalized.Scope)
            {
                case MemoryScope.CurrentTurn:
                    lock (_gate)
                    {
                        UpsertInList(_turnRecords, normalized, 32);
                    }
                    break;

                case MemoryScope.CurrentConversation:
                    lock (_gate)
                    {
                        string convKey = string.IsNullOrWhiteSpace(normalized.ConversationId) ? "__default__" : normalized.ConversationId!;
                        normalized.ConversationId = convKey;
                        if (!_conversationRecords.TryGetValue(convKey, out var list))
                        {
                            list = new List<MemoryRecord>();
                            _conversationRecords[convKey] = list;
                        }
                        UpsertInList(list, normalized, MaxConversationFacts);
                    }
                    break;

                case MemoryScope.CurrentSession:
                    lock (_gate)
                    {
                        UpsertInList(_sessionRecords, normalized, MaxSessionFacts);
                    }
                    break;

                case MemoryScope.CurrentProject:
                    if (string.IsNullOrWhiteSpace(normalized.ProjectKey))
                        throw new ArgumentException("ProjectKey is required for CurrentProject memory.", nameof(record));
                    var mem = LoadSynced(normalized.ProjectKey!);
                    AddProjectRecord(mem, normalized);
                    mem.UpdatedUtc = DateTime.UtcNow;
                    mem.SynchronizeRecords(normalized.ProjectKey);
                    _store.Save(normalized.ProjectKey!, mem);
                    break;

                case MemoryScope.UserGlobal:
                    var global = LoadGlobalList();
                    UpsertInList(global, normalized, MaxGlobalFacts);
                    SaveGlobalList(global);
                    break;
            }

            return normalized.Clone();
        }

        public bool UpdateRecord(
            string id,
            string newContent,
            MemoryScope? newScope = null,
            DateTime? expiresUtc = null,
            string? projectKey = null,
            string? conversationId = null,
            bool explicitUserApproval = true)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(newContent)) return false;

            var existing = FindRecord(id, projectKey, conversationId);
            if (existing == null || !existing.IsUserEditable) return false;

            MemoryScope targetScope = newScope ?? existing.Scope;
            if (targetScope == MemoryScope.UserGlobal && !explicitUserApproval) return false;

            if (targetScope != existing.Scope)
            {
                DeleteRecord(id, projectKey ?? existing.ProjectKey, conversationId ?? existing.ConversationId);
            }

            existing.Content = Clip(newContent.Trim(), MaxFactLen);
            existing.Scope = targetScope;
            existing.ExpiresUtc = expiresUtc;
            existing.UpdatedUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(projectKey)) existing.ProjectKey = projectKey;
            if (!string.IsNullOrWhiteSpace(conversationId)) existing.ConversationId = conversationId;

            AddOrUpdate(existing, explicitUserApproval: explicitUserApproval);
            return true;
        }

        public bool DeleteRecord(string id, string? projectKey = null, string? conversationId = null)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            bool removed = false;

            lock (_gate)
            {
                removed |= _turnRecords.RemoveAll(r => r.Id == id) > 0;
                if (!string.IsNullOrWhiteSpace(conversationId) &&
                    _conversationRecords.TryGetValue(conversationId!, out var cList))
                {
                    removed |= cList.RemoveAll(r => r.Id == id) > 0;
                }
                else
                {
                    foreach (var kv in _conversationRecords.Values)
                        removed |= kv.RemoveAll(r => r.Id == id) > 0;
                }
                removed |= _sessionRecords.RemoveAll(r => r.Id == id) > 0;
            }

            if (!string.IsNullOrWhiteSpace(projectKey))
            {
                var mem = LoadSynced(projectKey!);
                int rem = mem.Records.RemoveAll(r => r.Id == id);
                if (rem > 0)
                {
                    mem.SynchronizeRecords(projectKey);
                    _store.Save(projectKey!, mem);
                    removed = true;
                }
            }

            var global = LoadGlobalList();
            if (global.RemoveAll(r => r.Id == id) > 0)
            {
                SaveGlobalList(global);
                removed = true;
            }

            return removed;
        }

        public bool Promote(
            string id,
            MemoryScope targetScope,
            string? projectKey = null,
            string? conversationId = null,
            bool explicitUserApproval = true)
        {
            if (targetScope == MemoryScope.UserGlobal && !explicitUserApproval) return false;
            var existing = FindRecord(id, projectKey, conversationId);
            if (existing == null) return false;

            DeleteRecord(id, projectKey ?? existing.ProjectKey, conversationId ?? existing.ConversationId);
            existing.Scope = targetScope;
            existing.Source = explicitUserApproval ? "user_promoted" : existing.Source;
            existing.Confidence = Math.Max(existing.Confidence, 0.9);
            existing.UpdatedUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(projectKey)) existing.ProjectKey = projectKey;
            if (!string.IsNullOrWhiteSpace(conversationId)) existing.ConversationId = conversationId;
            AddOrUpdate(existing, explicitUserApproval: explicitUserApproval);
            return true;
        }

        public int PruneExpired(string? projectKey = null)
        {
            int count = 0;
            var now = DateTime.UtcNow;
            lock (_gate)
            {
                count += _turnRecords.RemoveAll(r => r.IsExpired(now));
                foreach (var c in _conversationRecords.Values)
                    count += c.RemoveAll(r => r.IsExpired(now));
                count += _sessionRecords.RemoveAll(r => r.IsExpired(now));
            }

            if (!string.IsNullOrWhiteSpace(projectKey))
            {
                var mem = _store.Load(projectKey!);
                int before = mem.Records.Count;
                mem.SynchronizeRecords(projectKey, now);
                int diff = Math.Max(0, before - mem.Records.Count);
                if (diff > 0)
                {
                    _store.Save(projectKey!, mem);
                    count += diff;
                }
            }

            var global = LoadGlobalList();
            int gRemoved = global.RemoveAll(r => r.IsExpired(now));
            if (gRemoved > 0)
            {
                SaveGlobalList(global);
                count += gRemoved;
            }

            return count;
        }

        public void ClearTurn(string? turnId = null)
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(turnId)) _turnRecords.Clear();
                else _turnRecords.RemoveAll(r => string.Equals(r.TurnId, turnId, StringComparison.OrdinalIgnoreCase));
            }
        }

        public void ClearConversation(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return;
            lock (_gate)
            {
                _conversationRecords.Remove(conversationId);
            }
        }

        public void ClearSession()
        {
            lock (_gate)
            {
                _sessionRecords.Clear();
                _turnRecords.Clear();
            }
        }

        // Deterministic pattern-based extraction. Public + static so it is unit-testable in isolation.
        public static IEnumerable<string> ExtractFacts(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (var (rx, label) in Patterns)
            {
                var m = rx.Match(text);
                if (!m.Success) continue;
                string captured = m.Groups[1].Value.Trim().TrimEnd('.', ',', ';', ':', ' ');
                if (captured.Length == 0) continue;
                yield return Clip(string.Format(label, captured), MaxFactLen);
            }
        }

        private ConversationMemory LoadSynced(string projectKey)
        {
            var mem = _store.Load(projectKey);
            mem.SynchronizeRecords(projectKey);
            return mem;
        }

        private MemoryRecord? FindRecord(string id, string? projectKey, string? conversationId)
        {
            lock (_gate)
            {
                var hit = _turnRecords.FirstOrDefault(r => r.Id == id)
                       ?? _sessionRecords.FirstOrDefault(r => r.Id == id);
                if (hit != null) return hit.Clone();

                if (!string.IsNullOrWhiteSpace(conversationId) &&
                    _conversationRecords.TryGetValue(conversationId!, out var cList))
                {
                    hit = cList.FirstOrDefault(r => r.Id == id);
                    if (hit != null) return hit.Clone();
                }
                else
                {
                    foreach (var list in _conversationRecords.Values)
                    {
                        hit = list.FirstOrDefault(r => r.Id == id);
                        if (hit != null) return hit.Clone();
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(projectKey))
            {
                var mem = LoadSynced(projectKey!);
                var projHit = mem.Records.FirstOrDefault(r => r.Id == id);
                if (projHit != null) return projHit.Clone();
            }

            var gHit = LoadGlobalList().FirstOrDefault(r => r.Id == id);
            return gHit?.Clone();
        }

        private static void AddProjectRecord(ConversationMemory mem, MemoryRecord rec)
        {
            UpsertInList(mem.Records, rec, MaxFacts);
        }

        private static void UpsertInList(List<MemoryRecord> list, MemoryRecord rec, int maxItems)
        {
            int idx = list.FindIndex(r =>
                r.Id == rec.Id ||
                string.Equals(r.Content, rec.Content, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0)
            {
                rec.TimestampUtc = list[idx].TimestampUtc;
                list[idx] = rec;
            }
            else
            {
                list.Add(rec);
                if (list.Count > maxItems)
                    list.RemoveRange(0, list.Count - maxItems);
            }
        }

        private List<MemoryRecord> LoadGlobalList()
        {
            if (_globalStore != null) return _globalStore.LoadGlobal();
            lock (_gate) { return _inMemoryGlobal.Select(r => r.Clone()).ToList(); }
        }

        private void SaveGlobalList(List<MemoryRecord> records)
        {
            if (_globalStore != null) { _globalStore.SaveGlobal(records); return; }
            lock (_gate)
            {
                _inMemoryGlobal.Clear();
                _inMemoryGlobal.AddRange(records.Select(r => r.Clone()));
            }
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
