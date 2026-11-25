using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Models.Ai;
using LlmodelBox.Services;

namespace LlmodelBox.Providers
{
    // Phase 1/2 seam: normalizes the existing CloudApiService (the provider "heart", holding all
    // provider-specific HTTP transport) behind IModelProvider. This is a WRAPPER, not a rewrite —
    // provider-specific request/response logic stays in CloudApiService. Phase 2 enriches this with
    // capability tables, retry/timeout/fallback, real streaming and normalized errors.
    //
    // ---------------------------------------------------------------------------------------------
    // EXTENSION POINT — HOW TO ADD A NEW PROVIDER ADAPTER
    // ---------------------------------------------------------------------------------------------
    // The runtime talks only to IModelProvider, so a new provider is a new class in this folder:
    //   1. Add `sealed class MyProvider : IModelProvider` under src/Providers/.
    //   2. Implement CanHandle(ModelItem) to claim the models you serve (match Provider/Family/Id).
    //   3. Implement GetCapabilities — reuse ModelCapabilityResolver.Resolve(model) for free.
    //   4. Implement GenerateAsync (normalize your transport into AiResponse; never throw for
    //      provider-side errors — return AiResponse.Error(...); only rethrow user cancellation).
    //   5. Implement StreamAsync (emit real SSE if your transport supports it; otherwise reuse the
    //      honest pseudo-streaming pattern below).
    //   6. Register the instance in the IEnumerable<IModelProvider> passed to ModelRouter. The
    //      router picks the first CanHandle==true provider and falls back to the next on hard error.
    // No interface or runtime edits are required — providers compose purely through the DI list.
    // ---------------------------------------------------------------------------------------------
    public sealed class CloudApiProvider : IModelProvider
    {
        private readonly ICloudApiService _cloud;

        // Default per-call ceiling; the underlying transport may enforce its own shorter timeout.
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(120);
        private const int MaxRetries = 2; // total attempts = 1 + MaxRetries

        public CloudApiProvider(ICloudApiService cloud) => _cloud = cloud;

        public string Name => "CloudApi";

        // The wrapper can serve any model the existing service handles (cloud + custom).
        public bool CanHandle(ModelItem model) => model != null;

        // Capability inference is delegated to the deterministic per-family resolver. See
        // ModelCapabilityResolver for the honesty note on how these flags are derived.
        public ModelCapabilities GetCapabilities(ModelItem model) => ModelCapabilityResolver.Resolve(model);

        public async Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // Tool-aware path: when the caller advertises tools, use the structured function-calling
            // transport (CallToolAwareAsync) instead of the flattened string prompt, so the model can
            // request tool calls and we can feed prior tool results back as structured turns.
            if (request.Tools.Count > 0)
                return await GenerateWithToolsAsync(request, ct).ConfigureAwait(false);

            string prompt = BuildPrompt(request);
            string? system = ResolveSystem(request);

            var sw = Stopwatch.StartNew();
            DateTime start = DateTime.UtcNow;

            string? raw = null;
            string? transientError = null;

            // Retry loop: attempt once, then up to MaxRetries more times on TRANSIENT failures
            // (network exceptions, timeouts that are not user-initiated, and 5xx-looking service
            // error strings). Non-transient errors return immediately; user cancellation rethrows.
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                // Linked CTS: user cancellation OR the per-call timeout can cancel the transport.
                using var timeoutCts = new CancellationTokenSource(CallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    // NOTE: ICloudApiService.CallCloudApiAsync does not currently accept a
                    // CancellationToken, so the linked token cannot hard-abort an in-flight HTTP
                    // request inside the service. We still gate before/after and honor the timeout
                    // as a wall-clock budget; passing a token into the transport is a future change
                    // owned by CloudApiService (out of this adapter's scope).
                    var callTask = _cloud.CallCloudApiAsync(request.Model, prompt, request.Settings, system);
                    raw = await callTask.WaitAsync(linked.Token).ConfigureAwait(false);

                    if (IsTransientErrorString(raw) && attempt < MaxRetries)
                    {
                        transientError = raw;
                        await BackoffAsync(attempt, ct).ConfigureAwait(false);
                        continue;
                    }
                    transientError = null;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // genuine user cancellation — never swallow.
                }
                catch (OperationCanceledException)
                {
                    // Timeout (linked token fired but user token did not): transient.
                    transientError = $"[ERROR: Timeout] Provider call exceeded {CallTimeout.TotalSeconds:0}s.";
                    if (attempt < MaxRetries) { await BackoffAsync(attempt, ct).ConfigureAwait(false); continue; }
                }
                catch (HttpRequestException hex)
                {
                    transientError = $"[ERROR: Network] {hex.Message}";
                    if (attempt < MaxRetries) { await BackoffAsync(attempt, ct).ConfigureAwait(false); continue; }
                }
                catch (Exception ex)
                {
                    // Non-transient: normalize and return without retrying.
                    sw.Stop();
                    return AiResponse.Error($"[ERROR: {ex.GetType().Name}] {ex.Message}");
                }
            }
            sw.Stop();

            // Exhausted retries on a transient failure with no usable body.
            if (raw == null)
                return Finalize(AiResponse.Error(transientError ?? "[ERROR] Provider call failed."), request, sw.ElapsedMilliseconds);

            string clean = ReasoningFilter.Split(raw, out string reasoning);
            bool isErr = clean.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase);

            var resp = new AiResponse
            {
                Text = clean,
                Reasoning = string.IsNullOrWhiteSpace(reasoning) ? null : reasoning,
                IsError = isErr,
                ErrorMessage = isErr ? clean : null,
                LatencyMs = sw.ElapsedMilliseconds,
                ModelId = request.Model?.Id ?? "",
                ModelName = request.Model?.Name ?? "",
                Provider = request.Model?.Provider ?? Name,
                FinishReason = isErr ? "error" : "stop"
            };

            var live = _cloud.LastLiveUsage;
            if (!isErr && live != null && live.TimestampUtc >= start) resp.Usage = live;
            return resp;
        }

        // Stamps latency + identity onto a normalized error/response produced off the happy path.
        private AiResponse Finalize(AiResponse resp, AiRequest request, long latencyMs)
        {
            resp.LatencyMs = latencyMs;
            resp.ModelId = request.Model?.Id ?? "";
            resp.ModelName = request.Model?.Name ?? "";
            resp.Provider = request.Model?.Provider ?? Name;
            resp.FinishReason ??= resp.IsError ? "error" : "stop";
            return resp;
        }

        // Exponential backoff (250ms, 500ms, …) honoring cancellation. Best-effort jitter-free —
        // the transport is a single shared service, so we keep waits short and bounded.
        private static Task BackoffAsync(int attempt, CancellationToken ct) =>
            Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), ct);

        // Heuristic: the service returns "[ERROR ...]" strings for failures. Treat 5xx / rate-limit /
        // timeout / connection wording as transient (retryable); auth / 4xx / bad-request as terminal.
        private static bool IsTransientErrorString(string? s)
        {
            if (string.IsNullOrEmpty(s) || !s.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase))
                return false;
            string t = s.ToLowerInvariant();
            bool transient = t.Contains(" 500") || t.Contains(" 502") || t.Contains(" 503") || t.Contains(" 504")
                || t.Contains("500 ") || t.Contains("502 ") || t.Contains("503 ") || t.Contains("504 ")
                || t.Contains("429") || t.Contains("rate limit") || t.Contains("timeout") || t.Contains("timed out")
                || t.Contains("temporarily") || t.Contains("overloaded") || t.Contains("unavailable")
                || t.Contains("connection") || t.Contains("reset") || t.Contains("network");
            bool terminal = t.Contains(" 400") || t.Contains(" 401") || t.Contains(" 403") || t.Contains(" 404")
                || t.Contains("unauthorized") || t.Contains("invalid api key") || t.Contains("api key")
                || t.Contains("forbidden") || t.Contains("not found");
            return transient && !terminal;
        }

        // HONEST PSEUDO-STREAMING. The underlying transport (CloudApiService.CallCloudApiAsync)
        // returns the FULL completed string in one shot — there is NO real token-by-token SSE here.
        // To keep the UI's streaming contract usable, we run the normal (buffered) generation and
        // then re-emit the finished text as chunked TokenDelta events. This is NOT true streaming
        // and must not be presented as such; latency-to-first-token equals full completion latency.
        //
        // WHERE REAL SSE PLUGS IN: when CloudApiService (or a new dedicated streaming adapter) gains
        // a token-streaming API — e.g. IAsyncEnumerable<string> StreamCloudApiAsync(...) reading the
        // provider's SSE `data:` frames — replace the GenerateAsync-then-chunk block below with a
        // pass-through that yields AiEvent.Delta per real token and assembles the final AiResponse
        // from the accumulated text + usage trailer. The Start/Reason/Delta/Done event shape stays
        // identical, so no runtime/UI changes are needed for that upgrade.
        public async IAsyncEnumerable<AiEvent> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return AiEvent.Start();
            AiResponse resp;
            bool cancelled = false;
            try { resp = await GenerateAsync(request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancelled = true; resp = AiResponse.Error("cancelled"); }
            if (cancelled) { yield return AiEvent.Fail("cancelled"); yield break; }

            if (resp.IsError) { yield return AiEvent.Fail(resp.ErrorMessage ?? resp.Text); yield return AiEvent.Done(resp); yield break; }

            if (!string.IsNullOrEmpty(resp.Reasoning)) yield return AiEvent.Reason(resp.Reasoning!);

            const int chunk = 24;
            for (int i = 0; i < resp.Text.Length; i += chunk)
            {
                ct.ThrowIfCancellationRequested();
                yield return AiEvent.Delta(resp.Text.Substring(i, Math.Min(chunk, resp.Text.Length - i)));
            }
            yield return AiEvent.Done(resp);
        }

        // Structured function-calling generation. Mirrors GenerateAsync's timeout/retry envelope but
        // routes through ICloudApiService.CallToolAwareAsync, which DOES accept a CancellationToken and
        // can be hard-aborted. Maps the normalized AiRequest onto the transport DTOs and maps any tool
        // calls the model requests back onto AiResponse.ToolCalls for the agent loop to execute.
        private async Task<AiResponse> GenerateWithToolsAsync(AiRequest request, CancellationToken ct)
        {
            string? system = ResolveSystem(request);
            var cloudReq = new CloudCallRequest
            {
                Model = request.Model ?? new ModelItem(),
                Settings = request.Settings,
                System = system,
                Messages = MapMessages(request),
                MaxOutputTokens = request.MaxOutputTokens.GetValueOrDefault() > 0 ? request.MaxOutputTokens!.Value : 4096,
            };
            foreach (var t in request.Tools)
                cloudReq.Tools.Add(new CloudToolDef { Name = t.Name, Description = t.Description, ParametersJson = t.ParametersJson });

            var sw = Stopwatch.StartNew();
            DateTime start = DateTime.UtcNow;
            CloudCallResult? result = null;
            string? transientError = null;

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                using var timeoutCts = new CancellationTokenSource(CallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                try
                {
                    result = await _cloud.CallToolAwareAsync(cloudReq, linked.Token).ConfigureAwait(false);
                    if (result.IsError && IsTransientErrorString(result.ErrorMessage ?? result.Text) && attempt < MaxRetries)
                    {
                        transientError = result.ErrorMessage ?? result.Text;
                        await BackoffAsync(attempt, ct).ConfigureAwait(false);
                        continue;
                    }
                    transientError = null;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                {
                    transientError = $"[ERROR: Timeout] Provider call exceeded {CallTimeout.TotalSeconds:0}s.";
                    if (attempt < MaxRetries) { await BackoffAsync(attempt, ct).ConfigureAwait(false); continue; }
                }
                catch (HttpRequestException hex)
                {
                    transientError = $"[ERROR: Network] {hex.Message}";
                    if (attempt < MaxRetries) { await BackoffAsync(attempt, ct).ConfigureAwait(false); continue; }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return Finalize(AiResponse.Error($"[ERROR: {ex.GetType().Name}] {ex.Message}"), request, sw.ElapsedMilliseconds);
                }
            }
            sw.Stop();

            if (result == null)
                return Finalize(AiResponse.Error(transientError ?? "[ERROR] Provider call failed."), request, sw.ElapsedMilliseconds);

            if (result.IsError)
                return Finalize(AiResponse.Error(result.ErrorMessage ?? result.Text), request, sw.ElapsedMilliseconds);

            var resp = new AiResponse
            {
                Text = result.Text ?? string.Empty,
                Reasoning = string.IsNullOrWhiteSpace(result.Reasoning) ? null : result.Reasoning,
                IsError = false,
                LatencyMs = sw.ElapsedMilliseconds,
                ModelId = request.Model?.Id ?? "",
                ModelName = request.Model?.Name ?? "",
                Provider = request.Model?.Provider ?? Name,
                FinishReason = result.ToolCalls.Count > 0 ? "tool_calls" : "stop",
            };
            foreach (var c in result.ToolCalls)
                resp.ToolCalls.Add(new AiToolCall { Id = c.Id, Name = c.Name, ArgumentsJson = c.ArgumentsJson });

            var live = _cloud.LastLiveUsage;
            if (live != null && live.TimestampUtc >= start) resp.Usage = live;
            return resp;
        }

        // Maps the normalized message history onto transport DTOs. System turns are excluded (carried
        // separately). Tool-result turns resolve their originating tool NAME from the matching tool_call
        // id in an earlier assistant turn — required by the Gemini functionResponse format.
        private static List<CloudMessage> MapMessages(AiRequest request)
        {
            var list = new List<CloudMessage>();
            foreach (var m in request.Messages)
            {
                if (m.Role == AiRole.System) continue;
                if (m.Role == AiRole.Tool)
                {
                    list.Add(new CloudMessage
                    {
                        Role = "tool",
                        Text = m.Text,
                        ToolCallId = m.ToolCallId,
                        ToolName = ResolveToolName(request, m.ToolCallId),
                    });
                }
                else if (m.Role == AiRole.Assistant && m.ToolCalls.Count > 0)
                {
                    var cm = new CloudMessage { Role = "assistant", Text = m.Text };
                    foreach (var c in m.ToolCalls)
                        cm.ToolCalls.Add(new CloudToolCall { Id = c.Id, Name = c.Name, ArgumentsJson = c.ArgumentsJson });
                    list.Add(cm);
                }
                else
                {
                    list.Add(new CloudMessage { Role = m.Role == AiRole.Assistant ? "assistant" : "user", Text = m.Text });
                }
            }
            return list;
        }

        private static string? ResolveToolName(AiRequest request, string? toolCallId)
        {
            if (string.IsNullOrEmpty(toolCallId)) return null;
            foreach (var m in request.Messages)
                if (m.Role == AiRole.Assistant)
                    foreach (var c in m.ToolCalls)
                        if (c.Id == toolCallId) return c.Name;
            return null;
        }

        private static string? ResolveSystem(AiRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt)) return request.SystemPrompt;
            foreach (var m in request.Messages)
                if (m.Role == AiRole.System && !string.IsNullOrWhiteSpace(m.Text)) return m.Text;
            return null;
        }

        // Flattens conversation history into the single prompt the current transport accepts.
        // History is prefixed as labelled turns; the trailing user turn is the live prompt.
        private static string BuildPrompt(AiRequest request)
        {
            var convo = new List<AiMessage>();
            foreach (var m in request.Messages)
                if (m.Role != AiRole.System) convo.Add(m);

            if (convo.Count <= 1) return request.PromptText;

            var sb = new StringBuilder();
            for (int i = 0; i < convo.Count - 1; i++)
            {
                var m = convo[i];
                string who = m.Role switch { AiRole.User => "User", AiRole.Assistant => "Assistant", AiRole.Tool => "Tool", _ => "User" };
                sb.Append(who).Append(": ").AppendLine(m.Text);
            }
            sb.AppendLine().Append(convo[^1].Text);
            return sb.ToString();
        }
    }
}
