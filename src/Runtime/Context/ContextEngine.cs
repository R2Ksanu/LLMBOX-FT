using System;
using System.Collections.Generic;
using System.Text;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Tokens;
using LlmodelBox.Services;

namespace LlmodelBox.Runtime.Context
{
    /// <summary>
    /// Context Engine — delegates multi-bucket allocation, live prompt prioritization,
    /// model-specific BPE tokenization, and token-aware truncation to TokenBudgetManager.
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
            var report = _budgetManager.Allocate(new TokenBudgetRequest
            {
                Model = request.Model,
                Capabilities = capabilities,
                SystemInstructions = request.SystemInstruction,
                UserPrompt = request.UserPrompt ?? string.Empty,
                OpenFilePath = request.OpenFilePath,
                OpenFileText = request.OpenFileText,
                RecentHistory = request.History ?? new List<AiMessage>(),
                ConversationSummary = request.ConversationSummary,
                RetrievedCodeSnippets = request.KnowledgeBlock,
                Attachments = request.Attachments ?? new List<string>(),
                ToolSchemas = request.ToolSchemas ?? new List<AiToolDefinition>(),
                Memory = request.MemoryPreamble
            }, counter);

            var result = new ContextBuildResult
            {
                InputBudget = report.EffectiveInputBudget,
                InputTokenEstimate = report.TotalInputAllocatedTokens,
                IncludedHistoryTurns = report.IncludedHistoryTurns,
                DroppedHistoryTurns = report.DroppedHistoryTurns,
                OpenFileIncluded = report.OpenFileIncluded,
                OpenFileTruncated = report.OpenFileTruncated,
                BudgetReport = report,
                Request = new AiRequest
                {
                    Model = request.Model,
                    Settings = request.Settings,
                    SystemPrompt = null, // transport delivers system instruction
                    SessionId = request.SessionId,
                    TaskId = request.TaskId
                }
            };

            if (report.MemoryTokens > 0)
                AgentActivityBus.Emit(AgentNode.Memory, "Recalled durable memory (facts + session goal)");
            if (report.RetrievedCodeSnippetsTokens > 0)
                AgentActivityBus.Emit(AgentNode.Context, $"Retrieved project knowledge ({report.RetrievedCodeSnippetsTokens:N0} tok)");
            if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
                AgentActivityBus.Emit(AgentNode.Memory, $"System instruction budgeted ({report.SystemInstructionsTokens:N0} tok)");

            foreach (var m in report.KeptHistory)
                result.Request.Messages.Add(m);

            var finalUser = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(request.MemoryPreamble) && report.MemoryTokens > 0)
                finalUser.Append(request.MemoryPreamble!.TrimEnd()).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(request.ConversationSummary) && report.ConversationSummariesTokens > 0)
                finalUser.Append(request.ConversationSummary!.TrimEnd()).Append("\n\n");
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
                    $"{System.IO.Path.GetFileName(request.OpenFilePath)}: trimmed via {report.TruncationMethod}", "warn");

            AgentActivityBus.Emit(AgentNode.Context,
                $"Context assembled ({report.SourceLabel}) · {report.TotalInputAllocatedTokens:N0}/{report.EffectiveInputBudget:N0} tok · {report.IncludedHistoryTurns} turn(s)");

            return result;
        }
    }
}
