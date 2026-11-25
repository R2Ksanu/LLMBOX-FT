using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Providers;
using LlmodelBox.Runtime;
using Xunit;

namespace LlmodelBox.Tests
{
    // Phase-1 contract tests: the runtime delegates to the router, the router falls back across
    // capable providers on error, and AiRequest.FromPrompt wires the prompt into PromptText. No WPF
    // UI types are constructed here — only the provider/runtime seam.
    public sealed class RuntimeContractTests
    {
        // Minimal in-memory IModelProvider. Returns a fixed response (text or error) and records the
        // request it saw, so tests can assert routing/fallback behavior without any transport.
        private sealed class FakeProvider : IModelProvider
        {
            private readonly AiResponse _response;
            public FakeProvider(AiResponse response) => _response = response;

            public string Name => "Fake";
            public int GenerateCalls { get; private set; }
            public AiRequest? LastRequest { get; private set; }

            public bool CanHandle(ModelItem model) => true;
            public ModelCapabilities GetCapabilities(ModelItem model) => ModelCapabilities.Default();

            public Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken ct)
            {
                GenerateCalls++;
                LastRequest = request;
                return Task.FromResult(_response);
            }

            public async IAsyncEnumerable<AiEvent> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.CompletedTask;
                yield return AiEvent.Done(_response);
            }
        }

        private static AiRuntime Runtime(params IModelProvider[] providers)
            => new AiRuntime(new ModelRouter(providers));

        [Fact]
        public async Task RunAsync_ReturnsProviderText()
        {
            var provider = new FakeProvider(new AiResponse { Text = "hello from provider" });
            var runtime = Runtime(provider);

            var resp = await runtime.RunAsync(
                AiRequest.FromPrompt(new ModelItem(), "hi", new UserSettings()),
                CancellationToken.None);

            Assert.False(resp.IsError);
            Assert.Equal("hello from provider", resp.Text);
            Assert.Equal(1, provider.GenerateCalls);
        }

        [Fact]
        public async Task Router_FallsBackToSecondProvider_WhenFirstReturnsError()
        {
            var failing = new FakeProvider(AiResponse.Error("[ERROR] first provider down"));
            var healthy = new FakeProvider(new AiResponse { Text = "served by second" });
            var runtime = Runtime(failing, healthy);

            var resp = await runtime.RunAsync(
                AiRequest.FromPrompt(new ModelItem(), "hi", new UserSettings()),
                CancellationToken.None);

            Assert.False(resp.IsError);
            Assert.Equal("served by second", resp.Text);
            Assert.Equal(1, failing.GenerateCalls);
            Assert.Equal(1, healthy.GenerateCalls);
        }

        [Fact]
        public void FromPrompt_SetsPromptText()
        {
            var req = AiRequest.FromPrompt(new ModelItem(), "the prompt", new UserSettings());

            Assert.Equal("the prompt", req.PromptText);
        }
    }
}
