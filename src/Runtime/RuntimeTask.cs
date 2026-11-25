using System;

namespace LlmodelBox.Runtime
{
    // A unit of agent work tracked through its AiTaskState lifecycle. Phase 1 is a plain record of
    // identity + title + state; later phases attach plans, steps, artifacts and verification results.
    public sealed class RuntimeTask
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = string.Empty;
        public AiTaskState State { get; set; } = AiTaskState.Created;
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    }
}
