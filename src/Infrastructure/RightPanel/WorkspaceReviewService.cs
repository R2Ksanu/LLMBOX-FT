using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Common;
using LlmodelBox.Models;
using LlmodelBox.Runtime.Tools.Builtin;
using LlmodelBox.Services;

namespace LlmodelBox.Infrastructure.RightPanel
{
    public enum ChangeOriginKind
    {
        AiGenerated,
        ManualEdit,
        GitWorkingTree
    }

    public sealed class DiffHunk
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string Header { get; init; } = string.Empty;
        public List<DiffLineItem> Lines { get; init; } = new();
        public bool IsAccepted { get; set; }
        public bool IsRejected { get; set; }
        public int Additions => Lines.Count(l => l.Type == "Added");
        public int Deletions => Lines.Count(l => l.Type == "Removed");
    }

    public sealed class ReviewableFileChange
    {
        public string RelativePath { get; init; } = string.Empty;
        public ChangeOriginKind Origin { get; set; }
        public string BeforeContent { get; set; } = string.Empty;
        public string AfterContent { get; set; } = string.Empty;
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public List<DiffHunk> Hunks { get; set; } = new();
        public string OriginLabel => Origin switch
        {
            ChangeOriginKind.AiGenerated => "AI",
            ChangeOriginKind.ManualEdit => "MANUAL",
            _ => "GIT"
        };
    }

    public sealed class SideBySideDiffRow
    {
        public string LeftLineNumber { get; init; } = string.Empty;
        public string LeftText { get; init; } = string.Empty;
        public string LeftType { get; init; } = "Unchanged";
        public string RightLineNumber { get; init; } = string.Empty;
        public string RightText { get; init; } = string.Empty;
        public string RightType { get; init; } = "Unchanged";
    }

    public sealed class ReviewHistoryEntry
    {
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
        public string Action { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
    }

    public sealed class WorkspaceReviewService
    {
        private readonly IWorkspaceService _workspace;
        private readonly IGitService _git;
        private readonly object _gate = new();
        private readonly Dictionary<string, ReviewableFileChange> _tracked = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ReviewHistoryEntry> _history = new();

        public WorkspaceReviewService(IWorkspaceService workspace, IGitService git)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _git = git ?? throw new ArgumentNullException(nameof(git));
        }

        public IReadOnlyList<ReviewHistoryEntry> History
        {
            get { lock (_gate) return _history.AsReadOnly(); }
        }

        public void RecordAiDiffs(string workspaceRoot, IEnumerable<ChangedFileEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || entries == null) return;
            lock (_gate)
            {
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.FilePath)) continue;
                    string rel = NormalizeRelPath(workspaceRoot, entry.FilePath);
                    var lines = _workspace.GenerateDiff(entry.BeforeContent, entry.AfterContent);
                    _tracked[rel] = new ReviewableFileChange
                    {
                        RelativePath = rel,
                        Origin = ChangeOriginKind.AiGenerated,
                        BeforeContent = entry.BeforeContent,
                        AfterContent = entry.AfterContent,
                        Additions = entry.Additions > 0 ? entry.Additions : lines.Count(l => l.Type == "Added"),
                        Deletions = entry.Deletions > 0 ? entry.Deletions : lines.Count(l => l.Type == "Removed"),
                        Hunks = ExtractHunks(lines)
                    };
                    AddHistoryUnsafe("AI modified", rel);
                }
            }
        }

        public void RecordManualUnsavedEdit(string workspaceRoot, string filePath, string diskBefore, string editorNow)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(filePath)) return;
            string rel = NormalizeRelPath(workspaceRoot, filePath);
            var lines = _workspace.GenerateDiff(diskBefore, editorNow);
            int adds = lines.Count(l => l.Type == "Added");
            int dels = lines.Count(l => l.Type == "Removed");

            lock (_gate)
            {
                if (adds == 0 && dels == 0)
                {
                    if (_tracked.TryGetValue(rel, out var existing) && existing.Origin == ChangeOriginKind.ManualEdit)
                    {
                        _tracked.Remove(rel);
                    }
                    return;
                }

                _tracked[rel] = new ReviewableFileChange
                {
                    RelativePath = rel,
                    Origin = ChangeOriginKind.ManualEdit,
                    BeforeContent = diskBefore,
                    AfterContent = editorNow,
                    Additions = adds,
                    Deletions = dels,
                    Hunks = ExtractHunks(lines)
                };
            }
        }

        public async Task<List<ReviewableFileChange>> CollectAllChangesAsync(string workspaceRoot, CancellationToken ct = default)
        {
            var result = new Dictionary<string, ReviewableFileChange>(StringComparer.OrdinalIgnoreCase);

            lock (_gate)
            {
                foreach (var kv in _tracked)
                {
                    result[kv.Key] = kv.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot))
            {
                var gitStatus = await _git.GetStatusAsync(workspaceRoot, ct).ConfigureAwait(false);
                if (gitStatus.IsGitRepository)
                {
                    foreach (var g in gitStatus.Changes)
                    {
                        if (result.ContainsKey(g.RelativePath)) continue;
                        string patch = await _git.GetDiffAsync(workspaceRoot, g.RelativePath, ct).ConfigureAwait(false);
                        string diskContent = string.Empty;
                        try
                        {
                            string full = WorkspacePaths.ResolveInside(workspaceRoot, g.RelativePath);
                            if (File.Exists(full)) diskContent = File.ReadAllText(full);
                        }
                        catch { }

                        var parsed = ParseGitDiffLines(patch);
                        if (parsed.Count == 0 && !string.IsNullOrEmpty(diskContent) && g.StatusLabel == "Untracked")
                        {
                            parsed = _workspace.GenerateDiff(string.Empty, diskContent);
                        }
                        int adds = parsed.Count(l => l.Type == "Added");
                        int dels = parsed.Count(l => l.Type == "Removed");
                        result[g.RelativePath] = new ReviewableFileChange
                        {
                            RelativePath = g.RelativePath,
                            Origin = ChangeOriginKind.GitWorkingTree,
                            BeforeContent = ReconstructSide(parsed, keepAdded: false),
                            AfterContent = string.IsNullOrEmpty(diskContent) ? ReconstructSide(parsed, keepAdded: true) : diskContent,
                            Additions = adds,
                            Deletions = dels,
                            Hunks = ExtractHunks(parsed)
                        };
                    }
                }
            }

            return result.Values.OrderBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public void AcceptHunk(string relativePath, string hunkId)
        {
            lock (_gate)
            {
                if (_tracked.TryGetValue(relativePath, out var file))
                {
                    var h = file.Hunks.FirstOrDefault(x => x.Id == hunkId);
                    if (h != null)
                    {
                        h.IsAccepted = true;
                        h.IsRejected = false;
                        AddHistoryUnsafe("Accepted hunk", relativePath);
                    }
                }
            }
        }

        public bool RejectHunk(string workspaceRoot, string relativePath, string hunkId)
        {
            ReviewableFileChange? file;
            DiffHunk? hunk;
            lock (_gate)
            {
                if (!_tracked.TryGetValue(relativePath, out file)) return false;
                hunk = file.Hunks.FirstOrDefault(x => x.Id == hunkId);
                if (hunk == null) return false;
                hunk.IsRejected = true;
                hunk.IsAccepted = false;
            }

            string full = WorkspacePaths.ResolveInside(workspaceRoot, relativePath);
            string newContent = ApplyHunkDecision(file.BeforeContent, file.AfterContent, hunk, accept: false);
            AtomicFile.WriteAllText(full, newContent);

            lock (_gate)
            {
                file.AfterContent = newContent;
                var lines = _workspace.GenerateDiff(file.BeforeContent, file.AfterContent);
                file.Additions = lines.Count(l => l.Type == "Added");
                file.Deletions = lines.Count(l => l.Type == "Removed");
                file.Hunks = ExtractHunks(lines);
                AddHistoryUnsafe("Rejected hunk", relativePath);
            }
            return true;
        }

        public bool RevertFile(string workspaceRoot, ReviewableFileChange change)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || change == null) return false;
            string full = WorkspacePaths.ResolveInside(workspaceRoot, change.RelativePath);
            AtomicFile.WriteAllText(full, change.BeforeContent);
            lock (_gate)
            {
                _tracked.Remove(change.RelativePath);
                AddHistoryUnsafe("Reverted file", change.RelativePath);
            }
            return true;
        }

        public List<SideBySideDiffRow> BuildSideBySide(string beforeContent, string afterContent)
        {
            var inline = _workspace.GenerateDiff(beforeContent, afterContent);
            var rows = new List<SideBySideDiffRow>();
            int i = 0;
            while (i < inline.Count)
            {
                var cur = inline[i];
                if (cur.Type == "Unchanged")
                {
                    rows.Add(new SideBySideDiffRow
                    {
                        LeftLineNumber = cur.DisplayOldLine,
                        LeftText = cur.Content,
                        LeftType = "Unchanged",
                        RightLineNumber = cur.DisplayNewLine,
                        RightText = cur.Content,
                        RightType = "Unchanged"
                    });
                    i++;
                }
                else if (cur.Type == "Removed")
                {
                    var nextAdd = (i + 1 < inline.Count && inline[i + 1].Type == "Added") ? inline[i + 1] : null;
                    rows.Add(new SideBySideDiffRow
                    {
                        LeftLineNumber = cur.DisplayOldLine,
                        LeftText = cur.Content,
                        LeftType = "Removed",
                        RightLineNumber = nextAdd?.DisplayNewLine ?? "",
                        RightText = nextAdd?.Content ?? "",
                        RightType = nextAdd != null ? "Added" : "Empty"
                    });
                    i += nextAdd != null ? 2 : 1;
                }
                else
                {
                    rows.Add(new SideBySideDiffRow
                    {
                        LeftLineNumber = "",
                        LeftText = "",
                        LeftType = "Empty",
                        RightLineNumber = cur.DisplayNewLine,
                        RightText = cur.Content,
                        RightType = "Added"
                    });
                    i++;
                }
            }
            return rows;
        }

        private string ApplyHunkDecision(string before, string after, DiffHunk targetHunk, bool accept)
        {
            if (accept) return after;
            var result = new List<string>();
            var all = _workspace.GenerateDiff(before, after);
            var hunkAddedLines = new HashSet<int>(targetHunk.Lines.Where(l => l.Type == "Added" && l.LineNumberNew.HasValue).Select(l => l.LineNumberNew!.Value));
            var hunkRemovedLines = new HashSet<int>(targetHunk.Lines.Where(l => l.Type == "Removed" && l.LineNumberOld.HasValue).Select(l => l.LineNumberOld!.Value));

            foreach (var l in all)
            {
                if (l.Type == "Unchanged")
                {
                    result.Add(l.Content);
                }
                else if (l.Type == "Removed")
                {
                    if (l.LineNumberOld.HasValue && hunkRemovedLines.Contains(l.LineNumberOld.Value))
                    {
                        result.Add(l.Content); // Restore removed line from rejected hunk
                    }
                }
                else if (l.Type == "Added")
                {
                    if (!l.LineNumberNew.HasValue || !hunkAddedLines.Contains(l.LineNumberNew.Value))
                    {
                        result.Add(l.Content);
                    }
                }
            }
            return string.Join(Environment.NewLine, result);
        }

        private static List<DiffHunk> ExtractHunks(List<DiffLineItem> lines)
        {
            var hunks = new List<DiffHunk>();
            const int Context = 2;
            int i = 0;
            while (i < lines.Count)
            {
                if (lines[i].Type == "Unchanged") { i++; continue; }

                int start = Math.Max(0, i - Context);
                int end = i;
                while (end < lines.Count)
                {
                    if (lines[end].Type != "Unchanged")
                    {
                        end++;
                        continue;
                    }
                    // Merge adjacent hunks separated by small context
                    int lookahead = end;
                    while (lookahead < lines.Count && lookahead - end <= Context * 2 && lines[lookahead].Type == "Unchanged")
                        lookahead++;
                    if (lookahead < lines.Count && lines[lookahead].Type != "Unchanged")
                    {
                        end = lookahead;
                        continue;
                    }
                    break;
                }
                end = Math.Min(lines.Count, end + Context);

                var slice = lines.Skip(start).Take(end - start).ToList();
                int oldStart = slice.FirstOrDefault(l => l.LineNumberOld.HasValue)?.LineNumberOld ?? 1;
                int newStart = slice.FirstOrDefault(l => l.LineNumberNew.HasValue)?.LineNumberNew ?? 1;
                hunks.Add(new DiffHunk
                {
                    Header = $"@@ -{oldStart} +{newStart} @@ (+{slice.Count(l => l.Type == "Added")} / -{slice.Count(l => l.Type == "Removed")})",
                    Lines = slice
                });
                i = end;
            }
            return hunks;
        }

        private static List<DiffLineItem> ParseGitDiffLines(string patch)
        {
            var list = new List<DiffLineItem>();
            if (string.IsNullOrWhiteSpace(patch)) return list;
            int oldLn = 1, newLn = 1;
            foreach (var raw in patch.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.StartsWith("diff --git") || raw.StartsWith("index ") || raw.StartsWith("--- ") || raw.StartsWith("+++ ") || raw.StartsWith("@@"))
                {
                    if (raw.StartsWith("@@"))
                        list.Add(new DiffLineItem { Type = "Header", Prefix = "@", Content = raw });
                    continue;
                }
                if (raw.StartsWith("+"))
                    list.Add(new DiffLineItem { Type = "Added", Prefix = "+", LineNumberNew = newLn++, Content = raw.Substring(1) });
                else if (raw.StartsWith("-"))
                    list.Add(new DiffLineItem { Type = "Removed", Prefix = "-", LineNumberOld = oldLn++, Content = raw.Substring(1) });
                else
                    list.Add(new DiffLineItem { Type = "Unchanged", Prefix = " ", LineNumberOld = oldLn++, LineNumberNew = newLn++, Content = raw.StartsWith(" ") ? raw.Substring(1) : raw });
            }
            return list;
        }

        private static string ReconstructSide(List<DiffLineItem> items, bool keepAdded)
        {
            var sb = new StringBuilder();
            foreach (var l in items)
            {
                if (l.Type == "Header") continue;
                if (l.Type == "Unchanged" || (keepAdded && l.Type == "Added") || (!keepAdded && l.Type == "Removed"))
                {
                    sb.AppendLine(l.Content);
                }
            }
            return sb.ToString();
        }

        private static string NormalizeRelPath(string root, string path)
        {
            try
            {
                string full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
                if (full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetRelativePath(root, full);
                }
            }
            catch { }
            return path;
        }

        private void AddHistoryUnsafe(string action, string relPath)
        {
            _history.Insert(0, new ReviewHistoryEntry { Action = action, RelativePath = relPath });
            if (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
        }
    }
}
