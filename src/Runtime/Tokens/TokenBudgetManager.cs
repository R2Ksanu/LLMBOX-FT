using System;
using System.Collections.Generic;
using System.Linq;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Runtime.Tokens
{
    public sealed class TokenBudgetRequest
    {
        public ModelItem? Model { get; init; }
        public ModelCapabilities Capabilities { get; init; } = ModelCapabilities.Default();
        public string? SystemInstructions { get; init; }
        public string UserPrompt { get; init; } = string.Empty;
        public string? OpenFilePath { get; init; }
        public string? OpenFileText { get; init; }
        public IReadOnlyList<AiMessage> RecentHistory { get; init; } = Array.Empty<AiMessage>();
        public string? ConversationSummary { get; init; }
        public string? RetrievedCodeSnippets { get; init; }
        public IReadOnlyList<string> Attachments { get; init; } = Array.Empty<string>();
        public IReadOnlyList<AiToolDefinition> ToolSchemas { get; init; } = Array.Empty<AiToolDefinition>();
        public string? Memory { get; init; }
    }

    /// <summary>
    /// Full 10-category token budget snapshot produced by TokenBudgetManager so middleware
    /// and UI telemetry reflect real token allocation, truncation method, and confidence.
    /// </summary>
    public sealed class TokenBudgetReport
    {
        public int ModelContextWindow { get; init; }
        public int EffectiveInputBudget { get; init; }

        // All 10 tracked Part 4 categories
        public int SystemInstructionsTokens { get; init; }
        public int RecentHistoryTokens { get; init; }
        public int ConversationSummariesTokens { get; init; }
        public int CurrentUserPromptTokens { get; init; }
        public int OpenFilesTokens { get; init; }
        public int RetrievedCodeSnippetsTokens { get; init; }
        public int AttachmentsTokens { get; init; }
        public int ToolSchemasTokens { get; init; }
        public int MemoryTokens { get; init; }
        public int OutputReserveTokens { get; init; }

        public int TotalInputAllocatedTokens =>
            SystemInstructionsTokens + RecentHistoryTokens + ConversationSummariesTokens +
            CurrentUserPromptTokens + OpenFilesTokens + RetrievedCodeSnippetsTokens +
            AttachmentsTokens + ToolSchemasTokens + MemoryTokens;

        public int RemainingInputHeadroom => Math.Max(0, EffectiveInputBudget - TotalInputAllocatedTokens);
        public double ContextUtilizationPercent =>
            ModelContextWindow > 0 ? Math.Min(100.0, (TotalInputAllocatedTokens / (double)ModelContextWindow) * 100.0) : 0.0;

        public TokenCountConfidence Confidence { get; init; }
        public string SourceLabel { get; init; } = string.Empty;
        public string TokenizerId { get; init; } = string.Empty;
        public string TruncationMethod { get; init; } = string.Empty;

        public int IncludedHistoryTurns { get; init; }
        public int DroppedHistoryTurns { get; init; }
        public bool OpenFileIncluded { get; init; }
        public bool OpenFileTruncated { get; init; }
        public bool KnowledgeTruncated { get; init; }

        public string PreparedOpenFileBlock { get; init; } = string.Empty;
        public string PreparedKnowledgeBlock { get; init; } = string.Empty;
        public List<AiMessage> KeptHistory { get; init; } = new();

        public TokenCostBreakdown? EstimatedCost { get; init; }
    }

    /// <summary>
    /// Allocates tokens across all 10 IDE context categories while strictly prioritizing
    /// the live user request and applying token-accurate BPE boundary truncation when available.
    /// </summary>
    public sealed class TokenBudgetManager
    {
        private const int MinUsefulBlockTokens = 48;

        public TokenBudgetReport Allocate(TokenBudgetRequest req, IModelTokenCounter counter)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            counter ??= TokenizerRegistry.Default.FallbackCounter;

            int window = Math.Max(req.Capabilities.ContextWindow, req.Capabilities.InputTokenLimit);
            if (window <= 0) window = 8192;

            int outputReserve = Math.Clamp(req.Capabilities.OutputTokenLimit, 256, Math.Max(256, window / 4));
            const int protocolSafetyReserve = 256;
            int inputBudget = Math.Max(512, window - outputReserve - protocolSafetyReserve);

            // 1. Live User Prompt is highest priority (never dropped behind optional context).
            int promptTokens = counter.CountTokens(req.UserPrompt);

            // 2. Fixed protocol/system overhead: System Instructions + Tool Schemas + Attachments descriptor.
            int sysTokens = string.IsNullOrWhiteSpace(req.SystemInstructions) ? 0 : counter.CountTokens(req.SystemInstructions);
            int toolSchemaTokens = CountToolSchemas(req.ToolSchemas, counter);
            int attachmentsTokens = req.Attachments.Count > 0
                ? counter.CountTokens($"[Attached Files/Media: {string.Join(", ", req.Attachments)}]")
                : 0;

            int used = promptTokens + sysTokens + toolSchemaTokens + attachmentsTokens;

            // 3. Memory + Conversation Summary (compact high-signal state).
            int memoryTokens = 0;
            if (!string.IsNullOrWhiteSpace(req.Memory))
            {
                int mCost = counter.CountTokens(req.Memory);
                if (used + mCost <= inputBudget)
                {
                    memoryTokens = mCost;
                    used += mCost;
                }
            }

            int summaryTokens = 0;
            if (!string.IsNullOrWhiteSpace(req.ConversationSummary))
            {
                int sCost = counter.CountTokens(req.ConversationSummary);
                if (used + sCost <= inputBudget)
                {
                    summaryTokens = sCost;
                    used += sCost;
                }
            }

            // 4. Open File — token-aware truncation using BPE index when supported.
            string openFileBlock = string.Empty;
            int openFileTokens = 0;
            bool openFileIncluded = false;
            bool openFileTruncated = false;

            if (!string.IsNullOrWhiteSpace(req.OpenFilePath) && !string.IsNullOrWhiteSpace(req.OpenFileText))
            {
                int remaining = inputBudget - used;
                if (remaining >= MinUsefulBlockTokens)
                {
                    string fn = System.IO.Path.GetFileName(req.OpenFilePath);
                    string header = $"[Current Open File: {fn}]\n```\n";
                    string footer = "\n...[truncated for context budget]\n```\n";
                    string fullFooter = "\n```\n";
                    int scaffoldCost = counter.CountTokens(header + footer);
                    int contentBudget = Math.Max(0, remaining - scaffoldCost);

                    int rawTokens = counter.CountTokens(req.OpenFileText);
                    if (rawTokens <= contentBudget)
                    {
                        openFileBlock = header + req.OpenFileText + fullFooter;
                        openFileTokens = counter.CountTokens(openFileBlock);
                        openFileIncluded = true;
                    }
                    else if (contentBudget >= 24)
                    {
                        int trimTarget = contentBudget;
                        for (int pass = 0; pass < 2 && trimTarget > 0; pass++)
                        {
                            string trimmed = counter.TruncateToFit(req.OpenFileText!, trimTarget, fromEnd: false);
                            openFileBlock = header + trimmed + footer;
                            openFileTokens = counter.CountTokens(openFileBlock);
                            if (openFileTokens <= remaining) break;
                            trimTarget = Math.Max(0, trimTarget - (openFileTokens - remaining + 2));
                        }
                        if (openFileTokens > remaining)
                        {
                            openFileBlock = string.Empty;
                            openFileTokens = 0;
                        }
                        else
                        {
                            openFileIncluded = true;
                            openFileTruncated = true;
                        }
                    }
                    used += openFileTokens;
                }
            }

            // 5. Retrieved Code Snippets (Knowledge) — keep intact or trim to remaining headroom.
            string knowledgeBlock = string.Empty;
            int knowledgeTokens = 0;
            bool knowledgeTruncated = false;
            if (!string.IsNullOrWhiteSpace(req.RetrievedCodeSnippets))
            {
                int remaining = inputBudget - used;
                int rawK = counter.CountTokens(req.RetrievedCodeSnippets);
                if (rawK <= remaining)
                {
                    knowledgeBlock = req.RetrievedCodeSnippets!;
                    knowledgeTokens = rawK;
                    used += rawK;
                }
                else if (remaining >= MinUsefulBlockTokens)
                {
                    knowledgeBlock = counter.TruncateToFit(req.RetrievedCodeSnippets!, remaining - 8, fromEnd: false) + "\n...[knowledge trimmed]";
                    knowledgeTokens = counter.CountTokens(knowledgeBlock);
                    knowledgeTruncated = true;
                    used += knowledgeTokens;
                }
            }

            // 6. Recent History — keep newest turns first while remaining budget allows.
            var keptReversed = new List<AiMessage>();
            int historyTokens = 0;
            int droppedHistory = 0;
            for (int i = req.RecentHistory.Count - 1; i >= 0; i--)
            {
                var m = req.RecentHistory[i];
                if (string.IsNullOrWhiteSpace(m.Text)) continue;
                int cost = counter.CountTokens(m.Text) + 4;
                if (used + cost > inputBudget)
                {
                    droppedHistory++;
                    continue;
                }
                used += cost;
                historyTokens += cost;
                keptReversed.Add(m);
            }
            keptReversed.Reverse();

            var pricing = ModelPricingSpec.Parse(req.Model?.Pricing);
            var estCost = pricing.Calculate(used, 0);

            return new TokenBudgetReport
            {
                ModelContextWindow = window,
                EffectiveInputBudget = inputBudget,
                SystemInstructionsTokens = sysTokens,
                RecentHistoryTokens = historyTokens,
                ConversationSummariesTokens = summaryTokens,
                CurrentUserPromptTokens = promptTokens,
                OpenFilesTokens = openFileTokens,
                RetrievedCodeSnippetsTokens = knowledgeTokens,
                AttachmentsTokens = attachmentsTokens,
                ToolSchemasTokens = toolSchemaTokens,
                MemoryTokens = memoryTokens,
                OutputReserveTokens = outputReserve,
                Confidence = counter.Confidence,
                SourceLabel = counter.SourceLabel,
                TokenizerId = counter.TokenizerId,
                TruncationMethod = counter.Confidence == TokenCountConfidence.ExactBpe
                    ? $"Exact BPE boundary ({counter.TokenizerId})"
                    : "Heuristic char-boundary fallback",
                IncludedHistoryTurns = keptReversed.Count,
                DroppedHistoryTurns = droppedHistory,
                OpenFileIncluded = openFileIncluded,
                OpenFileTruncated = openFileTruncated,
                KnowledgeTruncated = knowledgeTruncated,
                PreparedOpenFileBlock = openFileBlock,
                PreparedKnowledgeBlock = knowledgeBlock,
                KeptHistory = keptReversed,
                EstimatedCost = estCost
            };
        }

        private static int CountToolSchemas(IReadOnlyList<AiToolDefinition> tools, IModelTokenCounter counter)
        {
            if (tools == null || tools.Count == 0) return 0;
            int sum = 0;
            foreach (var t in tools)
            {
                sum += counter.CountTokens($"{t.Name} {t.Description} {t.ParametersJson}") + 12;
            }
            return sum;
        }
    }
}
