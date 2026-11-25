using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Services;

namespace LlmodelBox.Providers
{
    /// <summary>
    /// Layer 1 Provider Gateway contract: single unified boundary for model discovery,
    /// adapter selection, capability resolution, request execution, streaming, and error normalization.
    /// </summary>
    public interface IProviderGateway
    {
        IReadOnlyList<IModelProvider> Adapters { get; }
        void RegisterAdapter(IModelProvider adapter);
        ModelCapabilities GetCapabilities(ModelItem? model);
        IModelProvider SelectAdapter(AiRequest request);
        Task<AiResponse> ExecuteAsync(AiRequest request, CancellationToken ct);
        IAsyncEnumerable<AiEvent> StreamAsync(AiRequest request, CancellationToken ct);
        NormalizedProviderError NormalizeError(string? errorText, Exception? exception = null);
        Task<List<ModelItem>> DiscoverCatalogAsync(CancellationToken ct = default);
        bool HasConfiguredCredentials(ModelItem? model, UserSettings? settings);
    }

    /// <summary>
    /// Layer 1 Gateway implementation composing registered IModelProvider adapters, ModelRouter,
    /// ModelCapabilityResolver, and IModelLibraryService without exposing transport details upstream.
    /// </summary>
    public sealed class ProviderGateway : IProviderGateway
    {
        private readonly object _gate = new();
        private readonly List<IModelProvider> _adapters = new();
        private readonly IModelLibraryService? _libraryService;
        private IModelRouter _router;

        public ProviderGateway(IEnumerable<IModelProvider> initialAdapters, IModelLibraryService? libraryService = null)
        {
            if (initialAdapters == null) throw new ArgumentNullException(nameof(initialAdapters));
            _adapters.AddRange(initialAdapters.Where(a => a != null));
            if (_adapters.Count == 0)
            {
                throw new ArgumentException("ProviderGateway requires at least one IModelProvider adapter.", nameof(initialAdapters));
            }
            _libraryService = libraryService;
            _router = new ModelRouter(_adapters);
        }

        public IModelRouter Router
        {
            get { lock (_gate) return _router; }
        }

        public IReadOnlyList<IModelProvider> Adapters
        {
            get { lock (_gate) return _adapters.ToList(); }
        }

        public void RegisterAdapter(IModelProvider adapter)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));
            lock (_gate)
            {
                if (_adapters.Any(a => string.Equals(a.Name, adapter.Name, StringComparison.OrdinalIgnoreCase)))
                    return;
                // Custom adapters take priority before generic catch-all fallback adapters (e.g. CloudApi).
                _adapters.Insert(0, adapter);
                _router = new ModelRouter(_adapters);
            }
        }

        public ModelCapabilities GetCapabilities(ModelItem? model)
            => ModelCapabilityResolver.Resolve(model);

        public IModelProvider SelectAdapter(AiRequest request)
        {
            lock (_gate) return _router.Select(request);
        }

        public Task<AiResponse> ExecuteAsync(AiRequest request, CancellationToken ct)
        {
            IModelRouter currentRouter;
            lock (_gate) currentRouter = _router;
            return currentRouter.RouteAsync(request, ct);
        }

        public IAsyncEnumerable<AiEvent> StreamAsync(AiRequest request, CancellationToken ct)
        {
            IModelRouter currentRouter;
            lock (_gate) currentRouter = _router;
            return currentRouter.RouteStreamAsync(request, ct);
        }

        public NormalizedProviderError NormalizeError(string? errorText, Exception? exception = null)
            => ProviderErrorNormalizer.Normalize(errorText, exception);

        public Task<List<ModelItem>> DiscoverCatalogAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_libraryService == null) return Task.FromResult(new List<ModelItem>());
            return Task.FromResult(_libraryService.LoadModels());
        }

        public bool HasConfiguredCredentials(ModelItem? model, UserSettings? settings)
        {
            if (model == null) return false;
            if (model.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ||
                model.Provider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (settings == null) return false;

            var keys = settings.ApiKeys ?? new ApiConfig();
            string p = (model.Provider ?? string.Empty).ToLowerInvariant();
            if (p.Contains("openai") && !string.IsNullOrWhiteSpace(keys.OpenAI)) return true;
            if ((p.Contains("anthropic") || p.Contains("claude")) && !string.IsNullOrWhiteSpace(keys.Anthropic)) return true;
            if ((p.Contains("google") || p.Contains("gemini")) && !string.IsNullOrWhiteSpace(keys.Gemini)) return true;
            if ((p.Contains("grok") || p.Contains("xai")) && !string.IsNullOrWhiteSpace(keys.Grok)) return true;
            if (p.Contains("deepseek") && !string.IsNullOrWhiteSpace(keys.DeepSeek)) return true;
            if (p.Contains("openrouter") && !string.IsNullOrWhiteSpace(keys.OpenRouter)) return true;
            if (p.Contains("cloudflare") && !string.IsNullOrWhiteSpace(keys.Cloudflare)) return true;
            if (p.Contains("nvidia") && !string.IsNullOrWhiteSpace(keys.Nvidia)) return true;
            if (p.Contains("groq") && !string.IsNullOrWhiteSpace(keys.Groq)) return true;
            if (p.Contains("mistral") && !string.IsNullOrWhiteSpace(keys.Mistral)) return true;
            if (p.Contains("cohere") && !string.IsNullOrWhiteSpace(keys.Cohere)) return true;
            if (p.Contains("sambanova") && !string.IsNullOrWhiteSpace(keys.SambaNova)) return true;
            if (p.Contains("hugging") && !string.IsNullOrWhiteSpace(keys.HuggingFace)) return true;
            if ((p.Contains("zhipu") || p.Contains("glm")) && !string.IsNullOrWhiteSpace(keys.Zhipu)) return true;

            return settings.CustomProviders?.Any(c =>
                c != null &&
                !string.IsNullOrWhiteSpace(c.ApiKey) &&
                string.Equals(c.Name, model.Provider, StringComparison.OrdinalIgnoreCase)) == true;
        }
    }
}
