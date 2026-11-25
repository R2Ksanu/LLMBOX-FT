using System.Collections.Generic;

namespace LlmodelBox.Runtime.Intent
{
    // The Intent Engine's verdict for one turn: the classified kind, a rough confidence, the derived
    // gates the runtime consults (tools / knowledge), and a lightweight decomposition of the turn into
    // ordered task titles. Everything is deterministic and cheap to compute.
    public sealed class IntentResult
    {
        public IntentKind Kind { get; set; } = IntentKind.Unknown;

        // 0..1 heuristic confidence in the classification.
        public double Confidence { get; set; }

        // Whether the runtime should advertise tools / retrieve project knowledge for this turn.
        public bool WantsTools { get; set; }
        public bool WantsKnowledge { get; set; }

        // Ordered task titles the turn decomposes into (>= 1). A simple turn yields a single task.
        public IReadOnlyList<string> Tasks { get; set; } = new List<string>();
    }
}
