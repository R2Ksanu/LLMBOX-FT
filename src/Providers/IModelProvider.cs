using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Providers
{
    // A provider adapter normalizes one transport (OpenAI-compatible, Anthropic, Gemini, local,
    // custom, …) behind the runtime's request/response/streaming contract. Provider-specific wire
    // formats live here (or in the service it wraps), never in the runtime or UI.
    public interface IModelProvider
    {
        string Name { get; }

        // True if this adapter can serve the given model (by provider/endpoint/family).
        bool CanHandle(ModelItem model);

        // Normalized capabilities for the given model (context window, streaming, tools, vision…).
        ModelCapabilities GetCapabilities(ModelItem model);

        Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken ct);

        IAsyncEnumerable<AiEvent> StreamAsync(AiRequest request, CancellationToken ct);
    }
}
