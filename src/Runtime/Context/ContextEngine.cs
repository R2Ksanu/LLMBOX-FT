using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Tokens;
using LlmodelBox.Services;

namespace LlmodelBox.Runtime.Context
{
    /// <summary>
    /// Part 5 Context Engine — Priority-based context allocation pipeline:
    ///   1. Critical non-droppable reservations (Live prompt, System, Tool schemas, Attachments)
    ///   2. Scoped Memory & Auto-Compacted Conversation Handoff Summary
    ///   3. Relevance-scored Open & Candidate Files
    ///   4. Deduplicated Knowledge Excerpts
    ///   5. Rolling Recent History + Pre-flight Auto-Compaction when near threshold
    /// Produces full ContextSourceReport attribution for inspection and telemetry.
    /// </summary>
    public sealed class ContextEngine : IContextEngine
    {
        private readonly ITokenizerRegistry _tokenizers;
        private readonly TokenBudgetManager _budgetManager;

        public ContextEngine() : this(TokenizerRegistry.Default, new TokenBudgetManager()) { }

        public ContextEngine(ITokenizerRegistry tokenizers, TokenBudgetManager? budgetManager = null)
        {
            _tokenizers = tokenizers ?? TokenizerRegistry.Default;
            _budgetManager = budgetManager ?? new TokenBudgetManager();
        }

        public ContextBuildResult Build(ContextRequest request, ModelCapabilities capabilities)
        {
            var counter = _tokenizers.Resolve(request.Model);

            // 1. Score open & candidate files by relevance to the current user request.
            var candidates = new List<CandidateFileContext>(request.CandidateFiles ?? new List<CandidateFileContext>());
            if (!string.IsNullOrWhiteSpace(request.OpenFilePath) && !string.IsNullOrWhiteSpace(request.OpenFileText) &&
                !candidates.Any(c => string.Equals(c.Path, request.OpenFilePath, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Insert(0, new CandidateFileContext
                {
                    Path = request.OpenFilePath!,
                    Content = request.OpenFileText!,
                    IsActiveOpenFile = true,
                    OpenRecencyRank = 0
                });
            }

            var rankedFiles = FileRelevanceScorer.RankCandidates(candidates, request.UserPrompt);
            string? effectiveOpenPath = request.OpenFilePath;
            string? effectiveOpenText = request.OpenFileText;
            double openFileScore = rankedFiles.Count > 0 ? rankedFiles[0].Score : 0;

            if (string.IsNullOrWhiteSpace(effectiveOpenText) && rankedFiles.Count > 0)
            {
                effectiveOpenPath = rankedFiles[0].File.Path;
                effectiveOpenText = rankedFiles[0].File.Content;
            }

            // 2. Deduplicate Knowledge snippets against the open file and scoped memory.
            var (dedupKnowledge, dedupCount) = ContextDeduplicator.DeduplicateKnowledgeAgainstOpenFile(
                request.KnowledgeBlock,
                effectiveOpenPath,
                effectiveOpenText,
                request.MemoryPreamble);

            // 3. Evaluate Auto-Compaction policy before final token budget packing.
            int window = Math.Max(capabilities.ContextWindow, capabilities.InputTokenLimit);
            if (window <= 0) window = 8192;
            int outReserve = Math.Clamp(capabilities.OutputTokenLimit, 256, Math.Max(256, window / 4));
            int inputBudgetEst = Math.Max(512, window - outReserve - 256);

            int fixedEst = counter.CountTokens(request.UserPrompt ?? string.Empty)
                         + counter.CountTokens(request.SystemInstruction ?? string.Empty)
                         + counter.CountTokens(request.MemoryPreamble ?? string.Empty)
                         + Math.Min(inputBudgetEst / 3, counter.CountTokens(effectiveOpenText ?? string.Empty))
                         + counter.CountTokens(dedupKnowledge ?? string.Empty);

            var compactOptions = new AutoCompactOptions
            {
                Enabled = request.EnableAutoCompact && (request.Settings?.AutoCompactEnabled ?? true),
                SoftTriggerRatio = request.AutoCompactThresholdRatio
                    ?? (request.Settings?.AutoCompactThresholdPercent > 0
                        ? Math.Clamp(request.Settings.AutoCompactThresholdPercent / 100.0, 0.40, 0.92)
                        : 0.72)
            };

            var compressor = new ContextCompressionEngine(compactOptions);
            var compaction = compressor.CompactIfNeeded(
                request.History ?? new List<AiMessage>(),
                request.ConversationSummary,
                inputBudgetEst,
                fixedEst,
                counter,
                manualForce: request.ForceCompact,
                focusInstruction: request.CompactFocusInstruction);

            string? effectiveSummary = !string.IsNullOrWhiteSpace(compaction.ConversationSummary)
                ? compaction.ConversationSummary
                : request.ConversationSummary;
            List<AiMessage> effectiveHistory = compaction.CompactionTriggered
                ? compaction.CompactedHistory
                : (request.History ?? new List<AiMessage>());

            // 4. Execute priority-tiered TokenBudgetManager allocation.
            var report = _budgetManager.Allocate(new TokenBudgetRequest
            {
                Model = request.Model,
                Capabilities = capabilities,
                SystemInstructions = request.SystemInstruction,
                UserPrompt = request.UserPrompt ?? string.Empty,
                OpenFilePath = effectiveOpenPath,
                OpenFileText = effectiveOpenText,
                RecentHistory = effectiveHistory,
                ConversationSummary = effectiveSummary,
                RetrievedCodeSnippets = dedupKnowledge,
                Attachments = request.Attachments ?? new List<string>(),
                ToolSchemas = request.ToolSchemas ?? new List<AiToolDefinition>(),
                Memory = request.MemoryPreamble
            }, counter);

            var scoredFileResults = rankedFiles
                .Select((x, idx) => new ScoredFileResult
                {
                    Path = x.File.Path,
                    Score = Math.Round(x.Score, 2),
                    Reason = x.Reason,
                    Included = idx == 0 && report.OpenFileIncluded
                })
                .ToList();

            var sourceReport = BuildSourceReport(
                request,
                effectiveOpenPath,
                openFileScore,
                dedupCount,
                compaction,
                report);

            var result = new ContextBuildResult
            {
                InputBudget = report.EffectiveInputBudget,
                InputTokenEstimate = report.TotalInputAllocatedTokens,
                IncludedHistoryTurns = report.IncludedHistoryTurns,
                DroppedHistoryTurns = report.DroppedHistoryTurns + compaction.SummarizedMessagesCount,
                OpenFileIncluded = report.OpenFileIncluded,
                OpenFileTruncated = report.OpenFileTruncated,
                BudgetReport = report,
                AutoCompacted = compaction.CompactionTriggered,
                CompactionReason = compaction.TriggerReason,
                UpdatedConversationSummary = compaction.CompactionTriggered ? effectiveSummary : request.ConversationSummary,
                DeduplicatedItemsCount = dedupCount,
                ScoredFiles = scoredFileResults,
                SourceReport = sourceReport,
                Request = new AiRequest
                {
                    Model = request.Model ?? new Models.ModelItem(),
                    Settings = request.Settings ?? new Models.UserSettings(),
                    SystemPrompt = null,
                    SessionId = request.SessionId,
                    TaskId = request.TaskId
                }
            };

            if (report.MemoryTokens > 0)
                AgentActivityBus.Emit(AgentNode.Memory, $"Recalled scoped memory ({report.MemoryTokens:N0} tok)");
            if (report.RetrievedCodeSnippetsTokens > 0)
                AgentActivityBus.Emit(AgentNode.Context, $"Retrieved project knowledge ({report.RetrievedCodeSnippetsTokens:N0} tok)");
            if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
                AgentActivityBus.Emit(AgentNode.Memory, $"System instruction budgeted ({report.SystemInstructionsTokens:N0} tok)");
            if (compaction.CompactionTriggered)
                AgentActivityBus.Emit(AgentNode.Compaction,
                    $"Auto-compacted ({compaction.TriggerReason}): summarized {compaction.SummarizedMessagesCount} turn(s), pruned {compaction.PrunedToolMessages} tool payload(s), saved {compaction.TokensSaved:N0} tok");

            foreach (var m in report.KeptHistory)
                result.Request.Messages.Add(m);

            var finalUser = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(request.MemoryPreamble) && report.MemoryTokens > 0)
                finalUser.Append(request.MemoryPreamble!.TrimEnd()).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(effectiveSummary) && report.ConversationSummariesTokens > 0)
                finalUser.Append(effectiveSummary!.TrimEnd()).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(report.PreparedKnowledgeBlock))
                finalUser.Append(report.PreparedKnowledgeBlock.TrimEnd()).Append("\n\n");
            if (request.Attachments != null && request.Attachments.Count > 0)
                finalUser.AppendLine($"[Attached Files/Media: {string.Join(", ", request.Attachments)}]");
            if (!string.IsNullOrEmpty(report.PreparedOpenFileBlock))
                finalUser.Append(report.PreparedOpenFileBlock);
            finalUser.Append(request.UserPrompt ?? string.Empty);

            result.Request.Messages.Add(AiMessage.User(finalUser.ToString()));

            if (report.DroppedHistoryTurns > 0)
                AgentActivityBus.Emit(AgentNode.Compaction,
                    $"Dropped {report.DroppedHistoryTurns} oldest turn(s) to fit {report.EffectiveInputBudget:N0}-token budget", "warn");
            if (report.OpenFileTruncated)
                AgentActivityBus.Emit(AgentNode.Compaction,
                    $"{System.IO.Path.GetFileName(effectiveOpenPath)}: trimmed via {report.TruncationMethod}", "warn");

            AgentActivityBus.Emit(AgentNode.Context,
                $"Context assembled ({report.SourceLabel}) · {report.TotalInputAllocatedTokens:N0}/{report.EffectiveInputBudget:N0} tok · {report.IncludedHistoryTurns} turn(s)");

            return result;
        }

        private static List<ContextSourceEntry> BuildSourceReport(
            ContextRequest req,
            string? openPath,
            double openScore,
            int dedupCount,
            CompactionResult compaction,
            TokenBudgetReport r)
        {
            return new List<ContextSourceEntry>
            {
                new()
                {
                    Category = "User Prompt",
                    SourceName = "Live Input",
                    Tier = ContextPriorityTier.CriticalPromptAndSystem,
                    TokensAllocated = r.CurrentUserPromptTokens,
                    RelevanceScore = 100.0,
                    Included = true,
                    Reason = "Highest priority reservation"
                },
                new()
                {
                    Category = "System Instructions",
                    SourceName = string.IsNullOrWhiteSpace(req.SystemInstruction) ? "Disabled" : "Active Persona",
                    Tier = ContextPriorityTier.CriticalPromptAndSystem,
                    TokensAllocated = r.SystemInstructionsTokens,
                    RelevanceScore = 100.0,
                    Included = r.SystemInstructionsTokens > 0,
                    Reason = r.SystemInstructionsTokens > 0 ? "Reserved system budget" : "None configured"
                },
                new()
                {
                    Category = "Tool Schemas",
                    SourceName = $"{req.ToolSchemas?.Count ?? 0} tool(s)",
                    Tier = ContextPriorityTier.CriticalPromptAndSystem,
                    TokensAllocated = r.ToolSchemasTokens,
                    RelevanceScore = 95.0,
                    Included = r.ToolSchemasTokens > 0,
                    Reason = r.ToolSchemasTokens > 0 ? "Reserved tool definitions" : "No tools advertised"
                },
                new()
                {
                    Category = "Scoped Memory",
                    SourceName = string.IsNullOrWhiteSpace(req.ProjectKey) ? "Global / Session" : "Project + Global Scopes",
                    Tier = ContextPriorityTier.PinnedMemoryAndSummary,
                    TokensAllocated = r.MemoryTokens,
                    RelevanceScore = 90.0,
                    Included = r.MemoryTokens > 0,
                    Reason = r.MemoryTokens > 0 ? "Pinned scoped memory injected" : "Empty or over budget"
                },
                new()
                {
                    Category = "Conversation Summary",
                    SourceName = compaction.CompactionTriggered ? $"Handoff ({compaction.TriggerReason})" : "Prior Handoff",
                    Tier = ContextPriorityTier.PinnedMemoryAndSummary,
                    TokensAllocated = r.ConversationSummariesTokens,
                    RelevanceScore = 88.0,
                    Included = r.ConversationSummariesTokens > 0,
                    Reason = compaction.CompactionTriggered
                        ? $"Auto-compacted {compaction.SummarizedMessagesCount} turn(s), saved {compaction.TokensSaved:N0} tok"
                        : (r.ConversationSummariesTokens > 0 ? "Retained handoff summary" : "Not needed")
                },
                new()
                {
                    Category = "Open File",
                    SourceName = string.IsNullOrWhiteSpace(openPath) ? "None" : System.IO.Path.GetFileName(openPath),
                    Tier = ContextPriorityTier.ScoredFiles,
                    TokensAllocated = r.OpenFilesTokens,
                    RelevanceScore = openScore,
                    Included = r.OpenFileIncluded,
                    Truncated = r.OpenFileTruncated,
                    Reason = r.OpenFileIncluded ? (r.OpenFileTruncated ? "Token-truncated" : "Included full") : "None / over headroom"
                },
                new()
                {
                    Category = "Knowledge Retrieval",
                    SourceName = string.IsNullOrWhiteSpace(req.ProjectKey) ? "Unavailable" : "Incremental Index",
                    Tier = ContextPriorityTier.KnowledgeRetrieval,
                    TokensAllocated = r.RetrievedCodeSnippetsTokens,
                    RelevanceScore = 75.0,
                    Included = r.RetrievedCodeSnippetsTokens > 0,
                    Truncated = r.KnowledgeTruncated,
                    Deduplicated = dedupCount > 0,
                    Reason = dedupCount > 0 ? $"Filtered {dedupCount} duplicate snippet(s)" : (r.RetrievedCodeSnippetsTokens > 0 ? "Retrieved excerpts" : "No hits")
                },
                new()
                {
                    Category = "Recent History",
                    SourceName = $"{r.IncludedHistoryTurns} kept / {r.DroppedHistoryTurns} dropped",
                    Tier = ContextPriorityTier.RecentHistory,
                    TokensAllocated = r.RecentHistoryTokens,
                    RelevanceScore = 80.0,
                    Included = r.IncludedHistoryTurns > 0,
                    Truncated = r.DroppedHistoryTurns > 0,
                    Reason = compaction.CompactionTriggered ? "Compacted + newest turns retained" : "Recency order"
                },
                new()
                {
                    Category = "Output Reserve",
                    SourceName = r.SourceLabel,
                    Tier = ContextPriorityTier.OutputReserve,
                    TokensAllocated = r.OutputReserveTokens,
                    RelevanceScore = 100.0,
                    Included = true,
                    Reason = "Protected completion reserve"
                }
            };
        }
    }
}
