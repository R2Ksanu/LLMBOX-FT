using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Runtime.Memory;

namespace LlmodelBox.Runtime.Continuity
{
    /// <summary>
    /// Coordinated application-scoped service bridging Layers A (Archive), B (Checkpoints),
    /// C (Long-Term Memory), and E (Progressive Disclosure Retrieval) across both Home Chat
    /// and Workspace Chat.
    /// </summary>
    public sealed class ContinuityService : IDisposable
    {
        private static readonly Regex FileRefRegex = new(
            @"\b[\w\-.\\/]+\.(?:cs|xaml|json|xml|md|py|ts|tsx|js|jsx|go|rs|java|cpp|h|sql|ps1|yaml|yml)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DecisionRegex = new(
            @"(?im)^(?:[-*•]\s*)?(?:decided|decision|architecture|approach|chose|using)\s*[:\-–]\s*(.{8,180})$",
            RegexOptions.Compiled);

        private readonly IConversationRepository _repo;
        private readonly bool _ownsRepo;
        private DateTime _lastStreamCheckpointUtc = DateTime.MinValue;

        public IConversationRepository Repository => _repo;
        public MigrationSummaryReport LastMigrationReport { get; private set; } = new();
        public int RecoveredInterruptedOnStartup { get; private set; }

        public event Action<PersistenceDiagnosticReport>? DiagnosticsChanged;
        public event Action? ArchiveUpdated;

        public ContinuityService(IConversationRepository repository, bool ownsRepository = false)
        {
            _repo = repository ?? throw new ArgumentNullException(nameof(repository));
            _ownsRepo = ownsRepository;
            _repo.DiagnosticsChanged += rep => DiagnosticsChanged?.Invoke(rep);
        }

        public static ContinuityService CreateDefault(
            string? dbPath = null,
            string? legacyChatDir = null,
            string? backupRootDir = null,
            IEnumerable<ProjectItem>? knownProjects = null)
        {
            string resolvedDb = string.IsNullOrWhiteSpace(dbPath)
                ? Path.Combine(AppConstants.DataDirectory, "llmodelbox_continuity.db")
                : dbPath!;
            string resolvedBackups = string.IsNullOrWhiteSpace(backupRootDir)
                ? Path.Combine(AppConstants.DataDirectory, "backups")
                : backupRootDir!;
            string resolvedChatDir = string.IsNullOrWhiteSpace(legacyChatDir)
                ? AppConstants.ChatHistoryDir
                : legacyChatDir!;

            var repo = new SqliteContinuityRepository(resolvedDb, resolvedBackups);
            var svc = new ContinuityService(repo, ownsRepository: true);
            svc.Initialize(resolvedChatDir, resolvedBackups, knownProjects);
            return svc;
        }

        public void Initialize(string? legacyChatDir = null, string? backupRootDir = null, IEnumerable<ProjectItem>? knownProjects = null)
        {
            if (!string.IsNullOrWhiteSpace(legacyChatDir))
            {
                LastMigrationReport = new LegacyDataMigrator().MigrateIfNeeded(
                    _repo,
                    legacyChatDir!,
                    string.IsNullOrWhiteSpace(backupRootDir) ? Path.Combine(AppConstants.DataDirectory, "backups") : backupRootDir!,
                    knownProjects);
            }

            RecoveredInterruptedOnStartup = _repo.RecoverInterruptedTurns();
        }

        public PersistenceDiagnosticReport GetDiagnostics() => _repo.GetDiagnostics();
        public bool RetryPendingWrites() => _repo.RetryPendingWrites();

        public IReadOnlyList<ConversationRecord> ListConversations(
            string? projectKey = null,
            bool includeArchived = false,
            bool includeDeleted = false,
            int offset = 0,
            int limit = int.MaxValue)
            => _repo.ListConversations(projectKey, includeArchived, includeDeleted, offset, limit);

        public ConversationRecord? GetConversation(string conversationId)
            => _repo.GetConversation(conversationId);

        public ConversationRecord StartNewConversation(
            string? projectKey,
            string? projectName,
            string? activeModelId = null,
            string? activeProvider = null,
            string? title = null,
            string? parentConversationId = null)
        {
            var now = DateTime.UtcNow;
            var conv = new ConversationRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = string.IsNullOrWhiteSpace(title) ? "New Conversation" : title.Trim(),
                ProjectKey = projectKey ?? string.Empty,
                ProjectName = projectName ?? string.Empty,
                Status = ConversationStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastActivityUtc = now,
                ActiveModelId = activeModelId ?? string.Empty,
                ActiveProvider = activeProvider ?? string.Empty,
                ParentConversationId = parentConversationId
            };
            _repo.UpsertConversation(conv);
            _repo.RecordWorkEvent(new WorkEventRecord
            {
                CorrelationId = $"conv_created:{conv.Id}",
                ConversationId = conv.Id,
                ProjectKey = conv.ProjectKey,
                EventType = "ConversationLifecycle",
                Summary = $"Started conversation '{conv.Title}'",
                Status = "Active"
            });
            ArchiveUpdated?.Invoke();
            return conv;
        }

        public ConversationRecord GetOrCreateLatestProjectConversation(
            string projectKey,
            string projectName,
            string? activeModelId = null,
            string? activeProvider = null)
        {
            var existing = _repo.ListConversations(projectKey, includeArchived: false, includeDeleted: false, offset: 0, limit: 1);
            if (existing.Count > 0) return existing[0];
            return StartNewConversation(projectKey, projectName, activeModelId, activeProvider, "Workspace Chat");
        }

        public List<ChatMessageItem> LoadMessages(string conversationId)
        {
            return _repo.GetMessages(conversationId)
                .Select(m => m.ToChatMessageItem())
                .ToList();
        }

        public void SaveConversationMessages(
            string conversationId,
            string? projectKey,
            string? projectName,
            IReadOnlyList<ChatMessageItem> messages,
            string? title = null,
            string? modelId = null,
            string? provider = null)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return;
            var existing = _repo.GetConversation(conversationId);
            var now = DateTime.UtcNow;

            string resolvedTitle = !string.IsNullOrWhiteSpace(title)
                ? title!
                : (existing != null && !string.Equals(existing.Title, "New Conversation", StringComparison.Ordinal)
                    ? existing.Title
                    : DeriveTitleFromMessages(messages, projectName));

            var conv = existing ?? new ConversationRecord
            {
                Id = conversationId,
                CreatedAtUtc = now
            };
            conv.Title = resolvedTitle;
            conv.ProjectKey = projectKey ?? conv.ProjectKey;
            conv.ProjectName = projectName ?? conv.ProjectName;
            conv.UpdatedAtUtc = now;
            conv.LastActivityUtc = now;
            conv.MessageCount = messages.Count;
            if (!string.IsNullOrWhiteSpace(modelId)) conv.ActiveModelId = modelId!;
            if (!string.IsNullOrWhiteSpace(provider)) conv.ActiveProvider = provider!;

            _repo.UpsertConversation(conv);

            var records = messages
                .Select((m, idx) =>
                {
                    if (m.CreatedAtUtc == default) m.CreatedAtUtc = now;
                    m.UpdatedAtUtc = now;
                    m.ConversationId = conversationId;
                    m.SequenceNumber = idx + 1;
                    return ConversationMessageRecord.FromChatMessageItem(m, conversationId, idx + 1);
                })
                .ToList();
            _repo.ReplaceMessages(conversationId, records);
            ArchiveUpdated?.Invoke();
        }

        public TurnRecord BeginTurn(
            string conversationId,
            string? projectKey,
            ChatMessageItem userMessage,
            ChatMessageItem assistantPlaceholder,
            int promptTokensEstimate = 0)
        {
            string turnId = Guid.NewGuid().ToString("N");
            userMessage.TurnId = turnId;
            assistantPlaceholder.TurnId = turnId;

            var turn = new TurnRecord
            {
                Id = turnId,
                ConversationId = conversationId,
                ProjectKey = projectKey ?? string.Empty,
                UserMessageId = userMessage.Id,
                AssistantMessageId = assistantPlaceholder.Id,
                UserPrompt = userMessage.Content,
                AssistantOutput = string.Empty,
                Status = TurnLifecycleStatus.Streaming,
                StartedAtUtc = DateTime.UtcNow,
                CheckpointSequence = 0,
                PromptTokensEstimate = promptTokensEstimate
            };
            _repo.UpsertTurn(turn);

            _repo.RecordWorkEvent(new WorkEventRecord
            {
                CorrelationId = $"prompt:{turnId}",
                ConversationId = conversationId,
                TurnId = turnId,
                ProjectKey = projectKey ?? string.Empty,
                EventType = "PromptSubmitted",
                Summary = Clip(userMessage.Content, 160),
                Status = "Started"
            });

            return turn;
        }

        public void CheckpointStreamingProgress(
            TurnRecord turn,
            ChatMessageItem assistantMsg,
            string partialContent,
            bool force = false)
        {
            if (turn == null || assistantMsg == null) return;
            var now = DateTime.UtcNow;
            if (!force && (now - _lastStreamCheckpointUtc).TotalMilliseconds < 650)
                return;

            _lastStreamCheckpointUtc = now;
            turn.CheckpointSequence++;
            turn.Status = TurnLifecycleStatus.Streaming;
            turn.AssistantOutput = partialContent;
            _repo.UpsertTurn(turn);

            assistantMsg.Content = partialContent;
            assistantMsg.UpdatedAtUtc = now;
            var rec = ConversationMessageRecord.FromChatMessageItem(
                assistantMsg,
                turn.ConversationId,
                Math.Max(1, assistantMsg.SequenceNumber),
                TurnLifecycleStatus.Streaming);
            _repo.UpsertMessage(rec);
        }

        public HandoffCheckpoint CompleteTurnAndCheckpoint(
            TurnRecord turn,
            ChatMessageItem assistantMsg,
            TurnLifecycleStatus finalStatus,
            string finalText,
            IReadOnlyList<WorkEventRecord>? runtimeWorkEvents = null,
            IMemoryEngine? memoryEngine = null,
            string? overrideNextStep = null,
            string? overrideSummary = null,
            int promptTokens = 0,
            int completionTokens = 0)
        {
            var now = DateTime.UtcNow;
            turn.Status = finalStatus;
            turn.EndedAtUtc = now;
            turn.AssistantOutput = finalText ?? string.Empty;
            turn.PromptTokensEstimate = promptTokens > 0 ? promptTokens : turn.PromptTokensEstimate;
            turn.CompletionTokensEstimate = completionTokens;
            if (finalStatus == TurnLifecycleStatus.Failed || finalStatus == TurnLifecycleStatus.Interrupted)
                turn.ErrorMessage = Clip(finalText ?? "Turn did not complete.", 220);

            _repo.UpsertTurn(turn);

            assistantMsg.UpdatedAtUtc = now;
            assistantMsg.Content = finalText ?? string.Empty;
            var msgRec = ConversationMessageRecord.FromChatMessageItem(
                assistantMsg,
                turn.ConversationId,
                Math.Max(1, assistantMsg.SequenceNumber),
                finalStatus);
            _repo.UpsertMessage(msgRec);

            var recordedEventIds = new List<string>();
            if (runtimeWorkEvents != null)
            {
                foreach (var we in runtimeWorkEvents)
                {
                    we.ConversationId = turn.ConversationId;
                    we.TurnId = turn.Id;
                    we.ProjectKey = turn.ProjectKey;
                    _repo.RecordWorkEvent(we);
                    recordedEventIds.Add(we.Id);
                }
            }

            var prior = _repo.GetLatestCheckpoint(turn.ConversationId);
            var conv = _repo.GetConversation(turn.ConversationId);
            var cp = BuildDeterministicCheckpoint(turn, conv, prior, finalStatus, finalText ?? string.Empty,
                assistantMsg, runtimeWorkEvents, overrideNextStep, overrideSummary, recordedEventIds);

            _repo.SaveCheckpoint(cp);

            // Structured provenance-tagged extraction (never promotes into UserGlobal automatically).
            if (memoryEngine != null && finalStatus == TurnLifecycleStatus.Completed)
            {
                CaptureStructuredObservations(memoryEngine, turn, assistantMsg, runtimeWorkEvents, cp);
            }

            ArchiveUpdated?.Invoke();
            return cp;
        }

        public void ArchiveConversation(string conversationId, bool archived = true)
        {
            _repo.ArchiveConversation(conversationId, archived);
            _repo.RecordWorkEvent(new WorkEventRecord
            {
                CorrelationId = $"arch:{conversationId}:{archived}:{DateTime.UtcNow.Ticks}",
                ConversationId = conversationId,
                EventType = "ConversationLifecycle",
                Summary = archived ? "Conversation archived" : "Conversation restored from archive",
                Status = archived ? "Archived" : "Active"
            });
            ArchiveUpdated?.Invoke();
        }

        public void SoftDeleteConversation(string conversationId, bool deleteDerivedMemories = false)
        {
            _repo.SoftDeleteConversation(conversationId, deleteDerivedMemories);
            ArchiveUpdated?.Invoke();
        }

        public void RestoreConversation(string conversationId)
        {
            _repo.RestoreConversation(conversationId);
            ArchiveUpdated?.Invoke();
        }

        public void DeleteConversationPermanently(string conversationId, bool deleteDerivedMemories = false)
        {
            _repo.DeleteConversationPermanently(conversationId, deleteDerivedMemories);
            ArchiveUpdated?.Invoke();
        }

        public int ForgetProjectMemory(string projectKey)
        {
            int count = _repo.ForgetProjectMemory(projectKey);
            ArchiveUpdated?.Invoke();
            return count;
        }

        public HandoffCheckpoint? GetLatestCheckpoint(string conversationId)
            => _repo.GetLatestCheckpoint(conversationId);

        public ProjectPreviousWorkPointer? GetPreviousWorkPointer(string? projectKey, string? currentConversationId = null)
            => string.IsNullOrWhiteSpace(projectKey) ? null : _repo.GetLatestProjectWorkPointer(projectKey!, currentConversationId);

        // Layer E Progressive Disclosure: compact startup context (strictly project-isolated + bounded)
        public string BuildCompactStartupContext(
            string? projectKey,
            string? currentConversationId,
            string? userPrompt,
            int maxChars = 1600)
        {
            var sb = new StringBuilder();
            var currentCp = !string.IsNullOrWhiteSpace(currentConversationId)
                ? _repo.GetLatestCheckpoint(currentConversationId!)
                : null;

            if (currentCp != null)
            {
                sb.AppendLine("[Active Conversation Handoff]");
                sb.AppendLine($"Goal: {currentCp.CurrentGoal}");
                if (!string.IsNullOrWhiteSpace(currentCp.SummaryText))
                    sb.AppendLine($"State: {Clip(currentCp.SummaryText, 360)}");
                if (currentCp.FilesChanged.Count > 0)
                    sb.AppendLine($"Files: {string.Join(", ", currentCp.FilesChanged.Take(8))}");
                if (currentCp.Blockers.Count > 0)
                    sb.AppendLine($"Blockers: {string.Join("; ", currentCp.Blockers.Take(3))}");
                if (!string.IsNullOrWhiteSpace(currentCp.NextStep))
                    sb.AppendLine($"Next step: {currentCp.NextStep}");
                sb.AppendLine();
            }
            else if (!string.IsNullOrWhiteSpace(projectKey))
            {
                // New conversation inside an existing project -> show compact previous-work pointer without pulling full transcript!
                var prev = _repo.GetLatestProjectWorkPointer(projectKey!, currentConversationId);
                if (prev != null && !string.IsNullOrWhiteSpace(prev.CurrentGoal))
                {
                    sb.AppendLine("[Previous Project Work Pointer (latest separate conversation)]");
                    sb.AppendLine($"Prior chat: \"{prev.ConversationTitle}\" ({prev.Status}, {prev.UpdatedAtUtc:yyyy-MM-dd HH:mm} UTC)");
                    sb.AppendLine($"Prior goal: {Clip(prev.CurrentGoal, 180)}");
                    if (!string.IsNullOrWhiteSpace(prev.NextStep))
                        sb.AppendLine($"Recorded next step: {Clip(prev.NextStep, 180)}");
                    sb.AppendLine();
                }
            }

            // Query-aware compact index of related hits within same project only (strict isolation)
            if (!string.IsNullOrWhiteSpace(userPrompt) && !string.IsNullOrWhiteSpace(projectKey))
            {
                var hits = _repo.Search(userPrompt!, projectKey, crossProject: false, limit: 4)
                    .Where(h => !string.Equals(h.ConversationId, currentConversationId, StringComparison.Ordinal))
                    .ToList();
                if (hits.Count > 0)
                {
                    sb.AppendLine("[Related History Index (use details tool if needed)]");
                    foreach (var h in hits)
                        sb.AppendLine($"- [{h.Kind} #{h.Id}] ({h.TimestampUtc:yyyy-MM-dd}): {Clip(h.Title, 60)} — {Clip(h.Snippet, 90)}");
                }
            }

            string result = sb.ToString().TrimEnd();
            return result.Length <= maxChars ? result : result[..(maxChars - 1)] + "…";
        }

        private HandoffCheckpoint BuildDeterministicCheckpoint(
            TurnRecord turn,
            ConversationRecord? conv,
            HandoffCheckpoint? prior,
            TurnLifecycleStatus finalStatus,
            string finalText,
            ChatMessageItem assistantMsg,
            IReadOnlyList<WorkEventRecord>? runtimeWorkEvents,
            string? overrideNextStep,
            string? overrideSummary,
            List<string> recordedEventIds)
        {
            string goal = !string.IsNullOrWhiteSpace(prior?.CurrentGoal)
                ? prior!.CurrentGoal
                : DeriveTitleFromText(turn.UserPrompt, 180);

            var filesChanged = new HashSet<string>(prior?.FilesChanged ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(assistantMsg.ActiveFilePath))
                filesChanged.Add(assistantMsg.ActiveFilePath);

            foreach (Match m in FileRefRegex.Matches(finalText))
                if (filesChanged.Count < 16) filesChanged.Add(m.Value);

            var completed = new List<string>(prior?.Completed ?? new List<string>());
            var decisions = new List<string>(prior?.Decisions ?? new List<string>());
            var blockers = new List<string>();
            var tests = new List<TestOutcomeRecord>(prior?.Tests ?? new List<TestOutcomeRecord>());

            if (runtimeWorkEvents != null)
            {
                foreach (var we in runtimeWorkEvents)
                {
                    if (!string.IsNullOrWhiteSpace(we.FilePath)) filesChanged.Add(we.FilePath);
                    if (we.EventType == "TestResult")
                    {
                        var outcome = string.Equals(we.Status, "Passed", StringComparison.OrdinalIgnoreCase)
                            ? TestExecutionOutcome.Passed
                            : (string.Equals(we.Status, "Failed", StringComparison.OrdinalIgnoreCase)
                                ? TestExecutionOutcome.Failed
                                : TestExecutionOutcome.Unknown);
                        tests.Add(new TestOutcomeRecord
                        {
                            Command = we.ToolName,
                            Outcome = outcome,
                            Summary = we.Summary,
                            EventId = we.Id,
                            RecordedAtUtc = we.CreatedAtUtc
                        });
                    }
                    else if (we.EventType == "ToolCompleted" && !string.IsNullOrWhiteSpace(we.Summary))
                    {
                        completed.Add(Clip(we.Summary, 140));
                    }
                    else if (we.EventType == "ToolFailed" && !string.IsNullOrWhiteSpace(we.Summary))
                    {
                        blockers.Add(Clip(we.Summary, 140));
                    }
                }
            }

            foreach (Match dm in DecisionRegex.Matches(finalText))
            {
                string d = Clip(dm.Groups[1].Value.Trim(), 160);
                if (!string.IsNullOrWhiteSpace(d) && !decisions.Contains(d, StringComparer.OrdinalIgnoreCase))
                    decisions.Add(d);
            }

            if (finalStatus == TurnLifecycleStatus.Completed && !string.IsNullOrWhiteSpace(finalText))
            {
                string firstSentence = ExtractFirstSubstantialLine(finalText);
                if (!string.IsNullOrWhiteSpace(firstSentence))
                    completed.Add(Clip(firstSentence, 160));
            }
            else if (finalStatus == TurnLifecycleStatus.Interrupted || finalStatus == TurnLifecycleStatus.Failed || finalStatus == TurnLifecycleStatus.Cancelled)
            {
                blockers.Add($"Last turn ended with status {finalStatus}: {Clip(finalText, 120)}");
            }

            string nextStep = !string.IsNullOrWhiteSpace(overrideNextStep)
                ? overrideNextStep!
                : (finalStatus == TurnLifecycleStatus.Interrupted || finalStatus == TurnLifecycleStatus.Failed
                    ? "Resume or retry from the interrupted turn's last verified checkpoint."
                    : "Verify latest changes and continue next task step.");

            string summary = !string.IsNullOrWhiteSpace(overrideSummary)
                ? overrideSummary!
                : $"Goal: {goal} | Status: {finalStatus} | Files: {(filesChanged.Count > 0 ? string.Join(", ", filesChanged.Take(5)) : "none")} | Next: {nextStep}";

            var msgIds = new List<string>(prior?.SourceMessageIds ?? new List<string>());
            if (!string.IsNullOrWhiteSpace(turn.UserMessageId)) msgIds.Add(turn.UserMessageId);
            if (!string.IsNullOrWhiteSpace(assistantMsg.Id)) msgIds.Add(assistantMsg.Id);

            return new HandoffCheckpoint
            {
                Id = $"cp_{turn.ConversationId}_{(prior?.CheckpointVersion ?? 0) + 1}",
                ConversationId = turn.ConversationId,
                ProjectKey = turn.ProjectKey,
                CheckpointVersion = (prior?.CheckpointVersion ?? 0) + 1,
                UpdatedAtUtc = DateTime.UtcNow,
                SourceTurnId = turn.Id,
                Status = finalStatus == TurnLifecycleStatus.Interrupted
                    ? ConversationStatus.Interrupted
                    : (conv?.Status ?? ConversationStatus.Active),
                CurrentGoal = goal,
                UserIntent = DeriveTitleFromText(turn.UserPrompt, 200),
                Completed = completed.Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(8).ToList(),
                Decisions = decisions.TakeLast(6).ToList(),
                FilesChanged = filesChanged.Take(16).ToList(),
                Tests = tests.TakeLast(6).ToList(),
                Blockers = blockers.Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(5).ToList(),
                NextStep = nextStep,
                SourceMessageIds = msgIds.Distinct().TakeLast(20).ToList(),
                SourceEventIds = recordedEventIds.TakeLast(20).ToList(),
                Confidence = finalStatus == TurnLifecycleStatus.Completed ? 0.90 : 0.75,
                EvidenceLevel = runtimeWorkEvents != null && runtimeWorkEvents.Count > 0
                    ? MemoryProvenance.ToolVerified
                    : MemoryProvenance.AssistantInferred,
                SummaryText = Clip(summary, 500)
            };
        }

        private static void CaptureStructuredObservations(
            IMemoryEngine memoryEngine,
            TurnRecord turn,
            ChatMessageItem assistantMsg,
            IReadOnlyList<WorkEventRecord>? workEvents,
            HandoffCheckpoint cp)
        {
            // Never save empty chatter; capture concrete decisions, blockers, and verified tool outcomes cleanly.
            foreach (var dec in cp.Decisions.TakeLast(3))
            {
                try
                {
                    memoryEngine.AddOrUpdate(new MemoryRecord
                    {
                        Id = ConversationMemory.DeterministicId("dec", dec),
                        Content = $"Decision: {dec}",
                        Scope = !string.IsNullOrWhiteSpace(turn.ProjectKey) ? MemoryScope.CurrentProject : MemoryScope.CurrentConversation,
                        Category = MemoryCategory.Decision,
                        Provenance = MemoryProvenance.AssistantInferred,
                        Source = "assistant_observation",
                        SourceMessageId = assistantMsg.Id,
                        ProjectKey = turn.ProjectKey,
                        ConversationId = turn.ConversationId,
                        Confidence = 0.74,
                        IsUserEditable = true
                    }, explicitUserApproval: false);
                }
                catch { }
            }

            if (workEvents != null)
            {
                foreach (var we in workEvents.Where(w => w.EventType == "FileChanged" && !string.IsNullOrWhiteSpace(w.FilePath)).Take(4))
                {
                    try
                    {
                        memoryEngine.AddOrUpdate(new MemoryRecord
                        {
                            Id = ConversationMemory.DeterministicId("file", $"{turn.ConversationId}:{we.FilePath}"),
                            Content = $"Modified {we.FilePath}: {Clip(we.Summary, 100)}",
                            Scope = MemoryScope.CurrentConversation,
                            Category = MemoryCategory.TaskState,
                            Provenance = MemoryProvenance.ToolVerified,
                            Source = "tool_file_change",
                            SourceEventId = we.Id,
                            SourceFilePath = we.FilePath,
                            ProjectKey = turn.ProjectKey,
                            ConversationId = turn.ConversationId,
                            Confidence = 0.95,
                            ExpiresUtc = DateTime.UtcNow.AddDays(14),
                            IsUserEditable = true
                        }, explicitUserApproval: false);
                    }
                    catch { }
                }
            }
        }

        private static string DeriveTitleFromMessages(IReadOnlyList<ChatMessageItem> messages, string? projectName)
        {
            var firstUser = messages.FirstOrDefault(m => m.IsUser && !string.IsNullOrWhiteSpace(m.Content));
            if (firstUser != null) return DeriveTitleFromText(firstUser.Content, 48);
            return string.IsNullOrWhiteSpace(projectName) ? "New Conversation" : $"{projectName} Chat";
        }

        private static string DeriveTitleFromText(string? text, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(text)) return "New Conversation";
            string clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length <= maxLen ? clean : clean[..maxLen].TrimEnd() + "...";
        }

        private static string ExtractFirstSubstantialLine(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length < 12 || line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("#"))
                    continue;
                return line;
            }
            return DeriveTitleFromText(text, 140);
        }

        private static string Clip(string? s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            string clean = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length <= max ? clean : clean[..max] + "…";
        }

        public void Dispose()
        {
            if (_ownsRepo) _repo.Dispose();
        }
    }
}
