using System.Collections.Generic;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    // Transport-level DTOs for tool-aware (function-calling) provider calls. Deliberately independent
    // of the runtime's Models.Ai types so the Services layer stays free of a runtime dependency — the
    // CloudApiProvider adapter maps AiRequest/AiResponse <-> these. Strings in, strings out (arguments
    // and tool outputs are JSON text), so the contract is provider-agnostic.
    public sealed class CloudToolDef
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        // JSON Schema object (as text) describing the tool's parameters.
        public string ParametersJson { get; set; } = "{\"type\":\"object\",\"properties\":{}}";
    }

    public sealed class CloudToolCall
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ArgumentsJson { get; set; } = "{}";
    }

    // role: "user" | "assistant" | "tool". For an assistant turn that requested tools, ToolCalls is
    // populated. For a tool result turn, Role="tool", ToolCallId is set, and Text is the output.
    public sealed class CloudMessage
    {
        public string Role { get; set; } = "user";
        public string Text { get; set; } = string.Empty;
        public List<CloudToolCall> ToolCalls { get; set; } = new();
        public string? ToolCallId { get; set; }
        public string? ToolName { get; set; }
    }

    public sealed class CloudCallRequest
    {
        public ModelItem Model { get; set; } = new();
        public UserSettings Settings { get; set; } = new();
        public string? System { get; set; }
        public List<CloudMessage> Messages { get; set; } = new();
        public List<CloudToolDef> Tools { get; set; } = new();
        public int MaxOutputTokens { get; set; } = 4096;
    }

    public sealed class CloudCallResult
    {
        public string Text { get; set; } = string.Empty;
        public string? Reasoning { get; set; }
        public bool IsError { get; set; }
        public string? ErrorMessage { get; set; }
        public List<CloudToolCall> ToolCalls { get; set; } = new();
    }
}
