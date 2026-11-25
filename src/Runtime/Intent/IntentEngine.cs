using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LlmodelBox.Runtime.Intent
{
    // Phase 7 Intent + Task Engine implementation. Deterministic keyword/heuristic classification and
    // task decomposition — no model round-trip, no network. Defaults are deliberately permissive: an
    // unclassifiable turn still gets tools + knowledge so a real request is never starved.
    public sealed class IntentEngine : IIntentEngine
    {
        private const int MaxTasks = 6;
        private const int TaskTitleMax = 120;

        private static readonly string[] Greetings =
        {
            "hi", "hello", "hey", "yo", "thanks", "thank you", "ok", "okay", "cool", "nice",
            "great", "good morning", "good night", "bye", "gm", "sup", "cheers"
        };

        private static readonly string[] EditVerbs =
        {
            "add", "create", "implement", "fix", "refactor", "rename", "update", "change", "modify",
            "replace", "remove", "delete", "write", "optimize", "improve", "build", "make", "generate",
            "convert", "migrate", "wire", "extract", "split", "merge", "start"
        };

        private static readonly string[] RunVerbs =
        {
            "run", "execute", "list", "read", "open", "print", "compile"
        };

        private static readonly string[] SearchVerbs =
        {
            "find", "search", "locate", "where is", "where's", "look for", "which file", "grep"
        };

        private static readonly string[] ExplainVerbs =
        {
            "explain", "understand", "what does", "how does", "walk me through", "describe",
            "why does", "what is the purpose"
        };

        private static readonly Regex QuestionStart = new(
            @"^\s*(what|why|how|when|where|who|which|can|could|would|should|do|does|did|is|are|will)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ListItem = new(
            @"^\s*(?:\d+[.)]|[-*])\s+(.*\S)", RegexOptions.Compiled);

        // APPEND-INTENT-MARKER
        public IntentResult Classify(string? userPrompt)
        {
            string text = (userPrompt ?? string.Empty).Trim();
            var tasks = Decompose(text);

            if (text.Length == 0)
                return Make(IntentKind.Chat, 0.2, tasks, tools: false, knowledge: false);

            string lower = text.ToLowerInvariant();
            bool hasQuestion = text.Contains('?') || QuestionStart.IsMatch(text);

            // Greeting / small talk: short, opens with a greeting, and asks nothing.
            if (IsSmallTalk(lower, hasQuestion))
                return Make(IntentKind.Chat, 0.9, tasks, tools: false, knowledge: false);

            // Explain/understand takes priority over the imperative check so "explain how add works"
            // is not misread as an edit.
            if (ContainsAny(lower, ExplainVerbs))
                return Make(IntentKind.ExplainCode, 0.8, tasks, tools: false, knowledge: true);

            if (StartsWithAny(lower, EditVerbs))
                return Make(IntentKind.EditCode, 0.85, tasks, tools: true, knowledge: true);

            if (ContainsAny(lower, SearchVerbs))
                return Make(IntentKind.Search, 0.8, tasks, tools: false, knowledge: true);

            if (StartsWithAny(lower, RunVerbs))
                return Make(IntentKind.RunTool, 0.8, tasks, tools: true, knowledge: false);

            if (hasQuestion)
                return Make(IntentKind.Question, 0.7, tasks, tools: false, knowledge: true);

            // Fallback: stay permissive so we never starve a real request of tools/knowledge.
            return Make(IntentKind.Unknown, 0.4, tasks, tools: true, knowledge: true);
        }

        private static IntentResult Make(IntentKind kind, double conf, IReadOnlyList<string> tasks, bool tools, bool knowledge)
            => new IntentResult { Kind = kind, Confidence = conf, Tasks = tasks, WantsTools = tools, WantsKnowledge = knowledge };

        private static bool IsSmallTalk(string lower, bool hasQuestion)
        {
            if (hasQuestion) return false;
            int words = lower.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
            if (words > 4) return false;
            foreach (var g in Greetings)
                if (lower == g || lower.StartsWith(g + " ") || lower.StartsWith(g + "!") || lower.StartsWith(g + ",")) return true;
            return false;
        }

        private static bool StartsWithAny(string lower, string[] verbs)
        {
            foreach (var v in verbs)
                if (lower == v || lower.StartsWith(v + " ")) return true;
            return false;
        }

        private static bool ContainsAny(string lower, string[] needles)
        {
            foreach (var n in needles)
                if (lower.Contains(n)) return true;
            return false;
        }

        // Deterministic decomposition: prefer explicit numbered/bulleted lists, else split on sequence
        // markers ("then", "and then", "after that", ";", newlines). Always returns >= 1 task for a
        // non-empty turn. Public+static so the splitting logic is unit-testable in isolation.
        public static IReadOnlyList<string> Decompose(string? text)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.Length == 0) return Array.Empty<string>();

            var numbered = new List<string>();
            foreach (var line in t.Split('\n'))
            {
                var m = ListItem.Match(line);
                if (m.Success) numbered.Add(m.Groups[1].Value.Trim());
            }
            if (numbered.Count >= 2) return Finalize(numbered);

            var parts = Regex.Split(t, @"\s+(?:and then|then|after that)\s+|;\s*|\n+", RegexOptions.IgnoreCase)
                             .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count >= 2) return Finalize(parts);

            return Finalize(new List<string> { t });
        }

        private static IReadOnlyList<string> Finalize(List<string> raw)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outp = new List<string>();
            foreach (var s in raw)
            {
                var title = Clip(s.Trim(), TaskTitleMax);
                if (title.Length == 0 || !seen.Add(title)) continue;
                outp.Add(title);
                if (outp.Count >= MaxTasks) break;
            }
            return outp;
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
