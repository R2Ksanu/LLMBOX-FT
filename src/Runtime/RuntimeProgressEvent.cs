using System;
using LlmodelBox.Models;

namespace LlmodelBox.Runtime
{
    public sealed class RuntimeProgressEvent
    {
        public MessageActivityState State { get; init; }
        public string Detail { get; init; } = string.Empty;
        public string? TokenDelta { get; init; }
        public string? FilePath { get; init; }
        public int FilesModified { get; init; }
        public int Additions { get; init; }
        public int Deletions { get; init; }
        public ChangedFileEntry? ChangedFile { get; init; }
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    }
}
