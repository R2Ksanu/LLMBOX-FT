using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Providers
{
    // Phase 1/2 seam: routes a request to the first capable provider. The user's explicitly chosen
    // model is always honoured (this router never overrides it); it only selects which adapter can
    // physically serve that model, with fallback to the next capable provider on hard failure.
    // Phase 7 layers capability/cost/latency-aware smart routing on top of this contract.
    public sealed class ModelRouter : IModelRouter
    {
        private readonly IReadOnlyList<IModelProvider> _providers;

        public ModelRouter(IEnumerable<IModelProvider> providers)
        {
            _providers = providers?.ToList() ?? new List<IModelProvider>();
            if (_providers.Count == 0)
                throw new ArgumentException("At least one IModelProvider is required.", nameof(providers));
        }

        // Returns the best usable provider for this request. Selection NEVER changes the user's
        // chosen model — it only picks which adapter serves it. Among the adapters that CanHandle
        // the model, we prefer one whose declared capabilities actually FIT the request (e.g. a
        // Vision-capable adapter when the request carries image parts), but always fall back to the
        // first capable adapter (and finally the first registered adapter) so a provider is returned.
        public IModelProvider Select(AiRequest request)
        {
            var capable = _providers.Where(p => p.CanHandle(request.Model)).ToList();
            if (capable.Count == 0) return _providers[0];
            if (capable.Count == 1) return capable[0];

            bool needsVision = RequestHasImages(request);
            if (needsVision)
            {
                var visionCapable = capable.FirstOrDefault(p => SafeCaps(p, request.Model).Vision);
                if (visionCapable != null) return visionCapable;
            }

            if (request.Stream)
            {
                var streaming = capable.FirstOrDefault(p => SafeCaps(p, request.Model).Streaming);
                if (streaming != null) return streaming;
            }

            return capable[0];
        }

        public async Task<AiResponse> RouteAsync(AiRequest request, CancellationToken ct)
        {
            // Order capable providers so the best-fit (per Select) is tried first, then the rest as
            // fallbacks. On a hard (IsError) result we advance to the next capable provider.
            var ordered = OrderedCapable(request);

            AiResponse? last = null;
            var failures = new List<string>();
            foreach (var provider in ordered)
            {
                ct.ThrowIfCancellationRequested();
                var resp = await provider.GenerateAsync(request, ct).ConfigureAwait(false);
                if (!resp.IsError) return resp; // usage/latency already carried on AiResponse
                last = resp;
                failures.Add($"{provider.Name}: {resp.ErrorMessage ?? resp.Text}");
            }

            if (last != null && failures.Count == 1) return last; // single provider — return its normalized error verbatim
            return AiResponse.Error("[ERROR] All providers failed. " + string.Join(" | ", failures));
        }

        public async IAsyncEnumerable<AiEvent> RouteStreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var ordered = OrderedCapable(request);

            for (int i = 0; i < ordered.Count; i++)
            {
                var provider = ordered[i];
                bool isLast = i == ordered.Count - 1;

                // Defer this provider's events until we know it is viable. Because the current
                // transport is buffered (Start arrives, then the whole result), an early Error can
                // still fall back to the next capable provider WITHOUT the UI ever seeing a dangling
                // Start. Once a TokenDelta/Completed arrives we commit: flush the buffer and stream
                // the rest live (we can't retract output already shown to the UI).
                var pending = new List<AiEvent>();
                bool committed = false;
                bool fellBack = false;

                await foreach (var ev in provider.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    if (!committed && ev.Type == AiEventType.Error && !isLast)
                    {
                        fellBack = true; // discard the buffered events; try the next provider
                        break;
                    }

                    if (!committed && (ev.Type == AiEventType.TokenDelta || ev.Type == AiEventType.Completed))
                    {
                        committed = true;
                        foreach (var b in pending) yield return b; // flush buffered Start/Reason
                        pending.Clear();
                    }

                    if (committed) yield return ev;
                    else pending.Add(ev);
                }

                if (fellBack) continue;

                // Terminal provider (or an error on the last provider): flush anything still buffered
                // so the caller always sees a coherent event sequence.
                foreach (var b in pending) yield return b;
                yield break;
            }
        }

        // Capable providers ordered best-fit-first (Select's choice), then the remaining capable
        // ones as fallbacks; defaults to the first registered provider when none claim the model.
        private List<IModelProvider> OrderedCapable(AiRequest request)
        {
            var capable = _providers.Where(p => p.CanHandle(request.Model)).ToList();
            if (capable.Count == 0) return new List<IModelProvider> { _providers[0] };
            var primary = Select(request);
            var ordered = new List<IModelProvider> { primary };
            ordered.AddRange(capable.Where(p => !ReferenceEquals(p, primary)));
            return ordered;
        }

        private static bool RequestHasImages(AiRequest request)
        {
            foreach (var m in request.Messages)
                foreach (var part in m.Parts)
                    if (part.Kind == AiContentKind.Image || part.Kind == AiContentKind.Video) return true;
            return false;
        }

        // GetCapabilities is best-effort; never let a misbehaving adapter break routing.
        private static ModelCapabilities SafeCaps(IModelProvider p, ModelItem model)
        {
            try { return p.GetCapabilities(model) ?? ModelCapabilities.Default(); }
            catch { return ModelCapabilities.Default(); }
        }
    }
}
