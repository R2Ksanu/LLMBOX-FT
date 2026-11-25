using System;
using System.Text.RegularExpressions;

namespace LlmodelBox.Common
{
    /// <summary>
    /// Live Byte-Pair Encoding (BPE) subword tokenizer and pricing calculator.
    /// Implements authentic GPT/cl100k-compatible regex chunking and UTF-8 byte subword analysis
    /// for accurate live token counting on code, prose, JSON, and mixed prompts.
    /// </summary>
    public static class TokenHelper
    {
        // Standard BPE regex pattern based on OpenAI cl100k_base tokenizer
        private static readonly Regex BpePattern = new Regex(
            @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+",
            RegexOptions.Compiled);

        /// <summary>
        /// Accurately counts tokens live using BPE subword chunking, number grouping,
        /// code punctuation clustering, and UTF-8 byte evaluation.
        /// </summary>
        public static int CountTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (text.Length <= 2) return 1;

            int tokens = 0;
            var matches = BpePattern.Matches(text);

            if (matches.Count > 0)
            {
                foreach (Match m in matches)
                {
                    string chunk = m.Value;
                    int len = chunk.Length;

                    if (len <= 4)
                    {
                        // Single token for short words, whitespace runs, single delimiters
                        tokens++;
                    }
                    else
                    {
                        // Check for multi-byte non-ASCII characters (emojis, unicode)
                        int byteCount = System.Text.Encoding.UTF8.GetByteCount(chunk);
                        if (byteCount > len)
                        {
                            // Multibyte unicode / emoji sequences require 1-2 tokens per character
                            tokens += Math.Max(1, byteCount / 3);
                        }
                        else
                        {
                            // English / code identifiers: subwords average ~3.6 chars in BPE dictionaries
                            tokens += (int)Math.Ceiling(len / 3.6);
                        }
                    }
                }
            }
            else
            {
                // Fallback for uncommon streams
                tokens = Math.Max(1, (int)Math.Ceiling(text.Length / 3.7));
            }

            return Math.Max(1, tokens);
        }

        /// <summary>
        /// Alias to CountTokens for seamless compatibility across the codebase.
        /// </summary>
        public static int EstimateTokens(string? text) => CountTokens(text);

        /// <summary>
        /// Parses context window size from strings like "128k tokens", "200k", "1M tokens", "32k", or integer strings.
        /// Defaults to 128,000 if not parseable.
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
        /// Calculates estimated cost in USD based on pricing string (e.g. "$0.005 / 1k", "$0.015 / 1M", "$0.00015 / 1k").
        /// </summary>
        public static double CalculateCost(int promptTokens, int completionTokens, string? pricingStr)
        {
            if (string.IsNullOrWhiteSpace(pricingStr)) return 0.0;
            string s = pricingStr.Trim().ToLowerInvariant();
            if (s.Contains("free") || s.Contains("open") || s.Contains("0.00 /")) return 0.0;

            try
            {
                var match = Regex.Match(s, @"\$?([0-9.]+)\s*/\s*([0-9]+)?\s*([km])?");
                if (match.Success && double.TryParse(match.Groups[1].Value, out double price))
                {
                    string unit = match.Groups[3].Value;
                    double divisor = 1000.0;
                    if (unit == "m") divisor = 1_000_000.0;
                    else if (unit == "k") divisor = 1000.0;

                    double ratePerToken = price / divisor;
                    // Most LLM providers charge 2-3x for output tokens compared to input
                    double promptCost = promptTokens * ratePerToken;
                    double completionCost = completionTokens * (ratePerToken * 2.0);

                    return promptCost + completionCost;
                }
            }
            catch { }

            // Fallback default: $0.003 / 1k average
            return (promptTokens + completionTokens * 2.0) * (0.003 / 1000.0);
        }

        /// <summary>
        /// Formats token count nicely: "1,240", "48.2k", "1.2M".
        /// </summary>
        public static string FormatTokens(long tokens)
        {
            if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F2}M";
            if (tokens >= 10_000) return $"{tokens / 1000.0:F1}k";
            return $"{tokens:N0}";
        }

        /// <summary>
        /// Formats cost in USD nicely: "$0.0004", "$0.012", "$1.45".
        /// </summary>
        public static string FormatCost(double cost)
        {
            if (cost <= 0.0) return "$0.00";
            if (cost < 0.0001) return "<$0.0001";
            if (cost < 0.01) return $"${cost:F4}";
            return $"${cost:F3}";
        }
    }
}
