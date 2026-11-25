namespace LlmodelBox.Runtime.Memory
{
    // The Memory Engine (Phase 4). Distills durable, cross-session memory from the conversation and
    // recalls it as a compact preamble the Context Engine can inject — so the model retains the
    // session goal and key facts even after the raw transcript is compacted away.
    public interface IMemoryEngine
    {
        // Distilled memory for a project (empty snapshot if none / no key).
        MemorySnapshot Recall(string? projectKey);

        // A compact, budget-friendly preamble built from a snapshot, or null when there is nothing
        // worth injecting.
        string? BuildPreamble(MemorySnapshot snapshot);

        // Incrementally update durable memory from one completed exchange. Cheap and deterministic —
        // no extra model round-trip. No-op when projectKey is null/empty.
        void Observe(string? projectKey, string userText, string assistantText);

        // Forget everything for a project (used when the user clears the chat).
        void Forget(string? projectKey);
    }
}
