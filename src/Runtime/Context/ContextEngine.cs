using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LlmodelBox.Common;
using LlmodelBox.Models.Ai;
using LlmodelBox.Services;

namespace LlmodelBox.Runtime.Context
{
    // Phase 3 — the Context Engine. Turns raw materials (user turn, system instruction, prior turns,
    // open file, attachments) into a budgeted AiRequest that fits the model's context window.
    //
    // Budgeting priority (highest first): the live user turn is always kept; the system instruction is
    // reserved next (its delivery stays with the transport — see ContextRequest.SystemInstruction);
    // then the open file is truncated to fit; then history is added newest-first until the budget is
    // spent, dropping the OLDEST turns first. All trimming is reported on the AgentActivityBus so the
    // Neural Net view reflects real compaction rather than a cosmetic animation.
    public sealed class ContextEngine : IContextEngine
    {
        // Chars kept for the open-file block's fixed scaffolding (header + fences) when truncating.
        private const int FileScaffoldChars = 64;
        // Approximate chars-per-token used only to convert a token budget into a char cut length.
        private const int ApproxCharsPerToken = 4;
        // Never include an open-file fragment smaller than this many tokens — drop it instead.
        private const int MinFileTokens = 48;

        public ContextBuildResult Build(ContextRequest request, ModelCapabilities capabilities)
        {
            var result = new ContextBuildResult();
            int budget = ComputeInputBudget(capabilities);
            result.InputBudget = budget;

            int used = 0;

            // 1) System instruction — reserve its cost (transport delivers it from settings).
            if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
            {
                used += TokenHelper.CountTokens(request.SystemInstruction);
                AgentActivityBus.Emit(AgentNode.Memory,
                    $"System instruction loaded ({request.SystemInstruction!.Trim().Length} chars)");
            }

            // 2) Live user turn — always included. Memory preamble + attachments note + open-file block
            //    prefix the prompt so they ride in the turn the transport treats as the live message.
            var prefix = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(request.MemoryPreamble))
            {
                prefix.Append(request.MemoryPreamble!.TrimEnd()).Append("\n\n");
                AgentActivityBus.Emit(AgentNode.Memory, "Recalled durable memory (facts + session goal)");
            }
            if (!string.IsNullOrWhiteSpace(request.KnowledgeBlock))
            {
                prefix.Append(request.KnowledgeBlock!.TrimEnd()).Append("\n\n");
                AgentActivityBus.Emit(AgentNode.Context,
                    $"Retrieved project knowledge ({request.KnowledgeBlock!.Trim().Length:N0} chars)");
            }
            if (request.Attachments != null && request.Attachments.Count > 0)
                prefix.AppendLine($"[Attached Files/Media: {string.Join(", ", request.Attachments)}]");

            string prompt = request.UserPrompt ?? string.Empty;
            int promptTok = TokenHelper.CountTokens(prompt) + TokenHelper.CountTokens(prefix.ToString());
            used += promptTok;

            result.Request = new AiRequest
            {
                Model = request.Model,
                Settings = request.Settings,
                SystemPrompt = null, // transport resolves the system instruction from settings (no dup)
                SessionId = request.SessionId,
                TaskId = request.TaskId
            };

            // 3) Open file — truncate to the remaining budget; drop entirely if too little room.
            string fileBlock = BuildOpenFileBlock(request, budget - used, result);
            if (fileBlock.Length > 0)
                used += TokenHelper.CountTokens(fileBlock);

            // 4) History — newest-first while it fits; oldest dropped first.
            var history = request.History ?? new List<AiMessage>();
            var keptReversed = new List<AiMessage>();
            for (int i = history.Count - 1; i >= 0; i--)
            {
                var m = history[i];
                if (string.IsNullOrWhiteSpace(m.Text)) continue;
                int cost = TokenHelper.CountTokens(m.Text) + 4; // small per-turn label overhead
                if (used + cost > budget) { result.DroppedHistoryTurns++; continue; }
                used += cost;
                keptReversed.Add(m);
            }
            keptReversed.Reverse(); // back to oldest-first
            result.IncludedHistoryTurns = keptReversed.Count;

            foreach (var m in keptReversed) result.Request.Messages.Add(m);

            // Final user turn assembled last.
            var finalUser = new StringBuilder();
            if (prefix.Length > 0) finalUser.Append(prefix);
            if (fileBlock.Length > 0) finalUser.Append(fileBlock);
            finalUser.Append(prompt);
            result.Request.Messages.Add(AiMessage.User(finalUser.ToString()));

            result.InputTokenEstimate = used;

            if (result.DroppedHistoryTurns > 0)
                AgentActivityBus.Emit(AgentNode.Compaction,
                    $"Dropped {result.DroppedHistoryTurns} oldest turn(s) to fit {budget:N0}-token budget", "warn");

            AgentActivityBus.Emit(AgentNode.Context,
                $"Context assembled · ~{used:N0}/{budget:N0} tokens · {result.IncludedHistoryTurns} turn(s)" +
                (result.OpenFileIncluded ? (result.OpenFileTruncated ? " · file(trimmed)" : " · file") : ""));

            return result;
        }

        // Reserve output headroom so a long context does not starve the completion.
        private static int ComputeInputBudget(ModelCapabilities caps)
        {
            int window = Math.Max(caps.ContextWindow, caps.InputTokenLimit);
            if (window <= 0) window = 8192;
            int outputReserve = Math.Clamp(caps.OutputTokenLimit, 256, Math.Max(256, window / 4));
            int safety = 256;
            return Math.Max(512, window - outputReserve - safety);
        }

        private static string BuildOpenFileBlock(ContextRequest request, int remainingTokens, ContextBuildResult result)
        {
            if (string.IsNullOrEmpty(request.OpenFilePath) || string.IsNullOrWhiteSpace(request.OpenFileText))
                return string.Empty;
            if (remainingTokens < MinFileTokens)
            {
                AgentActivityBus.Emit(AgentNode.Compaction,
                    $"{System.IO.Path.GetFileName(request.OpenFilePath)}: open-file context dropped (no budget)", "warn");
                return string.Empty;
            }

            string fn = System.IO.Path.GetFileName(request.OpenFilePath);
            string text = request.OpenFileText!;
            int fileTokens = TokenHelper.CountTokens(text);
            bool truncated = false;

            if (fileTokens > remainingTokens)
            {
                int maxChars = Math.Max(0, (remainingTokens * ApproxCharsPerToken) - FileScaffoldChars);
                if (maxChars < FileScaffoldChars) maxChars = FileScaffoldChars;
                if (text.Length > maxChars)
                {
                    text = text.Substring(0, maxChars) + "\n...[truncated for context budget]";
                    truncated = true;
                    AgentActivityBus.Emit(AgentNode.Compaction,
                        $"{fn}: trimmed {request.OpenFileText!.Length:N0}→{maxChars:N0} chars to fit context", "warn");
                }
            }

            result.OpenFileIncluded = true;
            result.OpenFileTruncated = truncated;
            return $"[Current Open File: {fn}]\n```\n{text}\n```\n";
        }
    }
}
