using System;
using System.Text.RegularExpressions;
using LlmodelBox.Models;
using LlmodelBox.Runtime.Tokens;

namespace LlmodelBox.Common
{
    /// <summary>
    /// Unified token counting and cost estimation facade. Uses model-specific exact BPE
    /// tokenizers (Tiktoken o200k_base / cl100k_base via TokenizerRegistry) when available and
    /// explicitly falls back to heuristic estimation for unshipped model vocabularies — never
    /// claiming heuristic counts as universal exact BPE.
    /// </summary>
    public static class TokenHelper
    {
        public static TokenCountResult CountWithConfidence(string? text, ModelItem? model = null)
        {
            var counter = TokenizerRegistry.Default.Resolve(model);
            return counter.Count(text);
        }

        public static int CountTokens(string? text)
            => TokenizerRegistry.Default.FallbackCounter.CountTokens(text);

        public static int CountTokens(string? text, ModelItem? model)
            => TokenizerRegistry.Default.Resolve(model).CountTokens(text);

        /// <summary>
        /// Explicit heuristic estimate helper for legacy call sites without a target model.
        /// </summary>
        public static int EstimateTokens(string? text)
            => TokenizerRegistry.Default.FallbackCounter.CountTokens(text);

        /// <summary>
        /// Parses context window size from catalog strings ("128k tokens", "200k", "1M tokens", "2M").
        /// Returns 128,000 when unparseable.
        /// </summary>
        public static int ParseContextWindowTokens(string? contextStr)
        {
            if (string.IsNullOrWhiteSpace(contextStr)) return 128_000;

            string s = contextStr.Trim().ToLowerInvariant();
            try
            {
                if (s.Contains("1m") || s.Contains("1000k")) return 1_000_000;
                if (s.Contains("2m")) return 2_000_000;

                var match = Regex.Match(s, @"(\d+)\s*k");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int k))
                {
                    return k * 1000;
                }

                var numMatch = Regex.Match(s, @"\d+");
                if (numMatch.Success && int.TryParse(numMatch.Value, out int direct))
                {
                    return direct > 1000 ? direct : direct * 1000;
                }
            }
            catch { }

            return 128_000;
        }

        /// <summary>
        /// Calculates estimated USD cost using separate input and output rates when known.
        /// Returns 0.0 for free models or when pricing cannot be parsed (never invents hidden rates).
        /// </summary>
        public static double CalculateCost(int promptTokens, int completionTokens, string? pricingStr)
        {
            var breakdown = CalculateCostBreakdown(promptTokens, completionTokens, pricingStr);
            return breakdown.HasKnownPricing ? breakdown.TotalCostUsd : 0.0;
        }

        public static TokenCostBreakdown CalculateCostBreakdown(int promptTokens, int completionTokens, string? pricingStr, int cachedTokens = 0)
            => ModelPricingSpec.Parse(pricingStr).Calculate(promptTokens, completionTokens, cachedTokens);

        public static string FormatTokens(long tokens)
        {
            if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F2}M";
            if (tokens >= 10_000) return $"{tokens / 1000.0:F1}k";
            return $"{tokens:N0}";
        }

        public static string FormatCost(double cost)
        {
            if (cost <= 0.0) return "$0.00";
            if (cost < 0.0001) return "<$0.0001";
            if (cost < 0.01) return $"${cost:F4}";
            return $"${cost:F3}";
        }

        public static string FormatCostOrUnknown(TokenCostBreakdown? cost)
        {
            if (cost == null || !cost.HasKnownPricing) return "See provider";
            if (cost.IsFree) return "$0.00 (Free)";
            return FormatCost(cost.TotalCostUsd);
        }
    }
}
