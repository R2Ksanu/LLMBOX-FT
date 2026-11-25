using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace LlmodelBox.Common
{
    /// <summary>
    /// Lightweight Markdown → WPF FlowDocument renderer for chat output. Supports fenced code
    /// blocks (with language label), ATX headings, unordered/ordered lists, blockquotes, horizontal
    /// rules, and inline bold/italic/code/links. All colors resolve from the host's theme brushes so
    /// output adapts to Daylight/Slate/Midnight. Attach via MarkdownProps.Text on a RichTextBox.
    /// </summary>
    public static class MarkdownRenderer
    {
        private static Brush Brush(FrameworkElement host, string key, string fallback)
        {
            if (host?.TryFindResource(key) is SolidColorBrush b) return b;
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)); }
            catch { return Brushes.Gray; }
        }

        public static FlowDocument Build(string markdown, FrameworkElement host)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 12.5,
                Foreground = Brush(host, "BrushTextPrimary", "#fafafa"),
                Background = Brushes.Transparent
            };
            if (string.IsNullOrEmpty(markdown)) return doc;

            var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int i = 0;
            var listBuffer = new List<(string text, bool ordered)>();

            void FlushList()
            {
                if (listBuffer.Count == 0) return;
                var list = new List { MarkerStyle = listBuffer[0].ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Padding = new Thickness(18, 0, 0, 0), Margin = new Thickness(0, 2, 0, 6) };
                foreach (var (text, _) in listBuffer)
                {
                    var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                    AddInlines(p, text, host);
                    list.ListItems.Add(new ListItem(p));
                }
                doc.Blocks.Add(list);
                listBuffer.Clear();
            }
            while (i < lines.Length)
            {
                string line = lines[i];

                // Fenced code block
                if (line.TrimStart().StartsWith("```"))
                {
                    FlushList();
                    string lang = line.TrimStart().Substring(3).Trim();
                    var code = new System.Text.StringBuilder();
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                    {
                        code.AppendLine(lines[i]);
                        i++;
                    }
                    i++; // consume closing fence
                    doc.Blocks.Add(BuildCodeBlock(code.ToString().TrimEnd('\n'), lang, host));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line)) { FlushList(); i++; continue; }

                // Horizontal rule
                if (Regex.IsMatch(line.Trim(), @"^(-{3,}|\*{3,}|_{3,})$"))
                {
                    FlushList();
                    doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 6, 0, 6), BorderBrush = Brush(host, "BrushBorder", "#26263a"), BorderThickness = new Thickness(0, 0, 0, 1) });
                    i++;
                    continue;
                }

                // Heading
                var h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                if (h.Success)
                {
                    FlushList();
                    int level = h.Groups[1].Value.Length;
                    var p = new Paragraph { Margin = new Thickness(0, level <= 2 ? 8 : 5, 0, 3), FontWeight = FontWeights.Bold, FontSize = level <= 1 ? 17 : (level == 2 ? 15 : 13.5), Foreground = Brush(host, "BrushTextPrimary", "#fafafa") };
                    AddInlines(p, h.Groups[2].Value, host);
                    doc.Blocks.Add(p);
                    i++;
                    continue;
                }

                // Blockquote
                if (line.TrimStart().StartsWith(">"))
                {
                    FlushList();
                    string q = line.TrimStart().TrimStart('>').TrimStart();
                    var p = new Paragraph { Margin = new Thickness(0, 2, 0, 4), Padding = new Thickness(10, 4, 6, 4), BorderBrush = Brush(host, "BrushAccent", "#3b82f6"), BorderThickness = new Thickness(3, 0, 0, 0), Background = Brush(host, "BrushCardInnerBg", "#101013"), Foreground = Brush(host, "BrushTextSecondary", "#a1a1aa") };
                    AddInlines(p, q, host);
                    doc.Blocks.Add(p);
                    i++;
                    continue;
                }

                // Unordered list
                var ul = Regex.Match(line, @"^\s*[-*+]\s+(.*)$");
                if (ul.Success)
                {
                    if (listBuffer.Count > 0 && listBuffer[0].ordered) FlushList();
                    listBuffer.Add((ul.Groups[1].Value, false));
                    i++;
                    continue;
                }

                // Ordered list
                var ol = Regex.Match(line, @"^\s*\d+[.)]\s+(.*)$");
                if (ol.Success)
                {
                    if (listBuffer.Count > 0 && !listBuffer[0].ordered) FlushList();
                    listBuffer.Add((ol.Groups[1].Value, true));
                    i++;
                    continue;
                }

                // Paragraph
                FlushList();
                var para = new Paragraph { Margin = new Thickness(0, 1, 0, 5), LineHeight = 17 };
                AddInlines(para, line, host);
                doc.Blocks.Add(para);
                i++;
            }
            FlushList();
            return doc;
        }

        private static Block BuildCodeBlock(string code, string lang, FrameworkElement host)
        {
            var outer = new Section { Margin = new Thickness(0, 4, 0, 6) };
            if (!string.IsNullOrWhiteSpace(lang))
            {
                var label = new Paragraph(new Run(lang.ToLowerInvariant()))
                {
                    Margin = new Thickness(0),
                    Padding = new Thickness(10, 3, 10, 3),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brush(host, "BrushAccentLight", "#93c5fd"),
                    Background = Brush(host, "BrushBorderSubtle", "#161619")
                };
                outer.Blocks.Add(label);
            }
            var body = new Paragraph(new Run(code))
            {
                Margin = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 8),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = Brush(host, "BrushTextPrimary", "#e4e4e7"),
                Background = Brush(host, "BrushCardInnerBg", "#101013"),
                BorderBrush = Brush(host, "BrushBorder", "#26263a"),
                BorderThickness = new Thickness(1)
            };
            outer.Blocks.Add(body);
            return outer;
        }

        // Inline parser: **bold**, *italic* / _italic_, `code`, [text](url). Handles nesting shallowly.
        private static readonly Regex InlineRx = new(
            @"(\*\*(?<b>.+?)\*\*)|(`(?<c>[^`]+?)`)|(\*(?<i>.+?)\*)|(_(?<i2>.+?)_)|(\[(?<lt>.+?)\]\((?<lu>[^)]+)\))",
            RegexOptions.Compiled);

        private static void AddInlines(Paragraph p, string text, FrameworkElement host)
        {
            int last = 0;
            foreach (Match m in InlineRx.Matches(text))
            {
                if (m.Index > last) p.Inlines.Add(new Run(text.Substring(last, m.Index - last)));

                if (m.Groups["b"].Success)
                    p.Inlines.Add(new Bold(new Run(m.Groups["b"].Value)));
                else if (m.Groups["c"].Success)
                {
                    var run = new Run(m.Groups["c"].Value)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = Brush(host, "BrushBorderSubtle", "#161619"),
                        Foreground = Brush(host, "BrushAccentLight", "#93c5fd")
                    };
                    p.Inlines.Add(run);
                }
                else if (m.Groups["i"].Success)
                    p.Inlines.Add(new Italic(new Run(m.Groups["i"].Value)));
                else if (m.Groups["i2"].Success)
                    p.Inlines.Add(new Italic(new Run(m.Groups["i2"].Value)));
                else if (m.Groups["lt"].Success)
                {
                    try
                    {
                        if (!WebLink.TryParse(m.Groups["lu"].Value, out var uri))
                        {
                            p.Inlines.Add(new Run(m.Groups["lt"].Value));
                            last = m.Index + m.Length;
                            continue;
                        }
                        var link = new Hyperlink(new Run(m.Groups["lt"].Value)) { NavigateUri = uri, Foreground = Brush(host, "BrushAccentLight", "#93c5fd") };
                        link.RequestNavigate += (_, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true }); } catch { } };
                        p.Inlines.Add(link);
                    }
                    catch { p.Inlines.Add(new Run(m.Groups["lt"].Value)); }
                }

                last = m.Index + m.Length;
            }
            if (last < text.Length) p.Inlines.Add(new Run(text.Substring(last)));
        }
    }

    /// <summary>Attached property that renders a Markdown string into a RichTextBox as a themed FlowDocument.</summary>
    public static class MarkdownProps
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached("Text", typeof(string), typeof(MarkdownProps),
                new PropertyMetadata(null, OnTextChanged));

        public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);
        public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RichTextBox rtb)
                rtb.Document = MarkdownRenderer.Build(e.NewValue as string ?? string.Empty, rtb);
        }
    }
}
