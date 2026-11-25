using System;
using System.Collections.Generic;

namespace LlmodelBox.Runtime
{
    // In-memory registry of runtime sessions, keyed by project path. GetOrCreate is idempotent so
    // repeated opens of the same project reuse the same session (and its accumulated history).
    // Thread-safe: signals and UI callbacks may resolve sessions from threadpool continuations.
    public sealed class SessionManager
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, RuntimeSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

        public RuntimeSession GetOrCreate(string projectPath)
        {
            if (projectPath == null) throw new ArgumentNullException(nameof(projectPath));
            lock (_gate)
            {
                if (!_sessions.TryGetValue(projectPath, out var session))
                {
                    session = new RuntimeSession(projectPath);
                    _sessions[projectPath] = session;
                }
                return session;
            }
        }
    }
}
