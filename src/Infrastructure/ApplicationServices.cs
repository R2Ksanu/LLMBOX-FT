using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Common;
using LlmodelBox.Runtime.Tools.Builtin;

namespace LlmodelBox.Infrastructure
{
    public sealed class FileSnapshotService : IFileSnapshotService
    {
        private readonly object _gate = new();
        private readonly List<FileSnapshotRecord> _history = new();

        public FileSnapshotRecord Capture(string workspaceRoot, string relativePath)
        {
            string full = WorkspacePaths.ResolveInside(workspaceRoot, relativePath);
            string rel = Path.GetRelativePath(workspaceRoot, full);
            bool exists = File.Exists(full);
            var rec = new FileSnapshotRecord
            {
                WorkspaceRoot = workspaceRoot,
                RelativePath = rel,
                ExistedBefore = exists,
                ContentBefore = exists ? File.ReadAllText(full) : string.Empty
            };
            lock (_gate)
            {
                _history.Add(rec);
                if (_history.Count > 100) _history.RemoveAt(0);
            }
            return rec;
        }

        public void Restore(FileSnapshotRecord snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            string full = WorkspacePaths.ResolveInside(snapshot.WorkspaceRoot, snapshot.RelativePath);
            if (snapshot.ExistedBefore)
            {
                AtomicFile.WriteAllText(full, snapshot.ContentBefore);
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
            }
        }

        public IReadOnlyList<FileSnapshotRecord> GetRecentSnapshots(string workspaceRoot)
        {
            lock (_gate)
            {
                return _history
                    .Where(h => string.Equals(h.WorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                    .Reverse()
                    .ToList();
            }
        }
    }

    public sealed class GitService : IGitService
    {
        private static string ResolveGitExecutable()
        {
            string ghDesktopGit = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GitHubDesktop");
            if (Directory.Exists(ghDesktopGit))
            {
                var found = Directory.GetFiles(ghDesktopGit, "git.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(found)) return found;
            }
            return "git";
        }

        public async Task<GitRepoStatus> GetStatusAsync(string workspaceRoot, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            {
                return new GitRepoStatus { IsGitRepository = false };
            }

            var probe = await RunGitAsync(workspaceRoot, "rev-parse --abbrev-ref HEAD", ct).ConfigureAwait(false);
            if (probe.ExitCode != 0)
            {
                return new GitRepoStatus { IsGitRepository = false };
            }

            string branch = probe.Output.Trim();
            var statusRes = await RunGitAsync(workspaceRoot, "status --porcelain", ct).ConfigureAwait(false);
            var changes = new List<GitChangeEntry>();
            if (statusRes.ExitCode == 0)
            {
                foreach (var raw in statusRes.Output.Replace("\r\n", "\n").Split('\n'))
                {
                    if (raw.Length < 4) continue;
                    string code = raw.Substring(0, 2);
                    string path = raw.Substring(3).Trim().Trim('"');
                    if (!string.IsNullOrEmpty(path))
                    {
                        changes.Add(new GitChangeEntry { StatusCode = code, RelativePath = path });
                    }
                }
            }

            return new GitRepoStatus
            {
                IsGitRepository = true,
                BranchName = string.IsNullOrEmpty(branch) ? "HEAD" : branch,
                Changes = changes
            };
        }

        public async Task<string> GetDiffAsync(string workspaceRoot, string? relativePath = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return string.Empty;

            string args = "diff";
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                string full = WorkspacePaths.ResolveInside(workspaceRoot, relativePath);
                string rel = Path.GetRelativePath(workspaceRoot, full).Replace('\\', '/');
                args = $"diff -- \"{rel}\"";
            }
            var res = await RunGitAsync(workspaceRoot, args, ct).ConfigureAwait(false);
            return res.Output;
        }

        private static async Task<BuildRunResult> RunGitAsync(string root, string arguments, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ResolveGitExecutable(),
                    Arguments = arguments,
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return new BuildRunResult { ExitCode = -1 };

                string stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                return new BuildRunResult
                {
                    Succeeded = proc.ExitCode == 0,
                    ExitCode = proc.ExitCode,
                    Output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout,
                    Duration = sw.Elapsed
                };
            }
            catch (Exception ex)
            {
                return new BuildRunResult { Succeeded = false, ExitCode = -1, Output = ex.Message, Duration = sw.Elapsed };
            }
        }
    }

    public sealed class BuildTestRunner : IBuildTestRunner
    {
        public async Task<BuildRunResult> RunCommandAsync(string workspaceRoot, string fileName, string arguments, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            {
                return new BuildRunResult { Succeeded = false, ExitCode = -1, Output = "Invalid workspace root." };
            }

            var sw = Stopwatch.StartNew();
            var sb = new StringBuilder();
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return new BuildRunResult { Succeeded = false, ExitCode = -1, Output = "Failed to start process." };
            }

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            return new BuildRunResult
            {
                Succeeded = proc.ExitCode == 0,
                ExitCode = proc.ExitCode,
                Output = sb.ToString(),
                Duration = sw.Elapsed
            };
        }
    }

    public sealed class AppLogger : IAppLogger
    {
        private readonly object _gate = new();
        private readonly List<AppLogEntry> _entries = new();

        public event Action<AppLogEntry>? LogAdded;

        public void Info(string category, string message) => Append("INFO", category, message);
        public void Warn(string category, string message) => Append("WARN", category, message);
        public void Error(string category, string message) => Append("ERROR", category, message);

        private void Append(string level, string category, string message)
        {
            var entry = new AppLogEntry
            {
                Level = level,
                Category = category ?? "Core",
                Message = message ?? string.Empty
            };
            lock (_gate)
            {
                _entries.Add(entry);
                if (_entries.Count > 500) _entries.RemoveAt(0);
            }
            try { LogAdded?.Invoke(entry); } catch { }
        }

        public IReadOnlyList<AppLogEntry> GetEntries(int maxCount = 200)
        {
            lock (_gate)
            {
                return _entries.TakeLast(Math.Max(1, maxCount)).ToList();
            }
        }
    }
}
