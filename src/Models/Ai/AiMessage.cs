using System.Collections.Generic;
using System.Linq;

namespace LlmodelBox.Models.Ai
{
    public enum AiRole { System, User, Assistant, Tool }

    // A normalized, provider-independent chat message. Parts allow multimodal content;
    // ToolCalls/ToolCallId support the agent loop introduced in later phases.
    public sealed class AiMessage
    {
        public AiRole Role { get; set; } = AiRole.User;
        public string? Name { get; set; }
        public List<AiContentPart> Parts { get; set; } = new();
        public List<AiToolCall> ToolCalls { get; set; } = new();
        public string? ToolCallId { get; set; }

        public string Text => string.Join("\n", Parts.Where(p => p.Kind == AiContentKind.Text).Select(p => p.Text));

        public static AiMessage System(string t) => new() { Role = AiRole.System, Parts = { AiContentPart.FromText(t) } };
        public static AiMessage User(string t) => new() { Role = AiRole.User, Parts = { AiContentPart.FromText(t) } };
        public static AiMessage Assistant(string t) => new() { Role = AiRole.Assistant, Parts = { AiContentPart.FromText(t) } };
    }
}
