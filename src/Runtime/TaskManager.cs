using System;
using System.Collections.Generic;

namespace LlmodelBox.Runtime
{
    // In-memory store of RuntimeTasks. Later phases persist and stream these; Phase 1 keeps the
    // create/get/update surface the UI and agent loop need. Thread-safe for cross-thread updates.
    public sealed class TaskManager
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, RuntimeTask> _tasks = new(StringComparer.Ordinal);

        public RuntimeTask Create(string title)
        {
            var task = new RuntimeTask { Title = title ?? string.Empty };
            lock (_gate) { _tasks[task.Id] = task; }
            return task;
        }

        public RuntimeTask? Get(string id)
        {
            if (id == null) return null;
            lock (_gate) { return _tasks.TryGetValue(id, out var task) ? task : null; }
        }

        public bool UpdateState(string id, AiTaskState state)
        {
            if (id == null) return false;
            lock (_gate)
            {
                if (!_tasks.TryGetValue(id, out var task)) return false;
                task.State = state;
                return true;
            }
        }
    }
}
