using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Context;

namespace LlmodelBox.Runtime
{
    // The single entry point the UI calls instead of a provider-specific service. Wraps context
    // building, model routing and execution around the model API — and, in later phases, memory,
    // tools, planning, verification and self-correction. The provider API remains the heart.
    public interface IAiRuntime
    {
        Task<AiResponse> RunAsync(AiRequest request, CancellationToken cancellationToken);

        // Phase 3: the UI hands raw materials; the runtime resolves the model's capabilities and lets
        // the Context Engine assemble a budgeted request before routing. Preferred UI entry point.
        Task<AiResponse> RunAsync(ContextRequest request, CancellationToken cancellationToken);

        IAsyncEnumerable<AiEvent> StreamAsync(AiRequest request, CancellationToken cancellationToken);

        Task<AiResponse> RunWithProgressAsync(
            ContextRequest request,
            Action<RuntimeProgressEvent> onProgress,
            CancellationToken cancellationToken)
            => RunAsync(request, cancellationToken);
    }
}
