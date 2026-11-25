using System;
using System.Threading.Tasks;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    public interface ICloudApiService
    {
        // Raised after a successful chat call whose response reported token usage.
        event Action<Models.TokenUsageRecord>? UsageRecorded;

        // Stores the most recent live API usage record parsed from provider response
        Models.TokenUsageRecord? LastLiveUsage { get; }

        string GetApiKeyForProvider(string provider, UserSettings settings);
        Task<string> CallCloudApiAsync(ModelItem model, string prompt, UserSettings settings);
        Task<string> CallCloudApiAsync(ModelItem model, string prompt, UserSettings settings, string? systemPrompt);

        // Real SSE token streaming where supported, falling back to CallCloudApiAsync cleanly.
        async Task<string> StreamCloudApiAsync(
            ModelItem model,
            string prompt,
            UserSettings settings,
            string? systemPrompt,
            Action<string> onTextDelta,
            Action<string> onReasoningDelta,
            System.Threading.CancellationToken ct)
        {
            string res = await CallCloudApiAsync(model, prompt, settings, systemPrompt).ConfigureAwait(false);
            if (!res.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(res))
            {
                onTextDelta(res);
            }
            return res;
        }

        // Tool-aware call: sends tool definitions + full structured message history (including prior
        // tool calls and their results) and returns any tool calls the model requested. Providers
        // whose wire format doesn't support tools degrade to a plain text completion.
        Task<CloudCallResult> CallToolAwareAsync(CloudCallRequest request, System.Threading.CancellationToken cancellationToken);
        Task<System.Collections.Generic.List<ModelItem>> FetchProviderModelsAsync(string providerId, string providerName, string apiKey);
        Task TestApiConnectionsAsync(UserSettings settings, Action<string> logCallback);
        Task TestCustomProviderAsync(CustomProviderItem cp, UserSettings settings, Action<string> logCallback);
    }
}
