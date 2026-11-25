using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Common;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    // Tool-aware transport (Phase 5b). Adds real function-calling on top of the existing string API
    // WITHOUT touching CallCloudApiAsync: it sends tool definitions + structured multi-turn messages
    // (including prior tool calls and their results) and parses the model's requested tool calls back
    // out. Three wire formats are implemented — OpenAI-compatible, Anthropic, Google Gemini — which
    // together cover every tool-capable provider in this app. Formats without tool support degrade to
    // a plain completion (no tool calls), never an error.
    public partial class CloudApiService
    {
        public async Task<CloudCallResult> CallToolAwareAsync(CloudCallRequest request, CancellationToken cancellationToken)
        {
            var model = request.Model;
            var settings = request.Settings;

            string? sys = string.IsNullOrWhiteSpace(request.System)
                ? (settings.SystemInstructionEnabled && !string.IsNullOrWhiteSpace(settings.SystemInstruction) ? settings.SystemInstruction : null)
                : request.System;

            string apiKey = model.IsCustom && !string.IsNullOrWhiteSpace(model.CustomApiKey)
                ? model.CustomApiKey
                : GetApiKeyForProvider(model.Provider, settings);

            if (string.IsNullOrWhiteSpace(apiKey) && !model.IsCustom)
                return Err($"[ERROR: API KEY NOT CONFIGURED] No API key found for provider '{model.Provider}'.");

            bool gemini = (model.Provider?.Contains("Gemini", StringComparison.OrdinalIgnoreCase) ?? false)
                          || (model.Provider?.Contains("Google", StringComparison.OrdinalIgnoreCase) ?? false)
                          || (model.Endpoint?.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ?? false);
            bool anthropic = model.Provider?.Contains("Anthropic", StringComparison.OrdinalIgnoreCase) ?? false;

            try
            {
                var timer = Stopwatch.StartNew();
                if (gemini) return await GeminiToolCallAsync(request, sys, apiKey, timer, cancellationToken).ConfigureAwait(false);
                if (anthropic) return await AnthropicToolCallAsync(request, sys, apiKey, timer, cancellationToken).ConfigureAwait(false);
                return await OpenAiToolCallAsync(request, sys, apiKey, timer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex) { return Err($"[ERROR: Network] {ex.Message}"); }
            catch (Exception ex) { return Err($"[ERROR: {ex.GetType().Name}] {ex.Message}"); }
        }

        private static CloudCallResult Err(string message) =>
            new() { IsError = true, ErrorMessage = message, Text = message };

        // Parses a JSON-Schema string into a JsonNode, falling back to an empty object schema.
        private static JsonNode SchemaNode(string? parametersJson)
        {
            if (!string.IsNullOrWhiteSpace(parametersJson))
            {
                try { var n = JsonNode.Parse(parametersJson!); if (n != null) return n; } catch { }
            }
            return new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        }

        // ---- OpenAI-compatible (OpenAI, Grok, DeepSeek, Groq, OpenRouter, Mistral, NVIDIA, etc.) ----
        private async Task<CloudCallResult> OpenAiToolCallAsync(CloudCallRequest req, string? sys, string apiKey, Stopwatch timer, CancellationToken ct)
        {
            string url = !string.IsNullOrEmpty(req.Model.Endpoint) ? req.Model.Endpoint! : "https://api.openai.com/v1/chat/completions";
            var messages = new JsonArray();
            if (!string.IsNullOrWhiteSpace(sys)) messages.Add(new JsonObject { ["role"] = "system", ["content"] = sys });

            foreach (var m in req.Messages)
            {
                if (m.Role == "tool")
                {
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId ?? "", ["content"] = m.Text ?? "" });
                }
                else if (m.Role == "assistant" && m.ToolCalls.Count > 0)
                {
                    var tc = new JsonArray();
                    foreach (var c in m.ToolCalls)
                        tc.Add(new JsonObject { ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson ?? "{}" } });
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = string.IsNullOrEmpty(m.Text) ? null : m.Text, ["tool_calls"] = tc });
                }
                else
                {
                    messages.Add(new JsonObject { ["role"] = m.Role == "assistant" ? "assistant" : "user", ["content"] = m.Text ?? "" });
                }
            }

            var body = new JsonObject { ["model"] = req.Model.Id, ["messages"] = messages };
            if (req.Tools.Count > 0)
            {
                var tools = new JsonArray();
                foreach (var t in req.Tools)
                    tools.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = SchemaNode(t.ParametersJson) } });
                body["tools"] = tools;
                body["tool_choice"] = "auto";
            }

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            httpReq.Headers.Add("HTTP-Referer", "https://llmodelbox.local");
            httpReq.Headers.Add("X-Title", "LlmodelBox");
            httpReq.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            var resp = await _httpClient.SendAsync(httpReq, ct).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Err($"[API ERROR: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}]\r\n{text}");

            return ParseOpenAi(text, req.Model, timer.ElapsedMilliseconds);
        }

        private CloudCallResult ParseOpenAi(string text, ModelItem model, long elapsedMs)
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            EmitUsage(root, model, elapsedMs);

            var result = new CloudCallResult();
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var msg = choices[0].TryGetProperty("message", out var m) ? m : default;
                if (msg.ValueKind == JsonValueKind.Object)
                {
                    if (msg.TryGetProperty("reasoning_content", out var r1) && r1.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(r1.GetString()))
                        result.Reasoning = r1.GetString();
                    else if (msg.TryGetProperty("reasoning", out var r2) && r2.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(r2.GetString()))
                        result.Reasoning = r2.GetString();

                    if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        result.Text = c.GetString() ?? "";

                    if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in tcs.EnumerateArray())
                        {
                            string id = tc.TryGetProperty("id", out var idE) && idE.ValueKind == JsonValueKind.String ? (idE.GetString() ?? "") : "";
                            if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                            {
                                string name = fn.TryGetProperty("name", out var nE) ? (nE.GetString() ?? "") : "";
                                string args = fn.TryGetProperty("arguments", out var aE) && aE.ValueKind == JsonValueKind.String ? (aE.GetString() ?? "{}") : "{}";
                                if (!string.IsNullOrWhiteSpace(name))
                                    result.ToolCalls.Add(new CloudToolCall { Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id, Name = name, ArgumentsJson = string.IsNullOrWhiteSpace(args) ? "{}" : args });
                            }
                        }
                    }
                }
            }
            if (result.ToolCalls.Count == 0 && string.IsNullOrEmpty(result.Text)) result.Text = text;
            return result;
        }

        // ---- Anthropic (Claude) — tools / tool_use / tool_result content blocks ----
        private async Task<CloudCallResult> AnthropicToolCallAsync(CloudCallRequest req, string? sys, string apiKey, Stopwatch timer, CancellationToken ct)
        {
            string url = !string.IsNullOrEmpty(req.Model.Endpoint) ? req.Model.Endpoint! : "https://api.anthropic.com/v1/messages";
            var messages = new JsonArray();

            foreach (var m in req.Messages)
            {
                if (m.Role == "tool")
                {
                    var content = new JsonArray { new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId ?? "", ["content"] = m.Text ?? "" } };
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                }
                else if (m.Role == "assistant" && m.ToolCalls.Count > 0)
                {
                    var content = new JsonArray();
                    if (!string.IsNullOrEmpty(m.Text)) content.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                    foreach (var c in m.ToolCalls)
                    {
                        JsonNode input;
                        try { input = JsonNode.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson!) ?? new JsonObject(); }
                        catch { input = new JsonObject(); }
                        content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = input });
                    }
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                }
                else
                {
                    messages.Add(new JsonObject { ["role"] = m.Role == "assistant" ? "assistant" : "user", ["content"] = m.Text ?? "" });
                }
            }

            var body = new JsonObject { ["model"] = req.Model.Id, ["max_tokens"] = req.MaxOutputTokens, ["messages"] = messages };
            if (!string.IsNullOrWhiteSpace(sys)) body["system"] = sys;
            if (req.Tools.Count > 0)
            {
                var tools = new JsonArray();
                foreach (var t in req.Tools)
                    tools.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = SchemaNode(t.ParametersJson) });
                body["tools"] = tools;
            }

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
            httpReq.Headers.Add("x-api-key", apiKey);
            httpReq.Headers.Add("anthropic-version", "2023-06-01");
            httpReq.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            var resp = await _httpClient.SendAsync(httpReq, ct).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Err($"[API ERROR: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}]\r\n{text}");

            return ParseAnthropic(text, req.Model, timer.ElapsedMilliseconds);
        }

        private CloudCallResult ParseAnthropic(string text, ModelItem model, long elapsedMs)
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            EmitUsage(root, model, elapsedMs);

            var result = new CloudCallResult();
            var sb = new StringBuilder();
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    string type = block.TryGetProperty("type", out var tE) ? (tE.GetString() ?? "") : "";
                    if (type == "text" && block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                        sb.Append(txt.GetString());
                    else if (type == "tool_use")
                    {
                        string id = block.TryGetProperty("id", out var idE) ? (idE.GetString() ?? "") : "";
                        string name = block.TryGetProperty("name", out var nE) ? (nE.GetString() ?? "") : "";
                        string args = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                        if (!string.IsNullOrWhiteSpace(name))
                            result.ToolCalls.Add(new CloudToolCall { Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id, Name = name, ArgumentsJson = string.IsNullOrWhiteSpace(args) ? "{}" : args });
                    }
                }
            }
            result.Text = sb.ToString();
            if (result.ToolCalls.Count == 0 && string.IsNullOrEmpty(result.Text)) result.Text = text;
            return result;
        }

        // ---- Google Gemini — functionDeclarations / functionCall / functionResponse ----
        private async Task<CloudCallResult> GeminiToolCallAsync(CloudCallRequest req, string? sys, string apiKey, Stopwatch timer, CancellationToken ct)
        {
            string url = !string.IsNullOrEmpty(req.Model.Endpoint)
                ? req.Model.Endpoint!
                : $"https://generativelanguage.googleapis.com/v1beta/models/{req.Model.Id}:generateContent";
            if (!url.Contains("key=", StringComparison.OrdinalIgnoreCase))
                url += (url.Contains('?') ? "&" : "?") + "key=" + Uri.EscapeDataString(apiKey);

            var contents = new JsonArray();
            foreach (var m in req.Messages)
            {
                if (m.Role == "tool")
                {
                    JsonNode responseNode;
                    try { responseNode = JsonNode.Parse(string.IsNullOrWhiteSpace(m.Text) ? "{}" : m.Text!) ?? new JsonObject(); }
                    catch { responseNode = new JsonObject { ["result"] = m.Text ?? "" }; }
                    if (responseNode is not JsonObject) responseNode = new JsonObject { ["result"] = responseNode };
                    var fr = new JsonObject { ["name"] = m.ToolName ?? "", ["response"] = responseNode };
                    contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray { new JsonObject { ["functionResponse"] = fr } } });
                }
                else if (m.Role == "assistant" && m.ToolCalls.Count > 0)
                {
                    var parts = new JsonArray();
                    if (!string.IsNullOrEmpty(m.Text)) parts.Add(new JsonObject { ["text"] = m.Text });
                    foreach (var c in m.ToolCalls)
                    {
                        JsonNode argsNode;
                        try { argsNode = JsonNode.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson!) ?? new JsonObject(); }
                        catch { argsNode = new JsonObject(); }
                        parts.Add(new JsonObject { ["functionCall"] = new JsonObject { ["name"] = c.Name, ["args"] = argsNode } });
                    }
                    contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
                }
                else
                {
                    contents.Add(new JsonObject { ["role"] = m.Role == "assistant" ? "model" : "user", ["parts"] = new JsonArray { new JsonObject { ["text"] = m.Text ?? "" } } });
                }
            }

            var body = new JsonObject { ["contents"] = contents };
            if (!string.IsNullOrWhiteSpace(sys))
                body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = sys } } };
            if (req.Tools.Count > 0)
            {
                var decls = new JsonArray();
                foreach (var t in req.Tools)
                    decls.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = SchemaNode(t.ParametersJson) });
                body["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = decls } };
            }

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
            httpReq.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            var resp = await _httpClient.SendAsync(httpReq, ct).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Err($"[API ERROR: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}]\r\n{text}");

            return ParseGemini(text, req.Model, timer.ElapsedMilliseconds);
        }

        private CloudCallResult ParseGemini(string text, ModelItem model, long elapsedMs)
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            EmitUsage(root, model, elapsedMs);

            var result = new CloudCallResult();
            var sb = new StringBuilder();
            if (root.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0)
            {
                if (cands[0].TryGetProperty("content", out var contentEl) && contentEl.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                            sb.Append(txt.GetString());
                        else if (part.TryGetProperty("functionCall", out var fc) && fc.ValueKind == JsonValueKind.Object)
                        {
                            string name = fc.TryGetProperty("name", out var nE) ? (nE.GetString() ?? "") : "";
                            string args = fc.TryGetProperty("args", out var aE) ? aE.GetRawText() : "{}";
                            if (!string.IsNullOrWhiteSpace(name))
                                result.ToolCalls.Add(new CloudToolCall { Id = Guid.NewGuid().ToString("N"), Name = name, ArgumentsJson = string.IsNullOrWhiteSpace(args) ? "{}" : args });
                        }
                    }
                }
            }
            result.Text = sb.ToString();
            if (result.ToolCalls.Count == 0 && string.IsNullOrEmpty(result.Text)) result.Text = text;
            return result;
        }
    }
}
