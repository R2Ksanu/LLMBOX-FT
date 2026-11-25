using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LlmodelBox.Runtime.Memory
{
    // Phase 4 Memory Engine. Distills durable memory (session goal + salient facts) from each
    // completed exchange using deterministic heuristics — NO extra model round-trip — and recalls it
    // as a compact preamble. Honest scope: fact extraction is pattern-based (common self-disclosure
    // phrasings), not a semantic model; it favours precision over recall and is bounded in size.
    public sealed class MemoryEngine : IMemoryEngine
    {
        private const int MaxFacts = 24;
        private const int MaxFactLen = 160;
        private const int GoalMaxLen = 300;

        private readonly IMemoryStore _store;

        public MemoryEngine() : this(new JsonMemoryStore()) { }
        public MemoryEngine(IMemoryStore store) => _store = store;

        // Terminator lookahead: stop a captured value at a conjunction, clause punctuation, or end so a
        // greedy match can't swallow the rest of the sentence (e.g. "Rust and I'm using .NET 8").
        private const string Term = @"(?=\s+(?:and|but|or|then)\s+|[.!?,;\n]|$)";

        // Heuristic self-disclosure patterns → fact templates ({0} = captured group).
        private static readonly (Regex rx, string label)[] Patterns =
        {
            (new Regex(@"\bmy name is\s+([A-Za-z][\w'\-]{1,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled), "User's name is {0}"),
            (new Regex(@"\bcall me\s+([A-Za-z][\w'\-]{1,40})", RegexOptions.IgnoreCase | RegexOptions.Compiled), "User goes by {0}"),
            (new Regex(@"\bi (?:really )?(?:like|love|prefer|enjoy)\s+(.{2,80}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "User likes {0}"),
            (new Regex(@"\bi(?:\s+am|'m)\s+(?:using|working with|building (?:in|with))\s+(.{2,80}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "User is using {0}"),
            (new Regex(@"\bwe (?:use|are using)\s+(.{2,80}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "Team uses {0}"),
            (new Regex(@"\bi(?:\s+am|'m)?\s+work(?:ing)? on\s+(.{2,80}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "User works on {0}"),
            (new Regex(@"\bremember (?:that )?(.{3,120}?)" + Term, RegexOptions.IgnoreCase | RegexOptions.Compiled), "Remember: {0}"),
        };

        public MemorySnapshot Recall(string? projectKey)
        {
            if (string.IsNullOrWhiteSpace(projectKey)) return new MemorySnapshot();
            var mem = _store.Load(projectKey!);
            return new MemorySnapshot { SessionGoal = mem.SessionGoal, Facts = mem.Facts.ToList() };
        }

        public string? BuildPreamble(MemorySnapshot snapshot)
        {
            if (snapshot == null || snapshot.IsEmpty) return null;
            var sb = new StringBuilder();
            sb.AppendLine("[Memory — durable context for this project]");
            if (!string.IsNullOrWhiteSpace(snapshot.SessionGoal))
                sb.AppendLine($"Session goal: {snapshot.SessionGoal}");
            if (snapshot.Facts.Count > 0)
            {
                sb.AppendLine("Known facts:");
                foreach (var f in snapshot.Facts) sb.AppendLine($"- {f}");
            }
            return sb.ToString().TrimEnd();
        }

        public void Observe(string? projectKey, string userText, string assistantText)
        {
            if (string.IsNullOrWhiteSpace(projectKey)) return;
            var mem = _store.Load(projectKey!);
            mem.TurnCount++;

            // The first user turn defines the session goal — preserved even after history compaction.
            if (string.IsNullOrWhiteSpace(mem.SessionGoal) && !string.IsNullOrWhiteSpace(userText))
                mem.SessionGoal = Clip(userText.Trim(), GoalMaxLen);

            foreach (var fact in ExtractFacts(userText))
                AddFact(mem, fact);

            mem.UpdatedUtc = DateTime.UtcNow;
            _store.Save(projectKey!, mem);
        }

        public void Forget(string? projectKey)
        {
            if (string.IsNullOrWhiteSpace(projectKey)) return;
            _store.Clear(projectKey!);
        }

        // Deterministic pattern-based extraction. Public + static so it is unit-testable in isolation.
        public static IEnumerable<string> ExtractFacts(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (var (rx, label) in Patterns)
            {
                var m = rx.Match(text);
                if (!m.Success) continue;
                string captured = m.Groups[1].Value.Trim().TrimEnd('.', ',', ';', ':', ' ');
                if (captured.Length == 0) continue;
                yield return Clip(string.Format(label, captured), MaxFactLen);
            }
        }

        private static void AddFact(ConversationMemory mem, string fact)
        {
            if (mem.Facts.Any(f => string.Equals(f, fact, StringComparison.OrdinalIgnoreCase))) return;
            mem.Facts.Add(fact);
            // Bound growth: keep the most recent facts, drop the oldest.
            if (mem.Facts.Count > MaxFacts)
                mem.Facts.RemoveRange(0, mem.Facts.Count - MaxFacts);
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
