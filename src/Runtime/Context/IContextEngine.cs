using LlmodelBox.Models.Ai;

namespace LlmodelBox.Runtime.Context
{
    // Assembles raw UI/session materials into a budgeted, provider-independent AiRequest.
    // Later phases (memory recall, knowledge/RAG retrieval) enrich the engine WITHOUT changing this
    // contract: the runtime and UI keep calling Build the same way.
    public interface IContextEngine
    {
        ContextBuildResult Build(ContextRequest request, ModelCapabilities capabilities);
    }
}
