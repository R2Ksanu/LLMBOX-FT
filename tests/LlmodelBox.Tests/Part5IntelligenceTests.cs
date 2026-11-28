using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Context;
using LlmodelBox.Runtime.Knowledge;
using LlmodelBox.Runtime.Memory;
using LlmodelBox.Runtime.Tokens;
using Xunit;

namespace LlmodelBox.Tests
{
    public class Part5IntelligenceTests : IDisposable
    {
        private readonly string _tempDir;

        public Part5IntelligenceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "p5_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void MemoryEngine_SupportsAllFiveScopesAndCrud()
        {
            var store = new JsonMemoryStore(Path.Combine(_tempDir, "mem"));
            var engine = new MemoryEngine(store);

            var turnRec = engine.AddOrUpdate(new MemoryRecord
            {
                Content = "Check line 42 NullReference",
                Scope = MemoryScope.CurrentTurn,
                TurnId = "t1",
                Confidence = 0.9
            });
            var convRec = engine.AddOrUpdate(new MemoryRecord
            {
                Content = "Focus on AvalonEdit tabs",
                Scope = MemoryScope.CurrentConversation,
                ConversationId = "c1",
                Confidence = 0.85
            });
            var sessRec = engine.AddOrUpdate(new MemoryRecord
            {
                Content = "Running Release build verification",
                Scope = MemoryScope.CurrentSession,
                Confidence = 0.95
            });
            var projRec = engine.AddOrUpdate(new MemoryRecord
            {
                Content = "Use WPF DynamicResource brushes",
                Scope = MemoryScope.CurrentProject,
                ProjectKey = "projA",
                Confidence = 0.98
            });
            var globalRec = engine.AddOrUpdate(new MemoryRecord
            {
                Content = "Prefer concise git commits",
                Scope = MemoryScope.UserGlobal,
                Confidence = 1.0
            }, explicitUserApproval: true);

            var all = engine.Inspect(null, projectKey: "projA", conversationId: "c1", turnId: "t1");
            Assert.Equal(5, all.Count);
            Assert.Contains(all, r => r.Id == turnRec.Id && r.Scope == MemoryScope.CurrentTurn);
            Assert.Contains(all, r => r.Id == convRec.Id && r.Scope == MemoryScope.CurrentConversation);
            Assert.Contains(all, r => r.Id == sessRec.Id && r.Scope == MemoryScope.CurrentSession);
            Assert.Contains(all, r => r.Id == projRec.Id && r.Scope == MemoryScope.CurrentProject);
            Assert.Contains(all, r => r.Id == globalRec.Id && r.Scope == MemoryScope.UserGlobal);

            // Update and Delete lifecycle
            Assert.True(engine.UpdateRecord(projRec.Id, "Always use DynamicResource + GhostToolBtn", projectKey: "projA"));
            var updated = engine.Inspect(MemoryScope.CurrentProject, projectKey: "projA");
            Assert.Contains(updated, r => r.Id == projRec.Id && r.Content.Contains("GhostToolBtn"));

            Assert.True(engine.DeleteRecord(projRec.Id, projectKey: "projA"));
            Assert.DoesNotContain(engine.Inspect(MemoryScope.CurrentProject, projectKey: "projA"), r => r.Id == projRec.Id);
        }

        [Fact]
        public void MemoryEngine_BlocksUnapprovedGlobalPromotionAndExpiresRecords()
        {
            var store = new JsonMemoryStore(Path.Combine(_tempDir, "mem2"));
            var engine = new MemoryEngine(store);

            Assert.Throws<InvalidOperationException>(() =>
                engine.AddOrUpdate(new MemoryRecord
                {
                    Content = "Unauthorized global preference",
                    Scope = MemoryScope.UserGlobal
                }, explicitUserApproval: false));

            // Arbitrary normal chat messages must NOT auto-create facts or global records.
            engine.Observe("projX", "conv1", "Can you inspect MainWindow.xaml please?", "Sure, looking at MainWindow.xaml.");
            var snap = engine.Recall("projX", "conv1");
            Assert.Empty(snap.Facts);
            Assert.Empty(engine.Inspect(MemoryScope.UserGlobal, "projX"));

            // Expiration check
            engine.AddOrUpdate(new MemoryRecord
            {
                Content = "Temporary note",
                Scope = MemoryScope.CurrentSession,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(-5)
            });
            Assert.Empty(engine.Inspect(MemoryScope.CurrentSession));
        }

        [Fact]
        public void AutoCompact_TriggersAtSoftThresholdAndGeneratesHandoff()
        {
            var history = new List<AiMessage>();
            string detail = new string('x', 160);
            for (int i = 0; i < 6; i++)
            {
                history.Add(AiMessage.User($"Turn {i}: update Engine{i}.cs with validation checks {detail}."));
                history.Add(AiMessage.Assistant($"Updated Engine{i}.cs and verified unit tests passed {detail}."));
            }

            var compressor = new ContextCompressionEngine(new AutoCompactOptions
            {
                Enabled = true,
                SoftTriggerRatio = 0.70,
                KeepRecentMessages = 4
            });

            // History ~650 tok + fixedOverhead 500 = ~1150 tok, between 70% (980) and 100% (1400) -> AutoThreshold.
            var result = compressor.CompactIfNeeded(
                history,
                existingSummary: null,
                inputBudget: 1400,
                fixedOverheadTokens: 500,
                TokenizerRegistry.Default.FallbackCounter);

            Assert.True(result.CompactionTriggered);
            Assert.Equal("AutoThreshold", result.TriggerReason);
            Assert.Equal(4, result.CompactedHistory.Count);
            Assert.True(result.SummarizedMessagesCount > 0);
            Assert.True(result.TokensSaved > 0);
            Assert.Contains("Auto-Compacted Conversation Handoff", result.ConversationSummary);
            Assert.Contains("Engine0.cs", result.ConversationSummary);
        }

        [Fact]
        public void ContextEngine_ScoresFilesDeduplicatesAndReportsSources()
        {
            string openCode = "public class TokenBudgetManager { public int AllocateTokens() => 42; }";
            var req = new ContextRequest
            {
                Model = new ModelItem { Id = "gpt-4o", Name = "GPT-4o", Provider = "OpenAI", Context = "128k" },
                Settings = new UserSettings(),
                UserPrompt = "How does TokenBudgetManager.cs calculate tokens?",
                CandidateFiles = new List<CandidateFileContext>
                {
                    new() { Path = @"C:\repo\Other.cs", Content = "class Other {}", IsActiveOpenFile = false, OpenRecencyRank = 2 },
                    new() { Path = @"C:\repo\TokenBudgetManager.cs", Content = openCode, IsActiveOpenFile = true, IsDirty = true, OpenRecencyRank = 0 }
                },
                OpenFilePath = @"C:\repo\TokenBudgetManager.cs",
                OpenFileText = openCode,
                KnowledgeBlock = "[Knowledge — relevant excerpts from this project]\n\n// TokenBudgetManager.cs:1-1\n```\n" + openCode + "\n```\n\n// ContextEngine.cs:1-1\n```\npublic class ContextEngine {}\n```"
            };

            var res = new ContextEngine().Build(req, new ModelCapabilities { ContextWindow = 32768, InputTokenLimit = 32768, OutputTokenLimit = 2048 });

            Assert.True(res.DeduplicatedItemsCount >= 1);
            Assert.NotEmpty(res.ScoredFiles);
            Assert.Equal(@"C:\repo\TokenBudgetManager.cs", res.ScoredFiles[0].Path);
            Assert.Contains(res.SourceReport, s => s.Category == "User Prompt" && s.Included);
            Assert.Contains(res.SourceReport, s => s.Category == "Knowledge Retrieval" && s.Deduplicated);
        }

        private sealed class MockEmbeddingProvider : IKnowledgeEmbeddingProvider
        {
            private readonly bool _fail;
            public string ProviderId => "mock-emb";
            public bool IsConfigured { get; }
            public MockEmbeddingProvider(bool configured, bool fail = false) { IsConfigured = configured; _fail = fail; }

            public Task<IReadOnlyList<float[]?>> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
            {
                if (_fail) throw new InvalidOperationException("offline");
                IReadOnlyList<float[]?> res = inputs.Select(txt =>
                {
                    float v0 = txt.Contains("authentication", StringComparison.OrdinalIgnoreCase) || txt.Contains("AuthGuard", StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
                    float v1 = txt.Contains("logging", StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
                    return (float[]?)new[] { v0, v1, 0.1f };
                }).ToList();
                return Task.FromResult(res);
            }
        }

        [Fact]
        public async Task KnowledgeEngine_IncrementalHashOverlapExclusionsAndHybridFallback()
        {
            string proj = Path.Combine(_tempDir, "kb");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, ".llmodelboxignore"), "*.generated.cs\nsecret_dir/\n");
            Directory.CreateDirectory(Path.Combine(proj, "secret_dir"));
            File.WriteAllText(Path.Combine(proj, "secret_dir", "Keys.cs"), "class SecretKeys {}");
            File.WriteAllText(Path.Combine(proj, "Model.generated.cs"), "class GeneratedCode {}");

            string authFile = Path.Combine(proj, "AuthGuard.cs");
            var lines = Enumerable.Range(1, 55).Select(i => $"// AuthGuard authentication line {i} token verification guard_{i}");
            File.WriteAllText(authFile, string.Join("\n", lines));

            string logFile = Path.Combine(proj, "Logger.cs");
            File.WriteAllText(logFile, "public class Logger { public void WriteInfo() {} }");

            var cacheStore = new JsonKnowledgeIndexStore(Path.Combine(_tempDir, "kb_cache"));
            var opts = new KnowledgeIndexOptions { ChunkMaxChars = 500, ChunkOverlapLines = 4, PersistToDisk = true };
            var engine = new KnowledgeEngine(cacheStore, null, opts);

            var idx1 = engine.Index(proj, opts);
            Assert.DoesNotContain(idx1.Chunks, c => c.RelativePath.Contains("generated") || c.RelativePath.Contains("secret_dir"));

            // Verify overlapping line spans across AuthGuard chunks
            var authChunks = idx1.Chunks.Where(c => c.RelativePath == "AuthGuard.cs").ToList();
            Assert.True(authChunks.Count >= 2);
            Assert.True(authChunks[1].StartLine <= authChunks[0].EndLine, "Expected overlapping line boundary across consecutive chunks");

            // Touch Logger.cs timestamp without changing content -> fresh engine instance loads cache & reuses identical content hashes
            File.SetLastWriteTimeUtc(logFile, DateTime.UtcNow.AddSeconds(5));
            var engine2 = new KnowledgeEngine(cacheStore, null, opts);
            var idx2 = engine2.Index(proj, opts);
            Assert.Equal(2, idx2.LastStats.ReusedFiles);
            Assert.Equal(0, idx2.LastStats.ReindexedFiles);

            // Modify Logger.cs content -> only Logger.cs reindexed
            File.WriteAllText(logFile, "public class Logger { public void WriteErrorLog() {} }");
            var idx3 = engine2.Index(proj, opts);
            Assert.Equal(1, idx3.LastStats.ReusedFiles);
            Assert.Equal(1, idx3.LastStats.ReindexedFiles);

            // Hybrid retrieval via configured embedding provider vs deterministic fallback on failure
            var hybridHits = await engine2.RetrieveAsync(idx3, "authentication", 2, new MockEmbeddingProvider(configured: true));
            Assert.NotEmpty(hybridHits);
            Assert.Equal(KnowledgeRetrievalMode.Hybrid, hybridHits[0].RetrievalMode);
            Assert.Equal("AuthGuard.cs", hybridHits[0].RelativePath);

            var fallbackHits = await engine2.RetrieveAsync(idx3, "WriteErrorLog", 2, new MockEmbeddingProvider(configured: true, fail: true));
            Assert.NotEmpty(fallbackHits);
            Assert.Equal(KnowledgeRetrievalMode.DeterministicFallback, fallbackHits[0].RetrievalMode);
            Assert.Equal("Logger.cs", fallbackHits[0].RelativePath);
        }
    }
}
