namespace LlmodelBox.Runtime.Memory
{
    // Persistence boundary for distilled ConversationMemory. Split from the engine so the extraction
    // logic can be unit-tested against an in-memory fake without touching disk.
    public interface IMemoryStore
    {
        ConversationMemory Load(string projectKey);
        void Save(string projectKey, ConversationMemory memory);
        void Clear(string projectKey);
    }
}
