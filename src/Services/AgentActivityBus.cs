using System;
using System.Collections.Generic;
using System.Linq;

namespace LlmodelBox.Services
{
    // The stages a request passes through inside the agent. The Neural Net view
    // maps each of these to a node and lights it up as real signals flow.
    public enum AgentNode
    {
        Prompt,      // user input received
        Memory,      // persistent state (system instruction / settings) loaded
        Context,     // context window assembled (open file, attachments, history)
        Model,       // request dispatched to the cloud model
        Reasoning,   // model is thinking / generating
        Compaction,  // context trimmed to fit the window
        Response,    // answer received
        Tokens       // usage accounted
    }

    // A single fired signal: one neuron activation with a human-readable detail.
    public sealed class AgentSignal
    {
        public AgentNode Node { get; init; }
        public string Detail { get; init; } = string.Empty;
        public string Kind { get; init; } = "info"; // info | active | warn | error
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
        public long? Tokens { get; init; }
    }

    // Process-wide, lock-free-to-callers event bus. Any component can Emit; the
    // Neural Net visualizer subscribes. Signals may be raised from threadpool
    // continuations after an await, so subscribers must marshal to the UI thread.
    public static class AgentActivityBus
    {
        public const int MaxRecent = 240;
        private static readonly object _gate = new();
        private static readonly List<AgentSignal> _recent = new();

        public static event Action<AgentSignal>? SignalEmitted;

        public static IReadOnlyList<AgentSignal> Recent
        {
            get { lock (_gate) { return _recent.ToArray(); } }
        }

        public static void Emit(AgentNode node, string detail, string kind = "info", long? tokens = null)
        {
            var sig = new AgentSignal { Node = node, Detail = detail, Kind = kind, Tokens = tokens };
            lock (_gate)
            {
                _recent.Add(sig);
                if (_recent.Count > MaxRecent) _recent.RemoveRange(0, _recent.Count - MaxRecent);
            }
            try { SignalEmitted?.Invoke(sig); } catch { /* a bad subscriber must not break the pipeline */ }
        }

        public static void Clear()
        {
            lock (_gate) { _recent.Clear(); }
        }
    }
}
