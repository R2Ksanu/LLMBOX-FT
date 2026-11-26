using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace LlmodelBox.Infrastructure.Editor
{
    public enum EditorFoldingKind
    {
        None,
        Brace,
        Xml,
        Indent
    }

    public sealed record EditorLanguageInfo(
        string Id,
        string DisplayName,
        string Badge,
        string AvalonSyntaxName,
        EditorFoldingKind FoldingKind);

    /// <summary>
    /// Detects programming languages, provides AvalonEdit syntax highlighting adapted cleanly
    /// for Midnight / Slate (dark) and Daylight (light) themes, and computes code folding blocks.
    /// </summary>
    public static class EditorLanguageService
    {
        private static bool _customDefinitionsRegistered = false;
        private static readonly object _initGate = new();

        public static EditorLanguageInfo DetectLanguage(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return new EditorLanguageInfo("plaintext", "Plain Text", "TXT", string.Empty, EditorFoldingKind.None);

            string fileName = Path.GetFileName(filePath);
            if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
                return new EditorLanguageInfo("shell", "Dockerfile", "DOCKER", "PowerShell", EditorFoldingKind.Indent);
            if (fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
                return new EditorLanguageInfo("shell", "Makefile", "MAKE", "PowerShell", EditorFoldingKind.Indent);

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".cs" => new("csharp", "C#", "C#", "C#", EditorFoldingKind.Brace),
                ".csx" => new("csharp", "C# Script", "CSX", "C#", EditorFoldingKind.Brace),
                ".py" => new("python", "Python", "PY", "Python", EditorFoldingKind.Indent),
                ".js" or ".mjs" or ".cjs" => new("javascript", "JavaScript", "JS", "JavaScript", EditorFoldingKind.Brace),
                ".jsx" => new("javascript", "React JSX", "JSX", "JavaScript", EditorFoldingKind.Brace),
                ".ts" => new("typescript", "TypeScript", "TS", "JavaScript", EditorFoldingKind.Brace),
                ".tsx" => new("typescript", "React TSX", "TSX", "JavaScript", EditorFoldingKind.Brace),
                ".json" or ".jsonc" => new("json", "JSON", "JSON", "JSON", EditorFoldingKind.Brace),
                ".xaml" => new("xaml", "XAML", "XAML", "XML", EditorFoldingKind.Xml),
                ".xml" or ".csproj" or ".props" or ".targets" or ".svg" or ".config" or ".xshd"
                    => new("xml", "XML", "XML", "XML", EditorFoldingKind.Xml),
                ".html" or ".htm" => new("html", "HTML", "HTML", "HTML", EditorFoldingKind.Xml),
                ".css" or ".scss" or ".less" => new("css", "CSS", "CSS", "CSS", EditorFoldingKind.Brace),
                ".md" or ".markdown" => new("markdown", "Markdown", "MD", "MarkDown", EditorFoldingKind.Indent),
                ".ps1" or ".psm1" => new("powershell", "PowerShell", "PS1", "PowerShell", EditorFoldingKind.Brace),
                ".bat" or ".cmd" or ".sh" or ".bash" => new("shell", "Shell", "SH", "PowerShell", EditorFoldingKind.Indent),
                ".sql" => new("sql", "SQL", "SQL", "TSQL", EditorFoldingKind.None),
                ".cpp" or ".c" or ".h" or ".hpp" => new("cpp", "C/C++", "CPP", "C++", EditorFoldingKind.Brace),
                ".java" => new("java", "Java", "JAVA", "Java", EditorFoldingKind.Brace),
                ".php" => new("php", "PHP", "PHP", "PHP", EditorFoldingKind.Brace),
                ".yml" or ".yaml" => new("yaml", "YAML", "YAML", "JSON", EditorFoldingKind.Indent),
                _ => new("plaintext", "Plain Text", string.IsNullOrEmpty(ext) ? "TXT" : ext.TrimStart('.').ToUpperInvariant(), string.Empty, EditorFoldingKind.None)
            };
        }

        public static void EnsureCustomHighlightingsRegistered()
        {
            if (_customDefinitionsRegistered) return;
            lock (_initGate)
            {
                if (_customDefinitionsRegistered) return;
                RegisterXshdIfMissing("JSON", JsonXshd);
                RegisterXshdIfMissing("Python", PythonXshd);
                RegisterXshdIfMissing("PowerShell", PowerShellXshd);
                _customDefinitionsRegistered = true;
            }
        }

        private static void RegisterXshdIfMissing(string name, string xshdXml)
        {
            try
            {
                if (HighlightingManager.Instance.GetDefinition(name) != null) return;
                using var sr = new StringReader(xshdXml);
                using var xr = XmlReader.Create(sr);
                var def = HighlightingLoader.Load(xr, HighlightingManager.Instance);
                string[] exts = name switch
                {
                    "JSON" => new[] { ".json", ".jsonc" },
                    "Python" => new[] { ".py" },
                    "PowerShell" => new[] { ".ps1", ".psm1" },
                    _ => Array.Empty<string>()
                };
                HighlightingManager.Instance.RegisterHighlighting(name, exts, def);
            }
            catch { }
        }

        /// <summary>
        /// Returns an AvalonEdit IHighlightingDefinition with colors tuned so text stays sharp and
        /// high-contrast on Midnight (#000000), Slate (#0b0e14), or Daylight (#fafafa) backgrounds.
        /// </summary>
        public static IHighlightingDefinition? GetThemedHighlighting(EditorLanguageInfo lang, bool isDarkTheme)
        {
            EnsureCustomHighlightingsRegistered();
            if (string.IsNullOrWhiteSpace(lang.AvalonSyntaxName)) return null;

            var baseDef = HighlightingManager.Instance.GetDefinition(lang.AvalonSyntaxName);
            if (baseDef == null) return null;

            if (isDarkTheme)
            {
                ApplyDarkPalette(baseDef);
            }
            else
            {
                ApplyLightPalette(baseDef);
            }
            return baseDef;
        }

        private static void ApplyDarkPalette(IHighlightingDefinition def)
        {
            foreach (var c in def.NamedHighlightingColors)
            {
                string n = c.Name ?? string.Empty;
                Color? fg = n switch
                {
                    _ when ContainsAny(n, "Comment", "DocComment") => ColorFromHex("#6A9955"),
                    _ when ContainsAny(n, "String", "Char", "Verbatim") => ColorFromHex("#CE9178"),
                    _ when ContainsAny(n, "Number", "Digits") => ColorFromHex("#B5CEA8"),
                    _ when ContainsAny(n, "Preprocessor", "Directive", "Property") => ColorFromHex("#9CDCFE"),
                    _ when ContainsAny(n, "Method", "Function") => ColorFromHex("#DCDCAA"),
                    _ when ContainsAny(n, "Type", "Class", "Struct", "ValueType", "ReferenceType", "Tag", "XmlTag", "Element") => ColorFromHex("#4EC9B0"),
                    _ when ContainsAny(n, "AttributeName", "Key", "Variable", "Param") => ColorFromHex("#9CDCFE"),
                    _ when ContainsAny(n, "Keyword", "Modifiers", "Visibility", "Control", "OperatorKeywords", "TrueFalse", "Null", "Loop", "Jump") => ColorFromHex("#569CD6"),
                    _ when ContainsAny(n, "Punctuation", "Delimiter") => ColorFromHex("#CBD5E1"),
                    _ when ContainsAny(n, "Error") => ColorFromHex("#F87171"),
                    _ => RemapDarkColor(c.Foreground?.GetColor(null))
                };
                if (fg.HasValue)
                {
                    c.Foreground = new SimpleHighlightingBrush(fg.Value);
                }
            }
        }

        private static void ApplyLightPalette(IHighlightingDefinition def)
        {
            foreach (var c in def.NamedHighlightingColors)
            {
                string n = c.Name ?? string.Empty;
                Color? fg = n switch
                {
                    _ when ContainsAny(n, "Comment", "DocComment") => ColorFromHex("#008000"),
                    _ when ContainsAny(n, "String", "Char") => ColorFromHex("#A31515"),
                    _ when ContainsAny(n, "Number", "Digits") => ColorFromHex("#098658"),
                    _ when ContainsAny(n, "Method", "Function") => ColorFromHex("#795E26"),
                    _ when ContainsAny(n, "Type", "Class", "Struct", "Tag", "XmlTag") => ColorFromHex("#267F99"),
                    _ when ContainsAny(n, "Keyword", "Modifiers", "Visibility", "Control", "TrueFalse", "Null") => ColorFromHex("#0000FF"),
                    _ when ContainsAny(n, "AttributeName", "Key", "Variable") => ColorFromHex("#001080"),
                    _ => null
                };
                if (fg.HasValue)
                {
                    c.Foreground = new SimpleHighlightingBrush(fg.Value);
                }
            }
        }

        private static Color? RemapDarkColor(Color? existing)
        {
            if (!existing.HasValue) return null;
            var col = existing.Value;
            // Any very dark color meant for white paper becomes illegible on Midnight/Slate; lift luminance cleanly
            double lum = (0.299 * col.R + 0.587 * col.G + 0.114 * col.B) / 255.0;
            if (lum < 0.42)
            {
                return Color.FromRgb(
                    (byte)Math.Min(255, col.R + 125),
                    (byte)Math.Min(255, col.G + 135),
                    (byte)Math.Min(255, col.B + 150));
            }
            return null;
        }

        private static bool ContainsAny(string target, params string[] parts)
            => parts.Any(p => target.Contains(p, StringComparison.OrdinalIgnoreCase));

        private static Color ColorFromHex(string hex)
            => (Color)ColorConverter.ConvertFromString(hex);

        public static IEnumerable<NewFolding> ComputeFoldings(TextDocument document, EditorFoldingKind kind, out int firstErrorOffset)
        {
            firstErrorOffset = -1;
            if (document == null || document.TextLength == 0 || kind == EditorFoldingKind.None)
                return Array.Empty<NewFolding>();

            return kind switch
            {
                EditorFoldingKind.Brace => ComputeBraceFoldings(document, out firstErrorOffset),
                EditorFoldingKind.Indent => ComputeIndentFoldings(document),
                EditorFoldingKind.Xml => ComputeXmlFoldingsSafe(document, out firstErrorOffset),
                _ => Array.Empty<NewFolding>()
            };
        }

        private static IEnumerable<NewFolding> ComputeXmlFoldingsSafe(TextDocument document, out int firstErrorOffset)
        {
            firstErrorOffset = -1;
            try
            {
                var strategy = new XmlFoldingStrategy();
                return strategy.CreateNewFoldings(document, out firstErrorOffset);
            }
            catch
            {
                return Array.Empty<NewFolding>();
            }
        }

        /// <summary>
        /// Computes { ... } fold regions while skipping string literals and // or /* */ comments.
        /// </summary>
        public static List<NewFolding> ComputeBraceFoldings(TextDocument document, out int firstErrorOffset)
        {
            firstErrorOffset = -1;
            var foldings = new List<NewFolding>();
            var startOffsets = new Stack<int>();

            bool inLineComment = false;
            bool inBlockComment = false;
            bool inString = false;
            char stringQuote = '"';
            int lastNewLineOffset = 0;

            int len = document.TextLength;
            for (int i = 0; i < len; i++)
            {
                char c = document.GetCharAt(i);
                char next = i + 1 < len ? document.GetCharAt(i + 1) : '\0';

                if (c == '\n')
                {
                    inLineComment = false;
                    lastNewLineOffset = i + 1;
                    continue;
                }
                if (c == '\r')
                {
                    if (next != '\n')
                    {
                        inLineComment = false;
                        lastNewLineOffset = i + 1;
                    }
                    continue;
                }

                if (inLineComment) continue;
                if (inBlockComment)
                {
                    if (c == '*' && next == '/') { inBlockComment = false; i++; }
                    continue;
                }
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == stringQuote) inString = false;
                    continue;
                }

                if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
                if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
                if (c == '"' || c == '\'') { inString = true; stringQuote = c; continue; }

                if (c == '{')
                {
                    startOffsets.Push(i);
                }
                else if (c == '}' && startOffsets.Count > 0)
                {
                    int startOffset = startOffsets.Pop();
                    if (startOffset < lastNewLineOffset)
                    {
                        foldings.Add(new NewFolding(startOffset, i + 1) { Name = "{…}" });
                    }
                }
            }

            if (startOffsets.Count > 0)
            {
                firstErrorOffset = startOffsets.Peek();
            }

            foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
            return foldings;
        }

        /// <summary>
        /// Computes indentation-based folds for Python, Shell, YAML, and section headings for Markdown.
        /// </summary>
        public static List<NewFolding> ComputeIndentFoldings(TextDocument document)
        {
            var foldings = new List<NewFolding>();
            var stack = new Stack<(int Indent, int StartOffset, string Title)>();

            int lineCount = document.LineCount;
            int lastNonEmptyEnd = 0;

            for (int ln = 1; ln <= lineCount; ln++)
            {
                var line = document.GetLineByNumber(ln);
                string text = document.GetText(line);
                if (string.IsNullOrWhiteSpace(text)) continue;

                int indent = 0;
                while (indent < text.Length && (text[indent] == ' ' || text[indent] == '\t'))
                    indent++;

                while (stack.Count > 0 && indent <= stack.Peek().Indent)
                {
                    var top = stack.Pop();
                    if (lastNonEmptyEnd > top.StartOffset)
                    {
                        foldings.Add(new NewFolding(top.StartOffset, lastNonEmptyEnd) { Name = top.Title });
                    }
                }

                string trimmed = text.Trim();
                bool opensIndentBlock = trimmed.EndsWith(":", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal);
                if (opensIndentBlock)
                {
                    stack.Push((indent, line.Offset, trimmed.Length > 36 ? trimmed[..36] + "…" : trimmed));
                }

                lastNonEmptyEnd = line.EndOffset;
            }

            while (stack.Count > 0)
            {
                var top = stack.Pop();
                if (lastNonEmptyEnd > top.StartOffset)
                {
                    foldings.Add(new NewFolding(top.StartOffset, lastNonEmptyEnd) { Name = top.Title });
                }
            }

            foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
            return foldings;
        }

        private const string JsonXshd = """
            <?xml version="1.0" encoding="utf-8" ?>
            <SyntaxDefinition name="JSON" extensions=".json;.jsonc" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="#6A9955" />
              <Color name="String" foreground="#CE9178" />
              <Color name="Key" foreground="#9CDCFE" />
              <Color name="Number" foreground="#B5CEA8" />
              <Color name="TrueFalse" foreground="#569CD6" fontWeight="bold" />
              <Color name="Punctuation" foreground="#CBD5E1" />
              <RuleSet>
                <Span color="Comment" begin="//" />
                <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
                <Rule color="Key">"[^"\\]*(?:\\.[^"\\]*)*"(?=\s*:)</Rule>
                <Span color="String"><Begin>"</Begin><End>"</End><RuleSet><Span begin="\\" end="." /></RuleSet></Span>
                <Keywords color="TrueFalse">
                  <Word>true</Word><Word>false</Word><Word>null</Word>
                </Keywords>
                <Rule color="Number">-?\b\d+(\.\d+)?([eE][+-]?\d+)?\b</Rule>
                <Rule color="Punctuation">[{}\[\],:]</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;

        private const string PythonXshd = """
            <?xml version="1.0" encoding="utf-8" ?>
            <SyntaxDefinition name="Python" extensions=".py" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="#6A9955" />
              <Color name="String" foreground="#CE9178" />
              <Color name="Keyword" foreground="#569CD6" fontWeight="bold" />
              <Color name="Number" foreground="#B5CEA8" />
              <Color name="MethodCall" foreground="#DCDCAA" />
              <Color name="Type" foreground="#4EC9B0" />
              <RuleSet>
                <Span color="Comment" begin="#" />
                <Span color="String" multiline="true" begin="&quot;&quot;&quot;" end="&quot;&quot;&quot;" />
                <Span color="String" multiline="true" begin="'''" end="'''" />
                <Span color="String"><Begin>"</Begin><End>"</End><RuleSet><Span begin="\\" end="." /></RuleSet></Span>
                <Span color="String"><Begin>'</Begin><End>'</End><RuleSet><Span begin="\\" end="." /></RuleSet></Span>
                <Rule color="Number">\b0[xX][0-9a-fA-F]+|\b\d+(\.\d+)?([eE][+-]?\d+)?\b</Rule>
                <Keywords color="Keyword">
                  <Word>and</Word><Word>as</Word><Word>assert</Word><Word>async</Word><Word>await</Word>
                  <Word>break</Word><Word>class</Word><Word>continue</Word><Word>def</Word><Word>del</Word>
                  <Word>elif</Word><Word>else</Word><Word>except</Word><Word>False</Word><Word>finally</Word>
                  <Word>for</Word><Word>from</Word><Word>global</Word><Word>if</Word><Word>import</Word>
                  <Word>in</Word><Word>is</Word><Word>lambda</Word><Word>None</Word><Word>nonlocal</Word>
                  <Word>not</Word><Word>or</Word><Word>pass</Word><Word>raise</Word><Word>return</Word>
                  <Word>True</Word><Word>try</Word><Word>while</Word><Word>with</Word><Word>yield</Word>
                </Keywords>
                <Keywords color="Type">
                  <Word>int</Word><Word>str</Word><Word>float</Word><Word>bool</Word><Word>list</Word>
                  <Word>dict</Word><Word>set</Word><Word>tuple</Word><Word>self</Word><Word>cls</Word>
                </Keywords>
                <Rule color="MethodCall">\b[A-Za-z_][A-Za-z0-9_]*(?=\s*\()</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;

        private const string PowerShellXshd = """
            <?xml version="1.0" encoding="utf-8" ?>
            <SyntaxDefinition name="PowerShell" extensions=".ps1;.psm1" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="#6A9955" />
              <Color name="String" foreground="#CE9178" />
              <Color name="Variable" foreground="#9CDCFE" />
              <Color name="Keyword" foreground="#569CD6" fontWeight="bold" />
              <Color name="Number" foreground="#B5CEA8" />
              <RuleSet ignoreCase="true">
                <Span color="Comment" begin="#" />
                <Span color="Comment" multiline="true" begin="&lt;#" end="#&gt;" />
                <Span color="String"><Begin>"</Begin><End>"</End><RuleSet><Span begin="`" end="." /></RuleSet></Span>
                <Span color="String"><Begin>'</Begin><End>'</End></Span>
                <Rule color="Variable">\$[A-Za-z0-9_]+</Rule>
                <Rule color="Number">\b\d+(\.\d+)?\b</Rule>
                <Keywords color="Keyword">
                  <Word>function</Word><Word>param</Word><Word>if</Word><Word>else</Word><Word>elseif</Word>
                  <Word>foreach</Word><Word>for</Word><Word>while</Word><Word>switch</Word><Word>try</Word>
                  <Word>catch</Word><Word>finally</Word><Word>return</Word><Word>throw</Word><Word>echo</Word>
                </Keywords>
              </RuleSet>
            </SyntaxDefinition>
            """;
    }
}
