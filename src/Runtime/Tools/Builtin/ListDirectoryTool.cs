using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Runtime.Tools.Builtin
{
    // Lists immediate entries (dirs then files) of a directory inside the workspace. Read-only and
    // exposes only names/sizes, so it is Safe and runs without prompting.
    public sealed class ListDirectoryTool : ITool
    {
        private const int MaxEntries = 500;
        private readonly string _root;

        public ListDirectoryTool(string workspaceRoot) => _root = workspaceRoot;

        public ToolRisk Risk => ToolRisk.Safe;

        public AiToolDefinition Definition { get; } = new()
        {
            Name = "list_directory",
            Description = "List the files and subdirectories directly inside a workspace directory. " +
                          "Use \".\" for the workspace root. Returns names only.",
            ParametersJson = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"," +
                             "\"description\":\"Workspace-relative directory path, or '.' for the root\"}}}"
        };

        public Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rel = ToolArgs.GetString(call.ArgumentsJson, "path") ?? ".";
            string full = WorkspacePaths.ResolveInside(_root, rel);
            if (!Directory.Exists(full))
                return Task.FromResult(ToolArgs.Error(call, $"Directory not found: {rel}"));

            var sb = new StringBuilder();
            int count = 0;
            foreach (var dir in Directory.EnumerateDirectories(full).Take(MaxEntries + 1).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count++ >= MaxEntries) break;
                sb.Append("[dir]  ").AppendLine(Path.GetFileName(dir));
            }
            foreach (var file in Directory.EnumerateFiles(full).Take(Math.Max(0, MaxEntries + 1 - count)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count++ >= MaxEntries) break;
                var fi = new FileInfo(file);
                sb.Append("[file] ").Append(Path.GetFileName(file)).Append("  (").Append(fi.Length).AppendLine(" bytes)");
            }

            if (count == 0) sb.AppendLine("(empty)");
            else if (count > MaxEntries) sb.AppendLine($"... (truncated at {MaxEntries} entries)");

            return Task.FromResult(ToolArgs.Ok(call, sb.ToString().TrimEnd()));
        }
    }
}
