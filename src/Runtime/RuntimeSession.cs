using System;
using System.Collections.Generic;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Runtime
{
    // A single per-project conversation/runtime context. Holds the running message history that
    // later phases (memory, compaction, tool loops) read and mutate. One session per project path.
    public sealed class RuntimeSession
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string ProjectPath { get; }
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public List<AiMessage> History { get; } = new();

        public RuntimeSession(string projectPath)
            => ProjectPath = projectPath ?? throw new ArgumentNullException(nameof(projectPath));
    }
}
