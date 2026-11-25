using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LlmodelBox.Models;

namespace LlmodelBox.Controls.RichChat
{
    /// <summary>
    /// Visual WPF Canvas renderer for Mermaid diagrams:
    /// Flowchart/Graph, Sequence, Class, State, ER, Gantt, Timeline, and User Journey.
    /// Renders diagrams visually (nodes, edges, lifelines, bars) rather than raw text.
    /// </summary>
    public static class MermaidRenderer
    {
        public static UIElement Build(MermaidBlock block, FrameworkElement host)
        {
            var textPrimary = ResolveBrush(host, "BrushTextPrimary", "#fafafa");
            var textMuted = ResolveBrush(host, "BrushTextMuted", "#a1a1aa");
            var cardBg = ResolveBrush(host, "BrushCardBg", "#0c0e14");
            var border = ResolveBrush(host, "BrushBorder", "#262f40");
            var accent = ResolveBrush(host, "BrushAccentLight", "#60a5fa");
            var accentFill = ResolveBrush(host, "BrushAccentGlow", "#172554");

            string kind = (block.DiagramType ?? "flowchart").Trim().ToLowerInvariant();
            string src = (block.Source ?? string.Empty).Replace("\r\n", "\n").Trim();

            Canvas canvas = kind switch
            {
                "sequencediagram" => RenderSequence(src, textPrimary, textMuted, cardBg, border, accent),
                "gantt" => RenderGantt(src, textPrimary, textMuted, cardBg, border, accent, accentFill),
                "timeline" or "journey" => RenderTimelineOrJourney(src, textPrimary, textMuted, cardBg, border, accent, accentFill),
                "classdiagram" or "erdiagram" => RenderBoxGraph(src, kind == "erdiagram" ? "ER" : "CLASS", textPrimary, textMuted, cardBg, border, accent),
                _ => RenderFlowOrState(src, textPrimary, textMuted, cardBg, border, accent, accentFill)
            };

            return new Viewbox
            {
                MaxHeight = 340,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = canvas
            };
        }

        private static Canvas RenderFlowOrState(string src, Brush textPrimary, Brush textMuted, Brush cardBg, Brush border, Brush accent, Brush accentFill)
        {
            var nodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var edges = new List<(string From, string To, string Label)>();
            bool horizontal = src.Contains(" LR", StringComparison.OrdinalIgnoreCase) || src.Contains(" RL", StringComparison.OrdinalIgnoreCase);

            var edgeRx = new Regex(@"^\s*([A-Za-z0-9_.*]+)(?:\[([^\]]+)\]|\(([^)]+)\)|\{([^}]+)\})?\s*--+(?:>|\|([^|]+)\|)?-*>?\s*([A-Za-z0-9_.*]+)(?:\[([^\]]+)\]|\(([^)]+)\)|\{([^}]+)\})?", RegexOptions.Compiled);
            var stateRx = new Regex(@"^\s*([A-Za-z0-9_.*]+)\s*-->\s*([A-Za-z0-9_.*]+)\s*(?::\s*(.*))?$", RegexOptions.Compiled);

            foreach (var raw in src.Split('\n'))
            {
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("%%") || line.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("graph", StringComparison.OrdinalIgnoreCase) || line.StartsWith("statediagram", StringComparison.OrdinalIgnoreCase))
                    continue;

                var sm = stateRx.Match(line);
                if (sm.Success)
                {
                    string a = sm.Groups[1].Value, b = sm.Groups[2].Value, lbl = sm.Groups[3].Value.Trim();
                    nodes.TryAdd(a, a == "[*]" ? "Start/End" : a);
                    nodes.TryAdd(b, b == "[*]" ? "Start/End" : b);
                    edges.Add((a, b, lbl));
                    continue;
                }

                var em = edgeRx.Match(line);
                if (em.Success)
                {
                    string a = em.Groups[1].Value;
                    string aLbl = FirstNonEmpty(em.Groups[2].Value, em.Groups[3].Value, em.Groups[4].Value, a);
                    string edgeLbl = em.Groups[5].Value.Trim();
                    string b = em.Groups[6].Value;
                    string bLbl = FirstNonEmpty(em.Groups[7].Value, em.Groups[8].Value, em.Groups[9].Value, b);
                    if (!nodes.ContainsKey(a) || aLbl != a) nodes[a] = aLbl;
                    if (!nodes.ContainsKey(b) || bLbl != b) nodes[b] = bLbl;
                    edges.Add((a, b, edgeLbl));
                }
            }

            if (nodes.Count == 0) nodes["node1"] = "Diagram";

            var nodeKeys = nodes.Keys.ToList();
            var pos = new Dictionary<string, Point>();
            const double boxW = 140, boxH = 42, gapX = 56, gapY = 48;

            int cols = horizontal ? Math.Max(2, (int)Math.Ceiling(Math.Sqrt(nodeKeys.Count))) : Math.Min(3, nodeKeys.Count);
            for (int i = 0; i < nodeKeys.Count; i++)
            {
                int col = horizontal ? i % cols : i % cols;
                int row = horizontal ? i / cols : i / cols;
                double x = 20 + col * (boxW + gapX);
                double y = 20 + row * (boxH + gapY);
                pos[nodeKeys[i]] = new Point(x, y);
            }

            double maxX = pos.Values.Max(p => p.X) + boxW + 30;
            double maxY = pos.Values.Max(p => p.Y) + boxH + 30;
            var canvas = new Canvas { Width = Math.Max(360, maxX), Height = Math.Max(140, maxY) };

            // Draw edges
            foreach (var (from, to, label) in edges)
            {
                if (!pos.TryGetValue(from, out var p1) || !pos.TryGetValue(to, out var p2)) continue;
                var start = new Point(p1.X + boxW * 0.5, p1.Y + boxH * 0.5);
                var end = new Point(p2.X + boxW * 0.5, p2.Y + boxH * 0.5);
                canvas.Children.Add(new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = accent, StrokeThickness = 1.6 });

                // Arrowhead midpoint
                double mx = (start.X + end.X) * 0.5, my = (start.Y + end.Y) * 0.5;
                var arrow = new Ellipse { Width = 6, Height = 6, Fill = accent };
                Canvas.SetLeft(arrow, mx - 3);
                Canvas.SetTop(arrow, my - 3);
                canvas.Children.Add(arrow);

                if (!string.IsNullOrWhiteSpace(label))
                {
                    var tag = new Border
                    {
                        Background = cardBg,
                        BorderBrush = border,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(4, 1, 4, 1),
                        Child = new TextBlock { Text = label, FontSize = 9, Foreground = textMuted }
                    };
                    Canvas.SetLeft(tag, mx - 20);
                    Canvas.SetTop(tag, my - 10);
                    canvas.Children.Add(tag);
                }
            }

            // Draw nodes on top
            foreach (var k in nodeKeys)
            {
                var p = pos[k];
                var b = new Border
                {
                    Width = boxW,
                    Height = boxH,
                    Background = accentFill,
                    BorderBrush = accent,
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = new TextBlock
                    {
                        Text = nodes[k],
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = textPrimary,
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center
                    }
                };
                Canvas.SetLeft(b, p.X);
                Canvas.SetTop(b, p.Y);
                canvas.Children.Add(b);
            }

            return canvas;
        }

        private static Canvas RenderSequence(string src, Brush textPrimary, Brush textMuted, Brush cardBg, Brush border, Brush accent)
        {
            var actors = new List<string>();
            var msgs = new List<(string From, string To, string Msg)>();
            var msgRx = new Regex(@"^\s*([A-Za-z0-9_ ]+?)\s*-+>>?\s*([A-Za-z0-9_ ]+?)\s*:\s*(.+)$", RegexOptions.Compiled);

            foreach (var raw in src.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("participant ", StringComparison.OrdinalIgnoreCase))
                {
                    string a = line.Substring(12).Trim();
                    if (!actors.Contains(a)) actors.Add(a);
                }
                var m = msgRx.Match(line);
                if (m.Success)
                {
                    string f = m.Groups[1].Value.Trim(), t = m.Groups[2].Value.Trim(), txt = m.Groups[3].Value.Trim();
                    if (!actors.Contains(f)) actors.Add(f);
                    if (!actors.Contains(t)) actors.Add(t);
                    msgs.Add((f, t, txt));
                }
            }

            if (actors.Count == 0) actors.Add("Participant");
            const double colW = 150, rowH = 38;
            double w = Math.Max(380, actors.Count * colW + 40);
            double h = Math.Max(160, 80 + msgs.Count * rowH + 20);
            var canvas = new Canvas { Width = w, Height = h };

            var xPos = new Dictionary<string, double>();
            for (int i = 0; i < actors.Count; i++)
            {
                double x = 20 + i * colW + colW * 0.5;
                xPos[actors[i]] = x;

                var box = new Border
                {
                    Width = 110,
                    Height = 30,
                    Background = cardBg,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = new TextBlock { Text = actors[i], FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = textPrimary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                Canvas.SetLeft(box, x - 55);
                Canvas.SetTop(box, 14);
                canvas.Children.Add(box);

                canvas.Children.Add(new Line { X1 = x, Y1 = 44, X2 = x, Y2 = h - 14, Stroke = border, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 } });
            }

            for (int i = 0; i < msgs.Count; i++)
            {
                var (f, t, txt) = msgs[i];
                double y = 72 + i * rowH;
                double x1 = xPos[f], x2 = xPos[t];
                canvas.Children.Add(new Line { X1 = x1, Y1 = y, X2 = x2, Y2 = y, Stroke = accent, StrokeThickness = 1.6 });

                var lbl = new TextBlock { Text = txt, FontSize = 10, Foreground = textMuted };
                Canvas.SetLeft(lbl, Math.Min(x1, x2) + 8);
                Canvas.SetTop(lbl, y - 16);
                canvas.Children.Add(lbl);
            }
            return canvas;
        }

        private static Canvas RenderGantt(string src, Brush textPrimary, Brush textMuted, Brush cardBg, Brush border, Brush accent, Brush accentFill)
        {
            var tasks = new List<string>();
            foreach (var raw in src.Split('\n'))
            {
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("gantt", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("title", StringComparison.OrdinalIgnoreCase) || line.StartsWith("dateFormat", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("section", StringComparison.OrdinalIgnoreCase)) continue;
                int colon = line.IndexOf(':');
                tasks.Add(colon > 0 ? line.Substring(0, colon).Trim() : line);
            }
            if (tasks.Count == 0) tasks.Add("Task 1");

            double h = Math.Max(140, 30 + tasks.Count * 32);
            var canvas = new Canvas { Width = 520, Height = h };
            double barArea = 320;
            double slice = barArea / Math.Max(1, tasks.Count);

            for (int i = 0; i < tasks.Count; i++)
            {
                double y = 18 + i * 30;
                var lbl = new TextBlock { Text = tasks[i], FontSize = 10.5, Foreground = textPrimary, Width = 150, TextTrimming = TextTrimming.CharacterEllipsis };
                Canvas.SetLeft(lbl, 10);
                Canvas.SetTop(lbl, y + 3);
                canvas.Children.Add(lbl);

                var bar = new Border
                {
                    Width = Math.Max(42, slice * 1.15),
                    Height = 20,
                    Background = accentFill,
                    BorderBrush = accent,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4)
                };
                Canvas.SetLeft(bar, 170 + i * (slice * 0.78));
                Canvas.SetTop(bar, y);
                canvas.Children.Add(bar);
            }
            return canvas;
        }

        private static Canvas RenderTimelineOrJourney(string src, Brush textPrimary, Brush textMuted, Brush cardBg, Brush border, Brush accent, Brush accentFill)
        {
            var items = src.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("timeline", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("journey", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("title", StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .ToList();
            if (items.Count == 0) items.Add("Step 1");

            double w = Math.Max(420, items.Count * 145 + 30);
            var canvas = new Canvas { Width = w, Height = 140 };
            canvas.Children.Add(new Line { X1 = 30, Y1 = 70, X2 = w - 30, Y2 = 70, Stroke = border, StrokeThickness = 2 });

            for (int i = 0; i < items.Count; i++)
            {
                double x = 40 + i * 140;
                var dot = new Ellipse { Width = 12, Height = 12, Fill = accent };
                Canvas.SetLeft(dot, x + 50);
                Canvas.SetTop(dot, 64);
                canvas.Children.Add(dot);

                var card = new Border
                {
                    Width = 120,
                    Background = cardBg,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 4, 6, 4),
                    Child = new TextBlock { Text = items[i], FontSize = 10, Foreground = textPrimary, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40 }
                };
                Canvas.SetLeft(card, x);
                Canvas.SetTop(card, i % 2 == 0 ? 12 : 84);
                canvas.Children.Add(card);
            }
            return canvas;
        }

        private static Canvas RenderBoxGraph(string src, string badge, Brush textPrimary, Brush textMuted, Brush cardBg, Brush border, Brush accent)
        {
            var boxes = new List<string>();
            foreach (var raw in src.Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith("class ", StringComparison.OrdinalIgnoreCase))
                    boxes.Add(l.Substring(6).Trim('{', ' '));
                else if (l.Contains("{") && !l.StartsWith("%%"))
                    boxes.Add(l.Split('{')[0].Trim());
            }
            if (boxes.Count == 0) boxes.Add(badge);

            int cols = Math.Min(3, boxes.Count);
            double w = Math.Max(360, cols * 165 + 30);
            double h = Math.Max(140, ((boxes.Count + cols - 1) / cols) * 80 + 30);
            var canvas = new Canvas { Width = w, Height = h };

            for (int i = 0; i < boxes.Count; i++)
            {
                double x = 20 + (i % cols) * 165;
                double y = 20 + (i / cols) * 80;
                var b = new Border
                {
                    Width = 145,
                    Height = 56,
                    Background = cardBg,
                    BorderBrush = accent,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8),
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = badge, FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = accent },
                            new TextBlock { Text = boxes[i], FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = textPrimary, TextTrimming = TextTrimming.CharacterEllipsis }
                        }
                    }
                };
                Canvas.SetLeft(b, x);
                Canvas.SetTop(b, y);
                canvas.Children.Add(b);
            }
            return canvas;
        }

        private static string FirstNonEmpty(params string[] values)
            => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

        private static Brush ResolveBrush(FrameworkElement host, string key, string fallback)
        {
            if (host?.TryFindResource(key) is Brush b) return b;
            try
            {
                var sb = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
                sb.Freeze();
                return sb;
            }
            catch { return Brushes.White; }
        }
    }
}
