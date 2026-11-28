using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlmodelBox.Runtime.Knowledge
{
    /// <summary>
    /// Optional OpenAI-compatible embeddings provider (supports OpenAI / OpenRouter / custom base URLs).
    /// When apiKey or model is absent, IsConfigured returns false and KnowledgeEngine automatically
    /// falls back to deterministic BM25 retrieval.
    /// </summary>
    public sealed class OpenAiCompatibleEmbeddingProvider : IKnowledgeEmbeddingProvider
    {
        private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

        private readonly Func<string?> _apiKeyResolver;
        private readonly string _endpointUrl;
        private readonly string _model;

        public string ProviderId => $"embedding:{_model}";

        public OpenAiCompatibleEmbeddingProvider(
            Func<string?> apiKeyResolver,
            string model = "text-embedding-3-small",
            string endpointUrl = "https://api.openai.com/v1/embeddings")
        {
            _apiKeyResolver = apiKeyResolver ?? (() => null);
            _model = string.IsNullOrWhiteSpace(model) ? "text-embedding-3-small" : model.Trim();
            _endpointUrl = string.IsNullOrWhiteSpace(endpointUrl)
                ? "https://api.openai.com/v1/embeddings"
                : endpointUrl.Trim();
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKeyResolver());

        public async Task<IReadOnlyList<float[]?>> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
        {
            var result = Enumerable.Repeat<float[]?>(null, inputs.Count).ToArray();
            string? apiKey = _apiKeyResolver();
            if (string.IsNullOrWhiteSpace(apiKey) || inputs.Count == 0)
                return result;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _endpointUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                var payload = JsonSerializer.Serialize(new
                {
                    model = _model,
                    input = inputs.Select(s => s.Length > 6000 ? s[..6000] : s).ToArray()
                });
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var resp = await SharedHttp.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return result;

                string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var item in data.EnumerateArray())
                {
                    int idx = item.TryGetProperty("index", out var iEl) ? iEl.GetInt32() : -1;
                    if (idx < 0 || idx >= result.Length) continue;
                    if (item.TryGetProperty("embedding", out var embEl) && embEl.ValueKind == JsonValueKind.Array)
                    {
                        var vec = new float[embEl.GetArrayLength()];
                        int pos = 0;
                        foreach (var num in embEl.EnumerateArray())
                            vec[pos++] = (float)num.GetDouble();
                        result[idx] = vec;
                    }
                }
                return result;
            }
            catch
            {
                return Enumerable.Repeat<float[]?>(null, inputs.Count).ToArray();
            }
        }
    }
}
