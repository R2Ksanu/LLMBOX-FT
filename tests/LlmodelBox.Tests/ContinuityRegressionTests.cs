using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Context;
using LlmodelBox.Runtime.Continuity;
using LlmodelBox.Runtime.Knowledge;
using LlmodelBox.Runtime.Memory;
using LlmodelBox.Runtime.Tokens;
using Xunit;

namespace LlmodelBox.Tests
{
    public class ContinuityRegressionTests : IDisposable
    {
        private readonly string _dir;

        public ContinuityRegressionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cont_reg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string DbPath(string name = "continuity.db") => Path.Combine(_dir, name);
        private string BackupDir() => Path.Combine(_dir, "backups");

        [Fact] // 1. App restart continuity
        public void Test01_AppRestartContinuity_RestoresMessagesAndCheckpoint()
        {
            string db = DbPath();
            string convId;
            using (var svc = ContinuityService.CreateDefault(db, null, BackupDir()))
            {
                var conv = svc.StartNewConversation("projA", "Project A", title: "Fix Auth Bug");
                convId = conv.Id;
                var u = new ChatMessageItem { SenderRole = "User", Content = "Fix JWT token expiration in AuthService.cs" };
                var a = new ChatMessageItem { SenderRole = "Assistant", Content = "Updated AuthService.cs expiration logic." };
                var turn = svc.BeginTurn(convId, "projA", u, a);
                svc.SaveConversationMessages(convId, "projA", "Project A", new[] { u, a });
                svc.CompleteTurnAndCheckpoint(turn, a, TurnLifecycleStatus.Completed, a.Content,
                    overrideNextStep: "Run AuthService integration tests");
            }

            using (var svc2 = ContinuityService.CreateDefault(db, null, BackupDir()))
            {
                var restoredMsgs = svc2.LoadMessages(convId);
                var cp = svc2.GetLatestCheckpoint(convId);
                Assert.Equal(2, restoredMsgs.Count);
                Assert.Equal("Fix JWT token expiration in AuthService.cs", restoredMsgs[0].Content);
                Assert.NotNull(cp);
                Assert.Contains("Run AuthService integration tests", cp!.NextStep);
            }
        }

        [Fact] // 2. Next-day continuity
        public void Test02_NextDayContinuity_NewChatSeesPreviousWorkPointer()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var yesterdayChat = svc.StartNewConversation("projA", "Project A", title: "Yesterday Refactor");
            var u = new ChatMessageItem { SenderRole = "User", Content = "Refactor PaymentEngine.cs", CreatedAtUtc = DateTime.UtcNow.AddDays(-1) };
            var a = new ChatMessageItem { SenderRole = "Assistant", Content = "Updated PaymentEngine.cs", CreatedAtUtc = DateTime.UtcNow.AddDays(-1) };
            var turn = svc.BeginTurn(yesterdayChat.Id, "projA", u, a);
            svc.CompleteTurnAndCheckpoint(turn, a, TurnLifecycleStatus.Completed, a.Content, overrideNextStep: "Verify settlement rounding");

            var todayChat = svc.StartNewConversation("projA", "Project A", title: "Today Check-in");
            var ptr = svc.GetPreviousWorkPointer("projA", todayChat.Id);
            string startup = svc.BuildCompactStartupContext("projA", todayChat.Id, "Let's continue");

            Assert.NotNull(ptr);
            Assert.Contains("Verify settlement rounding", ptr!.NextStep);
            Assert.Contains("Previous Project Work Pointer", startup);
            Assert.Contains("Verify settlement rounding", startup);
        }

        [Fact] // 3. New chat does not erase memory
        public void Test03_NewChatDoesNotEraseProjectMemoryOrCheckpoints()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var mem = new MemoryEngine(svc.Repository);
            mem.AddOrUpdate(new MemoryRecord
            {
                Content = "Project uses .NET 8 WPF cleanly",
                Scope = MemoryScope.CurrentProject,
                ProjectKey = "projA"
            });

            var c1 = svc.StartNewConversation("projA", "Project A");
            var u = new ChatMessageItem { SenderRole = "User", Content = "Initial work" };
            var a = new ChatMessageItem { SenderRole = "Assistant", Content = "Done" };
            var turn = svc.BeginTurn(c1.Id, "projA", u, a);
            svc.SaveConversationMessages(c1.Id, "projA", "Project A", new[] { u, a });
            svc.CompleteTurnAndCheckpoint(turn, a, TurnLifecycleStatus.Completed, a.Content);

            var c2 = svc.StartNewConversation("projA", "Project A");
            Assert.NotEqual(c1.Id, c2.Id);
            Assert.NotEmpty(mem.Inspect(MemoryScope.CurrentProject, "projA"));
            Assert.NotNull(svc.GetLatestCheckpoint(c1.Id));
            Assert.Equal(2, svc.LoadMessages(c1.Id).Count);
        }

        [Fact] // 4. Resume versus branch
        public void Test04_ResumeRestoresTranscript_NewChatKeepsTranscriptSeparate()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var c1 = svc.StartNewConversation("projA", "Project A", title: "Refactoring Session");
            var u = new ChatMessageItem { SenderRole = "User", Content = "Start work" };
            var a = new ChatMessageItem { SenderRole = "Assistant", Content = "RAW_TRANSCRIPT_DETAIL_NEVER_AUTO_INJECTED" };
            var t = svc.BeginTurn(c1.Id, "projA", u, a);
            svc.SaveConversationMessages(c1.Id, "projA", "Project A", new[] { u, a }, title: "Refactoring Session");
            svc.CompleteTurnAndCheckpoint(t, a, TurnLifecycleStatus.Completed, a.Content);

            Assert.Equal(2, svc.LoadMessages(c1.Id).Count);
            var c2 = svc.StartNewConversation("projA", "Project A");
            Assert.Empty(svc.LoadMessages(c2.Id));
            Assert.DoesNotContain("RAW_TRANSCRIPT_DETAIL_NEVER_AUTO_INJECTED", svc.BuildCompactStartupContext("projA", c2.Id, "Unrelated new task"));
        }

        [Fact] // 5. Different project isolation
        public void Test05_DifferentProjectIsolation_NoCrossProjectLeak()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var mem = new MemoryEngine(svc.Repository);

            mem.AddOrUpdate(new MemoryRecord
            {
                Content = "ProjectA secret architecture tokenXYZ",
                Scope = MemoryScope.CurrentProject,
                ProjectKey = "projA"
            });

            var cA = svc.StartNewConversation("projA", "Proj A");
            svc.SaveConversationMessages(cA.Id, "projA", "Proj A", new[] { new ChatMessageItem { SenderRole = "User", Content = "AlphaSecretPhrase" } });

            var cB = svc.StartNewConversation("projB", "Proj B");
            var projBMemories = mem.Inspect(null, projectKey: "projB");
            var searchB = svc.Repository.Search("AlphaSecretPhrase", "projB", crossProject: false);
            var prevB = svc.GetPreviousWorkPointer("projB", cB.Id);

            Assert.DoesNotContain(projBMemories, m => m.Content.Contains("tokenXYZ"));
            Assert.Empty(searchB);
            Assert.Null(prevB);
        }

        [Fact] // 6. Same project multiple conversations & separate goals
        public void Test06_SameProjectMultipleConversations_SeparateGoalsAndHistory()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var c1 = svc.StartNewConversation("projA", "Project A", title: "Task 1");
            var u1 = new ChatMessageItem { SenderRole = "User", Content = "Build parser" };
            var a1 = new ChatMessageItem { SenderRole = "Assistant", Content = "Parser done" };
            var t1 = svc.BeginTurn(c1.Id, "projA", u1, a1);
            svc.CompleteTurnAndCheckpoint(t1, a1, TurnLifecycleStatus.Completed, a1.Content);

            var c2 = svc.StartNewConversation("projA", "Project A", title: "Task 2");
            var u2 = new ChatMessageItem { SenderRole = "User", Content = "Design WPF dark theme" };
            var a2 = new ChatMessageItem { SenderRole = "Assistant", Content = "Theme done" };
            var t2 = svc.BeginTurn(c2.Id, "projA", u2, a2);
            svc.CompleteTurnAndCheckpoint(t2, a2, TurnLifecycleStatus.Completed, a2.Content);

            Assert.Equal("Build parser", svc.GetLatestCheckpoint(c1.Id)!.CurrentGoal);
            Assert.Equal("Design WPF dark theme", svc.GetLatestCheckpoint(c2.Id)!.CurrentGoal);
            Assert.Equal(2, svc.ListConversations("projA").Count);
        }

        [Fact] // 7. 150+ conversations preserved without Take(100) truncation
        public void Test07_Over150Conversations_AllSurviveReload()
        {
            string db = DbPath();
            using (var svc = ContinuityService.CreateDefault(db, null, BackupDir()))
            {
                for (int i = 1; i <= 155; i++)
                {
                    var conv = svc.StartNewConversation("projScale", "Scale Project", title: $"Conv {i:D3}");
                    svc.SaveConversationMessages(conv.Id, "projScale", "Scale Project",
                        new[] { new ChatMessageItem { SenderRole = "User", Content = $"Turn in conv {i}" } });
                }
            }

            using (var svc2 = ContinuityService.CreateDefault(db, null, BackupDir()))
            {
                var all = svc2.ListConversations("projScale", includeArchived: true);
                Assert.Equal(155, all.Count);
            }
        }

        [Fact] // 8. Delete / Archive / Forget Project Memory semantics
        public void Test08_ArchiveRestoreSoftDeleteAndForgetProjectMemorySemantics()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var mem = new MemoryEngine(svc.Repository);
            mem.AddOrUpdate(new MemoryRecord { Content = "Project convention", Scope = MemoryScope.CurrentProject, ProjectKey = "projA" });
            mem.AddOrUpdate(new MemoryRecord { Content = "Global style", Scope = MemoryScope.UserGlobal }, explicitUserApproval: true);

            var c1 = svc.StartNewConversation("projA", "A", title: "Keep");
            var c2 = svc.StartNewConversation("projA", "A", title: "ArchiveMe");
            var c3 = svc.StartNewConversation("projA", "A", title: "DeleteMe");

            svc.ArchiveConversation(c2.Id, true);
            Assert.DoesNotContain(svc.ListConversations("projA", includeArchived: false), c => c.Id == c2.Id);
            Assert.Contains(svc.ListConversations("projA", includeArchived: true), c => c.Id == c2.Id);
            svc.RestoreConversation(c2.Id);
            Assert.Contains(svc.ListConversations("projA", includeArchived: false), c => c.Id == c2.Id);

            svc.SoftDeleteConversation(c3.Id, deleteDerivedMemories: false);
            Assert.DoesNotContain(svc.ListConversations("projA", includeArchived: true), c => c.Id == c3.Id);
            Assert.NotEmpty(mem.Inspect(MemoryScope.CurrentProject, "projA"));

            svc.ForgetProjectMemory("projA");
            Assert.Empty(mem.Inspect(MemoryScope.CurrentProject, "projA"));
            Assert.NotEmpty(mem.Inspect(MemoryScope.UserGlobal));
            Assert.Contains(svc.ListConversations("projA"), c => c.Id == c1.Id);
        }

        [Fact] // 9. Corrupt archive quarantined and reported, not silently empty
        public void Test09_CorruptArchive_IsQuarantinedAndReported()
        {
            string badDb = DbPath("corrupt.db");
            File.WriteAllText(badDb, "NOT A VALID SQLITE DATABASE HEADER");

            using var repo = new SqliteContinuityRepository(badDb, BackupDir());
            var diag = repo.GetDiagnostics();
            Assert.Equal(PersistenceState.RecoveredFromBackup, diag.State);
            Assert.NotEmpty(diag.QuarantineBackups);
            Assert.True(File.Exists(diag.QuarantineBackups[0]));

            var jsonStore = new JsonChatHistoryStore(Path.Combine(_dir, "jhist"), BackupDir());
            string rawChatFile = Path.Combine(_dir, "jhist", "bad.json");
            Directory.CreateDirectory(Path.GetDirectoryName(rawChatFile)!);
            // Write directly at hashed path for key "k1"
            jsonStore.Save("k1", new[] { new ChatMessageItem { Content = "hi" } });
            string hashedFile = Directory.GetFiles(Path.Combine(_dir, "jhist"), "*.json").First();
            File.WriteAllText(hashedFile, "{ broken json");
            _ = jsonStore.Load("k1");
            Assert.Equal(PersistenceState.DegradedError, jsonStore.LastDiagnostics.State);
            Assert.NotEmpty(jsonStore.LastDiagnostics.QuarantineBackups);
        }

        [Fact] // 10. Write failure retains dirty buffer & retries cleanly
        public void Test10_WriteFailure_TracksDirtyQueueAndRetriesCleanly()
        {
            using var repo = new SqliteContinuityRepository(DbPath(), BackupDir());
            repo.SetSimulatedWriteFailure(true);
            var conv = new ConversationRecord { Title = "Unsaved pending", ProjectKey = "projA" };
            repo.UpsertConversation(conv);

            var diag = repo.GetDiagnostics();
            Assert.Equal(PersistenceState.DegradedError, diag.State);
            Assert.True(diag.PendingRetryCount > 0);

            repo.SetSimulatedWriteFailure(false);
            Assert.True(repo.RetryPendingWrites());
            Assert.Equal(PersistenceState.Healthy, repo.GetDiagnostics().State);
            Assert.NotNull(repo.GetConversation(conv.Id));
        }

        [Fact] // 11. Interrupted streaming recovery on restart
        public void Test11_InterruptedStreaming_RecoveredAndLabeledOnRestart()
        {
            string db = DbPath();
            string cid;
            using (var svc = ContinuityService.CreateDefault(db, null, BackupDir()))
            {
                var c = svc.StartNewConversation("projA", "Proj A");
                cid = c.Id;
                var u = new ChatMessageItem { SenderRole = "User", Content = "Write long function" };
                var a = new ChatMessageItem { SenderRole = "Assistant", Content = "" };
                svc.SaveConversationMessages(cid, "projA", "Proj A", new[] { u, a });
                var t = svc.BeginTurn(cid, "projA", u, a);
                svc.CheckpointStreamingProgress(t, a, "public void PartialStart() {", force: true);
                // Simulated crash before CompleteTurnAndCheckpoint!
            }

            using (var svc2 = ContinuityService.CreateDefault(db, null, BackupDir()))
            {
                Assert.True(svc2.RecoveredInterruptedOnStartup > 0);
                var turns = svc2.Repository.GetTurns(cid);
                Assert.Equal(TurnLifecycleStatus.Interrupted, turns[0].Status);
                var msgs = svc2.LoadMessages(cid);
                Assert.Contains("public void PartialStart()", msgs[1].Content);
                Assert.Contains("Interrupted", msgs[1].ActivityDetail);
            }
        }

        [Fact] // 12. Cancellation / failure status preservation
        public void Test12_CancelledAndFailedTurns_AreNotMarkedCompleted()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var c = svc.StartNewConversation("projA", "Proj A");
            var u = new ChatMessageItem { SenderRole = "User", Content = "Try command" };
            var a = new ChatMessageItem { SenderRole = "Assistant", Content = "partial" };
            var t = svc.BeginTurn(c.Id, "projA", u, a);
            svc.SaveConversationMessages(c.Id, "projA", "Proj A", new[] { u, a });

            var cp = svc.CompleteTurnAndCheckpoint(t, a, TurnLifecycleStatus.Cancelled, "partial output before stop");
            var turns = svc.Repository.GetTurns(c.Id);
            Assert.Equal(TurnLifecycleStatus.Cancelled, turns[0].Status);
            Assert.NotEmpty(cp.Blockers);
        }

        [Fact] // 13. Tool event idempotency on replay
        public void Test13_ToolEvents_IdempotentByCorrelationId()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var c = svc.StartNewConversation("projA", "Proj A");
            bool first = svc.Repository.RecordWorkEvent(new WorkEventRecord
            {
                CorrelationId = "tool-call-abc-123",
                ConversationId = c.Id,
                ProjectKey = "projA",
                EventType = "ToolCompleted",
                ToolName = "WriteFile",
                Summary = "Wrote App.cs"
            });
            bool second = svc.Repository.RecordWorkEvent(new WorkEventRecord
            {
                CorrelationId = "tool-call-abc-123",
                ConversationId = c.Id,
                ProjectKey = "projA",
                EventType = "ToolCompleted",
                ToolName = "WriteFile",
                Summary = "Wrote App.cs"
            });

            Assert.True(first);
            Assert.False(second);
            Assert.Single(svc.Repository.GetWorkEvents(c.Id).Where(w => w.CorrelationId == "tool-call-abc-123"));
        }

        [Fact] // 14. Evidence correctness (suggested commands not recorded as passed tests)
        public void Test14_EvidenceCorrectness_OnlyActualTestEventsRecordedAsPassed()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var c = svc.StartNewConversation("projA", "Proj A");
            var u = new ChatMessageItem { SenderRole = "User", Content = "How do I test?" };
            var a = new ChatMessageItem { SenderRole = "Assistant", Content = "Run `dotnet test` to verify; suggested command passed locally." };
            var t = svc.BeginTurn(c.Id, "projA", u, a);

            var cp = svc.CompleteTurnAndCheckpoint(t, a, TurnLifecycleStatus.Completed, a.Content, runtimeWorkEvents: null);
            Assert.Empty(cp.Tests);
        }

        [Fact] // 15. Assistant + user extraction vs transient chatter
        public void Test15_AssistantAndUserExtraction_PreservesProvenanceAndIgnoresChatter()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var mem = new MemoryEngine(svc.Repository);

            mem.Observe("projA", "c1", "I prefer clean code", "Decision: Use WAL mode in SQLite for concurrency.");
            mem.Observe("projA", "c1", "Sounds good, thanks!", "Glad to help!");

            var items = mem.Inspect(null, "projA", "c1");
            Assert.Contains(items, r => r.Provenance == MemoryProvenance.UserExplicit && r.Content.Contains("clean code"));
            Assert.Contains(items, r => r.Provenance == MemoryProvenance.AssistantInferred && r.Content.Contains("WAL mode"));
            Assert.DoesNotContain(items, r => r.Content.Contains("thanks", StringComparison.OrdinalIgnoreCase));
        }

        [Fact] // 16. Global-memory approval enforcement
        public void Test16_ProjectDetailsAreNeverAutoSavedAsUserGlobal()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var mem = new MemoryEngine(svc.Repository);

            mem.Observe("projA", "c1", "I am using PostgreSQL 16 for this repository", "Ok");
            Assert.Empty(mem.Inspect(MemoryScope.UserGlobal));
            Assert.Throws<InvalidOperationException>(() =>
                mem.AddOrUpdate(new MemoryRecord { Content = "Auto global", Scope = MemoryScope.UserGlobal }, explicitUserApproval: false));
        }

        [Fact] // 17. Contradiction / correction supersession
        public void Test17_ContradictionCorrection_SupersedesOldActiveRecord()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var mem = new MemoryEngine(svc.Repository);

            var oldRec = mem.AddOrUpdate(new MemoryRecord
            {
                Content = "Build tool is npm",
                Scope = MemoryScope.CurrentProject,
                ProjectKey = "projA",
                CanonicalKey = "build.tool"
            });

            var newRec = mem.AddOrUpdate(new MemoryRecord
            {
                Content = "Build tool is pnpm",
                Scope = MemoryScope.CurrentProject,
                ProjectKey = "projA",
                CanonicalKey = "build.tool"
            });

            var active = mem.Inspect(MemoryScope.CurrentProject, "projA");
            Assert.Single(active, r => r.CanonicalKey == "build.tool");
            Assert.Equal("Build tool is pnpm", active[0].Content);
            var oldState = svc.Repository.GetMemory(oldRec.Id);
            Assert.Equal(MemoryLifecycleStatus.Superseded, oldState!.Status);
            Assert.Equal(newRec.Id, oldState.SupersededById);
        }

        [Fact] // 18. Secret redaction before persistence
        public void Test18_SecretRedaction_StripsApiKeysAndTokensBeforeSaving()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var mem = new MemoryEngine(svc.Repository);

            mem.AddOrUpdate(new MemoryRecord
            {
                Content = "Use sk-1234567890abcdefghijklmnop with Authorization: Bearer secretTokenValue999",
                Scope = MemoryScope.CurrentProject,
                ProjectKey = "projA"
            });

            var stored = mem.Inspect(MemoryScope.CurrentProject, "projA").Single();
            Assert.DoesNotContain("sk-1234567890abcdefghijklmnop", stored.Content);
            Assert.DoesNotContain("secretTokenValue999", stored.Content);
            Assert.Contains("[REDACTED", stored.Content);
        }

        [Fact] // 19. Progressive disclosure bounded startup context + deep detail fetch
        public async Task Test19_ProgressiveDisclosure_CompactStartupPlusSelectiveTools()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var cOld = svc.StartNewConversation("projA", "Proj A", title: "Old Deep Thread");
            var bigList = Enumerable.Range(1, 30)
                .Select(i => new ChatMessageItem
                {
                    SenderRole = i % 2 == 0 ? "Assistant" : "User",
                    Content = $"Historical item {i} talking about SqliteWAL indexing " + new string('z', 200)
                })
                .ToList();
            svc.SaveConversationMessages(cOld.Id, "projA", "Proj A", bigList);

            var cNew = svc.StartNewConversation("projA", "Proj A");
            string startup = svc.BuildCompactStartupContext("projA", cNew.Id, "SqliteWAL indexing", maxChars: 900);
            Assert.True(startup.Length <= 905);

            var searchTool = new SearchConversationHistoryTool(svc.Repository, "projA");
            var hit = await searchTool.ExecuteAsync(new AiToolCall { Name = "SearchConversationHistory", ArgumentsJson = """{"query":"SqliteWAL","limit":3}""" }, CancellationToken.None);
            Assert.False(hit.IsError);
            Assert.Contains(cOld.Id, hit.Output);

            var detailTool = new GetConversationDetailsTool(svc.Repository);
            var det = await detailTool.ExecuteAsync(new AiToolCall { Name = "GetConversationDetails", ArgumentsJson = $$"""{"conversationId":"{{cOld.Id}}","limit":2}""" }, CancellationToken.None);
            Assert.False(det.IsError);
            Assert.Contains("Old Deep Thread", det.Output);
        }

        [Fact] // 20. Context budget strictly enforced
        public void Test20_ContextBudget_EnforcesSmallWindowAndReservesOutput()
        {
            var req = new ContextRequest
            {
                Model = new ModelItem { Id = "gpt-4o", Name = "GPT-4o", Provider = "OpenAI" },
                Settings = new UserSettings(),
                UserPrompt = "Critical live question",
                MemoryPreamble = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"Memory line {i} content block")),
                History = Enumerable.Range(1, 20).Select(i => AiMessage.User($"Turn {i} filler " + new string('x', 150))).ToList()
            };
            var caps = new ModelCapabilities { ContextWindow = 800, InputTokenLimit = 800, OutputTokenLimit = 256 };
            var built = new ContextEngine().Build(req, caps);

            Assert.True(built.InputTokenEstimate <= built.InputBudget + 32);
            Assert.Contains("Critical live question", built.Request.Messages[^1].Text);
        }

        [Fact] // 21 & 22. Migration idempotency + Legacy imports (home_conversations, chat, mem, global)
        public void Test21_22_LegacyMigration_ImportsAllWithoutDuplication()
        {
            string legacyChatDir = Path.Combine(_dir, "legacy_chat");
            Directory.CreateDirectory(legacyChatDir);

            // Seed 125 legacy Home threads (must all import, not Take(100))
            var legacyThreads = Enumerable.Range(1, 125).Select(i => new LegacyHomeThreadDto
            {
                Id = $"home_{i}",
                Title = $"Home Chat {i}",
                ProjectPath = i == 1 ? @"C:\MyProj" : "",
                ProjectName = i == 1 ? "MyProj" : "",
                Messages = new List<ChatMessageItem> { new() { SenderRole = "User", Content = $"Msg {i}" } }
            }).ToList();
            File.WriteAllText(Path.Combine(legacyChatDir, "home_conversations.json"), JsonSerializer.Serialize(legacyThreads));

            // Seed legacy project memory + global memory + project transcript via legacy stores
            var legacyMemStore = new JsonMemoryStore(legacyChatDir, Path.Combine(legacyChatDir, "global-preferences.mem.json"), BackupDir());
            legacyMemStore.Save(@"C:\MyProj", new ConversationMemory
            {
                SessionGoal = "Legacy App Goal",
                Facts = new List<string> { "Legacy uses C#" }
            });
            legacyMemStore.SaveGlobal(new List<MemoryRecord>
            {
                new() { Id = "g1", Content = "Global dark mode pref", Scope = MemoryScope.UserGlobal }
            });

            var legacyHistStore = new JsonChatHistoryStore(legacyChatDir, BackupDir());
            legacyHistStore.Save(@"C:\OtherLegacyProj", new[] { new ChatMessageItem { SenderRole = "User", Content = "Old IDE msg" } });

            string db = DbPath("migrated.db");
            var projList = new[] { new ProjectItem { Name = "MyProj", Path = @"C:\MyProj" }, new ProjectItem { Name = "Other", Path = @"C:\OtherLegacyProj" } };

            using (var svc = ContinuityService.CreateDefault(db, legacyChatDir, BackupDir(), projList))
            {
                Assert.True(svc.LastMigrationReport.Executed);
                Assert.Equal(125, svc.LastMigrationReport.HomeThreadsImported);
                Assert.Equal(1, svc.LastMigrationReport.ProjectTranscriptsImported);
                Assert.True(Directory.Exists(svc.LastMigrationReport.BackupDirectory));
                Assert.Equal(126, svc.ListConversations(null, includeArchived: true).Count);

                // Verify idempotency on second run
                var secondReport = new LegacyDataMigrator().MigrateIfNeeded(svc.Repository, legacyChatDir, BackupDir(), projList);
                Assert.False(secondReport.Executed);
                Assert.Equal(126, svc.ListConversations(null, includeArchived: true).Count);
            }
        }

        [Fact] // 23. Concurrent access between UI inspector and runtime
        public async Task Test23_ConcurrentAccess_SerializedWritersDoNotOverwriteRecords()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var mem = new MemoryEngine(svc.Repository);

            var t1 = Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    mem.AddOrUpdate(new MemoryRecord
                    {
                        Id = $"ui_{i}",
                        Content = $"UI note {i}",
                        Scope = MemoryScope.CurrentProject,
                        ProjectKey = "projConc"
                    });
                }
            });

            var t2 = Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    var c = svc.StartNewConversation("projConc", "Conc", title: $"Run {i}");
                    svc.SaveConversationMessages(c.Id, "projConc", "Conc",
                        new[] { new ChatMessageItem { SenderRole = "User", Content = $"Runtime {i}" } });
                }
            });

            await Task.WhenAll(t1, t2);
            Assert.Equal(20, mem.Inspect(MemoryScope.CurrentProject, "projConc").Count);
            Assert.Equal(20, svc.ListConversations("projConc").Count);
        }

        [Fact] // 24. Works offline with no model provider configured
        public void Test24_NoModelProviderConfigured_DeterministicCheckpointWorks()
        {
            using var svc = ContinuityService.CreateDefault(DbPath(), null, BackupDir());
            var c = svc.StartNewConversation("projOffline", "Offline");
            var u = new ChatMessageItem { SenderRole = "User", Content = "Inspect Service.cs offline" };
            var a = new ChatMessageItem { SenderRole = "Assistant", Content = "Decision: Kept deterministic parsing active in Service.cs" };
            var turn = svc.BeginTurn(c.Id, "projOffline", u, a);
            var cp = svc.CompleteTurnAndCheckpoint(turn, a, TurnLifecycleStatus.Completed, a.Content);

            Assert.NotNull(cp);
            Assert.Contains("Service.cs", cp.FilesChanged);
            Assert.Contains("Kept deterministic parsing active in Service.cs", cp.Decisions[0]);
        }
    }
}
