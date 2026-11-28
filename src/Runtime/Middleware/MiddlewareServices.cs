using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Content;
using LlmodelBox.Runtime.Intent;
using LlmodelBox.Runtime.Tokens;
using LlmodelBox.Runtime.Tools.Builtin;

namespace LlmodelBox.Runtime.Middleware
{
    public sealed class ConversationEngine : IConversationEngine
    {
        public List<AiMessage> BuildHistoryWindow(IEnumerable<ChatMessageItem> messages, int maxTurns = 24)
        {
            if (messages == null) return new List<AiMessage>();
            var valid = messages
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Content) && (m.IsUser || (m.IsAssistant && !m.IsError)))
                .TakeLast(Math.Max(1, maxTurns));

            var result = new List<AiMessage>();
            foreach (var cm in valid)
            {
                result.Add(cm.IsUser ? AiMessage.User(cm.Content) : AiMessage.Assistant(cm.Content));
            }
            return result;
        }

        public string DeriveThreadTitle(string firstUserPrompt, int maxLength = 48)
        {
            if (string.IsNullOrWhiteSpace(firstUserPrompt)) return "New Conversation";
            string clean = firstUserPrompt.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length <= maxLength ? clean : clean.Substring(0, maxLength).TrimEnd() + "...";
        }
    }

    public sealed class TaskPlanningEngine : ITaskPlanningEngine
    {
        public ExecutionPlan BuildPlan(string userPrompt, IntentResult intent)
        {
            var plan = new ExecutionPlan
            {
                Goal = string.IsNullOrWhiteSpace(userPrompt) ? "Assist user" : userPrompt.Trim()
            };

            var understand = new ExecutionPlanStep { Title = "Analyze request & retrieve context" };
            plan.Steps.Add(understand);

            if (intent.WantsTools || intent.Kind == IntentKind.EditCode || intent.Kind == IntentKind.RunTool || intent.Kind == IntentKind.ExplainCode)
            {
                var execute = new ExecutionPlanStep
                {
                    Title = "Execute workspace inspection / file tools",
                    DependsOn = { understand.Id }
                };
                var verify = new ExecutionPlanStep
                {
                    Title = "Verify edits & summarize diff",
                    DependsOn = { execute.Id }
                };
                plan.Steps.Add(execute);
                plan.Steps.Add(verify);
            }
            else
            {
                plan.Steps.Add(new ExecutionPlanStep
                {
                    Title = "Generate structured response",
                    DependsOn = { understand.Id }
                });
            }

            return plan;
        }
    }

    public sealed class TokenIntelligenceEngine : ITokenIntelligenceEngine
    {
        private readonly ITokenizerRegistry _registry;
        private readonly TokenBudgetManager _budgetManager;

        public TokenIntelligenceEngine() : this(TokenizerRegistry.Default, new TokenBudgetManager()) { }

        public TokenIntelligenceEngine(ITokenizerRegistry registry, TokenBudgetManager? budgetManager = null)
        {
            _registry = registry ?? TokenizerRegistry.Default;
            _budgetManager = budgetManager ?? new TokenBudgetManager();
        }

        public int EstimateTokens(string? text, ModelItem? model = null)
            => string.IsNullOrEmpty(text) ? 0 : ResolveCounter(model).CountTokens(text);

        public TokenCountResult CountWithConfidence(string? text, ModelItem? model = null)
            => ResolveCounter(model).Count(text);

        public IModelTokenCounter ResolveCounter(ModelItem? model)
            => _registry.Resolve(model);

        public TokenBudgetReport AllocateBudget(TokenBudgetRequest request)
            => _budgetManager.Allocate(request, ResolveCounter(request.Model));

        public TokenReconciliationReport Reconcile(
            string? promptText,
            string? responseText,
            TokenUsageRecord? liveUsage,
            int contextPromptEstimate = 0,
            ModelItem? model = null)
        {
            var counter = ResolveCounter(model);
            int estimatedPrompt = contextPromptEstimate > 0 ? contextPromptEstimate : counter.CountTokens(promptText);

            if (liveUsage != null && liveUsage.TotalTokens > 0)
            {
                var breakdown = TokenHelper.CalculateCostBreakdown(
                    liveUsage.PromptTokens,
                    liveUsage.CompletionTokens,
                    model?.Pricing,
                    liveUsage.CachedTokens);

                return new TokenReconciliationReport
                {
                    PromptTokens = liveUsage.PromptTokens,
                    CompletionTokens = liveUsage.CompletionTokens,
                    TotalTokens = liveUsage.TotalTokens,
                    CachedTokens = liveUsage.CachedTokens,
                    ReasoningTokens = liveUsage.ReasoningTokens,
                    EstimatedPromptTokens = estimatedPrompt,
                    IsProviderAuthoritative = true,
                    Confidence = TokenCountConfidence.ProviderReported,
                    SourceLabel = "Provider API Reported",
                    CostBreakdown = breakdown
                };
            }

            int p = estimatedPrompt;
            int c = counter.CountTokens(responseText);
            var localBreakdown = TokenHelper.CalculateCostBreakdown(p, c, model?.Pricing);

            return new TokenReconciliationReport
            {
                PromptTokens = p,
                CompletionTokens = c,
                TotalTokens = p + c,
                EstimatedPromptTokens = estimatedPrompt,
                IsProviderAuthoritative = false,
                Confidence = counter.Confidence,
                SourceLabel = counter.SourceLabel,
                CostBreakdown = localBreakdown
            };
        }
    }

    public sealed class ContextCompressionEngine : IContextCompressionEngine
    {
        public CompressionResult CompressHistory(IReadOnlyList<AiMessage> history, int maxHistoryTokens)
        {
            if (history == null || history.Count == 0 || maxHistoryTokens <= 0)
            {
                return new CompressionResult();
            }

            var kept = new List<AiMessage>();
            int runningTokens = 0;

            for (int i = history.Count - 1; i >= 0; i--)
            {
                var msg = history[i];
                int cost = TokenHelper.CountTokens(msg.Text) + 6;
                if (kept.Count > 0 && runningTokens + cost > maxHistoryTokens)
                {
                    break;
                }
                kept.Insert(0, msg);
                runningTokens += cost;
            }

            int dropped = history.Count - kept.Count;
            string? note = null;
            if (dropped > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[Context compressed: {dropped} earlier message(s) omitted to fit budget]");
                foreach (var old in history.Take(Math.Min(dropped, 4)))
                {
                    string preview = (old.Text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                    if (preview.Length > 100) preview = preview.Substring(0, 100) + "...";
                    sb.AppendLine($"- {old.Role}: {preview}");
                }
                note = sb.ToString().TrimEnd();
            }

            return new CompressionResult
            {
                KeptMessages = kept,
                SummaryNote = note,
                DroppedCount = dropped
            };
        }
    }

    public sealed class ResponseNormalizationEngine : IResponseNormalizationEngine
    {
        public NormalizedAssistantTurn Normalize(string? rawText, bool isError, IEnumerable<ChatContentBlock>? runtimeBlocks = null)
        {
            string visible = ReasoningFilter.Split(rawText ?? string.Empty, out string reasoning);
            var blocks = isError
                ? new List<ChatContentBlock>
                {
                    new ErrorBlock
                    {
                        Title = "Assistant Error",
                        Message = visible,
                        CanRetry = true
                    }
                }
                : StructuredContentParser.Parse(visible, runtimeBlocks);

            return new NormalizedAssistantTurn
            {
                VisibleText = visible,
                ReasoningSummary = reasoning,
                Blocks = blocks,
                IsError = isError
            };
        }
    }

    public sealed class VerificationEngine : IVerificationEngine
    {
        public VerificationReport VerifyTurn(AiResponse response, string? workspaceRoot)
        {
            var checks = new List<string>();
            var warnings = new List<string>();

            if (response.IsError)
            {
                warnings.Add(response.ErrorMessage ?? "Model returned error.");
                return new VerificationReport { Passed = false, Checks = checks, Warnings = warnings };
            }

            foreach (var diffSummary in response.StructuredBlocks.OfType<DiffSummaryBlock>())
            {
                foreach (var file in diffSummary.Files)
                {
                    if (string.IsNullOrWhiteSpace(workspaceRoot)) continue;
                    try
                    {
                        string full = WorkspacePaths.ResolveInside(workspaceRoot, file.FilePath);
                        if (File.Exists(full))
                        {
                            checks.Add($"Verified file exists on disk: {file.FilePath}");
                        }
                        else
                        {
                            warnings.Add($"Edited file missing on disk: {file.FilePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Path verification skipped for {file.FilePath}: {ex.Message}");
                    }
                }
            }

            return new VerificationReport
            {
                Passed = warnings.Count == 0,
                Checks = checks,
                Warnings = warnings
            };
        }
    }

    public sealed class CheckpointEngine : ICheckpointEngine
    {
        private readonly object _gate = new();
        private readonly List<TurnCheckpoint> _checkpoints = new();

        public TurnCheckpoint CreateCheckpoint(string label, string workspaceRoot)
        {
            var cp = new TurnCheckpoint
            {
                Label = label ?? "Turn Checkpoint",
                WorkspaceRoot = workspaceRoot ?? string.Empty
            };
            lock (_gate)
            {
                _checkpoints.Add(cp);
                if (_checkpoints.Count > 50) _checkpoints.RemoveAt(0);
            }
            return cp;
        }

        public void CaptureBeforeEdit(TurnCheckpoint checkpoint, string filePath)
        {
            if (checkpoint == null || string.IsNullOrWhiteSpace(checkpoint.WorkspaceRoot) || string.IsNullOrWhiteSpace(filePath))
                return;

            string full = WorkspacePaths.ResolveInside(checkpoint.WorkspaceRoot, filePath);
            string rel = Path.GetRelativePath(checkpoint.WorkspaceRoot, full);

            lock (_gate)
            {
                if (checkpoint.Files.Any(f => string.Equals(f.RelativePath, rel, StringComparison.OrdinalIgnoreCase)))
                    return;

                bool exists = File.Exists(full);
                checkpoint.Files.Add(new FileCheckpointEntry
                {
                    RelativePath = rel,
                    ExistedBefore = exists,
                    PreviousContent = exists ? File.ReadAllText(full) : null
                });
            }
        }

        public IReadOnlyList<TurnCheckpoint> GetCheckpoints(string workspaceRoot)
        {
            lock (_gate)
            {
                return _checkpoints
                    .Where(c => string.Equals(c.WorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                    .Reverse()
                    .ToList();
            }
        }

        public Task<int> RestoreCheckpointAsync(string checkpointId, CancellationToken ct = default)
        {
            TurnCheckpoint? cp;
            lock (_gate)
            {
                cp = _checkpoints.FirstOrDefault(c => string.Equals(c.Id, checkpointId, StringComparison.Ordinal));
            }
            if (cp == null || string.IsNullOrWhiteSpace(cp.WorkspaceRoot)) return Task.FromResult(0);

            int restored = 0;
            foreach (var entry in cp.Files)
            {
                ct.ThrowIfCancellationRequested();
                string full = WorkspacePaths.ResolveInside(cp.WorkspaceRoot, entry.RelativePath);
                if (entry.ExistedBefore)
                {
                    AtomicFile.WriteAllText(full, entry.PreviousContent ?? string.Empty);
                }
                else if (File.Exists(full))
                {
                    File.Delete(full);
                }
                restored++;
            }
            return Task.FromResult(restored);
        }
    }

    public sealed class RealtimeEventEngine : IRealtimeEventEngine
    {
        private readonly object _gate = new();
        private readonly List<MiddlewareEvent> _recent = new();
        private long _sequence = 0;

        public event Action<MiddlewareEvent>? EventPublished;

        public void Publish(string conversationId, string turnId, MessageActivityState state, string detail)
        {
            var evt = new MiddlewareEvent
            {
                Sequence = Interlocked.Increment(ref _sequence),
                ConversationId = conversationId ?? string.Empty,
                TurnId = turnId ?? string.Empty,
                State = state,
                Detail = detail ?? string.Empty
            };
            lock (_gate)
            {
                _recent.Add(evt);
                if (_recent.Count > 200) _recent.RemoveAt(0);
            }
            try { EventPublished?.Invoke(evt); } catch { }
        }

        public IReadOnlyList<MiddlewareEvent> GetRecentEvents(int maxCount = 50)
        {
            lock (_gate)
            {
                return _recent.TakeLast(Math.Max(1, maxCount)).ToList();
            }
        }
    }
}
