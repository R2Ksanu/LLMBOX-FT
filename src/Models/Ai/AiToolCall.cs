using System;

namespace LlmodelBox.Models.Ai
{
    // A model-requested tool invocation. Not executed until later phases wire a ToolExecutor
    // behind the permission engine; defined now so the message/response contract is stable.
    public sealed class AiToolCall
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string ArgumentsJson { get; set; } = "{}";
    }

    // The result of executing an AiToolCall, fed back to the model on the next turn.
    public sealed class AiToolResult
    {
        public string ToolCallId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Output { get; set; } = string.Empty;
        public bool IsError { get; set; }
    }
}
