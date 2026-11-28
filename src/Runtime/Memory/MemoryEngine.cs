using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LlmodelBox.Runtime.Continuity;
using LlmodelBox.Runtime.Knowledge;

namespace LlmodelBox.Runtime.Memory
{
    /// <summary>
    /// Coordinated 5-scope Memory Engine:
    ///   - Persists ALL 5 scopes durably when backed by IConversationRepository (SQLite),
    ///     keeping JSON fallback compatibility when constructed standalone.
    ///   - Separates UserExplicit preferences/facts, ToolVerified evidence, and AssistantInferred observations.
    ///   - Ranks memories by query relevance + scope + recency + confidence + pin status with strict preamble caps.
    ///   - Never auto-promotes project facts or assistant inferences into UserGlobal without explicit approval.
    /// </summary>
    public sealed class MemoryEngine : IMemoryEngine
    {
        private const int MaxFacts = 32;
        private const int MaxConversationFacts = 48;
        private const int MaxSessionFacts = 48;
        private const int MaxGlobalFacts = 64;
        private const int MaxFactLen = 220;
        private const int GoalMaxLen = 300;

        private readonly IMemoryStore _store;
        private readonly IGlobalMemoryStore? _globalStore;
        private readonly IConversationRepository? _repo;
        private readonly object _gate = new();

        private readonly List<MemoryRecord> _turnRecords = new();
        private readonly Dictionary<string, List<MemoryRecord>> _conversationRecords = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<MemoryRecord> _sessionRecords = new();
        private readonly List<MemoryRecord> _inMemoryGlobal = new();

        public MemoryEngine() : this(new JsonMemoryStore()) { }

        public MemoryEngine(IMemoryStore store)
            : this(store, store as IGlobalMemoryStore, null)
        {
        }

        public MemoryEngine(IMemoryStore store, IGlobalMemoryStore? globalStore)
            : this(store, globalStore, null)
        {
        }

        public MemoryEngine(IConversationRepository repository, IMemoryStore? fallbackStore = null)
            : this(fallbackStore ?? new JsonMemoryStore(), fallbackStore as IGlobalMemoryStore, repository)
        {
        }

        public MemoryEngine(IMemoryStore store, IGlobalMemoryStore? globalStore, IConversationRepository? repository)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _globalStore = globalStore;
            _repo = repository;
        }

        public PersistenceDiagnosticReport GetDiagnostics()
        {
            if (_repo != null) return _repo.GetDiagnostics();
            if (_store is JsonMemoryStore jm) return jm.LastDiagnostics;
            return new PersistenceDiagnosticReport();
        }

        private const string Term = @"(?=\s+(?:and|but|or|then)\s+|[.!?,;\n]|$)";

        private static readonly (Regex rx, string label, MemoryCategory cat, string canonPrefix)[] Patterns =
        {
            (new Regex(@"\bmy name is\s+([A-Za-z][\w'\-]{1,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled), "User's name is {0}", MemoryCategory.Preference, "user.name"),
            (new Regex(@"\bcall me\s+([A-Za-z][\w'\-]{1,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled), "User goes by {0}", MemoryCategory.Preference, "user.alias"),
            (new Regex(@"\bi (?:really )?(?:like|love|prefer|enjoy)\s+(.{2,80}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "User likes {0}", MemoryCategory.Preference, "user.pref"),
            (new Regex(@"\bi(?:\s+am|'m)\s+(?:using|working with|building (?:in|with))\s+(.{2,80}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "User is using {0}", MemoryCategory.Architecture, "stack.primary"),
            (new Regex(@"\bwe (?:use|are using)\s+(.{2,80}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "Team uses {0}", MemoryCategory.Architecture, "stack.team"),
            (new Regex(@"\bi(?:\s+am|'m)?\s+work(?:ing)? on\s+(.{2,80}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "User works on {0}", MemoryCategory.TaskState, "work.topic"),
            (new Regex(@"\bremember (?:that )?(.{3,140}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "Remember: {0}", MemoryCategory.Constraint, ""),
            (new Regex(@"\b(?:instead\s+of|actually\s+use|switched\s+to|always\s+use)\s+(.{2,100}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "Preference correction: {0}", MemoryCategory.Constraint, "user.correction")
        };

        private static readonly Regex AssistantDecisionRegex = new(
            @"(?im)^(?:[-*•]\s*)?(?:decided|decision|implemented|fixed|updated|next\s+step)\s*[:\-–]\s*(.{8,180})$",
            RegexOptions.Compiled);

        public MemorySnapshot Recall(string? projectKey)
            => Recall(projectKey, conversationId: null, turnId: null);

        public MemorySnapshot Recall(string? projectKey, string? conversationId, string? turnId = null)
        {
            if (_repo != null)
            {
                var persisted = _repo.QueryMemories(null, projectKey, conversationId, turnId, includeInactive: false)
                    .Where(r => r.IsActiveAt())
                    .ToList();

                string? goal = null;
                if (!string.IsNullOrWhiteSpace(conversationId))
                {
                    var cp = _repo.GetLatestCheckpoint(conversationId!);
                    goal = cp?.CurrentGoal;
                }

                var facts = persisted
                    .Where(r => r.Scope == MemoryScope.CurrentProject)
                    .Select(r => r.Content)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new MemorySnapshot
                {
                    SessionGoal = goal,
                    Facts = facts,
                    ScopedRecords = persisted
                };
            }

            string? legacyGoal = null;
            var projectFacts = new List<string>();
            var scoped = new List<MemoryRecord>();

            if (!string.IsNullOrWhiteSpace(projectKey))
            {
                var mem = LoadSynced(projectKey!);
                legacyGoal = mem.SessionGoal;
                projectFacts.AddRange(mem.Facts);
                scoped.AddRange(mem.Records.Where(r => r.IsActiveAt()).Select(r => r.Clone()));
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
                if (g.IsActiveAt()) scoped.Add(g.Clone());
            }

            var ordered = scoped
                .OrderByDescending(r => r.IsPinned)
                .ThenByDescending(r => r.Scope)
                .ThenByDescending(r => r.Confidence)
                .ThenByDescending(r => r.UpdatedUtc)
                .ToList();

            return new MemorySnapshot
            {
                SessionGoal = legacyGoal,
                Facts = projectFacts,
                ScopedRecords = ordered
            };
        }

        public string? BuildPreamble(MemorySnapshot snapshot)
            => BuildPreamble(snapshot, query: null, maxChars: 1200, activeConversationId: null);

        /// <summary>
        /// Query-aware ranked memory retrieval bounded strictly by maxChars so prompts never get polluted.
        /// </summary>
        public string? BuildPreamble(MemorySnapshot snapshot, string? query, int maxChars = 1200, string? activeConversationId = null)
        {
            if (snapshot == null || snapshot.IsEmpty || maxChars <= 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine("[Memory — scoped durable context]");
            if (!string.IsNullOrWhiteSpace(snapshot.SessionGoal))
                sb.AppendLine($"Session goal: {snapshot.SessionGoal}");

            if (snapshot.ScopedRecords.Count > 0)
            {
                var queryTerms = new HashSet<string>(KnowledgeEngine.Tokenize(query), StringComparer.OrdinalIgnoreCase);
                var now = DateTime.UtcNow;

                var ranked = snapshot.ScopedRecords
                    .Where(r => r.IsActiveAt(now))
                    .Select(r => (Record: r, Score: ScoreRecord(r, queryTerms, activeConversationId, now)))
                    .OrderByDescending(x => x.Score)
                    .ToList();

                var picked = new List<MemoryRecord>();
                foreach (var item in ranked)
                {
                    string line = FormatLine(item.Record);
                    if (sb.Length + line.Length + 30 > maxChars) break;
                    picked.Add(item.Record);
                    sb.AppendLine(line);
                }
            }
            else if (snapshot.Facts.Count > 0)
            {
                sb.AppendLine("Known facts:");
                foreach (var f in snapshot.Facts)
                {
                    string line = $"- {f}";
                    if (sb.Length + line.Length + 2 > maxChars) break;
                    sb.AppendLine(line);
                }
            }

            string txt = sb.ToString().TrimEnd();
            return txt.Length > "[Memory — scoped durable context]".Length ? txt : null;
        }

        private static string FormatLine(MemoryRecord r)
        {
            string scopeTag = r.Scope switch
            {
                MemoryScope.UserGlobal => "global",
                MemoryScope.CurrentProject => "project",
                MemoryScope.CurrentSession => "session",
                MemoryScope.CurrentConversation => "conv",
                _ => "turn"
            };
            string provTag = r.Provenance == MemoryProvenance.AssistantInferred ? ", inferred" : string.Empty;
            return $"- [{scopeTag}{provTag}] {r.Content}";
        }

        private static double ScoreRecord(MemoryRecord r, HashSet<string> queryTerms, string? activeConvId, DateTime now)
        {
            double score = 0;
            if (r.IsPinned) score += 25.0;
            score += r.Scope switch
            {
                MemoryScope.CurrentTurn => 10.0,
                MemoryScope.CurrentConversation => (!string.IsNullOrWhiteSpace(activeConvId) && string.Equals(r.ConversationId, activeConvId, StringComparison.OrdinalIgnoreCase)) ? 12.0 : 2.0,
                MemoryScope.UserGlobal => 8.0,
                MemoryScope.CurrentProject => 7.0,
                MemoryScope.CurrentSession => 5.0,
                _ => 1.0
            };

            score += r.Provenance switch
            {
                MemoryProvenance.UserExplicit or MemoryProvenance.ManualEdit => 4.0,
                MemoryProvenance.ToolVerified => 3.5,
                MemoryProvenance.FileDerived => 2.5,
                _ => 1.0
            };

            score += r.Confidence * 3.0;
            double ageDays = Math.Max(0, (now - r.UpdatedUtc).TotalDays);
            score += Math.Max(0, 2.5 - ageDays * 0.15);

            if (queryTerms.Count > 0)
            {
                int hits = KnowledgeEngine.Tokenize(r.Content).Count(t => queryTerms.Contains(t));
                score += hits * 4.5;
            }
            return score;
        }

        public void Observe(string? projectKey, string userText, string assistantText)
            => Observe(projectKey, null, userText, assistantText);

        /// <summary>
        /// Captures explicit user disclosures + structured assistant decisions/fixes with honest provenance.
        /// Redacts secrets automatically; NEVER promotes arbitrary turns or inferences into UserGlobal.
        /// </summary>
        public void Observe(string? projectKey, string? conversationId, string userText, string assistantText)
        {
            if (string.IsNullOrWhiteSpace(projectKey) && string.IsNullOrWhiteSpace(conversationId))
                return;

            string safeUser = SecretRedactor.Redact(userText);
            string safeAssistant = SecretRedactor.Redact(assistantText);
            var extracted = ExtractFactDetails(safeUser).ToList();
            var assistantObs = ExtractAssistantObservations(safeAssistant).ToList();

            if (_repo != null)
            {
                foreach (var (fact, cat, canon) in extracted)
                {
                    AddOrUpdate(new MemoryRecord
                    {
                        Id = ConversationMemory.DeterministicId(!string.IsNullOrWhiteSpace(projectKey) ? "proj" : "conv", fact),
                        Content = fact,
                        Scope = !string.IsNullOrWhiteSpace(projectKey) ? MemoryScope.CurrentProject : MemoryScope.CurrentConversation,
                        Category = cat,
                        CanonicalKey = canon,
                        Provenance = MemoryProvenance.UserExplicit,
                        Source = "user_explicit_observation",
                        ProjectKey = projectKey,
                        ConversationId = conversationId,
                        Confidence = 0.85,
                        IsUserEditable = true
                    }, explicitUserApproval: false);
                }

                foreach (var obs in assistantObs.Take(3))
                {
                    AddOrUpdate(new MemoryRecord
                    {
                        Id = ConversationMemory.DeterministicId("asst", $"{conversationId}:{obs}"),
                        Content = obs,
                        Scope = string.IsNullOrWhiteSpace(conversationId) ? MemoryScope.CurrentProject : MemoryScope.CurrentConversation,
                        Category = MemoryCategory.Decision,
                        Provenance = MemoryProvenance.AssistantInferred,
                        Source = "assistant_inferred",
                        ProjectKey = projectKey,
                        ConversationId = conversationId,
                        Confidence = 0.68,
                        ExpiresUtc = DateTime.UtcNow.AddDays(30),
                        IsUserEditable = true
                    }, explicitUserApproval: false);
                }
                return;
            }

            if (!string.IsNullOrWhiteSpace(projectKey))
            {
                var mem = LoadSynced(projectKey!);
                mem.TurnCount++;

                if (string.IsNullOrWhiteSpace(mem.SessionGoal) && !string.IsNullOrWhiteSpace(safeUser))
                    mem.SessionGoal = Clip(safeUser.Trim(), GoalMaxLen);

                foreach (var (fact, cat, canon) in extracted)
                {
                    AddProjectRecord(mem, new MemoryRecord
                    {
                        Id = ConversationMemory.DeterministicId("proj", fact),
                        Content = fact,
                        Scope = MemoryScope.CurrentProject,
                        Category = cat,
                        CanonicalKey = canon,
                        Provenance = MemoryProvenance.UserExplicit,
                        Source = "heuristic_self_disclosure",
                        TimestampUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow,
                        LastConfirmedUtc = DateTime.UtcNow,
                        Confidence = 0.78,
                        IsUserEditable = true,
                        ProjectKey = projectKey
                    });
                }

                mem.UpdatedUtc = DateTime.UtcNow;
                mem.SynchronizeRecords(projectKey);
                _store.Save(projectKey!, mem);
            }

            if (!string.IsNullOrWhiteSpace(conversationId) && (extracted.Count > 0 || assistantObs.Count > 0))
            {
                foreach (var (fact, cat, canon) in extracted)
                {
                    AddOrUpdate(new MemoryRecord
                    {
                        Id = ConversationMemory.DeterministicId("conv", fact),
                        Content = fact,
                        Scope = MemoryScope.CurrentConversation,
                        Category = cat,
                        CanonicalKey = canon,
                        Provenance = MemoryProvenance.UserExplicit,
                        Source = "conversation_observation",
                        Confidence = 0.78,
                        ConversationId = conversationId,
                        IsUserEditable = true
                    }, explicitUserApproval: false);
                }

                foreach (var obs in assistantObs.Take(2))
                {
                    AddOrUpdate(new MemoryRecord
                    {
                        Id = ConversationMemory.DeterministicId("asst", obs),
                        Content = obs,
                        Scope = MemoryScope.CurrentConversation,
                        Category = MemoryCategory.Decision,
                        Provenance = MemoryProvenance.AssistantInferred,
                        Source = "assistant_inferred",
                        Confidence = 0.65,
                        ConversationId = conversationId,
                        IsUserEditable = true
                    }, explicitUserApproval: false);
                }
            }
        }

        public void Forget(string? projectKey)
        {
            if (string.IsNullOrWhiteSpace(projectKey)) return;
            if (_repo != null)
            {
                _repo.ForgetProjectMemory(projectKey!);
                return;
            }
            _store.Clear(projectKey!);
        }

        public IReadOnlyList<MemoryRecord> Inspect(
            MemoryScope? scope = null,
            string? projectKey = null,
            string? conversationId = null,
            string? turnId = null,
            bool includeExpired = false)
        {
            if (_repo != null)
                return _repo.QueryMemories(scope, projectKey, conversationId, turnId, includeInactive: includeExpired);

            var now = DateTime.UtcNow;
            var results = new List<MemoryRecord>();

            if (!scope.HasValue || scope.Value == MemoryScope.CurrentTurn)
            {
                lock (_gate)
                {
                    results.AddRange(_turnRecords
                        .Where(r => (includeExpired || r.IsActiveAt(now))
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
                    results.AddRange(seq.Where(r => includeExpired || r.IsActiveAt(now)).Select(r => r.Clone()));
                }
            }

            if (!scope.HasValue || scope.Value == MemoryScope.CurrentSession)
            {
                lock (_gate)
                {
                    results.AddRange(_sessionRecords.Where(r => includeExpired || r.IsActiveAt(now)).Select(r => r.Clone()));
                }
            }

            if ((!scope.HasValue || scope.Value == MemoryScope.CurrentProject) && !string.IsNullOrWhiteSpace(projectKey))
            {
                var mem = _store.Load(projectKey!);
                mem.SynchronizeRecords(projectKey, includeExpired ? DateTime.MinValue : now);
                results.AddRange(mem.Records
                    .Where(r => r.Scope == MemoryScope.CurrentProject && (includeExpired || r.IsActiveAt(now)))
                    .Select(r => r.Clone()));
            }

            if (!scope.HasValue || scope.Value == MemoryScope.UserGlobal)
            {
                results.AddRange(LoadGlobalList()
                    .Where(r => includeExpired || r.IsActiveAt(now))
                    .Select(r => r.Clone()));
            }

            return results
                .OrderByDescending(r => r.IsPinned)
                .ThenByDescending(r => r.Scope)
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
            normalized.Content = Clip(SecretRedactor.Redact(normalized.Content.Trim()), MaxFactLen);
            normalized.Confidence = Math.Clamp(normalized.Confidence, 0.0, 1.0);
            if (string.IsNullOrWhiteSpace(normalized.Id))
                normalized.Id = Guid.NewGuid().ToString("N")[..12];
            if (normalized.TimestampUtc == default)
                normalized.TimestampUtc = DateTime.UtcNow;
            normalized.UpdatedUtc = DateTime.UtcNow;
            if (normalized.LastConfirmedUtc == default)
                normalized.LastConfirmedUtc = DateTime.UtcNow;

            if (_repo != null)
            {
                return _repo.UpsertMemory(normalized);
            }

            switch (normalized.Scope)
            {
                case MemoryScope.CurrentTurn:
                    lock (_gate) { UpsertInList(_turnRecords, normalized, 32); }
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
                    lock (_gate) { UpsertInList(_sessionRecords, normalized, MaxSessionFacts); }
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

            var existing = _repo != null ? _repo.GetMemory(id) : FindRecord(id, projectKey, conversationId);
            if (existing == null || !existing.IsUserEditable) return false;

            MemoryScope targetScope = newScope ?? existing.Scope;
            if (targetScope == MemoryScope.UserGlobal && !explicitUserApproval) return false;

            if (_repo == null && targetScope != existing.Scope)
                DeleteRecord(id, projectKey ?? existing.ProjectKey, conversationId ?? existing.ConversationId);

            existing.Content = Clip(SecretRedactor.Redact(newContent.Trim()), MaxFactLen);
            existing.Scope = targetScope;
            existing.ExpiresUtc = expiresUtc;
            existing.Provenance = MemoryProvenance.ManualEdit;
            existing.Status = MemoryLifecycleStatus.Active;
            existing.UpdatedUtc = DateTime.UtcNow;
            existing.LastConfirmedUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(projectKey)) existing.ProjectKey = projectKey;
            if (!string.IsNullOrWhiteSpace(conversationId)) existing.ConversationId = conversationId;

            AddOrUpdate(existing, explicitUserApproval: explicitUserApproval);
            return true;
        }

        public bool SetPinned(string id, bool pinned, string? projectKey = null, string? conversationId = null)
        {
            var existing = _repo != null ? _repo.GetMemory(id) : FindRecord(id, projectKey, conversationId);
            if (existing == null) return false;
            existing.IsPinned = pinned;
            existing.UpdatedUtc = DateTime.UtcNow;
            AddOrUpdate(existing, explicitUserApproval: existing.Scope == MemoryScope.UserGlobal);
            return true;
        }

        public bool DeleteRecord(string id, string? projectKey = null, string? conversationId = null)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (_repo != null) return _repo.DeleteMemory(id);

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
            var existing = _repo != null ? _repo.GetMemory(id) : FindRecord(id, projectKey, conversationId);
            if (existing == null) return false;

            if (_repo == null)
                DeleteRecord(id, projectKey ?? existing.ProjectKey, conversationId ?? existing.ConversationId);

            existing.Scope = targetScope;
            existing.Source = explicitUserApproval ? "user_promoted" : existing.Source;
            existing.Provenance = explicitUserApproval ? MemoryProvenance.UserExplicit : existing.Provenance;
            existing.Confidence = Math.Max(existing.Confidence, 0.9);
            existing.UpdatedUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(projectKey)) existing.ProjectKey = projectKey;
            if (!string.IsNullOrWhiteSpace(conversationId)) existing.ConversationId = conversationId;
            AddOrUpdate(existing, explicitUserApproval: explicitUserApproval);
            return true;
        }

        public int PruneExpired(string? projectKey = null)
        {
            if (_repo != null)
            {
                int pruned = 0;
                var all = _repo.QueryMemories(null, projectKey, null, null, includeInactive: true);
                var now = DateTime.UtcNow;
                foreach (var r in all)
                {
                    if (r.IsExpired(now) && r.Status == MemoryLifecycleStatus.Active)
                    {
                        r.Status = MemoryLifecycleStatus.Expired;
                        _repo.UpsertMemory(r);
                        pruned++;
                    }
                }
                return pruned;
            }

            int count = 0;
            var utcNow = DateTime.UtcNow;
            lock (_gate)
            {
                count += _turnRecords.RemoveAll(r => r.IsExpired(utcNow));
                foreach (var c in _conversationRecords.Values)
                    count += c.RemoveAll(r => r.IsExpired(utcNow));
                count += _sessionRecords.RemoveAll(r => r.IsExpired(utcNow));
            }

            if (!string.IsNullOrWhiteSpace(projectKey))
            {
                var mem = _store.Load(projectKey!);
                int before = mem.Records.Count;
                mem.SynchronizeRecords(projectKey, utcNow);
                int diff = Math.Max(0, before - mem.Records.Count);
                if (diff > 0)
                {
                    _store.Save(projectKey!, mem);
                    count += diff;
                }
            }

            var global = LoadGlobalList();
            int gRemoved = global.RemoveAll(r => r.IsExpired(utcNow));
            if (gRemoved > 0)
            {
                SaveGlobalList(global);
                count += gRemoved;
            }

            return count;
        }

        public void ClearTurn(string? turnId = null)
        {
            if (_repo != null)
            {
                foreach (var r in _repo.QueryMemories(MemoryScope.CurrentTurn, null, null, turnId))
                    _repo.DeleteMemory(r.Id);
                return;
            }

            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(turnId)) _turnRecords.Clear();
                else _turnRecords.RemoveAll(r => string.Equals(r.TurnId, turnId, StringComparison.OrdinalIgnoreCase));
            }
        }

        public void ClearConversation(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return;
            if (_repo != null)
            {
                foreach (var r in _repo.QueryMemories(MemoryScope.CurrentConversation, null, conversationId))
                    _repo.DeleteMemory(r.Id);
                return;
            }

            lock (_gate) { _conversationRecords.Remove(conversationId); }
        }

        public void ClearSession()
        {
            if (_repo != null)
            {
                foreach (var r in _repo.QueryMemories(MemoryScope.CurrentSession))
                    _repo.DeleteMemory(r.Id);
                foreach (var r in _repo.QueryMemories(MemoryScope.CurrentTurn))
                    _repo.DeleteMemory(r.Id);
                return;
            }

            lock (_gate)
            {
                _sessionRecords.Clear();
                _turnRecords.Clear();
            }
        }

        public static IEnumerable<string> ExtractFacts(string? text)
            => ExtractFactDetails(text).Select(x => x.Content);

        public static IEnumerable<(string Content, MemoryCategory Category, string CanonicalKey)> ExtractFactDetails(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (var (rx, label, cat, canon) in Patterns)
            {
                var m = rx.Match(text);
                if (!m.Success) continue;
                string captured = m.Groups[1].Value.Trim().TrimEnd('.', ',', ';', ':', ' ');
                if (captured.Length == 0) continue;
                yield return (Clip(string.Format(label, captured), MaxFactLen), cat, canon);
            }
        }

        public static IEnumerable<string> ExtractAssistantObservations(string? assistantText)
        {
            if (string.IsNullOrWhiteSpace(assistantText)) yield break;
            foreach (Match m in AssistantDecisionRegex.Matches(assistantText))
            {
                string val = m.Groups[1].Value.Trim();
                if (val.Length >= 8) yield return Clip(val, MaxFactLen);
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
            => UpsertInList(mem.Records, rec, MaxFacts);

        private static void UpsertInList(List<MemoryRecord> list, MemoryRecord rec, int maxItems)
        {
            if (!string.IsNullOrWhiteSpace(rec.CanonicalKey))
            {
                foreach (var old in list.Where(r => r.Id != rec.Id && r.Status == MemoryLifecycleStatus.Active &&
                                                    string.Equals(r.CanonicalKey, rec.CanonicalKey, StringComparison.OrdinalIgnoreCase)))
                {
                    old.Status = MemoryLifecycleStatus.Superseded;
                    old.SupersededById = rec.Id;
                }
            }

            int idx = list.FindIndex(r =>
                r.Id == rec.Id ||
                (r.Status == MemoryLifecycleStatus.Active && string.Equals(r.Content, rec.Content, StringComparison.OrdinalIgnoreCase)));

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
