using System;
using System.Text;
using System.Text.Json;

namespace LlmodelBox.Runtime.Tools
{
    // Turns a tool call's raw JSON arguments into human-readable text. The model emits arguments as a
    // JSON blob (with escaped \n etc.); showing that verbatim in the chat pill or the approval dialog
    // reads like machine noise. This centralises two friendly renderings:
    //   Summary  — a one-line action label for the live "Working · …" pill ("Writing info.txt").
    //   Details  — a decoded key/value block for the approval modal (real newlines, values trimmed).
    public static class ToolCallFormatter
    {
        // Argument keys that identify the primary target of a call, in priority order.
        private static readonly string[] PrimaryKeys =
            { "path", "file", "filename", "query", "command", "cmd", "directory", "dir", "url", "pattern" };

        // Verb per known tool name so the pill reads like an action, not an API call.
        private static string Verb(string tool) => tool.ToLowerInvariant() switch
        {
            "write_file" => "Writing",
            "read_file" => "Reading",
            "list_directory" => "Listing",
            "run_command" or "shell" or "bash" => "Running",
            "search" or "grep" => "Searching",
            _ => "Using " + tool,
        };

        // One-line action label, e.g. "Writing info.txt" or "Listing src/". Falls back to the tool
        // name when the arguments can't be parsed or hold nothing useful.
        public static string Summary(string toolName, string? argumentsJson)
        {
            toolName ??= "tool";
            string? primary = TryGetPrimary(argumentsJson);
            return string.IsNullOrEmpty(primary) ? Verb(toolName) : $"{Verb(toolName)} {primary}";
        }

        // Decoded, readable argument block for the approval dialog. One "key: value" per line with
        // real newlines. Long text values (e.g. a file's whole content) are summarised as a size,
        // not dumped, so the dialog stays short and scannable.
        public static string Details(string? argumentsJson, int maxValueLen = 120)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson)) return "(no arguments)";
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return Clip(argumentsJson!.Trim(), maxValueLen);

                var sb = new StringBuilder();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    string val = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? string.Empty
                        : prop.Value.ToString();
                    val = val.Replace("\r\n", "\n").Trim();
                    // Collapse multi-line / bulky values to a size hint instead of a wall of text.
                    if (val.Length > maxValueLen || val.Contains('\n'))
                        val = $"({val.Length:N0} chars)";
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(prop.Name).Append(": ").Append(val);
                }
                return sb.Length == 0 ? "(no arguments)" : sb.ToString();
            }
            catch
            {
                return Clip(argumentsJson!.Trim(), maxValueLen);
            }
        }

        private static string? TryGetPrimary(string? argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                foreach (var key in PrimaryKeys)
                {
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        string s = (v.GetString() ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                        if (s.Length > 0) return Clip(s, 60);
                    }
                }
            }
            catch { }
            return null;
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
