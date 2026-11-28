using System;
using System.Collections.Generic;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Runtime.Middleware;
using LlmodelBox.Runtime.Tokens;
using Xunit;

namespace LlmodelBox.Tests
{
    public class TokenIntelligenceTests
    {
        [Fact]
        public void TokenizerRegistry_SelectsExactBpeForOpenAiAndHonestFallbackForOthers()
        {
            var registry = new TokenizerRegistry();

            var gpt4o = registry.Resolve(new ModelItem { Id = "openai/gpt-4o", Provider = "OpenRouter" });
            Assert.Equal(TokenCountConfidence.ExactBpe, gpt4o.Confidence);
            Assert.Equal("o200k_base", gpt4o.TokenizerId);
            Assert.Contains("Exact BPE", gpt4o.SourceLabel);

            var gpt4 = registry.Resolve(new ModelItem { Id = "gpt-4-turbo", Provider = "OpenAI" });
            Assert.Equal(TokenCountConfidence.ExactBpe, gpt4.Confidence);
            Assert.Equal("cl100k_base", gpt4.TokenizerId);

            // Non-OpenAI families must NOT pretend to have an exact local BPE tokenizer
            var claude = registry.Resolve(new ModelItem { Id = "anthropic/claude-opus-4", Provider = "Anthropic" });
            Assert.Equal(TokenCountConfidence.EstimatedFallback, claude.Confidence);
            Assert.Contains("Estimated", claude.SourceLabel);

            var gemini = registry.Resolve(new ModelItem { Id = "gemini-2.5-pro", Provider = "Google" });
            Assert.Equal(TokenCountConfidence.EstimatedFallback, gemini.Confidence);
        }

        [Fact]
        public void TiktokenModelTokenCounter_CountsAndTruncatesAtExactBpeTokenBoundary()
        {
            var counter = TokenizerRegistry.Default.Resolve("gpt-4o", "OpenAI");
            Assert.Equal(TokenCountConfidence.ExactBpe, counter.Confidence);

            string sample = "public static int ComputeSum(int alpha, int beta) { return alpha + beta; }";
            var res = counter.Count(sample);
            Assert.True(res.IsExact);
            Assert.InRange(res.Tokens, 10, 30);

            string truncated = counter.TruncateToFit(sample, maxTokens: 6);
            int trimmedTokens = counter.CountTokens(truncated);
            Assert.True(trimmedTokens <= 6 && trimmedTokens > 0);
        }

        [Fact]
        public void ModelPricingSpec_SeparatesInputOutputAndNeverInventsUnknownCost()
        {
            // Separate input ($2.50/M) vs output ($10.00/M)
            var paid = ModelPricingSpec.Parse("$2.50 / $10.00 MTok");
            Assert.True(paid.HasKnownPricing);
            Assert.False(paid.IsFree);
            Assert.Equal(2.50, paid.InputPerMillionUsd, precision: 4);
            Assert.Equal(10.00, paid.OutputPerMillionUsd, precision: 4);

            var cost = paid.Calculate(inputTokens: 1_000_000, outputTokens: 500_000);
            Assert.Equal(2.50, cost.InputCostUsd, precision: 4);
            Assert.Equal(5.00, cost.OutputCostUsd, precision: 4);
            Assert.Equal(7.50, cost.TotalCostUsd, precision: 4);

            // Explicit free
            var free = ModelPricingSpec.Parse("Free");
            Assert.True(free.HasKnownPricing);
            Assert.True(free.IsFree);
            Assert.Equal(0.0, free.Calculate(50_000, 10_000).TotalCostUsd);

            // Unknown pricing must NOT invent costs
            var unknown = ModelPricingSpec.Parse("See provider");
            Assert.False(unknown.HasKnownPricing);
            Assert.False(unknown.Calculate(50_000, 10_000).HasKnownPricing);
            Assert.Equal("See provider", TokenHelper.FormatCostOrUnknown(unknown.Calculate(1000, 1000)));
        }

        [Fact]
        public void TokenBudgetManager_TracksAll10CategoriesAndPrioritizesLiveUserPrompt()
        {
            var mgr = new TokenBudgetManager();
            var counter = TokenizerRegistry.Default.Resolve("gpt-4o", "OpenAI");

            var history = new List<AiMessage>();
            for (int i = 0; i < 30; i++)
            {
                history.Add(AiMessage.User($"Historical message {i} discussing setup details and configurations."));
                history.Add(AiMessage.Assistant($"Historical response {i} with explanation and extra prose."));
            }

            var req = new TokenBudgetRequest
            {
                Model = new ModelItem { Id = "gpt-4o", Provider = "OpenAI", Pricing = "$2.50 / $10.00 MTok" },
                Capabilities = new ModelCapabilities { ContextWindow = 1600, InputTokenLimit = 1600, OutputTokenLimit = 300 },
                SystemInstructions = "Be concise and precise.",
                ConversationSummary = "Earlier turns initialized the project structure.",
                Memory = "User prefers C# .NET 8.",
                RetrievedCodeSnippets = "public class Sample { public void Run() {} }",
                OpenFilePath = "BigFile.cs",
                OpenFileText = string.Join("\n", System.Linq.Enumerable.Range(0, 350).Select(i => $"public int ComputeValue{i}(int arg{i}) => arg{i} * {i} + 1;")),
                Attachments = new[] { "screenshot.png" },
                ToolSchemas = new[]
                {
                    new AiToolDefinition { Name = "read_file", Description = "Reads file", ParametersJson = "{\"type\":\"object\"}" }
                },
                RecentHistory = history,
                UserPrompt = "PRIORITY_LIVE_REQUEST: Refactor ComputeAsync."
            };

            var report = mgr.Allocate(req, counter);

            // Verify all 10 categories tracked
            Assert.True(report.SystemInstructionsTokens > 0);
            Assert.True(report.CurrentUserPromptTokens > 0);
            Assert.True(report.ToolSchemasTokens > 0);
            Assert.True(report.AttachmentsTokens > 0);
            Assert.True(report.MemoryTokens > 0);
            Assert.True(report.ConversationSummariesTokens > 0);
            Assert.True(report.OpenFilesTokens > 0);
            Assert.True(report.RetrievedCodeSnippetsTokens >= 0);
            Assert.True(report.OutputReserveTokens == 300);

            // Exact BPE truncation applied & budget respected
            Assert.True(report.OpenFileTruncated);
            Assert.True(report.DroppedHistoryTurns > 0);
            Assert.Equal(TokenCountConfidence.ExactBpe, report.Confidence);
            Assert.Contains("Exact BPE boundary", report.TruncationMethod);
            Assert.True(report.TotalInputAllocatedTokens <= report.EffectiveInputBudget);
        }

        [Fact]
        public void TokenIntelligenceEngine_ReconcilesProviderAuthoritativeVsExactBpeAndEstimate()
        {
            var engine = new TokenIntelligenceEngine();
            var gpt4o = new ModelItem { Id = "gpt-4o", Provider = "OpenAI", Pricing = "$2.50 / $10.00 MTok" };

            var live = new TokenUsageRecord
            {
                PromptTokens = 420,
                CompletionTokens = 180,
                TotalTokens = 600,
                CachedTokens = 100,
                ReasoningTokens = 40,
                IsLiveApi = true
            };

            var reportLive = engine.Reconcile("Hello GPT-4o", "Answer", live, contextPromptEstimate: 400, model: gpt4o);
            Assert.True(reportLive.IsProviderAuthoritative);
            Assert.Equal(TokenCountConfidence.ProviderReported, reportLive.Confidence);
            Assert.Equal("Provider API Reported", reportLive.SourceLabel);
            Assert.Equal(20, reportLive.ReconciliationDeltaTokens);
            Assert.Equal(100, reportLive.CachedTokens);
            Assert.Equal(40, reportLive.ReasoningTokens);
            Assert.NotNull(reportLive.CostBreakdown);
            Assert.True(reportLive.CostBreakdown!.HasKnownPricing);

            var reportLocal = engine.Reconcile("Hello GPT-4o world", "Short answer", liveUsage: null, contextPromptEstimate: 0, model: gpt4o);
            Assert.False(reportLocal.IsProviderAuthoritative);
            Assert.Equal(TokenCountConfidence.ExactBpe, reportLocal.Confidence);
            Assert.Contains("o200k_base", reportLocal.SourceLabel);
        }
    }
}
