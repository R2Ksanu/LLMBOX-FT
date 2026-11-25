using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LlmodelBox.Models.Ai;

namespace LlmodelBox.Runtime.Tools.Builtin
{
    // Reads a UTF-8 text file from inside the workspace. Sensitive: it can surface private file
    // contents, so the permission engine gates it unless the host opts in.
    public sealed class ReadFileTool : ITool
    {
        private const int MaxBytes = 256 * 1024;
        private readonly string _root;

        public ReadFileTool(string workspaceRoot) => _root = workspaceRoot;

        public ToolRisk Risk => ToolRisk.Sensitive;

        public AiToolDefinition Definition { get; } = new()
        {
            Name = "read_file",
            Description = "Read the UTF-8 text contents of a file within the current workspace. " +
                          "Returns up to 256 KB. Use a workspace-relative path.",
            ParametersJson = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"," +
                             "\"description\":\"Workspace-relative file path\"}},\"required\":[\"path\"]}"
        };

        public async Task<AiToolResult> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? rel = ToolArgs.GetString(call.ArgumentsJson, "path");
            string full = WorkspacePaths.ResolveInside(_root, rel);
            if (!File.Exists(full))
                return ToolArgs.Error(call, $"File not found: {rel}");

            var info = new FileInfo(full);
            if (info.Length > MaxBytes)
                return ToolArgs.Error(call, $"File too large ({info.Length} bytes; max {MaxBytes}).");

            // Bound the actual read as well as the initial size check (the file may grow).
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[MaxBytes + 1];
            int count = 0;
            while (count < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxBytes) return ToolArgs.Error(call, $"File exceeds {MaxBytes} bytes.");
            using var reader = new StreamReader(new MemoryStream(buffer, 0, count), System.Text.Encoding.UTF8, true);
            return ToolArgs.Ok(call, await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    // Small shared helpers for argument parsing + result shaping across built-in tools.
    internal static class ToolArgs
    {
        public static string? GetString(string? json, string key)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(key, out var el) &&
                    el.ValueKind == JsonValueKind.String)
                    return el.GetString();
            }
            catch (JsonException) { }
            return null;
        }

        public static AiToolResult Ok(AiToolCall call, string output) => new()
        {
            ToolCallId = call.Id,
            Name = call.Name,
            Output = output,
            IsError = false
        };

        public static AiToolResult Error(AiToolCall call, string message) => new()
        {
            ToolCallId = call.Id,
            Name = call.Name,
            Output = message,
            IsError = true
        };
    }
}
