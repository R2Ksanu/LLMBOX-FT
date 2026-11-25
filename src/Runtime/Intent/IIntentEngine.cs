namespace LlmodelBox.Runtime.Intent
{
    // Phase 7 — the Intent + Task Engine. Classifies the live user turn and decomposes it into ordered
    // task titles WITHOUT a model round-trip, so the runtime can gate tools/knowledge intelligently and
    // surface real task flow on the AgentActivityBus. Honest scope: keyword/heuristic classification,
    // not a semantic intent model — it favours safe, permissive defaults when unsure.
    public interface IIntentEngine
    {
        IntentResult Classify(string? userPrompt);
    }
}
