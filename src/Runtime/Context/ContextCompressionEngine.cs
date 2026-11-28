using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Tokens;

namespace LlmodelBox.Runtime.Context
{
    public sealed class AutoCompactOptions
    {
        public bool Enabled { get; set; } = true;
        public double SoftTriggerRatio { get; set; } = 0.72;
        public int KeepRecentMessages { get; set; } = 6;
        public int KeepRecentToolMessages { get; set; } = 2;
        public int CompactToolOutputThresholdChars { get; set; } = 800;
        public int MaxSummaryChars { get; set; } = 2400;
    }

    public interface ICompactionSummarizer
    {
        Task<string?> SummarizeAsync(
            IReadOnlyList<AiMessage> droppedMessages,
            string? existingSummary,
            string? focusInstruction,
            CancellationToken cancellationToken = default);
    }

    public sealed class CompactionResult
    {
        public bool CompactionTriggered { get; init; }
        public string TriggerReason { get; init; } = "None";
        public int PrunedToolMessages { get; init; }
        public int SummarizedMessagesCount { get; init; }
        public int TokensBefore { get; init; }
        public int TokensAfter { get; init; }
        public int TokensSaved => Math.Max(0, TokensBefore - TokensAfter);
        public string ConversationSummary { get; init; } = string.Empty;
        public List<AiMessage> CompactedHistory { get; init; } = new();
        public bool UsedModelSummarizer { get; init; }
    }

    /// <summary>
    /// Claude Code / OpenAI Codex style two-phase Auto-Compaction engine:
    /// Phase 1: Prunes stale verbose tool outputs outside recent turns while keeping pointers.
    /// Phase 2: When projected tokens cross SoftTriggerRatio (default 72%) or overflow input budget,
    /// condenses older turns into a structured Continuation Handoff Summary (model-assisted if
    /// configured, otherwise deterministic fallback) while keeping the newest turns verbatim.
    /// </summary>
    public sealed class ContextCompressionEngine
    {
        private static readonly Regex FileRefRegex = new(
            @"\b[\w\-.\\/]+\.(?:cs|xaml|json|xml|md|py|ts|tsx|js|jsx|go|rs|java|cpp|h|sql|ps1|yaml|yml)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly AutoCompactOptions _options;

        public ContextCompressionEngine() : this(new AutoCompactOptions()) { }

        public ContextCompressionEngine(AutoCompactOptions options)
        {
            _options = options ?? new AutoCompactOptions();
        }

        public CompactionResult CompactIfNeeded(
            IReadOnlyList<AiMessage> history,
            string? existingSummary,
            int inputBudget,
            int fixedOverheadTokens,
            IModelTokenCounter counter,
            bool manualForce = false,
            string? focusInstruction = null)
        {
            return CompactAsync(
                    history,
                    existingSummary,
                    inputBudget,
                    fixedOverheadTokens,
                    counter,
                    summarizer: null,
                    manualForce: manualForce,
                    focusInstruction: focusInstruction,
                    cancellationToken: CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        public async Task<CompactionResult> CompactAsync(
            IReadOnlyList<AiMessage> history,
            string? existingSummary,
            int inputBudget,
            int fixedOverheadTokens,
            IModelTokenCounter counter,
            ICompactionSummarizer? summarizer = null,
            bool manualForce = false,
            string? focusInstruction = null,
            CancellationToken cancellationToken = default)
        {
            history ??= Array.Empty<AiMessage>();
            counter ??= TokenizerRegistry.Default.FallbackCounter;
            string currentSummary = existingSummary?.Trim() ?? string.Empty;

            int beforeTokens = fixedOverheadTokens + CountMessages(history, counter)
                             + (string.IsNullOrEmpty(currentSummary) ? 0 : counter.CountTokens(currentSummary));

            if (history.Count == 0)
            {
                return new CompactionResult
                {
                    CompactionTriggered = false,
                    TriggerReason = "None",
                    TokensBefore = beforeTokens,
                    TokensAfter = beforeTokens,
                    ConversationSummary = currentSummary,
                    CompactedHistory = new List<AiMessage>()
                };
            }

            // Phase 1: Prune bloated historical tool outputs older than KeepRecentToolMessages.
            int prunedCount = 0;
            var prunedHistory = new List<AiMessage>(history.Count);
            int recentToolFloor = Math.Max(0, history.Count - _options.KeepRecentToolMessages * 2);
            for (int i = 0; i < history.Count; i++)
            {
                var msg = history[i];
                if (i < recentToolFloor && IsVerboseToolPayload(msg))
                {
                    prunedHistory.Add(PruneMessage(msg));
                    prunedCount++;
                }
                else
                {
                    prunedHistory.Add(msg);
                }
            }

            int afterPruneTokens = fixedOverheadTokens + CountMessages(prunedHistory, counter)
                                 + (string.IsNullOrEmpty(currentSummary) ? 0 : counter.CountTokens(currentSummary));

            bool overHardBudget = afterPruneTokens > inputBudget;
            bool overSoftRatio = _options.Enabled &&
                                 inputBudget > 0 &&
                                 afterPruneTokens >= (int)(inputBudget * _options.SoftTriggerRatio);

            bool shouldSummarize = (manualForce || overHardBudget || overSoftRatio) &&
                                   prunedHistory.Count > Math.Min(2, _options.KeepRecentMessages);

            if (!shouldSummarize && prunedCount == 0)
            {
                return new CompactionResult
                {
                    CompactionTriggered = false,
                    TriggerReason = "None",
                    TokensBefore = beforeTokens,
                    TokensAfter = beforeTokens,
                    ConversationSummary = currentSummary,
                    CompactedHistory = new List<AiMessage>(history)
                };
            }

            if (!shouldSummarize)
            {
                return new CompactionResult
                {
                    CompactionTriggered = true,
                    TriggerReason = "ToolOutputPrune",
                    PrunedToolMessages = prunedCount,
                    TokensBefore = beforeTokens,
                    TokensAfter = afterPruneTokens,
                    ConversationSummary = currentSummary,
                    CompactedHistory = prunedHistory
                };
            }

            int keepCount = manualForce
                ? Math.Min(Math.Max(2, _options.KeepRecentMessages / 2), prunedHistory.Count)
                : Math.Min(_options.KeepRecentMessages, Math.Max(1, prunedHistory.Count - 1));

            int dropCount = Math.Max(0, prunedHistory.Count - keepCount);
            var dropped = prunedHistory.Take(dropCount).ToList();
            var kept = prunedHistory.Skip(dropCount).ToList();

            bool usedModel = false;
            string? newSummary = null;
            if (summarizer != null && dropped.Count > 0)
            {
                try
                {
                    newSummary = await summarizer.SummarizeAsync(dropped, currentSummary, focusInstruction, cancellationToken)
                        .ConfigureAwait(false);
                    usedModel = !string.IsNullOrWhiteSpace(newSummary);
                }
                catch
                {
                    newSummary = null;
                }
            }

            if (string.IsNullOrWhiteSpace(newSummary))
                newSummary = BuildDeterministicHandoff(dropped, currentSummary, focusInstruction, _options.MaxSummaryChars);

            int afterTokens = fixedOverheadTokens + CountMessages(kept, counter)
                            + (string.IsNullOrWhiteSpace(newSummary) ? 0 : counter.CountTokens(newSummary));

            string reason = manualForce
                ? "Manual"
                : (overHardBudget ? "BudgetOverflow" : "AutoThreshold");

            return new CompactionResult
            {
                CompactionTriggered = true,
                TriggerReason = reason,
                PrunedToolMessages = prunedCount,
                SummarizedMessagesCount = dropped.Count,
                TokensBefore = beforeTokens,
                TokensAfter = afterTokens,
                ConversationSummary = newSummary ?? string.Empty,
                CompactedHistory = kept,
                UsedModelSummarizer = usedModel
            };
        }

        public static string BuildDeterministicHandoff(
            IReadOnlyList<AiMessage> droppedMessages,
            string? priorSummary,
            string? focusInstruction,
            int maxChars = 2400)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Auto-Compacted Conversation Handoff]");

            if (!string.IsNullOrWhiteSpace(focusInstruction))
                sb.AppendLine($"Focus directive: {focusInstruction.Trim()}");

            if (!string.IsNullOrWhiteSpace(priorSummary))
            {
                string trimmedPrior = priorSummary.Replace("[Auto-Compacted Conversation Handoff]", string.Empty).Trim();
                if (trimmedPrior.Length > 700) trimmedPrior = trimmedPrior[..700] + "…";
                if (!string.IsNullOrWhiteSpace(trimmedPrior))
                    sb.AppendLine("Prior handoff: " + trimmedPrior.Replace('\r', ' ').Replace('\n', ' '));
            }

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var userGoals = new List<string>();
            var outcomes = new List<string>();

            foreach (var msg in droppedMessages)
            {
                string text = msg.Text ?? string.Empty;
                foreach (Match m in FileRefRegex.Matches(text))
                {
                    if (files.Count < 14) files.Add(m.Value);
                }

                string oneLine = CollapseWhitespace(text);
                if (string.IsNullOrWhiteSpace(oneLine)) continue;

                if (msg.Role == AiRole.User)
                {
                    userGoals.Add(Clip(oneLine, 140));
                }
                else if (msg.Role == AiRole.Assistant || msg.Role == AiRole.Tool)
                {
                    if (oneLine.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                        oneLine.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                        oneLine.Contains("passed", StringComparison.OrdinalIgnoreCase) ||
                        oneLine.Contains("created", StringComparison.OrdinalIgnoreCase) ||
                        oneLine.Contains("updated", StringComparison.OrdinalIgnoreCase))
                    {
                        outcomes.Add(Clip(oneLine, 140));
                    }
                    else if (outcomes.Count < 4)
                    {
                        outcomes.Add(Clip(oneLine, 120));
                    }
                }
            }

            if (files.Count > 0)
                sb.AppendLine("Files referenced: " + string.Join(", ", files));

            if (userGoals.Count > 0)
            {
                sb.AppendLine("Key earlier user requests:");
                foreach (var g in userGoals.TakeLast(5))
                    sb.AppendLine($"- {g}");
            }

            if (outcomes.Count > 0)
            {
                sb.AppendLine("Earlier progress & results:");
                foreach (var o in outcomes.TakeLast(5))
                    sb.AppendLine($"- {o}");
            }

            string result = sb.ToString().TrimEnd();
            return result.Length <= maxChars ? result : result[..(maxChars - 1)] + "…";
        }

        private bool IsVerboseToolPayload(AiMessage m)
        {
            if (string.IsNullOrWhiteSpace(m.Text)) return false;
            return m.Text.Length >= _options.CompactToolOutputThresholdChars &&
                   (m.Role == AiRole.Tool ||
                    m.Text.Contains("[Tool", StringComparison.OrdinalIgnoreCase) ||
                    m.Text.Contains("```", StringComparison.Ordinal));
        }

        private static AiMessage PruneMessage(AiMessage msg)
        {
            string txt = msg.Text ?? string.Empty;
            string head = CollapseWhitespace(txt.Length > 180 ? txt[..180] : txt);
            string summary = $"[Pruned historical {msg.Role} output ({txt.Length:N0} chars): {head}…]";
            return msg.Role == AiRole.Assistant ? AiMessage.Assistant(summary) : AiMessage.User(summary);
        }

        private static int CountMessages(IReadOnlyList<AiMessage> messages, IModelTokenCounter counter)
        {
            int sum = 0;
            foreach (var m in messages)
                if (!string.IsNullOrWhiteSpace(m.Text))
                    sum += counter.CountTokens(m.Text) + 4;
            return sum;
        }

        private static string CollapseWhitespace(string s)
            => Regex.Replace(s.Trim(), @"\s+", " ");

        private static string Clip(string s, int max)
            => s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
