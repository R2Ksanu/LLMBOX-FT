using System;

namespace LlmodelBox.Models.Ai
{
    public enum AiEventType
    {
        Started,
        TokenDelta,
        Reasoning,
        ToolCallRequested,
        ToolResult,
        Completed,
        Error
    }

    // A streaming runtime event. StreamAsync yields these; the UI can render token deltas live
    // and the final Completed event carries the assembled AiResponse.
    public sealed class AiEvent
    {
        public AiEventType Type { get; set; }
        public string? TextDelta { get; set; }
        public AiResponse? Response { get; set; }   // set on Completed
        public string? Message { get; set; }         // set on Error / info events
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        public static AiEvent Start() => new() { Type = AiEventType.Started };
        public static AiEvent Delta(string t) => new() { Type = AiEventType.TokenDelta, TextDelta = t };
        public static AiEvent Reason(string t) => new() { Type = AiEventType.Reasoning, Message = t };
        public static AiEvent Done(AiResponse r) => new() { Type = AiEventType.Completed, Response = r };
        public static AiEvent Fail(string m) => new() { Type = AiEventType.Error, Message = m };
    }
}
