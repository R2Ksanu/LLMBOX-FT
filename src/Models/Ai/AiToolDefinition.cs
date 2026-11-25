namespace LlmodelBox.Models.Ai
{
    // A tool the model may call this turn: a name, a natural-language description the model uses to
    // decide when to call it, and a JSON-Schema string describing its parameters. Provider-independent
    // — adapters translate this into their own function-calling wire format (Phase 5).
    public sealed class AiToolDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        // JSON Schema (draft-07 object) describing the accepted arguments. Defaults to an empty object
        // schema meaning "no parameters".
        public string ParametersJson { get; set; } = "{\"type\":\"object\",\"properties\":{}}";
    }
}
