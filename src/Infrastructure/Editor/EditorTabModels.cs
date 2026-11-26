using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit.Document;

namespace LlmodelBox.Infrastructure.Editor
{
    public enum DiagnosticSeverity
    {
        Info,
        Warning,
        Error
    }

    public sealed class EditorDiagnostic
    {
        public string FilePath { get; init; } = string.Empty;
        public int Line { get; init; } = 1;
        public int Column { get; init; } = 1;
        public int Length { get; init; } = 1;
        public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;
        public string Code { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }

    public sealed class InlineAiChangeProposal
    {
        public string FilePath { get; init; } = string.Empty;
        public string BeforeContent { get; init; } = string.Empty;
        public string AfterContent { get; init; } = string.Empty;
        public int Additions { get; init; }
        public int Deletions { get; init; }
        public List<int> ChangedLineNumbers { get; init; } = new();
        public string Summary => $"+{Additions} -{Deletions} lines";
    }

    /// <summary>
    /// Isolated per-tab document & view state so switching across multiple open files preserves
    /// undo/redo history, scroll position, caret offset, dirty flag, and AI proposals.
    /// </summary>
    public sealed class EditorTabState
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName => string.IsNullOrEmpty(FilePath) ? "(welcome)" : Path.GetFileName(FilePath);
        public EditorLanguageInfo Language { get; set; } = EditorLanguageService.DetectLanguage(null);
        public TextDocument Document { get; set; } = new();
        public string OriginalDiskContent { get; set; } = string.Empty;
        public bool IsDirty { get; set; }
        public bool IsLargeTruncated { get; set; }
        public string EncodingName { get; set; } = "UTF-8";
        public int CaretOffset { get; set; }
        public double VerticalScrollOffset { get; set; }
        public InlineAiChangeProposal? AiProposal { get; set; }
    }

    public static class EditorDiagnosticsHelper
    {
        // Matches MSBuild / Roslyn diagnostics like: C:\dir\File.cs(42,13): error CS1002: ; expected
        private static readonly Regex MsBuildDiagRegex = new(
            @"^(?<file>.+?)\((?<line>\d+)(?:,(?<col>\d+))?\):\s*(?<sev>error|warning|info)\s+(?<code>[A-Za-z0-9_]+)?:\s*(?<msg>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches generic path:line:col: error: msg (TypeScript, Python tracebacks, GCC, etc.)
        private static readonly Regex GenericColonRegex = new(
            @"^(?<file>.+?):(?<line>\d+)(?::(?<col>\d+))?\s*[-:]\s*(?<sev>error|warning|SyntaxError|Error)\b[:\s]*(?<msg>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<EditorDiagnostic> ParseBuildOutput(string? output)
        {
            var list = new List<EditorDiagnostic>();
            if (string.IsNullOrWhiteSpace(output)) return list;

            foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = rawLine.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var m = MsBuildDiagRegex.Match(trimmed);
                if (!m.Success) m = GenericColonRegex.Match(trimmed);
                if (!m.Success) continue;

                int.TryParse(m.Groups["line"].Value, out int line);
                int.TryParse(m.Groups["col"].Value, out int col);
                string sevRaw = m.Groups["sev"].Value;
                var sev = sevRaw.Contains("warn", StringComparison.OrdinalIgnoreCase)
                    ? DiagnosticSeverity.Warning
                    : sevRaw.Equals("info", StringComparison.OrdinalIgnoreCase)
                        ? DiagnosticSeverity.Info
                        : DiagnosticSeverity.Error;

                list.Add(new EditorDiagnostic
                {
                    FilePath = m.Groups["file"].Value.Trim(),
                    Line = Math.Max(1, line),
                    Column = Math.Max(1, col),
                    Length = 6,
                    Severity = sev,
                    Code = m.Groups["code"].Value.Trim(),
                    Message = m.Groups["msg"].Value.Trim()
                });
            }
            return list;
        }

        /// <summary>
        /// Performs fast structural diagnostics for immediate in-editor feedback (e.g., invalid JSON
        /// syntax line/column, or unclosed brackets in C#/JS/TS/CSS/JSON).
        /// </summary>
        public static List<EditorDiagnostic> AnalyzeLightweight(string filePath, string languageId, string text)
        {
            var list = new List<EditorDiagnostic>();
            if (string.IsNullOrEmpty(text) || text.Length > 800_000) return list;

            if (languageId == "json")
            {
                try
                {
                    using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                }
                catch (JsonException jex)
                {
                    int line = (int)(jex.LineNumber ?? 0) + 1;
                    int col = (int)(jex.BytePositionInLine ?? 0) + 1;
                    list.Add(new EditorDiagnostic
                    {
                        FilePath = filePath,
                        Line = Math.Max(1, line),
                        Column = Math.Max(1, col),
                        Length = 4,
                        Severity = DiagnosticSeverity.Error,
                        Code = "JSON",
                        Message = jex.Message
                    });
                }
                return list;
            }

            if (languageId is "csharp" or "javascript" or "typescript" or "css" or "cpp" or "java")
            {
                var braceStack = new Stack<(char Ch, int Line, int Col)>();
                int line = 1, col = 0;
                bool inLineComment = false, inBlock = false, inStr = false;
                char quote = '"';

                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    char next = i + 1 < text.Length ? text[i + 1] : '\0';
                    if (c == '\n') { line++; col = 0; inLineComment = false; continue; }
                    if (c == '\r') continue;
                    col++;

                    if (inLineComment) continue;
                    if (inBlock) { if (c == '*' && next == '/') { inBlock = false; i++; col++; } continue; }
                    if (inStr) { if (c == '\\') { i++; col++; continue; } if (c == quote) inStr = false; continue; }
                    if (c == '/' && next == '/') { inLineComment = true; i++; col++; continue; }
                    if (c == '/' && next == '*') { inBlock = true; i++; col++; continue; }
                    if (c == '"' || c == '\'') { inStr = true; quote = c; continue; }

                    if (c is '{' or '(' or '[') braceStack.Push((c, line, col));
                    else if (c is '}' or ')' or ']')
                    {
                        char expected = c switch { '}' => '{', ')' => '(', _ => '[' };
                        if (braceStack.Count == 0 || braceStack.Peek().Ch != expected)
                        {
                            list.Add(new EditorDiagnostic
                            {
                                FilePath = filePath,
                                Line = line,
                                Column = col,
                                Length = 1,
                                Severity = DiagnosticSeverity.Error,
                                Code = "SYNTAX",
                                Message = $"Unexpected closing '{c}'."
                            });
                            break;
                        }
                        braceStack.Pop();
                    }
                }

                if (braceStack.Count > 0 && list.Count == 0)
                {
                    var top = braceStack.Peek();
                    list.Add(new EditorDiagnostic
                    {
                        FilePath = filePath,
                        Line = top.Line,
                        Column = top.Col,
                        Length = 1,
                        Severity = DiagnosticSeverity.Error,
                        Code = "SYNTAX",
                        Message = $"Unclosed '{top.Ch}'."
                    });
                }
            }

            return list;
        }

        /// <summary>
        /// Identifies which 1-based line numbers in <paramref name="afterText"/> differ from
        /// <paramref name="beforeText"/> so AI-edited lines can be highlighted inline in the editor.
        /// </summary>
        public static InlineAiChangeProposal BuildInlineProposal(string filePath, string beforeText, string afterText, int additions, int deletions)
        {
            var beforeLines = (beforeText ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            var afterLines = (afterText ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            var beforeSet = new HashSet<string>(beforeLines, StringComparer.Ordinal);
            var changed = new List<int>();

            for (int i = 0; i < afterLines.Length; i++)
            {
                bool differsFromPos = i >= beforeLines.Length || !string.Equals(beforeLines[i], afterLines[i], StringComparison.Ordinal);
                if (differsFromPos && !beforeSet.Contains(afterLines[i]))
                {
                    changed.Add(i + 1);
                }
            }

            // If lines only reordered or small delta didn't show in set check, mark first differing lines
            if (changed.Count == 0 && !string.Equals(beforeText, afterText, StringComparison.Ordinal))
            {
                for (int i = 0; i < afterLines.Length; i++)
                {
                    if (i >= beforeLines.Length || !string.Equals(beforeLines[i], afterLines[i], StringComparison.Ordinal))
                    {
                        changed.Add(i + 1);
                    }
                }
            }

            return new InlineAiChangeProposal
            {
                FilePath = filePath,
                BeforeContent = beforeText ?? string.Empty,
                AfterContent = afterText ?? string.Empty,
                Additions = Math.Max(additions, changed.Count),
                Deletions = Math.Max(0, deletions),
                ChangedLineNumbers = changed
            };
        }
    }
}
