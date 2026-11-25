using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Providers
{
    // Selects a capable provider for a request (respecting an explicitly chosen model) and
    // executes it, with fallback across capable providers. Phase 7 layers smart routing on top.
    public interface IModelRouter
    {
        // The provider that would serve this request right now.
        IModelProvider Select(AiRequest request);

        Task<AiResponse> RouteAsync(AiRequest request, CancellationToken ct);

        IAsyncEnumerable<AiEvent> RouteStreamAsync(AiRequest request, CancellationToken ct);
    }
}
