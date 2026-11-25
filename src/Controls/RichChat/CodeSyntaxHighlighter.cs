using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LlmodelBox.Controls.RichChat
{
    /// <summary>
    /// Fast regex-based syntax highlighter supporting C#, Python, JS/TS, Java, Go, Rust,
    /// C/C++, Kotlin, PHP, HTML, XML, CSS, JSON, YAML, SQL, Bash, and PowerShell.
    /// </summary>
    public static class CodeSyntaxHighlighter
    {
        private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "abstract","as","async","await","base","bool","break","byte","case","catch","char","checked",
            "class","const","continue","decimal","default","delegate","do","double","else","enum","event",
            "explicit","extern","false","finally","fixed","float","for","foreach","from","fun","function",
            "get","global","go","goto","if","implements","import","in","init","int","interface","internal",
            "is","let","lock","long","match","module","mut","namespace","new","null","none","nil","object",
            "operator","out","override","package","params","partial","private","protected","pub","public",
            "readonly","record","ref","return","sbyte","sealed","select","self","set","short","sizeof",
            "static","string","struct","super","switch","this","throw","true","try","type","typeof","uint",
            "ulong","unchecked","unsafe","ushort","using","val","var","virtual","void","volatile","where",
            "while","with","yield","def","elif","except","lambda","pass","raise","and","or","not","fn",
            "impl","trait","crate","use","mod","export","extends","declare","readonly","keyof","never",
            "select","insert","update","delete","create","alter","drop","join","left","right","inner",
            "outer","group","order","by","having","limit","offset","values","into","set","and","or",
            "echo","fi","then","elif","esac","done","local","export","param","begin","process","end"
        };

        private static readonly Regex TokenRx = new(
            @"(?<comment>//[^\n]*|#[^\n]*|/\*[\s\S]*?\*/|<!--[\s\S]*?-->)" +
            @"|(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|`(?:\\.|[^`\\])*`)" +
            @"|(?<tag></?[A-Za-z_][A-Za-z0-9:.-]*)" +
            @"|(?<number>\b0x[0-9a-fA-F]+\b|\b\d+(?:\.\d+)?\b)" +
            @"|(?<word>\b[A-Za-z_$][A-Za-z0-9_$-]*\b)",
            RegexOptions.Compiled);

        public static void AppendHighlightedLine(Paragraph target, string line, string? language, bool isLightTheme)
        {
            if (string.IsNullOrEmpty(line)) return;

            var kwBrush = MakeBrush(isLightTheme ? "#7c3aed" : "#c084fc");
            var strBrush = MakeBrush(isLightTheme ? "#059669" : "#86efac");
            var cmtBrush = MakeBrush(isLightTheme ? "#6b7280" : "#6b7280");
            var numBrush = MakeBrush(isLightTheme ? "#d97706" : "#fbbf24");
            var typeBrush = MakeBrush(isLightTheme ? "#0284c7" : "#7dd3fc");
            var defBrush = MakeBrush(isLightTheme ? "#18181b" : "#e4e4e7");

            string lang = (language ?? string.Empty).Trim().ToLowerInvariant();
            bool hashComment = lang is "python" or "py" or "bash" or "sh" or "zsh" or "yaml" or "yml" or "powershell" or "ps1" or "toml";

            int last = 0;
            foreach (Match m in TokenRx.Matches(line))
            {
                if (m.Index > last)
                {
                    target.Inlines.Add(new Run(line.Substring(last, m.Index - last)) { Foreground = defBrush });
                }

                string val = m.Value;
                if (m.Groups["comment"].Success)
                {
                    if (val.StartsWith("#") && !hashComment && lang is "c" or "cpp" or "csharp" or "cs")
                    {
                        target.Inlines.Add(new Run(val) { Foreground = kwBrush }); // preprocessor
                    }
                    else
                    {
                        target.Inlines.Add(new Run(val) { Foreground = cmtBrush, FontStyle = FontStyles.Italic });
                    }
                }
                else if (m.Groups["string"].Success)
                {
                    target.Inlines.Add(new Run(val) { Foreground = strBrush });
                }
                else if (m.Groups["tag"].Success)
                {
                    target.Inlines.Add(new Run(val) { Foreground = kwBrush });
                }
                else if (m.Groups["number"].Success)
                {
                    target.Inlines.Add(new Run(val) { Foreground = numBrush });
                }
                else if (m.Groups["word"].Success)
                {
                    if (Keywords.Contains(val))
                    {
                        target.Inlines.Add(new Run(val) { Foreground = kwBrush, FontWeight = FontWeights.SemiBold });
                    }
                    else if (char.IsUpper(val[0]) && val.Length > 1)
                    {
                        target.Inlines.Add(new Run(val) { Foreground = typeBrush });
                    }
                    else
                    {
                        target.Inlines.Add(new Run(val) { Foreground = defBrush });
                    }
                }

                last = m.Index + m.Length;
            }

            if (last < line.Length)
            {
                target.Inlines.Add(new Run(line.Substring(last)) { Foreground = defBrush });
            }
        }

        private static SolidColorBrush MakeBrush(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
