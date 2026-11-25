using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace LlmodelBox.Providers
{
    public enum ProviderErrorKind
    {
        None,
        Authentication,
        RateLimited,
        QuotaExceeded,
        ContextExceeded,
        ModelNotFound,
        Timeout,
        Network,
        ContentFiltered,
        InvalidRequest,
        ServerError,
        Cancelled,
        Unknown
    }

    public sealed class NormalizedProviderError
    {
        public ProviderErrorKind Kind { get; init; } = ProviderErrorKind.Unknown;
        public bool IsTransient { get; init; }
        public int? StatusCode { get; init; }
        public string UserMessage { get; init; } = string.Empty;
        public string TechnicalDetail { get; init; } = string.Empty;
    }

    /// <summary>
    /// Classifies provider HTTP status codes, transport exceptions, and API error strings
    /// into a uniform provider-independent error model with transient retry guidance.
    /// </summary>
    public static class ProviderErrorNormalizer
    {
        public static NormalizedProviderError Normalize(string? rawError, Exception? ex = null)
        {
            string msg = rawError ?? ex?.Message ?? string.Empty;
            string lower = msg.ToLowerInvariant();

            if (ex is OperationCanceledException)
            {
                return new NormalizedProviderError
                {
                    Kind = ProviderErrorKind.Cancelled,
                    IsTransient = false,
                    UserMessage = "Request was cancelled.",
                    TechnicalDetail = msg
                };
            }

            if (ex is TaskCanceledException || lower.Contains("timed out") || lower.Contains("timeout"))
            {
                return new NormalizedProviderError
                {
                    Kind = ProviderErrorKind.Timeout,
                    IsTransient = true,
                    StatusCode = 408,
                    UserMessage = "Provider request timed out. You can retry safely.",
                    TechnicalDetail = msg
                };
            }

            if (ex is HttpRequestException || lower.Contains("name resolution") || lower.Contains("connection refused") || lower.Contains("unreachable"))
            {
                return new NormalizedProviderError
                {
                    Kind = ProviderErrorKind.Network,
                    IsTransient = true,
                    UserMessage = "Network error communicating with provider.",
                    TechnicalDetail = msg
                };
            }

            if (lower.Contains("401") || lower.Contains("403") || lower.Contains("unauthorized") || lower.Contains("invalid api key") || lower.Contains("authentication"))
            {
                return new NormalizedProviderError
                {
                    Kind = ProviderErrorKind.Authentication,
                    IsTransient = false,
                    StatusCode = lower.Contains("403") ? 403 : 401,
                    UserMessage = "Authentication failed. Verify your API key in Settings.",
                    TechnicalDetail = msg
                };
            }

            if (lower.Contains("429") || lower.Contains("rate limit") || lower.Contains("too many requests"))
            {
                return new NormalizedProviderError
                {
                    Kind = ProviderErrorKind.RateLimited,
                    IsTransient = true,
                    StatusCode = 429,
                    UserMessage = "Provider rate limit reached. Retrying or waiting briefly is recommended.",
                    TechnicalDetail = msg
                };
            }

            if (lower.Contains("quota") || lower.Contains("insufficient_quota") || lower.Contains("billing") || lower.Contains("credits"))
            {
                return new NormalizedProviderError
                {
                    Kind = ProviderErrorKind.QuotaExceeded,
                    IsTransient = false,
                    StatusCode = 402,
                    UserMessage = "Provider quota or billing limit exceeded.",
                    TechnicalDetail = msg
                };
            }

            if (lower.Contains("context_length") || lower.Contains("maximum context") || lower.Contains("too many tokens") || lower.Contains("prompt is too long"))
            {
                return new NormalizedProviderError
                {
                    Kind = ProviderErrorKind.ContextExceeded,
                    IsTransient = false,
                    StatusCode = 400,
                    UserMessage = "Input exceeds the model's context window limit.",
                    TechnicalDetail = msg
                };
            }

            if (lower.Contains("404") || lower.Contains("model_not_found") || lower.Contains("does not exist"))
            {
                return new NormalizedProviderError
                {
                    Kind = ProviderErrorKind.ModelNotFound,
                    IsTransient = false,
                    StatusCode = 404,
                    UserMessage = "Selected model was not found on this provider endpoint.",
                    TechnicalDetail = msg
                };
            }

            if (lower.Contains("safety") || lower.Contains("content_filter") || lower.Contains("moderation"))
            {
                return new NormalizedProviderError
                {
                    Kind = ProviderErrorKind.ContentFiltered,
                    IsTransient = false,
                    StatusCode = 400,
                    UserMessage = "Response blocked by provider safety filter.",
                    TechnicalDetail = msg
                };
            }

            if (lower.Contains("500") || lower.Contains("502") || lower.Contains("503") || lower.Contains("504") || lower.Contains("overloaded") || lower.Contains("bad gateway"))
            {
                return new NormalizedProviderError
                {
                    Kind = ProviderErrorKind.ServerError,
                    IsTransient = true,
                    StatusCode = 503,
                    UserMessage = "Provider service is temporarily unavailable.",
                    TechnicalDetail = msg
                };
            }

            if (lower.Contains("400") || lower.Contains("invalid"))
            {
                return new NormalizedProviderError
                {
                    Kind = ProviderErrorKind.InvalidRequest,
                    IsTransient = false,
                    StatusCode = 400,
                    UserMessage = "Provider rejected the request parameters.",
                    TechnicalDetail = msg
                };
            }

            return new NormalizedProviderError
            {
                Kind = string.IsNullOrWhiteSpace(msg) ? ProviderErrorKind.None : ProviderErrorKind.Unknown,
                IsTransient = false,
                UserMessage = string.IsNullOrWhiteSpace(msg) ? string.Empty : msg,
                TechnicalDetail = msg
            };
        }
    }
}
