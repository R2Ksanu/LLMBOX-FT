using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LlmodelBox.Infrastructure
{
    public sealed class FileSnapshotRecord
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string WorkspaceRoot { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public bool ExistedBefore { get; init; }
        public string ContentBefore { get; init; } = string.Empty;
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    }

    public interface IFileSnapshotService
    {
        FileSnapshotRecord Capture(string workspaceRoot, string relativePath);
        void Restore(FileSnapshotRecord snapshot);
        IReadOnlyList<FileSnapshotRecord> GetRecentSnapshots(string workspaceRoot);
    }

    public sealed class GitChangeEntry
    {
        public string StatusCode { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public string StatusLabel => StatusCode.Trim() switch
        {
            "M" or "MM" or "AM" => "Modified",
            "A" => "Added",
            "D" => "Deleted",
            "R" => "Renamed",
            "??" => "Untracked",
            _ => StatusCode.Trim()
        };
    }

    public sealed class GitRepoStatus
    {
        public bool IsGitRepository { get; init; }
        public string BranchName { get; init; } = string.Empty;
        public List<GitChangeEntry> Changes { get; init; } = new();
    }

    public interface IGitService
    {
        Task<GitRepoStatus> GetStatusAsync(string workspaceRoot, CancellationToken ct = default);
        Task<string> GetDiffAsync(string workspaceRoot, string? relativePath = null, CancellationToken ct = default);
    }

    public sealed class BuildRunResult
    {
        public bool Succeeded { get; init; }
        public int ExitCode { get; init; }
        public string Output { get; init; } = string.Empty;
        public TimeSpan Duration { get; init; }
    }

    public interface IBuildTestRunner
    {
        Task<BuildRunResult> RunCommandAsync(string workspaceRoot, string fileName, string arguments, CancellationToken ct = default);
    }

    public sealed class AppLogEntry
    {
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
        public string Level { get; init; } = "INFO";
        public string Category { get; init; } = "Core";
        public string Message { get; init; } = string.Empty;
    }

    public interface IAppLogger
    {
        event Action<AppLogEntry>? LogAdded;
        void Info(string category, string message);
        void Warn(string category, string message);
        void Error(string category, string message);
        IReadOnlyList<AppLogEntry> GetEntries(int maxCount = 200);
    }
}
