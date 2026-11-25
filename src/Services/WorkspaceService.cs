using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    public class WorkspaceService : IWorkspaceService
    {
        public List<ProjectFileItem> GetProjectFileTree(string projectRoot)
        {
            var result = new List<ProjectFileItem>();
            if (string.IsNullOrWhiteSpace(projectRoot)) return result;

            if (!Directory.Exists(projectRoot))
            {
                try
                {
                    Directory.CreateDirectory(projectRoot);
                }
                catch
                {
                    return result;
                }
            }

            // Seed default starter files if project directory is completely empty
            EnsureStarterFiles(projectRoot);

            try
            {
                var dirInfo = new DirectoryInfo(projectRoot);
                PopulateDirectory(dirInfo, projectRoot, result);
            }
            catch { }

            return result;
        }

        private void EnsureStarterFiles(string projectRoot)
        {
            try
            {
                var entries = Directory.GetFileSystemEntries(projectRoot);
                if (entries.Length == 0)
                {
                    string projectName = Path.GetFileName(projectRoot);
                    string mainPyPath = Path.Combine(projectRoot, "main.py");
                    string readmePath = Path.Combine(projectRoot, "README.md");
                    string configPath = Path.Combine(projectRoot, "modelbox.json");

                    File.WriteAllText(mainPyPath, 
                        "# BlackBox AI Project Main Entry Point\n" +
                        "import sys\n\n" +
                        "def main():\n" +
                        $"    print('[BLACKBOX] Running workspace: {projectName}')\n" +
                        "    print('[STATUS] Ready for AI generation & neural inference.')\n\n" +
                        "if __name__ == '__main__':\n" +
                        "    main()\n");

                    File.WriteAllText(readmePath,
                        $"# {projectName}\n\n" +
                        "Cyberpunk LLM & neural inference workspace.\n\n" +
                        "## Architecture\n" +
                        "- **AI Model**: Direct cloud REST integration\n" +
                        "- **Engine**: LlmodelBox IDE Workspace\n" +
                        "- **Created**: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n");

                    File.WriteAllText(configPath,
                        "{\n" +
                        $"  \"project\": \"{projectName}\",\n" +
                        "  \"version\": \"1.0.0\",\n" +
                        "  \"runtime\": \"Python / DotNet / Multi-Model\",\n" +
                        "  \"artifacts_dir\": \"artifacts\",\n" +
                        "  \"uploads_dir\": \"uploads\"\n" +
                        "}\n");
                }

                string uploadsDir = Path.Combine(projectRoot, "uploads");
                if (!Directory.Exists(uploadsDir)) Directory.CreateDirectory(uploadsDir);

                string artifactsDir = Path.Combine(projectRoot, "artifacts");
                if (!Directory.Exists(artifactsDir)) Directory.CreateDirectory(artifactsDir);
            }
            catch { }
        }

        private void PopulateDirectory(DirectoryInfo dir, string projectRoot, List<ProjectFileItem> targetList, int depth = 0)
        {
            if (depth >= 64 || (dir.Attributes & FileAttributes.ReparsePoint) != 0) return;
            // Skip hidden or heavy build folders
            var dirs = dir.GetDirectories()
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0 && !d.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) && !d.Name.StartsWith(".") && !d.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && !d.Name.Equals("obj", StringComparison.OrdinalIgnoreCase) && !d.Name.Equals("__pycache__", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Name);

            foreach (var subDir in dirs)
            {
                var folderItem = new ProjectFileItem
                {
                    Name = subDir.Name,
                    FullPath = subDir.FullName,
                    RelativePath = Path.GetRelativePath(projectRoot, subDir.FullName),
                    IsDirectory = true,
                    LastModified = subDir.LastWriteTime
                };
                try { PopulateDirectory(subDir, projectRoot, folderItem.Children, depth + 1); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                targetList.Add(folderItem);
            }

            var files = dir.GetFiles()
                .Where(f => !f.Name.StartsWith(".") && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderBy(f => f.Name);

            foreach (var file in files)
            {
                targetList.Add(new ProjectFileItem
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    RelativePath = Path.GetRelativePath(projectRoot, file.FullName),
                    Extension = file.Extension,
                    IsDirectory = false,
                    Size = file.Length,
                    SizeFormatted = FormatFileSize(file.Length),
                    LastModified = file.LastWriteTime
                });
            }
        }

        public string ReadFileContent(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return string.Empty;
            try
            {
                var info = new FileInfo(filePath);
                if (info.Length > 4 * 1024 * 1024) // 4MB limit for in-memory editor
                {
                    using var reader = new StreamReader(filePath, Encoding.UTF8, true);
                    var preview = new char[100000];
                    int count = reader.ReadBlock(preview, 0, preview.Length);
                    return "[NOTICE: File is larger than 4MB. Showing first 100,000 characters]\n\n" + new string(preview, 0, count);
                }
                return File.ReadAllText(filePath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return $"[ERROR: Could not read file: {ex.Message}]";
            }
        }

        public bool SaveFileContent(string filePath, string content)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                LlmodelBox.Common.AtomicFile.WriteAllText(filePath, content);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool CreateNewFile(string projectRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(relativePath)) return false;
            try
            {
                string fullPath = LlmodelBox.Runtime.Tools.Builtin.WorkspacePaths.ResolveInside(projectRoot, relativePath);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(fullPath))
                {
                    File.WriteAllText(fullPath, "", Encoding.UTF8);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool CreateNewFolder(string projectRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(relativePath)) return false;
            try
            {
                string fullPath = LlmodelBox.Runtime.Tools.Builtin.WorkspacePaths.ResolveInside(projectRoot, relativePath);
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Guards against path traversal: the target must resolve inside projectRoot.
        private static bool IsInside(string projectRoot, string fullPath)
        {
            try
            {
                string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string target = Path.GetFullPath(fullPath);
                return target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public bool DeletePath(string projectRoot, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !IsInside(projectRoot, fullPath)) return false;
            try
            {
                if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                else if (File.Exists(fullPath)) File.Delete(fullPath);
                else return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool RenamePath(string projectRoot, string fullPath, string newName, out string newFullPath)
        {
            newFullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(newName)) return false;
            if (!IsInside(projectRoot, fullPath)) return false;
            // Reject path separators / invalid chars in the new leaf name.
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

            try
            {
                string? dir = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(dir)) return false;
                string target = Path.Combine(dir, newName);
                if (!IsInside(projectRoot, target)) return false;
                if (File.Exists(target) || Directory.Exists(target)) return false;

                if (Directory.Exists(fullPath)) Directory.Move(fullPath, target);
                else if (File.Exists(fullPath)) File.Move(fullPath, target);
                else return false;

                newFullPath = target;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Moves a file or folder into destRelativeDir (relative to projectRoot). "" / "." means the root.
        public bool MovePath(string projectRoot, string fullPath, string destRelativeDir, out string newFullPath)
        {
            newFullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(fullPath) || !IsInside(projectRoot, fullPath)) return false;
            try
            {
                string cleanRel = (destRelativeDir ?? "").Trim().Trim('/', '\\');
                if (cleanRel == ".") cleanRel = "";
                string destDir = string.IsNullOrEmpty(cleanRel) ? projectRoot : Path.Combine(projectRoot, cleanRel);
                if (!IsInside(projectRoot, destDir) && Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) != Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar))
                    return false;

                string leaf = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
                string target = Path.Combine(destDir, leaf);
                if (!IsInside(projectRoot, target) && Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) != Path.GetFullPath(Path.Combine(projectRoot, leaf)).TrimEnd(Path.DirectorySeparatorChar))
                    return false;

                // No-op or self-into-self guards.
                if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase)) return false;
                if (Directory.Exists(fullPath) &&
                    Path.GetFullPath(destDir).StartsWith(Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return false; // can't move a folder into itself

                Directory.CreateDirectory(destDir);
                if (File.Exists(target) || Directory.Exists(target)) return false;

                if (Directory.Exists(fullPath)) Directory.Move(fullPath, target);
                else if (File.Exists(fullPath)) File.Move(fullPath, target);
                else return false;

                newFullPath = target;
                return true;
            }
            catch
            {
                return false;
            }
        }
        public string SaveUploadedImage(string projectRoot, string sourceFilePath)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return string.Empty;

            try
            {
                string uploadsDir = Path.Combine(projectRoot, "uploads");
                if (!Directory.Exists(uploadsDir)) Directory.CreateDirectory(uploadsDir);

                string fileName = Path.GetFileNameWithoutExtension(sourceFilePath);
                string ext = Path.GetExtension(sourceFilePath);
                string uniqueName = $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
                string destPath = Path.Combine(uploadsDir, uniqueName);

                File.Copy(sourceFilePath, destPath, true);
                return destPath;
            }
            catch
            {
                return string.Empty;
            }
        }

        public List<ArtifactItem> GetProjectArtifacts(string projectRoot)
        {
            var list = new List<ArtifactItem>();
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot)) return list;

            try
            {
                var scannedDirs = new[]
                {
                    Path.Combine(projectRoot, "uploads"),
                    Path.Combine(projectRoot, "artifacts"),
                    projectRoot
                };

                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var folder in scannedDirs)
                {
                    if (!Directory.Exists(folder)) continue;
                    var files = Directory.GetFiles(folder);
                    foreach (var f in files)
                    {
                        if (seenPaths.Contains(f)) continue;
                        seenPaths.Add(f);

                        var fi = new FileInfo(f);
                        string ext = fi.Extension.ToLowerInvariant();
                        bool isImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".ico";
                        string fileType = isImage ? "Image" : (ext is ".cs" or ".py" or ".js" or ".ts" or ".html" ? "Code" : (ext is ".json" or ".csv" ? "Data" : "Document"));

                        list.Add(new ArtifactItem
                        {
                            Name = fi.Name,
                            FilePath = fi.FullName,
                            RelativePath = Path.GetRelativePath(projectRoot, fi.FullName),
                            FileType = fileType,
                            Size = fi.Length,
                            SizeFormatted = FormatFileSize(fi.Length),
                            CreatedAt = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                            IsImage = isImage
                        });
                    }
                }
            }
            catch { }

            return list.OrderByDescending(a => a.CreatedAt).ToList();
        }

        public List<DiffLineItem> GenerateDiff(string originalContent, string newContent)
        {
            var diff = new List<DiffLineItem>();

            var oldLines = (originalContent ?? "").Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var newLines = (newContent ?? "").Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            int max = Math.Max(oldLines.Length, newLines.Length);
            int oldLineNum = 1;
            int newLineNum = 1;

            int i = 0, j = 0;
            while (i < oldLines.Length && j < newLines.Length)
            {
                if (oldLines[i] == newLines[j])
                {
                    diff.Add(new DiffLineItem
                    {
                        LineNumberOld = oldLineNum++,
                        LineNumberNew = newLineNum++,
                        Type = "Unchanged",
                        Prefix = " ",
                        Content = oldLines[i]
                    });
                    i++;
                    j++;
                }
                else
                {
                    // Check if old line was removed
                    diff.Add(new DiffLineItem
                    {
                        LineNumberOld = oldLineNum++,
                        LineNumberNew = null,
                        Type = "Removed",
                        Prefix = "-",
                        Content = oldLines[i]
                    });
                    i++;

                    // Check if new line was added
                    diff.Add(new DiffLineItem
                    {
                        LineNumberOld = null,
                        LineNumberNew = newLineNum++,
                        Type = "Added",
                        Prefix = "+",
                        Content = newLines[j]
                    });
                    j++;
                }
            }

            while (i < oldLines.Length)
            {
                diff.Add(new DiffLineItem
                {
                    LineNumberOld = oldLineNum++,
                    LineNumberNew = null,
                    Type = "Removed",
                    Prefix = "-",
                    Content = oldLines[i++]
                });
            }

            while (j < newLines.Length)
            {
                diff.Add(new DiffLineItem
                {
                    LineNumberOld = null,
                    LineNumberNew = newLineNum++,
                    Type = "Added",
                    Prefix = "+",
                    Content = newLines[j++]
                });
            }

            return diff;
        }

        public async Task RunCliCommandAsync(string command, string workingDir, Action<string> outputCallback, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(command)) return;

            string cmdExecutable = "cmd.exe";
            string cmdArgs = $"/c {command}";

            var psi = new ProcessStartInfo
            {
                FileName = cmdExecutable,
                Arguments = cmdArgs,
                WorkingDirectory = Directory.Exists(workingDir) ? workingDir : Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            outputCallback($"[EXEC] > {command}");
            outputCallback($"[CWD] {psi.WorkingDirectory}");

            try
            {
                using var process = new Process { StartInfo = psi };
                var sw = Stopwatch.StartNew();

                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) outputCallback(e.Data);
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) outputCallback($"[ERR] {e.Data}");
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using (cancellationToken.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                }))
                {
                    await process.WaitForExitAsync(cancellationToken);
                }

                sw.Stop();
                outputCallback($"[EXIT] Process finished with exit code {process.ExitCode} ({sw.ElapsedMilliseconds}ms)\n");
            }
            catch (OperationCanceledException)
            {
                outputCallback($"[CANCELLED] Command execution aborted by operator.\n");
            }
            catch (Exception ex)
            {
                outputCallback($"[FAULT] Could not execute '{command}': {ex.Message}\n");
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
    }
}
