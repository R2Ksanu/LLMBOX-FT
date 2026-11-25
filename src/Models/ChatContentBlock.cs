using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LlmodelBox.Models
{
    public enum MessageActivityState
    {
        Idle,
        Queued,
        Connecting,
        Thinking,
        Generating,
        Streaming,
        ExecutingTool,
        EditingFiles,
        Completed,
        Cancelled,
        Failed
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(MarkdownBlock), "markdown")]
    [JsonDerivedType(typeof(CodeBlock), "code")]
    [JsonDerivedType(typeof(TableBlock), "table")]
    [JsonDerivedType(typeof(TaskListBlock), "tasklist")]
    [JsonDerivedType(typeof(DiffSummaryBlock), "diff_summary")]
    [JsonDerivedType(typeof(CodeDiffBlock), "code_diff")]
    [JsonDerivedType(typeof(ChartBlock), "chart")]
    [JsonDerivedType(typeof(MermaidBlock), "mermaid")]
    [JsonDerivedType(typeof(FileAttachmentBlock), "file")]
    [JsonDerivedType(typeof(ProgressBlock), "progress")]
    [JsonDerivedType(typeof(CitationBlock), "citation")]
    [JsonDerivedType(typeof(ErrorBlock), "error")]
    [JsonDerivedType(typeof(ImageBlock), "image")]
    public abstract class ChatContentBlock
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
    }

    public sealed class MarkdownBlock : ChatContentBlock
    {
        public string Text { get; set; } = string.Empty;
    }

    public sealed class CodeBlock : ChatContentBlock
    {
        public string Language { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
    }

    public sealed class TableBlock : ChatContentBlock
    {
        public string Caption { get; set; } = string.Empty;
        public List<string> Headers { get; set; } = new();
        public List<List<string>> Rows { get; set; } = new();
    }

    public sealed class TaskChecklistItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsCompleted { get; set; }
    }

    public sealed class TaskListBlock : ChatContentBlock
    {
        public string Title { get; set; } = "Tasks";
        public List<TaskChecklistItem> Tasks { get; set; } = new();
    }

    public sealed class ChangedFileEntry
    {
        public string FilePath { get; set; } = string.Empty;
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public string BeforeContent { get; set; } = string.Empty;
        public string AfterContent { get; set; } = string.Empty;
    }

    public sealed class DiffSummaryBlock : ChatContentBlock
    {
        public string Title { get; set; } = "Code Changes";
        public int FilesChanged { get; set; }
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public List<ChangedFileEntry> Files { get; set; } = new();
    }

    public sealed class CodeDiffBlock : ChatContentBlock
    {
        public string FilePath { get; set; } = string.Empty;
        public string BeforeContent { get; set; } = string.Empty;
        public string AfterContent { get; set; } = string.Empty;
        public string RawPatch { get; set; } = string.Empty;
        public int Additions { get; set; }
        public int Deletions { get; set; }
    }

    public enum ChartKind
    {
        Line,
        Bar,
        HorizontalBar,
        Pie,
        Donut,
        Area,
        Scatter
    }

    public sealed class ChartDataPoint
    {
        public string Label { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
    }

    public sealed class ChartSeries
    {
        public string Name { get; set; } = "Series";
        public string ColorHex { get; set; } = string.Empty;
        public List<ChartDataPoint> Points { get; set; } = new();
    }

    public sealed class ChartBlock : ChatContentBlock
    {
        public ChartKind Kind { get; set; } = ChartKind.Bar;
        public string Title { get; set; } = string.Empty;
        public string XLabel { get; set; } = string.Empty;
        public string YLabel { get; set; } = string.Empty;
        public List<ChartSeries> Series { get; set; } = new();
    }

    public sealed class MermaidBlock : ChatContentBlock
    {
        public string DiagramType { get; set; } = "flowchart";
        public string Title { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    public sealed class FileAttachmentBlock : ChatContentBlock
    {
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string Description { get; set; } = string.Empty;
        public string PreviewText { get; set; } = string.Empty;
    }

    public sealed class ProgressBlock : ChatContentBlock
    {
        public string Title { get; set; } = "Execution Progress";
        public string CurrentOperation { get; set; } = string.Empty;
        public int CompletedOperations { get; set; }
        public int TotalOperations { get; set; }
        public int FailedOperations { get; set; }
        public string Status { get; set; } = "Running";
        public double? PercentOverride { get; set; }
    }

    public sealed class CitationSourceItem
    {
        public string Title { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    public sealed class CitationBlock : ChatContentBlock
    {
        public string Title { get; set; } = "Sources";
        public List<CitationSourceItem> Sources { get; set; } = new();
    }

    public sealed class ErrorBlock : ChatContentBlock
    {
        public string Title { get; set; } = "Generation Error";
        public string Message { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string TechnicalDetails { get; set; } = string.Empty;
        public bool CanRetry { get; set; } = true;
    }

    public sealed class ImageBlock : ChatContentBlock
    {
        public string SourcePath { get; set; } = string.Empty;
        public string Caption { get; set; } = string.Empty;
    }
}
