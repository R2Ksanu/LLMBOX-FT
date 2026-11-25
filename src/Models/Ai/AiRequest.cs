using System.Collections.Generic;
using LlmodelBox.Models;

namespace LlmodelBox.Models.Ai
{
    // Normalized, provider-independent generation request. The runtime constructs this from UI
    // input + context; provider adapters translate it into provider-specific wire formats.
    public sealed class AiRequest
    {
        public ModelItem Model { get; set; } = new();
        public UserSettings Settings { get; set; } = new();
        public List<AiMessage> Messages { get; set; } = new();
        public string? SystemPrompt { get; set; }
        public double? Temperature { get; set; }
        public int? MaxOutputTokens { get; set; }
        public bool Stream { get; set; }

        // Tool definitions offered to the model this turn (Phase 5). Empty = plain generation.
        // Provider adapters that support function calling translate these into the wire format;
        // adapters that don't simply ignore them, so the request stays backward compatible.
        public List<AiToolDefinition> Tools { get; set; } = new();

        // Correlation for telemetry / tracing (later phases).
        public string? SessionId { get; set; }
        public string? TaskId { get; set; }

        // The last user message text — back-compat for single-prompt call sites.
        public string PromptText
        {
            get
            {
                for (int i = Messages.Count - 1; i >= 0; i--)
                    if (Messages[i].Role == AiRole.User) return Messages[i].Text;
                return string.Empty;
            }
        }

        public static AiRequest FromPrompt(ModelItem model, string prompt, UserSettings settings, string? system = null)
        {
            var r = new AiRequest { Model = model, Settings = settings, SystemPrompt = system };
            r.Messages.Add(AiMessage.User(prompt));
            return r;
        }
    }
}
