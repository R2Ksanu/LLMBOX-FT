using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LlmodelBox.Models;

namespace LlmodelBox.Runtime.Tools
{
    /// <summary>
    /// Scoped per-turn tracker using AsyncLocal so tools (e.g., WriteFileTool) and ToolExecutor
    /// can emit real-time progress and exact file-edit diffs back to the active UI turn.
    /// </summary>
    public sealed class ToolTurnSession
    {
        private readonly object _gate = new();
        private readonly List<ChangedFileEntry> _edits = new();
        private readonly Action<RuntimeProgressEvent>? _sink;

        public ToolTurnSession(Action<RuntimeProgressEvent>? sink) => _sink = sink;

        public void EmitToolStart(string toolName, string detail)
        {
            var state = string.Equals(toolName, "write_file", StringComparison.OrdinalIgnoreCase)
                ? MessageActivityState.EditingFiles
                : MessageActivityState.ExecutingTool;
            _sink?.Invoke(new RuntimeProgressEvent
            {
                State = state,
                Detail = detail
            });
        }

        public void RecordFileEdit(string relativePath, string before, string after)
        {
            var (adds, dels) = ComputeLineDelta(before, after);
            var entry = new ChangedFileEntry
            {
                FilePath = relativePath,
                BeforeContent = before,
                AfterContent = after,
                Additions = adds,
                Deletions = dels
            };

            int fileCount, totalAdds, totalDels;
            lock (_gate)
            {
                var existing = _edits.FirstOrDefault(e => string.Equals(e.FilePath, relativePath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    // Preserve original baseline beforeContent across multiple edits in the same turn
                    entry = new ChangedFileEntry
                    {
                        FilePath = relativePath,
                        BeforeContent = existing.BeforeContent,
                        AfterContent = after
                    };
                    var (netAdds, netDels) = ComputeLineDelta(existing.BeforeContent, after);
                    entry.Additions = netAdds;
                    entry.Deletions = netDels;
                    _edits.Remove(existing);
                }
                _edits.Add(entry);
                fileCount = _edits.Count;
                totalAdds = _edits.Sum(e => e.Additions);
                totalDels = _edits.Sum(e => e.Deletions);
            }

            _sink?.Invoke(new RuntimeProgressEvent
            {
                State = MessageActivityState.EditingFiles,
                Detail = $"Editing {relativePath}",
                FilePath = relativePath,
                FilesModified = fileCount,
                Additions = totalAdds,
                Deletions = totalDels,
                ChangedFile = entry
            });
        }

        public List<ChangedFileEntry> SnapshotEdits()
        {
            lock (_gate) { return _edits.ToList(); }
        }

        public static (int Additions, int Deletions) ComputeLineDelta(string before, string after)
        {
            var oldLines = string.IsNullOrEmpty(before)
                ? Array.Empty<string>()
                : before.Replace("\r\n", "\n").Split('\n');
            var newLines = string.IsNullOrEmpty(after)
                ? Array.Empty<string>()
                : after.Replace("\r\n", "\n").Split('\n');

            if (oldLines.Length == 0) return (newLines.Length, 0);
            if (newLines.Length == 0) return (0, oldLines.Length);

            // Fast LCS for reasonable file sizes; multiset fallback for huge files
            if ((long)oldLines.Length * newLines.Length <= 400_000)
            {
                int m = oldLines.Length, n = newLines.Length;
                int[,] dp = new int[m + 1, n + 1];
                for (int i = m - 1; i >= 0; i--)
                {
                    for (int j = n - 1; j >= 0; j--)
                    {
                        dp[i, j] = oldLines[i] == newLines[j]
                            ? 1 + dp[i + 1, j + 1]
                            : Math.Max(dp[i + 1, j], dp[i, j + 1]);
                    }
                }
                int common = dp[0, 0];
                return (n - common, m - common);
            }

            var oldSet = new HashSet<string>(oldLines);
            var newSet = new HashSet<string>(newLines);
            int adds = newLines.Count(l => !oldSet.Contains(l));
            int dels = oldLines.Count(l => !newSet.Contains(l));
            return (adds, dels);
        }
    }

    public static class ToolExecutionTracker
    {
        private static readonly AsyncLocal<ToolTurnSession?> CurrentSession = new();

        public static ToolTurnSession? Current => CurrentSession.Value;

        public static IDisposable BeginScope(ToolTurnSession session)
        {
            var prior = CurrentSession.Value;
            CurrentSession.Value = session;
            return new Scope(prior);
        }

        private sealed class Scope : IDisposable
        {
            private readonly ToolTurnSession? _prior;
            public Scope(ToolTurnSession? prior) => _prior = prior;
            public void Dispose() => CurrentSession.Value = _prior;
        }
    }
}
