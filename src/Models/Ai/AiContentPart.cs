namespace LlmodelBox.Models.Ai
{
    public enum AiContentKind { Text, Image, File, Audio, Video, StructuredData }

    // A single normalized piece of message content. Text is the common case today; the other
    // kinds carry a path/URI/bytes + mime so provider adapters can build multimodal requests later.
    public sealed class AiContentPart
    {
        public AiContentKind Kind { get; set; } = AiContentKind.Text;
        public string Text { get; set; } = string.Empty;
        public string? Path { get; set; }
        public string? Uri { get; set; }
        public string? MimeType { get; set; }
        public byte[]? Data { get; set; }

        public static AiContentPart FromText(string text) => new() { Kind = AiContentKind.Text, Text = text };
        public static AiContentPart FromImagePath(string path) => new() { Kind = AiContentKind.Image, Path = path };
        public static AiContentPart FromFilePath(string path) => new() { Kind = AiContentKind.File, Path = path };
    }
}
