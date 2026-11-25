using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LlmodelBox.Common;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LlmodelBox.Controls.RichChat
{
    /// <summary>
    /// Full GitHub Flavored Markdown (Markdig AST) -> WPF FlowDocument renderer.
    /// Supports H1-H6, bold/italic/strikethrough/underline, inline code, horizontal rules,
    /// nested ordered/unordered lists, task checklists, tables, nested blockquotes, footnotes,
    /// autolinks, sanitized HTTP/HTTPS links, and safe images. Never executes raw model HTML.
    /// </summary>
    public static class RichMarkdownView
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseEmphasisExtras()
            .UseTaskLists()
            .UseAutoLinks()
            .Build();

        private static Brush Brush(FrameworkElement host, string key, string fallback)
        {
            if (host?.TryFindResource(key) is Brush b) return b;
            try
            {
                var sb = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
                sb.Freeze();
                return sb;
            }
            catch { return Brushes.Gray; }
        }

        public static FlowDocument Build(string? markdown, FrameworkElement host, bool showCursor = false)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new FontFamily("Segoe UI, Consolas, sans-serif"),
                FontSize = 13,
                Foreground = Brush(host, "BrushTextPrimary", "#fafafa"),
                Background = Brushes.Transparent
            };

            string text = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
            if (string.IsNullOrEmpty(text))
            {
                if (showCursor)
                {
                    var p = new Paragraph { Margin = new Thickness(0) };
                    p.Inlines.Add(new Run("▌") { Foreground = Brush(host, "BrushAccentLight", "#60a5fa") });
                    doc.Blocks.Add(p);
                }
                return doc;
            }

            try
            {
                var parsed = Markdown.Parse(text, Pipeline);
                foreach (var block in parsed)
                {
                    RenderBlock(block, doc.Blocks, host, quoteDepth: 0);
                }

                if (showCursor)
                {
                    if (doc.Blocks.LastBlock is Paragraph lastPara)
                    {
                        lastPara.Inlines.Add(new Run(" ▌") { Foreground = Brush(host, "BrushAccentLight", "#60a5fa") });
                    }
                    else
                    {
                        var p = new Paragraph { Margin = new Thickness(0, 2, 0, 0) };
                        p.Inlines.Add(new Run("▌") { Foreground = Brush(host, "BrushAccentLight", "#60a5fa") });
                        doc.Blocks.Add(p);
                    }
                }
            }
            catch
            {
                doc.Blocks.Clear();
                doc.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0) });
            }

            return doc;
        }

        private static void RenderBlock(Markdig.Syntax.Block block, BlockCollection target, FrameworkElement host, int quoteDepth)
        {
            switch (block)
            {
                case HeadingBlock h:
                {
                    double size = h.Level switch { 1 => 19, 2 => 16.5, 3 => 14.5, 4 => 13.5, _ => 13 };
                    var p = new Paragraph
                    {
                        FontSize = size,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brush(host, "BrushTextPrimary", "#fafafa"),
                        Margin = new Thickness(0, h.Level <= 2 ? 10 : 6, 0, 4)
                    };
                    if (h.Inline != null) AddInlines(p.Inlines, h.Inline, host);
                    target.Add(p);
                    break;
                }

                case ThematicBreakBlock:
                {
                    target.Add(new Paragraph
                    {
                        Margin = new Thickness(0, 8, 0, 8),
                        BorderBrush = Brush(host, "BrushBorder", "#26263a"),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        FontSize = 2
                    });
                    break;
                }

                case QuoteBlock q:
                {
                    var sec = new Section
                    {
                        Margin = new Thickness(quoteDepth > 0 ? 10 : 0, 4, 0, 6),
                        Padding = new Thickness(12, 6, 8, 6),
                        BorderBrush = Brush(host, "BrushAccent", "#3b82f6"),
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Background = Brush(host, "BrushCardInnerBg", "#101013"),
                        Foreground = Brush(host, "BrushTextSecondary", "#a1a1aa")
                    };
                    foreach (var child in q) RenderBlock(child, sec.Blocks, host, quoteDepth + 1);
                    target.Add(sec);
                    break;
                }

                case ListBlock list:
                {
                    var wpList = new List
                    {
                        MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Padding = new Thickness(18, 0, 0, 0),
                        Margin = new Thickness(0, 2, 0, 6)
                    };
                    foreach (var itemObj in list)
                    {
                        if (itemObj is not ListItemBlock li) continue;
                        var wpItem = new ListItem();
                        foreach (var child in li) RenderBlock(child, wpItem.Blocks, host, quoteDepth);
                        wpList.ListItems.Add(wpItem);
                    }
                    target.Add(wpList);
                    break;
                }

                case Markdig.Extensions.Tables.Table mdTable:
                {
                    var wpTable = new System.Windows.Documents.Table
                    {
                        CellSpacing = 0,
                        BorderBrush = Brush(host, "BrushBorder", "#232b3b"),
                        BorderThickness = new Thickness(1),
                        Margin = new Thickness(0, 6, 0, 8)
                    };
                    var rg = new TableRowGroup();
                    foreach (var rowObj in mdTable)
                    {
                        if (rowObj is not Markdig.Extensions.Tables.TableRow mdRow) continue;
                        var wpRow = new System.Windows.Documents.TableRow();
                        if (mdRow.IsHeader) wpRow.Background = Brush(host, "BrushCardInnerBg", "#151923");
                        foreach (var cellObj in mdRow)
                        {
                            if (cellObj is not Markdig.Extensions.Tables.TableCell mdCell) continue;
                            var wpCell = new System.Windows.Documents.TableCell
                            {
                                BorderBrush = Brush(host, "BrushBorderSubtle", "#1f2430"),
                                BorderThickness = new Thickness(0, 0, 1, 1),
                                Padding = new Thickness(8, 5, 8, 5)
                            };
                            foreach (var child in mdCell) RenderBlock(child, wpCell.Blocks, host, quoteDepth);
                            wpRow.Cells.Add(wpCell);
                        }
                        rg.Rows.Add(wpRow);
                    }
                    wpTable.RowGroups.Add(rg);
                    target.Add(wpTable);
                    break;
                }

                case CodeBlock code:
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < code.Lines.Count; i++)
                    {
                        if (i > 0) sb.Append('\n');
                        sb.Append(code.Lines.Lines[i].Slice.ToString());
                    }
                    var p = new Paragraph(new Run(sb.ToString()))
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 4, 0, 6),
                        Background = Brush(host, "BrushCardInnerBg", "#101013"),
                        BorderBrush = Brush(host, "BrushBorder", "#232b3b"),
                        BorderThickness = new Thickness(1)
                    };
                    target.Add(p);
                    break;
                }

                case FootnoteGroup fg:
                {
                    var sec = new Section
                    {
                        Margin = new Thickness(0, 8, 0, 2),
                        Padding = new Thickness(0, 6, 0, 0),
                        BorderBrush = Brush(host, "BrushBorderSubtle", "#1f2430"),
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        FontSize = 11.5,
                        Foreground = Brush(host, "BrushTextMuted", "#71717a")
                    };
                    foreach (var fnObj in fg)
                    {
                        if (fnObj is Footnote fn)
                        {
                            foreach (var child in fn) RenderBlock(child, sec.Blocks, host, quoteDepth);
                        }
                    }
                    target.Add(sec);
                    break;
                }

                case HtmlBlock:
                    // Do not interpret raw model-generated HTML as trusted executable content.
                    break;

                case ParagraphBlock pb:
                {
                    var p = new Paragraph { Margin = new Thickness(0, 2, 0, 6), LineHeight = 19 };
                    if (pb.Inline != null) AddInlines(p.Inlines, pb.Inline, host);
                    target.Add(p);
                    break;
                }

                case ContainerBlock container:
                {
                    foreach (var child in container) RenderBlock(child, target, host, quoteDepth);
                    break;
                }
            }
        }

        private static void AddInlines(InlineCollection target, ContainerInline container, FrameworkElement host)
        {
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline lit:
                        target.Add(new Run(lit.Content.ToString()));
                        break;

                    case LineBreakInline lb:
                        target.Add(lb.IsHard ? new LineBreak() : new Run(" "));
                        break;

                    case CodeInline code:
                        target.Add(new Run($" {code.Content} ")
                        {
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 12,
                            Background = Brush(host, "BrushCardInnerBg", "#18181b"),
                            Foreground = Brush(host, "BrushAccentLight", "#93c5fd")
                        });
                        break;

                    case TaskList tl:
                        target.Add(new Run(tl.Checked ? "[x] " : "[ ] ")
                        {
                            FontFamily = new FontFamily("Consolas"),
                            FontWeight = FontWeights.Bold,
                            Foreground = tl.Checked ? Brush(host, "BrushSuccess", "#22c55e") : Brush(host, "BrushTextMuted", "#71717a")
                        });
                        break;

                    case EmphasisInline em:
                    {
                        Span span;
                        if (em.DelimiterChar == '~' && em.DelimiterCount == 2)
                        {
                            span = new Span();
                            span.TextDecorations = TextDecorations.Strikethrough;
                        }
                        else if (em.DelimiterChar == '+' && em.DelimiterCount == 2)
                        {
                            span = new Underline();
                        }
                        else if (em.DelimiterCount >= 3)
                        {
                            span = new Bold(new Italic());
                        }
                        else if (em.DelimiterCount == 2)
                        {
                            span = new Bold();
                        }
                        else
                        {
                            span = new Italic();
                        }
                        AddInlines(span.Inlines, em, host);
                        target.Add(span);
                        break;
                    }

                    case AutolinkInline auto:
                    {
                        AddSafeHyperlink(target, auto.Url, auto.Url, host);
                        break;
                    }

                    case LinkInline link:
                    {
                        if (link.IsImage)
                        {
                            if (WebLink.TryParse(link.Url, out var imgUri))
                            {
                                try
                                {
                                    var img = new Image { MaxWidth = 420, MaxHeight = 260, Stretch = Stretch.Uniform };
                                    var bmp = new BitmapImage();
                                    bmp.BeginInit();
                                    bmp.UriSource = imgUri;
                                    bmp.EndInit();
                                    img.Source = bmp;
                                    target.Add(new InlineUIContainer(img));
                                }
                                catch { target.Add(new Run($"[Image: {link.Url}]")); }
                            }
                            else
                            {
                                target.Add(new Run($"[Image: {link.Url}]"));
                            }
                        }
                        else
                        {
                            var label = new Span();
                            AddInlines(label.Inlines, link, host);
                            if (WebLink.TryParse(link.Url, out var uri))
                            {
                                var hl = new Hyperlink(label)
                                {
                                    NavigateUri = uri,
                                    Foreground = Brush(host, "BrushAccentLight", "#93c5fd")
                                };
                                hl.RequestNavigate += (_, _) =>
                                {
                                    try { Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); } catch { }
                                };
                                target.Add(hl);
                            }
                            else
                            {
                                target.Add(label);
                            }
                        }
                        break;
                    }

                    case FootnoteLink fl:
                        target.Add(new Run($"[{fl.Index}]")
                        {
                            BaselineAlignment = BaselineAlignment.Superscript,
                            FontSize = 10,
                            Foreground = Brush(host, "BrushAccentLight", "#93c5fd")
                        });
                        break;

                    case HtmlInline:
                        break;

                    case ContainerInline nested:
                        AddInlines(target, nested, host);
                        break;
                }
            }
        }

        private static void AddSafeHyperlink(InlineCollection target, string label, string? url, FrameworkElement host)
        {
            if (WebLink.TryParse(url, out var uri))
            {
                var hl = new Hyperlink(new Run(label))
                {
                    NavigateUri = uri,
                    Foreground = Brush(host, "BrushAccentLight", "#93c5fd")
                };
                hl.RequestNavigate += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); } catch { }
                };
                target.Add(hl);
            }
            else
            {
                target.Add(new Run(label));
            }
        }
    }
}
