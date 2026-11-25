using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Infrastructure;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Presentation;
using LlmodelBox.Providers;
using LlmodelBox.Runtime.Intent;
using LlmodelBox.Runtime.Middleware;
using Xunit;

namespace LlmodelBox.Tests
{
    public class ArchitectureLayerTests
    {
        private sealed class CustomMockAdapter : IModelProvider
        {
            public string Name => "CustomMock";
            public bool CanHandle(ModelItem model) => model?.Provider == "CustomMock";
            public ModelCapabilities GetCapabilities(ModelItem model) => new() { Streaming = true, ToolCalling = true };
            public Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken ct)
                => Task.FromResult(new AiResponse { Text = "from-custom-adapter", Provider = Name });
            public async IAsyncEnumerable<AiEvent> StreamAsync(AiRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                yield return AiEvent.Done(new AiResponse { Text = "from-custom-adapter", Provider = Name });
            }
        }

        [Theory]
        [InlineData("HTTP 401 Unauthorized: invalid api key", ProviderErrorKind.Authentication, false)]
        [InlineData("429 Too Many Requests: rate limit exceeded", ProviderErrorKind.RateLimited, true)]
        [InlineData("insufficient_quota: billing limit hit", ProviderErrorKind.QuotaExceeded, false)]
        [InlineData("maximum context length exceeded", ProviderErrorKind.ContextExceeded, false)]
        [InlineData("404 model_not_found", ProviderErrorKind.ModelNotFound, false)]
        [InlineData("503 Service temporarily overloaded", ProviderErrorKind.ServerError, true)]
        public void ProviderErrorNormalizer_ClassifiesErrorsAccurately(string msg, ProviderErrorKind expectedKind, bool expectedTransient)
        {
            var normalized = ProviderErrorNormalizer.Normalize(msg);
            Assert.Equal(expectedKind, normalized.Kind);
            Assert.Equal(expectedTransient, normalized.IsTransient);
            Assert.NotEmpty(normalized.UserMessage);
        }

        [Fact]
        public async Task ProviderGateway_AllowsAddingAdapterWithoutModifyingRuntimeOrUi()
        {
            var fallback = new CloudApiProvider(new Services.CloudApiService(new HttpClient()));
            var gateway = new ProviderGateway(new IModelProvider[] { fallback });

            Assert.Single(gateway.Adapters);
            gateway.RegisterAdapter(new CustomMockAdapter());
            Assert.Equal(2, gateway.Adapters.Count);

            var req = new AiRequest { Model = new ModelItem { Id = "m1", Name = "M1", Provider = "CustomMock" } };
            var selected = gateway.SelectAdapter(req);
            Assert.Equal("CustomMock", selected.Name);

            var resp = await gateway.ExecuteAsync(req, CancellationToken.None);
            Assert.Equal("from-custom-adapter", resp.Text);
        }

        [Fact]
        public void ProviderGateway_ChecksCredentialsAcrossLocalAndCloudProviders()
        {
            var gateway = new ProviderGateway(new IModelProvider[] { new CustomMockAdapter() });
            var settings = new UserSettings { ApiKeys = new ApiConfig { OpenAI = "sk-test" } };

            Assert.True(gateway.HasConfiguredCredentials(new ModelItem { Provider = "Ollama" }, settings));
            Assert.True(gateway.HasConfiguredCredentials(new ModelItem { Provider = "OpenAI" }, settings));
            Assert.False(gateway.HasConfiguredCredentials(new ModelItem { Provider = "Anthropic" }, settings));
        }

        [Fact]
        public async Task CheckpointEngineAndFileSnapshotService_CaptureRollbackAndBlockTraversal()
        {
            string root = Path.Combine(Path.GetTempPath(), "LlmodelBox_Arch_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "App.cs");
            await File.WriteAllTextAsync(file, "original");

            try
            {
                var cpEngine = new CheckpointEngine();
                var cp = cpEngine.CreateCheckpoint("Before Edit", root);
                cpEngine.CaptureBeforeEdit(cp, "App.cs");
                cpEngine.CaptureBeforeEdit(cp, "NewFile.cs");

                await File.WriteAllTextAsync(file, "modified");
                await File.WriteAllTextAsync(Path.Combine(root, "NewFile.cs"), "created");

                int restored = await cpEngine.RestoreCheckpointAsync(cp.Id);
                Assert.Equal(2, restored);
                Assert.Equal("original", await File.ReadAllTextAsync(file));
                Assert.False(File.Exists(Path.Combine(root, "NewFile.cs")));

                var snapSvc = new FileSnapshotService();
                Assert.Throws<UnauthorizedAccessException>(() => snapSvc.Capture(root, "../outside.txt"));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void MiddlewareEngines_CoverPlanningTokensCompressionNormalizationVerificationAndEvents()
        {
            var planner = new TaskPlanningEngine();
            var plan = planner.BuildPlan("Fix bug in App.cs", new IntentResult { Kind = IntentKind.EditCode, WantsTools = true });
            Assert.Equal(3, plan.Steps.Count);
            Assert.Contains(plan.Steps[0].Id, plan.Steps[1].DependsOn);

            var tokens = new TokenIntelligenceEngine();
            var est = tokens.Reconcile("hello world", "hi", null);
            Assert.False(est.IsProviderAuthoritative);
            Assert.True(est.TotalTokens > 0);
            var auth = tokens.Reconcile("hello", "hi", new TokenUsageRecord { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 });
            Assert.True(auth.IsProviderAuthoritative);
            Assert.Equal(15, auth.TotalTokens);

            var comp = new ContextCompressionEngine();
            var msgs = Enumerable.Range(1, 10).Select(i => AiMessage.User(new string('a', 200) + i)).ToList();
            var compressed = comp.CompressHistory(msgs, 120);
            Assert.True(compressed.DroppedCount > 0);
            Assert.NotNull(compressed.SummaryNote);

            var norm = new ResponseNormalizationEngine();
            var turn = norm.Normalize("<think>secret</think>Visible output", isError: false);
            Assert.Equal("Visible output", turn.VisibleText);
            Assert.Contains("secret", turn.ReasoningSummary);

            var bus = new RealtimeEventEngine();
            bus.Publish("c1", "t1", MessageActivityState.Thinking, "Analyzing");
            bus.Publish("c1", "t1", MessageActivityState.Streaming, "Writing");
            var events = bus.GetRecentEvents();
            Assert.Equal(2, events.Count);
            Assert.True(events[1].Sequence > events[0].Sequence);
        }

        [Fact]
        public async Task AppServiceHost_ComposesAllFourLayersCleanly()
        {
            using var http = new HttpClient();
            var host = new AppServiceHost(http, _ => false);

            Assert.NotNull(host.ProviderGateway);
            Assert.NotNull(host.Middleware);
            Assert.NotNull(host.Middleware.Orchestrator);
            Assert.NotNull(host.Middleware.Conversation);
            Assert.NotNull(host.Middleware.Intent);
            Assert.NotNull(host.Middleware.TaskPlanner);
            Assert.NotNull(host.Middleware.Memory);
            Assert.NotNull(host.Middleware.Context);
            Assert.NotNull(host.Middleware.TokenIntelligence);
            Assert.NotNull(host.Middleware.ContextCompression);
            Assert.NotNull(host.Middleware.Knowledge);
            Assert.NotNull(host.Middleware.ToolRegistry);
            Assert.NotNull(host.Middleware.ToolExecutor);
            Assert.NotNull(host.Middleware.Permissions);
            Assert.NotNull(host.Middleware.ResponseNormalization);
            Assert.NotNull(host.Middleware.Verification);
            Assert.NotNull(host.Middleware.Checkpoints);
            Assert.NotNull(host.Middleware.RealtimeEvents);
            Assert.NotNull(host.FileSnapshots);
            Assert.NotNull(host.Git);
            Assert.NotNull(host.BuildRunner);
            Assert.NotNull(host.Logger);

            var gitStatus = await host.Git.GetStatusAsync(Path.GetTempPath());
            Assert.False(gitStatus.IsGitRepository);
        }
    }
}
