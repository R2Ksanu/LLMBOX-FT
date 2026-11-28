using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using LlmodelBox.Models;

namespace LlmodelBox.Runtime.Tokens
{
    public enum TokenCountConfidence
    {
        EstimatedFallback,
        ExactBpe,
        ProviderReported
    }

    public sealed record TokenCountResult(
        int Tokens,
        TokenCountConfidence Confidence,
        string SourceLabel,
        string TokenizerId)
    {
        public bool IsExact => Confidence == TokenCountConfidence.ExactBpe || Confidence == TokenCountConfidence.ProviderReported;
    }

    public interface IModelTokenCounter
    {
        string TokenizerId { get; }
        TokenCountConfidence Confidence { get; }
        string SourceLabel { get; }
        TokenCountResult Count(string? text);
        int CountTokens(string? text);
        string TruncateToFit(string text, int maxTokens, bool fromEnd = false);
    }

    public interface ITokenizerRegistry
    {
        IModelTokenCounter Resolve(ModelItem? model);
        IModelTokenCounter Resolve(string? modelId, string? provider = null);
        IModelTokenCounter FallbackCounter { get; }
    }

    public sealed record ModelPricingSpec(
        bool HasKnownPricing,
        bool IsFree,
        double InputPerMillionUsd,
        double OutputPerMillionUsd,
        string SourceText)
    {
        private static readonly Regex TwoTierPriceRegex = new(
            @"\$?([0-9]+(?:\.[0-9]+)?)\s*/\s*\$?([0-9]+(?:\.[0-9]+)?)\s*(?:per\s*)?(mtok|m|k|1m|1k)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SingleTierPriceRegex = new(
            @"\$?([0-9]+(?:\.[0-9]+)?)\s*/\s*(?:([0-9]+)\s*)?(mtok|m|k)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static ModelPricingSpec Unknown(string? source = null)
            => new(false, false, 0.0, 0.0, source ?? string.Empty);

        public static ModelPricingSpec Free(string? source = null)
            => new(true, true, 0.0, 0.0, source ?? "Free");

        /// <summary>
        /// Parses catalog pricing without inventing arbitrary defaults when unknown.
        /// Supports separate input/output tiers ("$2.50 / $10.00 MTok", "$0.15/$0.60 1M") and
        /// single-unit strings ("$0.005 / 1k"). Returns HasKnownPricing=false for "See provider"
        /// or unparseable text.
        /// </summary>
        public static ModelPricingSpec Parse(string? pricingStr)
        {
            if (string.IsNullOrWhiteSpace(pricingStr)) return Unknown();

            string s = pricingStr.Trim();
            string lower = s.ToLowerInvariant();
            if (lower == "—" || lower.Contains("see provider", StringComparison.Ordinal) || lower.Contains("custom", StringComparison.Ordinal))
                return Unknown(s);
            if (lower.Contains("free", StringComparison.Ordinal) || lower.Contains("open weights", StringComparison.Ordinal))
                return Free(s);

            var two = TwoTierPriceRegex.Match(s);
            if (two.Success &&
                double.TryParse(two.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double inRaw) &&
                double.TryParse(two.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double outRaw))
            {
                string unit = two.Groups[3].Value.ToLowerInvariant();
                // Multi-tier catalog pricing like "$2.50 / $10.00 MTok" defaults to per 1M unless "k" is specified
                double scaleToMillion = unit is "k" or "1k" ? 1000.0 : 1.0;
                return new ModelPricingSpec(true, inRaw == 0 && outRaw == 0, inRaw * scaleToMillion, outRaw * scaleToMillion, s);
            }

            var one = SingleTierPriceRegex.Match(s);
            if (one.Success && double.TryParse(one.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rawPrice))
            {
                string unit = one.Groups[3].Value.ToLowerInvariant();
                double divisorTokens = unit is "m" or "mtok" ? 1_000_000.0 : 1_000.0;
                if (int.TryParse(one.Groups[2].Value, out int countMult) && countMult > 1)
                    divisorTokens *= countMult;

                double perMillion = (rawPrice / divisorTokens) * 1_000_000.0;
                // Legacy single-rate strings apply only to input directly; never fabricate hidden multipliers
                return new ModelPricingSpec(true, perMillion == 0.0, perMillion, perMillion, s);
            }

            return Unknown(s);
        }

        public TokenCostBreakdown Calculate(int inputTokens, int outputTokens, int cachedInputTokens = 0)
        {
            if (!HasKnownPricing)
            {
                return new TokenCostBreakdown(false, false, 0.0, 0.0, 0.0, 0, "Pricing unknown");
            }
            if (IsFree)
            {
                return new TokenCostBreakdown(true, true, 0.0, 0.0, 0.0, 0, SourceText);
            }

            int billableUncached = Math.Max(0, inputTokens - Math.Max(0, cachedInputTokens));
            // Standard cached-token discount across OpenAI/Anthropic/Gemini is ~10%..25%, kept conservative at input rate unless cached breakdown requested; only separate Input vs Output here without inventing unannounced cache tiers
            double inCost = (Math.Max(0, inputTokens) / 1_000_000.0) * InputPerMillionUsd;
            double outCost = (Math.Max(0, outputTokens) / 1_000_000.0) * OutputPerMillionUsd;
            return new TokenCostBreakdown(
                true,
                false,
                inCost,
                outCost,
                inCost + outCost,
                billableUncached == inputTokens ? 0 : cachedInputTokens,
                SourceText);
        }
    }

    public sealed record TokenCostBreakdown(
        bool HasKnownPricing,
        bool IsFree,
        double InputCostUsd,
        double OutputCostUsd,
        double TotalCostUsd,
        int UnbilledOrCachedTokens,
        string PricingLabel);
}
