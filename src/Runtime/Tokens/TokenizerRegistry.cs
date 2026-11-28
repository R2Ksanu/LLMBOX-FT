using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using LlmodelBox.Models;
using Microsoft.ML.Tokenizers;

namespace LlmodelBox.Runtime.Tokens
{
    /// <summary>
    /// Model-specific exact BPE tokenizer backed by Microsoft.ML.Tokenizers (o200k_base / cl100k_base)
    /// for OpenAI GPT-4o / GPT-4 / o-series / GPT-3.5 encodings. Thread-safe.
    /// </summary>
    public sealed class TiktokenModelTokenCounter : IModelTokenCounter
    {
        private readonly Tokenizer _tokenizer;

        public TiktokenModelTokenCounter(string encodingName, Tokenizer tokenizer)
        {
            TokenizerId = encodingName;
            _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        }

        public string TokenizerId { get; }
        public TokenCountConfidence Confidence => TokenCountConfidence.ExactBpe;
        public string SourceLabel => $"Exact BPE ({TokenizerId})";

        public TokenCountResult Count(string? text)
        {
            int count = CountTokens(text);
            return new TokenCountResult(count, Confidence, SourceLabel, TokenizerId);
        }

        public int CountTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return _tokenizer.CountTokens(text);
        }

        public string TruncateToFit(string text, int maxTokens, bool fromEnd = false)
        {
            if (string.IsNullOrEmpty(text) || maxTokens <= 0) return string.Empty;
            if (CountTokens(text) <= maxTokens) return text;

            if (!fromEnd)
            {
                int cutIdx = _tokenizer.GetIndexByTokenCount(text, maxTokens, out string? normalized, out _);
                string source = normalized ?? text;
                return cutIdx <= 0 ? string.Empty : source[..Math.Min(cutIdx, source.Length)];
            }
            else
            {
                int startIdx = _tokenizer.GetIndexByTokenCountFromEnd(text, maxTokens, out string? normalized, out _);
                string source = normalized ?? text;
                return startIdx >= source.Length ? string.Empty : source[Math.Max(0, startIdx)..];
            }
        }
    }

    /// <summary>
    /// Honest regex/subword length estimator used ONLY for model families whose proprietary
    /// tokenizer vocabulary is not shipped locally (e.g. Claude, Gemini, Llama, Mistral, DeepSeek).
    /// Explicitly reports TokenCountConfidence.EstimatedFallback so heuristic counts are never
    /// presented as exact BPE tokens.
    /// </summary>
    public sealed class HeuristicEstimatedTokenCounter : IModelTokenCounter
    {
        private static readonly Regex ChunkPattern = new(
            @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+",
            RegexOptions.Compiled);

        public string TokenizerId => "heuristic-subword-v1";
        public TokenCountConfidence Confidence => TokenCountConfidence.EstimatedFallback;
        public string SourceLabel => "Estimated (Heuristic fallback)";

        public TokenCountResult Count(string? text)
            => new(CountTokens(text), Confidence, SourceLabel, TokenizerId);

        public int CountTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (text.Length <= 2) return 1;

            int tokens = 0;
            var matches = ChunkPattern.Matches(text);
            if (matches.Count > 0)
            {
                foreach (Match m in matches)
                {
                    string chunk = m.Value;
                    int len = chunk.Length;
                    if (len <= 4)
                    {
                        tokens++;
                    }
                    else
                    {
                        int byteCount = Encoding.UTF8.GetByteCount(chunk);
                        if (byteCount > len)
                        {
                            tokens += Math.Max(1, byteCount / 3);
                        }
                        else
                        {
                            tokens += (int)Math.Ceiling(len / 3.6);
                        }
                    }
                }
            }
            else
            {
                tokens = Math.Max(1, (int)Math.Ceiling(text.Length / 3.7));
            }
            return Math.Max(1, tokens);
        }

        public string TruncateToFit(string text, int maxTokens, bool fromEnd = false)
        {
            if (string.IsNullOrEmpty(text) || maxTokens <= 0) return string.Empty;
            if (CountTokens(text) <= maxTokens) return text;

            int approxChars = Math.Max(16, maxTokens * 3);
            if (approxChars >= text.Length) return text;
            return fromEnd ? text[^approxChars..] : text[..approxChars];
        }
    }

    public sealed class TokenizerRegistry : ITokenizerRegistry
    {
        public static TokenizerRegistry Default { get; } = new();

        private readonly ConcurrentDictionary<string, IModelTokenCounter> _counters = new(StringComparer.OrdinalIgnoreCase);

        public IModelTokenCounter FallbackCounter { get; } = new HeuristicEstimatedTokenCounter();

        public IModelTokenCounter Resolve(ModelItem? model)
            => Resolve(model?.Id, model?.Provider);

        public IModelTokenCounter Resolve(string? modelId, string? provider = null)
        {
            string id = (modelId ?? string.Empty).Trim().ToLowerInvariant();
            string prov = (provider ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(id)) return FallbackCounter;

            // OpenAI BPE models (direct OpenAI or routed via OpenRouter/Azure/GitHub Models)
            if (IsO200kModel(id))
            {
                return GetOrCreateTiktoken("o200k_base", "gpt-4o");
            }
            if (IsCl100kModel(id, prov))
            {
                return GetOrCreateTiktoken("cl100k_base", "gpt-4");
            }

            // Non-Tiktoken model families (Claude, Gemini, Llama, DeepSeek, Mistral, Qwen, Grok, Cohere)
            // honestly receive the EstimatedFallback counter unless an authoritative tokenizer is added.
            return FallbackCounter;
        }

        private IModelTokenCounter GetOrCreateTiktoken(string encodingName, string sampleModelId)
        {
            return _counters.GetOrAdd(encodingName, _ =>
            {
                try
                {
                    Tokenizer tok = TiktokenTokenizer.CreateForEncoding(encodingName);
                    return new TiktokenModelTokenCounter(encodingName, tok);
                }
                catch
                {
                    try
                    {
                        Tokenizer tok = TiktokenTokenizer.CreateForModel(sampleModelId);
                        return new TiktokenModelTokenCounter(encodingName, tok);
                    }
                    catch
                    {
                        return FallbackCounter;
                    }
                }
            });
        }

        private static bool IsO200kModel(string id)
            => id.Contains("gpt-4o", StringComparison.Ordinal) ||
               id.Contains("4o-mini", StringComparison.Ordinal) ||
               id.Contains("gpt-4.1", StringComparison.Ordinal) ||
               id.Contains("gpt-4.5", StringComparison.Ordinal) ||
               id.Contains("gpt-5", StringComparison.Ordinal) ||
               id.Contains("/o1", StringComparison.Ordinal) || id.StartsWith("o1", StringComparison.Ordinal) ||
               id.Contains("/o3", StringComparison.Ordinal) || id.StartsWith("o3", StringComparison.Ordinal) ||
               id.Contains("/o4", StringComparison.Ordinal) || id.StartsWith("o4", StringComparison.Ordinal) ||
               id.Contains("text-embedding-3", StringComparison.Ordinal);

        private static bool IsCl100kModel(string id, string prov)
            => id.Contains("gpt-4", StringComparison.Ordinal) ||
               id.Contains("gpt-3.5", StringComparison.Ordinal) ||
               id.Contains("text-embedding-ada", StringComparison.Ordinal) ||
               (prov.Contains("openai", StringComparison.Ordinal) && id.Contains("davinci", StringComparison.Ordinal));
    }
}
