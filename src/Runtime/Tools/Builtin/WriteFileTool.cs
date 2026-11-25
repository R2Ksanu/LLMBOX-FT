using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Runtime.Tools.Builtin
{
    // Writes UTF-8 text to a file inside the workspace, creating parent directories as needed.
    // Dangerous: it mutates the filesystem, so the permission engine always refers it to the host
    // approver (and fails closed when none is configured).
    public sealed class WriteFileTool : ITool
    {
        private const int MaxContentBytes = 512 * 1024;
        private readonly string _root;

        public WriteFileTool(string workspaceRoot) => _root = workspaceRoot;

        public ToolRisk Risk => ToolRisk.Dangerous;

        public AiToolDefinition Definition { get; } = new()
        {
            Name = "write_file",
            Description = "Create or overwrite a UTF-8 text file within the current workspace. " +
                          "Requires user approval. Use a workspace-relative path.",
            ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                             "\"path\":{\"type\":\"string\",\"description\":\"Workspace-relative file path\"}," +
                             "\"content\":{\"type\":\"string\",\"description\":\"Full file contents to write\"}}," +
                             "\"required\":[\"path\",\"content\"]}"
        };

        public Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? rel = ToolArgs.GetString(call.ArgumentsJson, "path");
            string? content = ToolArgs.GetString(call.ArgumentsJson, "content");
            if (content == null)
                return Task.FromResult(ToolArgs.Error(call, "A string 'content' argument is required; use an empty string to clear a file."));
            if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxContentBytes)
                return Task.FromResult(ToolArgs.Error(call, $"Content exceeds {MaxContentBytes} bytes."));

            string full = WorkspacePaths.ResolveInside(_root, rel);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            cancellationToken.ThrowIfCancellationRequested();
            LlmodelBox.Common.AtomicFile.WriteAllText(full, content);

            return Task.FromResult(ToolArgs.Ok(call, $"Wrote {content.Length} chars to {rel}."));
        }
    }
}
