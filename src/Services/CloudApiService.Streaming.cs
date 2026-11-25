using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    public partial class CloudApiService
    {
        /// <summary>
        /// Streams real SSE token deltas from OpenAI-compatible, Anthropic, and Gemini endpoints.
        /// Falls back to buffered CallCloudApiAsync if the provider/endpoint does not support SSE.
        /// </summary>
        public async Task<string> StreamCloudApiAsync(
            ModelItem model,
            string prompt,
            UserSettings settings,
            string? systemPrompt,
            Action<string> onTextDelta,
            Action<string> onReasoningDelta,
            CancellationToken ct)
        {
            string? sys = !string.IsNullOrWhiteSpace(systemPrompt)
                ? systemPrompt
                : (settings.SystemInstructionEnabled && !string.IsNullOrWhiteSpace(settings.SystemInstruction) ? settings.SystemInstruction : null);
            if (string.IsNullOrWhiteSpace(sys)) sys = null;

            string apiKey = model.IsCustom && !string.IsNullOrWhiteSpace(model.CustomApiKey)
                ? model.CustomApiKey
                : GetApiKeyForProvider(model.Provider, settings);

            if (string.IsNullOrWhiteSpace(apiKey) && !model.IsCustom)
            {
                return await CallCloudApiAsync(model, prompt, settings, systemPrompt).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(model.Endpoint) && model.Endpoint.Contains("{account_id}", StringComparison.OrdinalIgnoreCase))
            {
                return await CallCloudApiAsync(model, prompt, settings, systemPrompt).ConfigureAwait(false);
            }

            string provider = model.Provider ?? string.Empty;
            string endpoint = model.Endpoint ?? string.Empty;
            var timer = Stopwatch.StartNew();

            try
            {
                // 1. Anthropic Messages SSE
                if (!model.IsCustom && provider.Contains("Anthropic", StringComparison.OrdinalIgnoreCase))
                {
                    string url = !string.IsNullOrEmpty(endpoint) ? endpoint : "https://api.anthropic.com/v1/messages";
                    var body = new Dictionary<string, object>
                    {
                        ["model"] = model.Id,
                        ["max_tokens"] = 4096,
                        ["stream"] = true,
                        ["messages"] = new[] { new { role = "user", content = prompt } }
                    };
                    if (!string.IsNullOrWhiteSpace(sys)) body["system"] = sys;

                    using var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.Add("x-api-key", apiKey);
                    req.Headers.Add("anthropic-version", "2023-06-01");
                    req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                    return await ReadSseAsync(req, SseWire.Anthropic, model, timer, onTextDelta, onReasoningDelta, ct).ConfigureAwait(false);
                }

                // 2. Google Gemini SSE
                if ((!model.IsCustom && (provider.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || provider.Contains("Google", StringComparison.OrdinalIgnoreCase)))
                    || endpoint.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
                {
                    string baseEp = !string.IsNullOrEmpty(endpoint) ? endpoint : "https://generativelanguage.googleapis.com/v1beta/models";
                    string url = $"{baseEp.TrimEnd('/')}/{model.Id}:streamGenerateContent?alt=sse&key={apiKey}";
                    object gBody = string.IsNullOrWhiteSpace(sys)
                        ? new { contents = new[] { new { parts = new[] { new { text = prompt } } } } }
                        : new { systemInstruction = new { parts = new[] { new { text = sys } } }, contents = new[] { new { parts = new[] { new { text = prompt } } } } };

                    using var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Content = new StringContent(JsonSerializer.Serialize(gBody), Encoding.UTF8, "application/json");
                    return await ReadSseAsync(req, SseWire.Gemini, model, timer, onTextDelta, onReasoningDelta, ct).ConfigureAwait(false);
                }

                // 3. OpenAI-compatible SSE (OpenAI, Grok, DeepSeek, Groq, OpenRouter, Mistral, NVIDIA, SambaNova, Zhipu, Custom)
                bool isOpenAiCompat = model.IsCustom
                    || provider.Contains("OpenAI", StringComparison.OrdinalIgnoreCase)
                    || provider.Contains("Grok", StringComparison.OrdinalIgnoreCase)
                    || provider.Contains("xAI", StringComparison.OrdinalIgnoreCase)
                    || provider.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase)
                    || provider.Contains("Groq", StringComparison.OrdinalIgnoreCase)
                    || provider.Contains("OpenRouter", StringComparison.OrdinalIgnoreCase)
                    || provider.Contains("Mistral", StringComparison.OrdinalIgnoreCase)
                    || provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                    || provider.Contains("SambaNova", StringComparison.OrdinalIgnoreCase)
                    || provider.Contains("Zhipu", StringComparison.OrdinalIgnoreCase)
                    || provider.Contains("Z.AI", StringComparison.OrdinalIgnoreCase);

                if (isOpenAiCompat && !endpoint.Contains("api.cohere.com", StringComparison.OrdinalIgnoreCase) && !endpoint.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase))
                {
                    string url = ResolveOpenAiEndpoint(provider, endpoint);
                    var body = new Dictionary<string, object>
                    {
                        ["model"] = model.Id,
                        ["messages"] = BuildMessages(sys, prompt),
                        ["stream"] = true
                    };
                    using var req = new HttpRequestMessage(HttpMethod.Post, url);
                    if (!string.IsNullOrWhiteSpace(apiKey))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    if (url.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
                    {
                        req.Headers.Add("HTTP-Referer", "https://llmodelbox.local");
                        req.Headers.Add("X-Title", "LlmodelBox");
                    }
                    req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                    return await ReadSseAsync(req, SseWire.OpenAi, model, timer, onTextDelta, onReasoningDelta, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fallback gracefully to non-streaming CallCloudApiAsync if SSE fails before yielding
            }

            string fallback = await CallCloudApiAsync(model, prompt, settings, systemPrompt).ConfigureAwait(false);
            if (!fallback.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase))
            {
                string clean = Common.ReasoningFilter.Split(fallback, out string reasoning);
                if (!string.IsNullOrWhiteSpace(reasoning)) onReasoningDelta(reasoning);
                const int chunk = 28;
                for (int i = 0; i < clean.Length; i += chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    onTextDelta(clean.Substring(i, Math.Min(chunk, clean.Length - i)));
                }
                return clean;
            }
            return fallback;
        }

        private enum SseWire { OpenAi, Anthropic, Gemini }

        private static string ResolveOpenAiEndpoint(string provider, string endpoint)
        {
            if (!string.IsNullOrWhiteSpace(endpoint)) return endpoint;
            if (provider.Contains("Grok", StringComparison.OrdinalIgnoreCase) || provider.Contains("xAI", StringComparison.OrdinalIgnoreCase))
                return "https://api.x.ai/v1/chat/completions";
            if (provider.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase))
                return "https://api.deepseek.com/chat/completions";
            if (provider.Contains("Groq", StringComparison.OrdinalIgnoreCase))
                return "https://api.groq.com/openai/v1/chat/completions";
            if (provider.Contains("OpenRouter", StringComparison.OrdinalIgnoreCase))
                return "https://openrouter.ai/api/v1/chat/completions";
            if (provider.Contains("Mistral", StringComparison.OrdinalIgnoreCase))
                return "https://api.mistral.ai/v1/chat/completions";
            if (provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                return "https://integrate.api.nvidia.com/v1/chat/completions";
            if (provider.Contains("SambaNova", StringComparison.OrdinalIgnoreCase))
                return "https://api.sambanova.ai/v1/chat/completions";
            if (provider.Contains("Zhipu", StringComparison.OrdinalIgnoreCase) || provider.Contains("Z.AI", StringComparison.OrdinalIgnoreCase))
                return "https://open.bigmodel.cn/api/paas/v4/chat/completions";
            return "https://api.openai.com/v1/chat/completions";
        }

        private async Task<string> ReadSseAsync(
            HttpRequestMessage req,
            SseWire wire,
            ModelItem model,
            Stopwatch timer,
            Action<string> onTextDelta,
            Action<string> onReasoningDelta,
            CancellationToken ct)
        {
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return $"[ERROR: HTTP {(int)resp.StatusCode}]\r\n{errBody}";
            }

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // Some custom endpoints ignore stream=true and return a plain JSON completion.
                string rawJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ExtractNonStreamFallback(rawJson, wire, model, timer, onTextDelta, onReasoningDelta);
            }

            var fullText = new StringBuilder();
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                string data = line.Substring(5).Trim();
                if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    if (wire == SseWire.OpenAi)
                    {
                        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        {
                            var ch = choices[0];
                            if (ch.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                            {
                                if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                                {
                                    string r = rc.GetString() ?? "";
                                    if (r.Length > 0) onReasoningDelta(r);
                                }
                                else if (delta.TryGetProperty("reasoning", out var r2) && r2.ValueKind == JsonValueKind.String)
                                {
                                    string r = r2.GetString() ?? "";
                                    if (r.Length > 0) onReasoningDelta(r);
                                }
                                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                                {
                                    string t = c.GetString() ?? "";
                                    if (t.Length > 0)
                                    {
                                        fullText.Append(t);
                                        onTextDelta(t);
                                    }
                                }
                            }
                        }
                        if (root.TryGetProperty("usage", out _)) EmitUsage(root, model, timer.ElapsedMilliseconds);
                    }
                    else if (wire == SseWire.Anthropic)
                    {
                        if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                        {
                            if (delta.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String)
                            {
                                string r = th.GetString() ?? "";
                                if (r.Length > 0) onReasoningDelta(r);
                            }
                            if (delta.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                            {
                                string t = tx.GetString() ?? "";
                                if (t.Length > 0)
                                {
                                    fullText.Append(t);
                                    onTextDelta(t);
                                }
                            }
                        }
                        if (root.TryGetProperty("usage", out _)) EmitUsage(root, model, timer.ElapsedMilliseconds);
                    }
                    else if (wire == SseWire.Gemini)
                    {
                        if (root.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0 &&
                            cands[0].TryGetProperty("content", out var cont) &&
                            cont.TryGetProperty("parts", out var parts))
                        {
                            foreach (var p in parts.EnumerateArray())
                            {
                                if (p.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                                {
                                    string t = tx.GetString() ?? "";
                                    if (t.Length > 0)
                                    {
                                        fullText.Append(t);
                                        onTextDelta(t);
                                    }
                                }
                            }
                        }
                        if (root.TryGetProperty("usageMetadata", out _)) EmitUsage(root, model, timer.ElapsedMilliseconds);
                    }
                }
                catch
                {
                    // Skip partial/non-JSON keepalive SSE frames
                }
            }

            return fullText.ToString();
        }

        private string ExtractNonStreamFallback(
            string rawJson,
            SseWire wire,
            ModelItem model,
            Stopwatch timer,
            Action<string> onTextDelta,
            Action<string> onReasoningDelta)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;
                EmitUsage(root, model, timer.ElapsedMilliseconds);
                string text = string.Empty;
                if (wire == SseWire.OpenAi && root.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0 &&
                    ch[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var cnt))
                {
                    text = cnt.GetString() ?? string.Empty;
                }
                else if (wire == SseWire.Anthropic && root.TryGetProperty("content", out var ac) && ac.GetArrayLength() > 0 &&
                         ac[0].TryGetProperty("text", out var at))
                {
                    text = at.GetString() ?? string.Empty;
                }
                else if (wire == SseWire.Gemini && root.TryGetProperty("candidates", out var gc) && gc.GetArrayLength() > 0 &&
                         gc[0].TryGetProperty("content", out var gco) && gco.TryGetProperty("parts", out var gp) && gp.GetArrayLength() > 0 &&
                         gp[0].TryGetProperty("text", out var gt))
                {
                    text = gt.GetString() ?? string.Empty;
                }
                string clean = Common.ReasoningFilter.Split(text, out string reasoning);
                if (!string.IsNullOrWhiteSpace(reasoning)) onReasoningDelta(reasoning);
                if (!string.IsNullOrEmpty(clean)) onTextDelta(clean);
                return clean;
            }
            catch
            {
                return rawJson;
            }
        }
    }
}
