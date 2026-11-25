using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LlmodelBox.Common;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    public partial class CloudApiService : ICloudApiService
    {
        private readonly HttpClient _httpClient;

        // Raised after a successful chat call whose response reported token usage.
        // Raised after a successful chat call whose response reported token usage.
        public event Action<Models.TokenUsageRecord>? UsageRecorded;

        // Stores the most recent live API usage record parsed from provider response
        public Models.TokenUsageRecord? LastLiveUsage { get; private set; }

        public CloudApiService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        // Parses the token-usage block from a response (OpenAI, Anthropic, Gemini, Cohere, Cloudflare)
        // and raises UsageRecorded. Best-effort: silent if the provider omits usage.
        private void EmitUsage(JsonElement root, ModelItem model, long elapsedMs = 0)
        {
            try
            {
                int prompt = 0, completion = 0, total = 0;

                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    // OpenAI-compatible, Groq, OpenRouter, DeepSeek, Mistral, NVIDIA NIM, SambaNova, Zhipu
                    if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number) prompt = pt.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number) completion = ct.GetInt32();
                    if (usage.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number) total = tt.GetInt32();
                    // Anthropic
                    if (usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number) prompt = it.GetInt32();
                    if (usage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number) completion = ot.GetInt32();
                }
                else if (root.TryGetProperty("usageMetadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    // Google Gemini
                    if (meta.TryGetProperty("promptTokenCount", out var pt) && pt.ValueKind == JsonValueKind.Number) prompt = pt.GetInt32();
                    if (meta.TryGetProperty("candidatesTokenCount", out var ct) && ct.ValueKind == JsonValueKind.Number) completion = ct.GetInt32();
                    if (meta.TryGetProperty("totalTokenCount", out var tt) && tt.ValueKind == JsonValueKind.Number) total = tt.GetInt32();
                }
                else if (root.TryGetProperty("result", out var cfResult) && cfResult.ValueKind == JsonValueKind.Object &&
                         cfResult.TryGetProperty("usage", out var cfUsage) && cfUsage.ValueKind == JsonValueKind.Object)
                {
                    // Cloudflare Workers AI
                    if (cfUsage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number) prompt = pt.GetInt32();
                    if (cfUsage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number) completion = ct.GetInt32();
                    if (cfUsage.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number) total = tt.GetInt32();
                }
                else if (root.TryGetProperty("meta", out var coMeta) && coMeta.ValueKind == JsonValueKind.Object)
                {
                    // Cohere v2 (meta.tokens or meta.billed_units)
                    if (coMeta.TryGetProperty("tokens", out var coTok) && coTok.ValueKind == JsonValueKind.Object)
                    {
                        if (coTok.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number) prompt = it.GetInt32();
                        if (coTok.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number) completion = ot.GetInt32();
                    }
                    else if (coMeta.TryGetProperty("billed_units", out var coBilled) && coBilled.ValueKind == JsonValueKind.Object)
                    {
                        if (coBilled.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number) prompt = it.GetInt32();
                        if (coBilled.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number) completion = ot.GetInt32();
                    }
                }

                if (total <= 0) total = prompt + completion;
                if (total <= 0) return;

                var rec = new Models.TokenUsageRecord
                {
                    TimestampUtc = DateTime.UtcNow,
                    Provider = model.Provider ?? string.Empty,
                    ModelId = model.Id ?? string.Empty,
                    ModelName = string.IsNullOrWhiteSpace(model.Name) ? (model.Id ?? string.Empty) : model.Name,
                    PromptTokens = prompt,
                    CompletionTokens = completion,
                    TotalTokens = total,
                    LatencyMs = elapsedMs,
                    IsLiveApi = true
                };

                LastLiveUsage = rec;
                UsageRecorded?.Invoke(rec);
            }
            catch { /* usage accounting must never break a chat response */ }
        }

        // Builds an OpenAI/Anthropic/Cohere-style messages array, prepending a system-role
        // message when a system instruction is present.
        private static object[] BuildMessages(string? sys, string prompt)
        {
            if (string.IsNullOrWhiteSpace(sys))
                return new object[] { new { role = "user", content = prompt } };
            return new object[]
            {
                new { role = "system", content = sys },
                new { role = "user", content = prompt }
            };
        }

        public string GetApiKeyForProvider(string provider, UserSettings settings)
        {
            if (provider.Contains("OpenAI", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.OpenAI) ? settings.ApiKeys.OpenAI : Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
            if (provider.Contains("Anthropic", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.Anthropic) ? settings.ApiKeys.Anthropic : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
            if (provider.Contains("Grok", StringComparison.OrdinalIgnoreCase) || provider.Contains("xAI", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.Grok) ? settings.ApiKeys.Grok : Environment.GetEnvironmentVariable("XAI_API_KEY") ?? Environment.GetEnvironmentVariable("GROK_API_KEY") ?? "";
            if (provider.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || provider.Contains("Google", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.Gemini) ? settings.ApiKeys.Gemini : Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? "";
            if (provider.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.DeepSeek) ? settings.ApiKeys.DeepSeek : Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
            if (provider.Contains("Groq", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.Groq) ? settings.ApiKeys.Groq : Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "";
            if (provider.Contains("OpenRouter", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.OpenRouter) ? settings.ApiKeys.OpenRouter : Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "";
            if (provider.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.Cloudflare) ? settings.ApiKeys.Cloudflare : Environment.GetEnvironmentVariable("CLOUDFLARE_API_KEY") ?? Environment.GetEnvironmentVariable("CF_API_TOKEN") ?? "";
            if (provider.Contains("Mistral", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.Mistral) ? settings.ApiKeys.Mistral : Environment.GetEnvironmentVariable("MISTRAL_API_KEY") ?? "";
            if (provider.Contains("Hugging", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.HuggingFace) ? settings.ApiKeys.HuggingFace : Environment.GetEnvironmentVariable("HUGGINGFACE_API_KEY") ?? Environment.GetEnvironmentVariable("HF_TOKEN") ?? "";
            if (provider.Contains("Cohere", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.Cohere) ? settings.ApiKeys.Cohere : Environment.GetEnvironmentVariable("COHERE_API_KEY") ?? Environment.GetEnvironmentVariable("CO_API_KEY") ?? "";
            if (provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.Nvidia) ? settings.ApiKeys.Nvidia : Environment.GetEnvironmentVariable("NVIDIA_API_KEY") ?? Environment.GetEnvironmentVariable("NIM_API_KEY") ?? "";
            if (provider.Contains("SambaNova", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.SambaNova) ? settings.ApiKeys.SambaNova : Environment.GetEnvironmentVariable("SAMBANOVA_API_KEY") ?? "";
            if (provider.Contains("Zhipu", StringComparison.OrdinalIgnoreCase) || provider.Contains("Z.AI", StringComparison.OrdinalIgnoreCase) || provider.Contains("BigModel", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(settings.ApiKeys.Zhipu) ? settings.ApiKeys.Zhipu : Environment.GetEnvironmentVariable("ZHIPU_API_KEY") ?? Environment.GetEnvironmentVariable("BIGMODEL_API_KEY") ?? "";

            // Fallback: check custom registered providers configured in Settings
            var cp = settings.CustomProviders?.FirstOrDefault(p => p.Name.Contains(provider, StringComparison.OrdinalIgnoreCase) || provider.Contains(p.Name, StringComparison.OrdinalIgnoreCase));
            if (cp != null && !string.IsNullOrWhiteSpace(cp.ApiKey)) return cp.ApiKey;

            return "";
        }

        public Task<string> CallCloudApiAsync(ModelItem model, string prompt, UserSettings settings)
            => CallCloudApiAsync(model, prompt, settings, null);

        public async Task<string> CallCloudApiAsync(ModelItem model, string prompt, UserSettings settings, string? systemPrompt)
        {
            // Resolve the effective system instruction: explicit arg wins, else the enabled global setting.
            string? sys = !string.IsNullOrWhiteSpace(systemPrompt)
                ? systemPrompt
                : (settings.SystemInstructionEnabled && !string.IsNullOrWhiteSpace(settings.SystemInstruction) ? settings.SystemInstruction : null);
            if (string.IsNullOrWhiteSpace(sys)) sys = null;

            string apiKey = model.IsCustom && !string.IsNullOrWhiteSpace(model.CustomApiKey)
                ? model.CustomApiKey
                : GetApiKeyForProvider(model.Provider, settings);

            if (string.IsNullOrWhiteSpace(apiKey) && !model.IsCustom)
            {
                return $"[ERROR: API KEY NOT CONFIGURED]\r\n" +
                       $"No API key found for provider '{model.Provider}'.\r\n" +
                       $"Please click 'ðŸ”‘ Configure API Keys' or 'âš™ Settings' to enter your API key, or set the corresponding environment variable (OPENAI_API_KEY, ANTHROPIC_API_KEY, XAI_API_KEY, GEMINI_API_KEY, DEEPSEEK_API_KEY, GROQ_API_KEY, OPENROUTER_API_KEY, MISTRAL_API_KEY, etc.).";
            }

            try
            {
                // Measures the wall-clock round-trip of whichever provider branch handles this call,
                // so Token Analysis can report latency and output throughput per model.
                var callTimer = Stopwatch.StartNew();

                // Guard: Cloudflare Workers AI endpoints embed the account id in the URL path
                // (â€¦/accounts/{account_id}/ai/â€¦). We cannot resolve it automatically, so fail with
                // an actionable message instead of firing a doomed request that returns a raw HTTP error.
                if (!string.IsNullOrEmpty(model.Endpoint) && model.Endpoint.Contains("{account_id}", StringComparison.OrdinalIgnoreCase))
                {
                    return "[SETUP REQUIRED: Cloudflare account id]\r\n" +
                           "This model's endpoint still contains the {account_id} placeholder. Cloudflare Workers AI " +
                           "requires your account id in the URL. Register this model as a Custom Provider with the full " +
                           "endpoint (https://api.cloudflare.com/client/v4/accounts/<YOUR_ACCOUNT_ID>/ai/v1/chat/completions) " +
                           "and your API token to use it.";
                }

                // 1. Custom Provider Dispatch
                if (model.IsCustom)
                {
                    string endpoint = !string.IsNullOrEmpty(model.Endpoint) ? model.Endpoint : "https://openrouter.ai/api/v1/chat/completions";

                    if (endpoint.Contains("generativelanguage.googleapis.com"))
                    {
                        string geminiKey = !string.IsNullOrWhiteSpace(apiKey) ? apiKey : GetApiKeyForProvider("Gemini", settings);
                        string geminiUrl = $"{endpoint}/{model.Id}:generateContent?key={geminiKey}";
                        object geminiBody = string.IsNullOrWhiteSpace(sys)
                            ? new { contents = new[] { new { parts = new[] { new { text = prompt } } } } }
                            : (object)new { systemInstruction = new { parts = new[] { new { text = sys } } }, contents = new[] { new { parts = new[] { new { text = prompt } } } } };
                        string gJson = JsonSerializer.Serialize(geminiBody);
                        using var gReq = new HttpRequestMessage(HttpMethod.Post, geminiUrl);
                        gReq.Content = new StringContent(gJson, Encoding.UTF8, "application/json");
                        var gResp = await _httpClient.SendAsync(gReq);
                        string gText = await gResp.Content.ReadAsStringAsync();
                        if (!gResp.IsSuccessStatusCode) return $"[GEMINI API ERROR: HTTP {(int)gResp.StatusCode}]\r\n{gText}";
                        using var gDoc = JsonDocument.Parse(gText);
                        if (gDoc.RootElement.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0 &&
                            cands[0].TryGetProperty("content", out var cContent) &&
                            cContent.TryGetProperty("parts", out var cParts) && cParts.GetArrayLength() > 0 &&
                            cParts[0].TryGetProperty("text", out var cTxt))
                        {
                            return cTxt.GetString() ?? "[Empty response text]";
                        }
                        return gText;
                    }
                    else if (endpoint.Contains("api.cohere.com"))
                    {
                        var cohereBody = new { model = model.Id, messages = BuildMessages(sys, prompt) };
                        string cJson = JsonSerializer.Serialize(cohereBody);
                        using var cReq = new HttpRequestMessage(HttpMethod.Post, endpoint);
                        if (!string.IsNullOrWhiteSpace(apiKey)) cReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                        cReq.Content = new StringContent(cJson, Encoding.UTF8, "application/json");
                        var cResp = await _httpClient.SendAsync(cReq);
                        string cText = await cResp.Content.ReadAsStringAsync();
                        if (!cResp.IsSuccessStatusCode) return $"[COHERE API ERROR: HTTP {(int)cResp.StatusCode}]\r\n{cText}";
                        using var coDoc = JsonDocument.Parse(cText);
                        if (coDoc.RootElement.TryGetProperty("message", out var coMsg) &&
                            coMsg.TryGetProperty("content", out var coCont) && coCont.GetArrayLength() > 0 &&
                            coCont[0].TryGetProperty("text", out var coTxt))
                        {
                            return coTxt.GetString() ?? "[Empty response text]";
                        }
                        return cText;
                    }

                    var customBody = new
                    {
                        model = model.Id,
                        messages = BuildMessages(sys, prompt)
                    };

                    string customJson = JsonSerializer.Serialize(customBody);
                    using var customReq = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        customReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    }
                    customReq.Headers.Add("HTTP-Referer", "https://llmodelbox.local");
                    customReq.Headers.Add("X-Title", "LlmodelBox");
                    customReq.Content = new StringContent(customJson, Encoding.UTF8, "application/json");

                    var customResp = await _httpClient.SendAsync(customReq);
                    string customRespText = await customResp.Content.ReadAsStringAsync();

                    if (!customResp.IsSuccessStatusCode)
                    {
                        return $"[CUSTOM API ERROR: HTTP {(int)customResp.StatusCode} {customResp.ReasonPhrase}]\r\n{customRespText}";
                    }

                    using var cDoc = JsonDocument.Parse(customRespText);
                    var cRoot = cDoc.RootElement;
                    EmitUsage(cRoot, model, callTimer.ElapsedMilliseconds);
                    if (cRoot.TryGetProperty("choices", out var cChoices) && cChoices.GetArrayLength() > 0)
                    {
                        var choice = cChoices[0];
                        if (choice.TryGetProperty("message", out var msg))
                        {
                            string reasoning = "";
                            if (msg.TryGetProperty("reasoning_content", out var rElem) && !string.IsNullOrEmpty(rElem.GetString()))
                            {
                                reasoning = $"[{model.Name.ToUpperInvariant()} REASONING / THINKING]\r\n{rElem.GetString()}\r\n-------------------------------------------------------------------------------\r\n";
                            }
                            else if (msg.TryGetProperty("reasoning", out var rElem2) && !string.IsNullOrEmpty(rElem2.GetString()))
                            {
                                reasoning = $"[{model.Name.ToUpperInvariant()} REASONING / THINKING]\r\n{rElem2.GetString()}\r\n-------------------------------------------------------------------------------\r\n";
                            }

                            if (msg.TryGetProperty("content", out var cElem))
                            {
                                return reasoning + (cElem.GetString() ?? "[Empty response]");
                            }
                        }
                    }
                    else if (cRoot.TryGetProperty("result", out var cfRes) && cfRes.TryGetProperty("response", out var cfResp))
                    {
                        return cfResp.GetString() ?? "[Empty response]";
                    }
                    else if (cRoot.TryGetProperty("message", out var cohereMsg) &&
                             cohereMsg.TryGetProperty("content", out var cohereCont) && cohereCont.GetArrayLength() > 0 &&
                             cohereCont[0].TryGetProperty("text", out var cohereTxt))
                    {
                        return cohereTxt.GetString() ?? "[Empty response]";
                    }

                    return customRespText;
                }

                // 2. Google Gemini API
                if (model.Provider.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || 
                    model.Provider.Contains("Google", StringComparison.OrdinalIgnoreCase))
                {
                    string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model.Id}:generateContent?key={apiKey}";
                    object requestBody = string.IsNullOrWhiteSpace(sys)
                        ? new
                        {
                            contents = new[]
                            {
                                new { parts = new[] { new { text = prompt } } }
                            }
                        }
                        : (object)new
                        {
                            systemInstruction = new { parts = new[] { new { text = sys } } },
                            contents = new[]
                            {
                                new { parts = new[] { new { text = prompt } } }
                            }
                        };

                    string jsonContent = JsonSerializer.Serialize(requestBody);
                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    var response = await _httpClient.SendAsync(request);
                    string responseText = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        return $"[API ERROR: HTTP {(int)response.StatusCode} {response.ReasonPhrase}]\r\n{responseText}";
                    }

                    using var doc = JsonDocument.Parse(responseText);
                    var root = doc.RootElement;
                    EmitUsage(root, model, callTimer.ElapsedMilliseconds);
                    if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                    {
                        var firstCandidate = candidates[0];
                        if (firstCandidate.TryGetProperty("content", out var content) &&
                            content.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                        {
                            var firstPart = parts[0];
                            if (firstPart.TryGetProperty("text", out var textElem))
                            {
                                return textElem.GetString() ?? "[Empty response text]";
                            }
                        }
                    }

                    return responseText;
                }

                // 3. Anthropic Claude API
                if (model.Provider.Contains("Anthropic", StringComparison.OrdinalIgnoreCase))
                {
                    string url = "https://api.anthropic.com/v1/messages";
                    object requestBody = string.IsNullOrWhiteSpace(sys)
                        ? new
                        {
                            model = model.Id,
                            max_tokens = 4096,
                            messages = new[] { new { role = "user", content = prompt } }
                        }
                        : (object)new
                        {
                            model = model.Id,
                            max_tokens = 4096,
                            system = sys,
                            messages = new[] { new { role = "user", content = prompt } }
                        };

                    string jsonContent = JsonSerializer.Serialize(requestBody);
                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("x-api-key", apiKey);
                    request.Headers.Add("anthropic-version", "2023-06-01");
                    request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    var response = await _httpClient.SendAsync(request);
                    string responseText = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        return $"[API ERROR: HTTP {(int)response.StatusCode} {response.ReasonPhrase}]\r\n{responseText}";
                    }

                    using var doc = JsonDocument.Parse(responseText);
                    var root = doc.RootElement;
                    EmitUsage(root, model, callTimer.ElapsedMilliseconds);
                    if (root.TryGetProperty("content", out var content) && content.GetArrayLength() > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.TryGetProperty("text", out var textElem))
                            {
                                sb.Append(textElem.GetString());
                            }
                        }
                        return sb.Length > 0 ? sb.ToString() : "[Empty response]";
                    }

                    return responseText;
                }

                // 4. Cohere v2 Dedicated Dispatch
                if (model.Provider.Contains("Cohere", StringComparison.OrdinalIgnoreCase))
                {
                    string cohereUrl = !string.IsNullOrEmpty(model.Endpoint) ? model.Endpoint : "https://api.cohere.com/v2/chat";
                    var coBody = new { model = model.Id, messages = BuildMessages(sys, prompt) };
                    using var coReq = new HttpRequestMessage(HttpMethod.Post, cohereUrl);
                    coReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    coReq.Content = new StringContent(JsonSerializer.Serialize(coBody), Encoding.UTF8, "application/json");
                    var coResp = await _httpClient.SendAsync(coReq);
                    string coText = await coResp.Content.ReadAsStringAsync();
                    if (!coResp.IsSuccessStatusCode) return $"[COHERE API ERROR: HTTP {(int)coResp.StatusCode}]\r\n{coText}";
                    using var coDoc = JsonDocument.Parse(coText);
                    EmitUsage(coDoc.RootElement, model, callTimer.ElapsedMilliseconds);
                    if (coDoc.RootElement.TryGetProperty("message", out var coMsg) &&
                        coMsg.TryGetProperty("content", out var coCont) && coCont.GetArrayLength() > 0 &&
                        coCont[0].TryGetProperty("text", out var coTxt))
                    {
                        return coTxt.GetString() ?? "[Empty response]";
                    }
                    return coText;
                }

                // 5. OpenAI, xAI (Grok), DeepSeek, Groq, OpenRouter, Mistral, HF, NVIDIA, SambaNova, Zhipu
                string endpointUrl = !string.IsNullOrEmpty(model.Endpoint) ? model.Endpoint : "https://api.openai.com/v1/chat/completions";
                var openAiBody = new
                {
                    model = model.Id,
                    messages = BuildMessages(sys, prompt)
                };

                string openAiJson = JsonSerializer.Serialize(openAiBody);
                using var openAiReq = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
                openAiReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                openAiReq.Headers.Add("HTTP-Referer", "https://llmodelbox.local");
                openAiReq.Headers.Add("X-Title", "LlmodelBox");
                openAiReq.Content = new StringContent(openAiJson, Encoding.UTF8, "application/json");

                var openAiResp = await _httpClient.SendAsync(openAiReq);
                string openAiRespText = await openAiResp.Content.ReadAsStringAsync();

                if (!openAiResp.IsSuccessStatusCode)
                {
                    return $"[API ERROR: HTTP {(int)openAiResp.StatusCode} {openAiResp.ReasonPhrase}]\r\n{openAiRespText}";
                }

                using var oDoc = JsonDocument.Parse(openAiRespText);
                var oRoot = oDoc.RootElement;
                EmitUsage(oRoot, model, callTimer.ElapsedMilliseconds);
                if (oRoot.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("message", out var msg))
                    {
                        string reasoning = "";
                        if (msg.TryGetProperty("reasoning_content", out var rElem) && !string.IsNullOrEmpty(rElem.GetString()))
                        {
                            reasoning = $"[{model.Name.ToUpperInvariant()} REASONING / THINKING]\r\n{rElem.GetString()}\r\n-------------------------------------------------------------------------------\r\n";
                        }
                        else if (msg.TryGetProperty("reasoning", out var rElem2) && !string.IsNullOrEmpty(rElem2.GetString()))
                        {
                            reasoning = $"[{model.Name.ToUpperInvariant()} REASONING / THINKING]\r\n{rElem2.GetString()}\r\n-------------------------------------------------------------------------------\r\n";
                        }

                        if (msg.TryGetProperty("content", out var cElem))
                        {
                            return reasoning + (cElem.GetString() ?? "[Empty response]");
                        }
                    }
                }
                else if (oRoot.TryGetProperty("result", out var cfResult) && cfResult.TryGetProperty("response", out var cfResp))
                {
                    return cfResp.GetString() ?? "[Empty response]";
                }
                else if (oRoot.TryGetProperty("message", out var cohereMsg) &&
                         cohereMsg.TryGetProperty("content", out var cohereCont) && cohereCont.GetArrayLength() > 0 &&
                         cohereCont[0].TryGetProperty("text", out var cohereTxt))
                {
                    return cohereTxt.GetString() ?? "[Empty response]";
                }

                return openAiRespText;
            }
            catch (HttpRequestException ex)
            {
                return $"[NETWORK CONNECTION ERROR] Could not reach API endpoint: {ex.Message}";
            }
            catch (TaskCanceledException)
            {
                return "[TIMEOUT ERROR] Request timed out after 60 seconds.";
            }
            catch (Exception ex)
            {
                return $"[EXCEPTION] {ex.GetType().Name}: {ex.Message}";
            }
        }

        public async Task TestApiConnectionsAsync(UserSettings settings, Action<string> logCallback)
        {
            logCallback("===============================================================================");
            logCallback("                 CLOUD LLM API GATEWAY CONNECTIVITY TEST");
            logCallback("===============================================================================");
            logCallback($"[TIMESTAMP] {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} // Active Operator: {settings.ProfileName}");
            logCallback("Testing HTTPS handshake & authentication status with provider endpoints...");
            logCallback("");

            var providers = new (string Name, string ProviderKey, string Endpoint)[]
            {
                ("OpenAI (GPT-4o / Mini)", "OpenAI", "https://api.openai.com/v1/models"),
                ("Anthropic (Claude 3.5)", "Anthropic", "https://api.anthropic.com/v1/messages"),
                ("xAI (Grok-2 / Beta)", "Grok", "https://api.x.ai/v1/models"),
                ("Google (Gemini 1.5/2.0)", "Gemini", "https://generativelanguage.googleapis.com/v1beta/models"),
                ("DeepSeek (V3 / R1)", "DeepSeek", "https://api.deepseek.com/models")
            };

            foreach (var p in providers)
            {
                string key = GetApiKeyForProvider(p.ProviderKey, settings);
                string masked = MaskHelper.MaskKey(key);
                bool hasKey = !string.IsNullOrWhiteSpace(key);

                var sw = Stopwatch.StartNew();
                string statusReport = "";

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, p.Endpoint);
                    if (hasKey)
                    {
                        if (p.ProviderKey == "Anthropic")
                        {
                            req.Headers.Add("x-api-key", key);
                            req.Headers.Add("anthropic-version", "2023-06-01");
                        }
                        else if (p.ProviderKey == "Gemini")
                        {
                            req.RequestUri = new Uri($"{p.Endpoint}?key={key}");
                        }
                        else
                        {
                            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                        }
                    }

                    var resp = await _httpClient.SendAsync(req);
                    sw.Stop();

                    if (resp.IsSuccessStatusCode)
                    {
                        statusReport = $"â— ONLINE (HTTP {(int)resp.StatusCode} OK, {sw.ElapsedMilliseconds}ms)";
                    }
                    else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        statusReport = hasKey 
                            ? $"â–² AUTH ERROR (HTTP {(int)resp.StatusCode}, Key may be invalid/expired, {sw.ElapsedMilliseconds}ms)"
                            : $"â—‹ ENDPOINT REACHABLE (HTTP {(int)resp.StatusCode}, Needs Key, {sw.ElapsedMilliseconds}ms)";
                    }
                    else if ((int)resp.StatusCode == 405 || (int)resp.StatusCode == 404 || (int)resp.StatusCode == 400)
                    {
                        statusReport = $"â— ENDPOINT REACHABLE (HTTP {(int)resp.StatusCode}, {sw.ElapsedMilliseconds}ms)";
                    }
                    else
                    {
                        statusReport = $"â— CONNECTED (HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}, {sw.ElapsedMilliseconds}ms)";
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    statusReport = $"âœ• FAILED ({ex.Message})";
                }

                logCallback($"  [{p.Name.PadRight(24)}] Key: {masked.PadRight(18)} | {statusReport}");
            }

            // Test Custom Providers
            if (settings.CustomProviders != null && settings.CustomProviders.Count > 0)
            {
                logCallback("");
                logCallback("  [CUSTOM REGISTERED PROVIDERS]");
                foreach (var cp in settings.CustomProviders)
                {
                    var sw = Stopwatch.StartNew();
                    string statusReport = "";
                    try
                    {
                        HttpRequestMessage req;
                        if (cp.BaseUrl.Contains("generativelanguage.googleapis.com"))
                        {
                            string geminiKey = !string.IsNullOrWhiteSpace(cp.ApiKey) ? cp.ApiKey : GetApiKeyForProvider("Gemini", settings);
                            string testUrl = $"{cp.BaseUrl}/{cp.Model}:generateContent?key={geminiKey}";
                            req = new HttpRequestMessage(HttpMethod.Post, testUrl);
                            var geminiBody = new { contents = new[] { new { parts = new[] { new { text = "ping" } } } } };
                            req.Content = new StringContent(JsonSerializer.Serialize(geminiBody), Encoding.UTF8, "application/json");
                        }
                        else
                        {
                            req = new HttpRequestMessage(HttpMethod.Post, cp.BaseUrl);
                            if (!string.IsNullOrWhiteSpace(cp.ApiKey))
                            {
                                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cp.ApiKey);
                            }
                            req.Headers.Add("HTTP-Referer", "https://llmodelbox.local");
                            req.Headers.Add("X-Title", "LlmodelBox");

                            var testBody = new
                            {
                                model = cp.Model,
                                messages = new[] { new { role = "user", content = "ping" } },
                                max_tokens = 5
                            };
                            req.Content = new StringContent(JsonSerializer.Serialize(testBody), Encoding.UTF8, "application/json");
                        }

                        var resp = await _httpClient.SendAsync(req);
                        sw.Stop();

                        if (resp.IsSuccessStatusCode)
                        {
                            statusReport = $"â— ONLINE (HTTP {(int)resp.StatusCode} OK, {sw.ElapsedMilliseconds}ms)";
                        }
                        else
                        {
                            statusReport = $"â— REACHABLE (HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}, {sw.ElapsedMilliseconds}ms)";
                        }
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        statusReport = $"âœ• FAILED ({ex.Message})";
                    }

                    string label = $"{cp.Name} ({cp.Model})";
                    if (label.Length > 24) label = label.Substring(0, 24);
                    logCallback($"  [{label.PadRight(24)}] Key: {MaskHelper.MaskKey(cp.ApiKey).PadRight(18)} | {statusReport}");
                }
            }

            logCallback("");
            logCallback("-------------------------------------------------------------------------------");
            logCallback("To configure missing keys or custom endpoints, open 'âš™ Settings' -> 'Custom Providers'");
            logCallback("===============================================================================");
            logCallback("");
        }

        public async Task TestCustomProviderAsync(CustomProviderItem cp, UserSettings settings, Action<string> logCallback)
        {
            logCallback("===============================================================================");
            logCallback($"       TESTING CUSTOM PROVIDER: {cp.Name.ToUpperInvariant()}");
            logCallback("===============================================================================");
            logCallback($"[MODEL] {cp.Model}");
            logCallback($"[ENDPOINT] {cp.BaseUrl}");
            logCallback($"[KEY] {MaskHelper.MaskKey(cp.ApiKey)}");
            logCallback($"[LOGO] {(string.IsNullOrEmpty(cp.LocalLogoPath) ? "Default Icon" : cp.LocalLogoPath)}");
            logCallback("Testing HTTPS handshake & chat completions endpoint...");

            var sw = Stopwatch.StartNew();
            try
            {
                HttpRequestMessage req;
                if (cp.BaseUrl.Contains("generativelanguage.googleapis.com"))
                {
                    string geminiKey = !string.IsNullOrWhiteSpace(cp.ApiKey) ? cp.ApiKey : GetApiKeyForProvider("Gemini", settings);
                    string testUrl = $"{cp.BaseUrl}/{cp.Model}:generateContent?key={geminiKey}";
                    req = new HttpRequestMessage(HttpMethod.Post, testUrl);
                    var geminiBody = new { contents = new[] { new { parts = new[] { new { text = "ping" } } } } };
                    req.Content = new StringContent(JsonSerializer.Serialize(geminiBody), Encoding.UTF8, "application/json");
                }
                else
                {
                    req = new HttpRequestMessage(HttpMethod.Post, cp.BaseUrl);
                    if (!string.IsNullOrWhiteSpace(cp.ApiKey))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cp.ApiKey);
                    }
                    req.Headers.Add("HTTP-Referer", "https://llmodelbox.local");
                    req.Headers.Add("X-Title", "LlmodelBox");

                    var testBody = new
                    {
                        model = cp.Model,
                        messages = new[] { new { role = "user", content = "ping" } },
                        max_tokens = 5
                    };
                    req.Content = new StringContent(JsonSerializer.Serialize(testBody), Encoding.UTF8, "application/json");
                }

                var resp = await _httpClient.SendAsync(req);
                sw.Stop();

                if (resp.IsSuccessStatusCode)
                {
                    logCallback($"[SUCCESS] â— ONLINE (HTTP {(int)resp.StatusCode} OK, {sw.ElapsedMilliseconds}ms)");
                    string body = await resp.Content.ReadAsStringAsync();
                    logCallback($"[RESPONSE] {body.Substring(0, Math.Min(200, body.Length))}...");
                }
                else
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    logCallback($"[RESPONSE] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} ({sw.ElapsedMilliseconds}ms)");
                    logCallback($"[DETAILS] {body.Substring(0, Math.Min(300, body.Length))}");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                logCallback($"[FAILED] Error reaching custom provider ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            }
            logCallback("===============================================================================");
        }

        // Aggregator providers (OpenRouter, Hugging Face) expose a live model catalog. Instead of
        // shipping a single hardcoded (and quickly stale) model id, we query the provider's OpenAI-style
        // /models listing once the user supplies a key and surface the real, currently-served models.
        public async Task<List<ModelItem>> FetchProviderModelsAsync(string providerId, string providerName, string apiKey)
        {
            var result = new List<ModelItem>();
            if (string.IsNullOrWhiteSpace(providerId)) return result;

            string listUrl = "";
            string chatEndpoint = "";
            string icon = "⚡";
            string pId = providerId.ToLowerInvariant();

            switch (pId)
            {
                case "openrouter":
                    listUrl = "https://openrouter.ai/api/v1/models";
                    chatEndpoint = "https://openrouter.ai/api/v1/chat/completions";
                    icon = "🌐";
                    break;
                case "openai":
                    listUrl = "https://api.openai.com/v1/models";
                    chatEndpoint = "https://api.openai.com/v1/chat/completions";
                    icon = "🟢";
                    break;
                case "groq":
                    listUrl = "https://api.groq.com/openai/v1/models";
                    chatEndpoint = "https://api.groq.com/openai/v1/chat/completions";
                    icon = "⚡";
                    break;
                case "mistral":
                    listUrl = "https://api.mistral.ai/v1/models";
                    chatEndpoint = "https://api.mistral.ai/v1/chat/completions";
                    icon = "🌪️";
                    break;
                case "deepseek":
                    listUrl = "https://api.deepseek.com/models";
                    chatEndpoint = "https://api.deepseek.com/chat/completions";
                    icon = "🟣";
                    break;
                case "xai":
                case "grok":
                    listUrl = "https://api.x.ai/v1/models";
                    chatEndpoint = "https://api.x.ai/v1/chat/completions";
                    icon = "⚪";
                    break;
                case "google":
                case "gemini":
                    listUrl = !string.IsNullOrWhiteSpace(apiKey)
                        ? $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}"
                        : "https://generativelanguage.googleapis.com/v1beta/models";
                    chatEndpoint = "https://generativelanguage.googleapis.com/v1beta/models";
                    icon = "🔵";
                    break;
                case "anthropic":
                    listUrl = "https://api.anthropic.com/v1/models";
                    chatEndpoint = "https://api.anthropic.com/v1/messages";
                    icon = "🟠";
                    break;
                case "cohere":
                    listUrl = "https://api.cohere.com/v1/models";
                    chatEndpoint = "https://api.cohere.com/v2/chat";
                    icon = "💠";
                    break;
                case "nvidia":
                    listUrl = "https://integrate.api.nvidia.com/v1/models";
                    chatEndpoint = "https://integrate.api.nvidia.com/v1/chat/completions";
                    icon = "🟢";
                    break;
                case "sambanova":
                    listUrl = "https://api.sambanova.ai/v1/models";
                    chatEndpoint = "https://api.sambanova.ai/v1/chat/completions";
                    icon = "🔷";
                    break;
                case "huggingface":
                    listUrl = "https://router.huggingface.co/v1/models";
                    chatEndpoint = "https://router.huggingface.co/v1/chat/completions";
                    icon = "🤗";
                    break;
                default:
                    return result;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, listUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                if (pId == "anthropic")
                {
                    req.Headers.Add("x-api-key", apiKey);
                    req.Headers.Add("anthropic-version", "2023-06-01");
                }
                else if (pId != "google" && pId != "gemini")
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }
            }

            var resp = await _httpClient.SendAsync(req);
            string text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }

            using var doc = JsonDocument.Parse(text);
            JsonElement itemsArray;

            if (doc.RootElement.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
            {
                itemsArray = dataElem;
            }
            else if (doc.RootElement.TryGetProperty("models", out var modelsElem) && modelsElem.ValueKind == JsonValueKind.Array)
            {
                itemsArray = modelsElem;
            }
            else
            {
                return result;
            }

            foreach (var item in itemsArray.EnumerateArray())
            {
                string id = "";
                if (item.TryGetProperty("id", out var idElem))
                {
                    id = idElem.GetString() ?? "";
                }
                else if (item.TryGetProperty("name", out var nElem))
                {
                    id = nElem.GetString() ?? "";
                    if (id.StartsWith("models/")) id = id.Substring("models/".Length);
                }

                if (string.IsNullOrWhiteSpace(id)) continue;

                // For OpenAI endpoints, optionally filter out non-chat legacy models (e.g. whisper, dall-e, tts, embedding)
                if (pId == "openai" && (id.Contains("whisper") || id.Contains("tts") || id.Contains("embedding") || id.Contains("dall-e") || id.Contains("moderation")))
                {
                    continue;
                }

                string name = id;
                if (item.TryGetProperty("name", out var nameElem) && nameElem.ValueKind == JsonValueKind.String)
                {
                    string nm = nameElem.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(nm)) name = nm;
                }
                else if (item.TryGetProperty("displayName", out var dnElem) && dnElem.ValueKind == JsonValueKind.String)
                {
                    string dn = dnElem.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(dn)) name = dn;
                }

                string context = "";
                if (item.TryGetProperty("context_length", out var ctxElem) && ctxElem.ValueKind == JsonValueKind.Number)
                {
                    context = FormatContext(ctxElem.GetInt64());
                }
                else if (item.TryGetProperty("context_window", out var cwElem) && cwElem.ValueKind == JsonValueKind.Number)
                {
                    context = FormatContext(cwElem.GetInt64());
                }
                else if (item.TryGetProperty("max_context_length", out var mcElem) && mcElem.ValueKind == JsonValueKind.Number)
                {
                    context = FormatContext(mcElem.GetInt64());
                }
                else if (item.TryGetProperty("inputTokenLimit", out var itElem) && itElem.ValueKind == JsonValueKind.Number)
                {
                    context = FormatContext(itElem.GetInt64());
                }

                string pricing = "See provider";
                if (item.TryGetProperty("pricing", out var priceElem) && priceElem.ValueKind == JsonValueKind.Object)
                {
                    string prompt = priceElem.TryGetProperty("prompt", out var pElem) ? (pElem.GetString() ?? "") : "";
                    string completion = priceElem.TryGetProperty("completion", out var comElem) ? (comElem.GetString() ?? "") : "";
                    if (double.TryParse(prompt, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double pv) &&
                        double.TryParse(completion, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cv))
                    {
                        pricing = pv == 0 && cv == 0 ? "Free" : $"${pv * 1_000_000:0.##} / ${cv * 1_000_000:0.##} MTok";
                    }
                }

                string desc = $"{providerName} live-catalog model fetched directly from provider endpoint.";
                if (item.TryGetProperty("description", out var descElem) && descElem.ValueKind == JsonValueKind.String)
                {
                    string d = descElem.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(d)) desc = d;
                }

                string webUrl = pId switch
                {
                    "openrouter" => $"https://openrouter.ai/models?q={Uri.EscapeDataString(id)}",
                    "huggingface" => $"https://huggingface.co/{id}",
                    "openai" => "https://platform.openai.com/docs/models",
                    "groq" => "https://console.groq.com/docs/models",
                    "mistral" => "https://docs.mistral.ai/getting-started/models/",
                    "deepseek" => "https://platform.deepseek.com",
                    "google" or "gemini" => "https://aistudio.google.com",
                    "anthropic" => "https://docs.anthropic.com/en/docs/about-claude/models",
                    "cohere" => "https://docs.cohere.com/docs/models",
                    _ => "https://llmodelbox.local"
                };

                result.Add(new ModelItem
                {
                    Id = id,
                    Name = name,
                    Provider = providerName,
                    Endpoint = chatEndpoint,
                    Family = "Live Provider Catalog",
                    Format = "Cloud REST API",
                    Context = string.IsNullOrEmpty(context) ? "128k" : context,
                    Parameters = "Provider-hosted",
                    Pricing = pricing,
                    Status = "API Ready",
                    Description = desc,
                    Icon = icon,
                    WebUrl = webUrl
                });
            }

            return result
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FormatContext(long tokens)
        {
            if (tokens <= 0) return "";
            if (tokens >= 1000) return $"{tokens / 1000}k";
            return tokens.ToString();
        }
    }
}
