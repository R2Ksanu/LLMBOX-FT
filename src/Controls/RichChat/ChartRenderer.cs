using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LlmodelBox.Models;

namespace LlmodelBox.Controls.RichChat
{
    /// <summary>
    /// Pure WPF vector Chart Renderer supporting Line, Bar, HorizontalBar, Pie, Donut,
    /// Area, and Scatter charts from structured ChartBlock data.
    /// </summary>
    public static class ChartRenderer
    {
        private static readonly string[] PaletteHex =
        {
            "#3b82f6", "#22c55e", "#f59e0b", "#a855f7", "#f43f5e", "#06b6d4", "#eab308"
        };

        public static UIElement Build(ChartBlock chart, FrameworkElement host)
        {
            var outer = new StackPanel();
            var textPrimary = ResolveBrush(host, "BrushTextPrimary", "#fafafa");
            var textMuted = ResolveBrush(host, "BrushTextMuted", "#71717a");
            var gridLine = ResolveBrush(host, "BrushBorderSubtle", "#1f2430");

            if (!string.IsNullOrWhiteSpace(chart.Title))
            {
                outer.Children.Add(new TextBlock
                {
                    Text = chart.Title,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = textPrimary,
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }

            if (chart.Series.Count == 0 || chart.Series.All(s => s.Points.Count == 0))
            {
                outer.Children.Add(new TextBlock { Text = "No chart data.", Foreground = textMuted, FontSize = 11.5 });
                return outer;
            }

            const double w = 520, h = 230;
            var canvas = new Canvas { Width = w, Height = h, ClipToBounds = true };

            if (chart.Kind is ChartKind.Pie or ChartKind.Donut)
            {
                RenderPieOrDonut(canvas, chart, w, h, textPrimary, textMuted);
            }
            else if (chart.Kind == ChartKind.HorizontalBar)
            {
                RenderHorizontalBars(canvas, chart, w, h, textPrimary, textMuted, gridLine);
            }
            else
            {
                RenderCartesian(canvas, chart, w, h, textPrimary, textMuted, gridLine);
            }

            outer.Children.Add(new Viewbox { MaxHeight = 250, Stretch = Stretch.Uniform, Child = canvas });

            // Legend
            if (chart.Series.Count > 1 || chart.Kind is ChartKind.Pie or ChartKind.Donut)
            {
                var legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                if (chart.Kind is ChartKind.Pie or ChartKind.Donut)
                {
                    var pts = chart.Series[0].Points;
                    for (int i = 0; i < pts.Count; i++)
                    {
                        legend.Children.Add(LegendChip(pts[i].Label, SeriesBrush(string.Empty, i), textMuted));
                    }
                }
                else
                {
                    for (int i = 0; i < chart.Series.Count; i++)
                    {
                        legend.Children.Add(LegendChip(chart.Series[i].Name, SeriesBrush(chart.Series[i].ColorHex, i), textMuted));
                    }
                }
                outer.Children.Add(legend);
            }

            return outer;
        }

        private static UIElement LegendChip(string label, Brush color, Brush textBrush)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 4) };
            sp.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = color, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = textBrush, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        private static void RenderPieOrDonut(Canvas canvas, ChartBlock chart, double w, double h, Brush textPrimary, Brush textMuted)
        {
            var pts = chart.Series[0].Points;
            double total = pts.Sum(p => Math.Max(0, p.Y));
            if (total <= 0) return;

            double cx = w * 0.5, cy = h * 0.5;
            double radius = Math.Min(w, h) * 0.38;
            double innerRadius = chart.Kind == ChartKind.Donut ? radius * 0.55 : 0;

            double angle = -90.0;
            for (int i = 0; i < pts.Count; i++)
            {
                double val = Math.Max(0, pts[i].Y);
                if (val <= 0) continue;
                double sweep = (val / total) * 360.0;
                if (sweep >= 359.9) sweep = 359.9;

                var path = BuildSlice(cx, cy, radius, innerRadius, angle, sweep, SeriesBrush(string.Empty, i));
                path.ToolTip = $"{pts[i].Label}: {val:0.##} ({val / total:P1})";
                canvas.Children.Add(path);

                double midRad = (angle + sweep * 0.5) * Math.PI / 180.0;
                double lx = cx + (radius + 18) * Math.Cos(midRad);
                double ly = cy + (radius + 18) * Math.Sin(midRad);
                var lbl = new TextBlock
                {
                    Text = $"{pts[i].Label} ({val / total:P0})",
                    FontSize = 9.5,
                    Foreground = textMuted
                };
                Canvas.SetLeft(lbl, lx - 24);
                Canvas.SetTop(lbl, ly - 7);
                canvas.Children.Add(lbl);

                angle += sweep;
            }

            if (chart.Kind == ChartKind.Donut)
            {
                var centerTxt = new TextBlock
                {
                    Text = $"{total:0.##}",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = textPrimary
                };
                Canvas.SetLeft(centerTxt, cx - 18);
                Canvas.SetTop(centerTxt, cy - 10);
                canvas.Children.Add(centerTxt);
            }
        }

        private static Path BuildSlice(double cx, double cy, double r, double ir, double startDeg, double sweepDeg, Brush fill)
        {
            double a1 = startDeg * Math.PI / 180.0;
            double a2 = (startDeg + sweepDeg) * Math.PI / 180.0;
            bool large = sweepDeg > 180.0;

            var p1 = new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));
            var p2 = new Point(cx + r * Math.Cos(a2), cy + r * Math.Sin(a2));

            var fig = new PathFigure { StartPoint = ir > 0 ? new Point(cx + ir * Math.Cos(a1), cy + ir * Math.Sin(a1)) : new Point(cx, cy), IsClosed = true };
            fig.Segments.Add(new LineSegment(p1, true));
            fig.Segments.Add(new ArcSegment(p2, new Size(r, r), 0, large, SweepDirection.Clockwise, true));
            if (ir > 0)
            {
                var p3 = new Point(cx + ir * Math.Cos(a2), cy + ir * Math.Sin(a2));
                var p4 = new Point(cx + ir * Math.Cos(a1), cy + ir * Math.Sin(a1));
                fig.Segments.Add(new LineSegment(p3, true));
                fig.Segments.Add(new ArcSegment(p4, new Size(ir, ir), 0, large, SweepDirection.Counterclockwise, true));
            }
            return new Path { Fill = fill, Data = new PathGeometry(new[] { fig }) };
        }

        private static void RenderHorizontalBars(Canvas canvas, ChartBlock chart, double w, double h, Brush textPrimary, Brush textMuted, Brush gridLine)
        {
            const double padL = 96, padR = 32, padT = 12, padB = 20;
            double plotW = w - padL - padR, plotH = h - padT - padB;
            var pts = chart.Series[0].Points;
            double maxVal = Math.Max(1e-6, pts.Max(p => p.Y));
            double rowH = plotH / Math.Max(1, pts.Count);
            double barH = Math.Min(22, rowH * 0.62);

            for (int i = 0; i < pts.Count; i++)
            {
                double y = padT + i * rowH + (rowH - barH) * 0.5;
                double bw = Math.Max(2, (pts[i].Y / maxVal) * plotW);

                var lbl = new TextBlock { Text = pts[i].Label, FontSize = 10, Foreground = textMuted, Width = padL - 8, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Right };
                Canvas.SetLeft(lbl, 0);
                Canvas.SetTop(lbl, y + (barH - 13) * 0.5);
                canvas.Children.Add(lbl);

                var rect = new Rectangle
                {
                    Width = bw,
                    Height = barH,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = SeriesBrush(chart.Series[0].ColorHex, i),
                    ToolTip = $"{pts[i].Label}: {pts[i].Y:0.##}"
                };
                Canvas.SetLeft(rect, padL);
                Canvas.SetTop(rect, y);
                canvas.Children.Add(rect);

                var valTxt = new TextBlock { Text = $"{pts[i].Y:0.##}", FontSize = 9.5, Foreground = textPrimary };
                Canvas.SetLeft(valTxt, padL + bw + 5);
                Canvas.SetTop(valTxt, y + (barH - 12) * 0.5);
                canvas.Children.Add(valTxt);
            }
        }

        private static void RenderCartesian(Canvas canvas, ChartBlock chart, double w, double h, Brush textPrimary, Brush textMuted, Brush gridLine)
        {
            const double padL = 48, padR = 16, padT = 14, padB = 34;
            double plotW = w - padL - padR, plotH = h - padT - padB;

            var allPts = chart.Series.SelectMany(s => s.Points).ToList();
            double minY = Math.Min(0, allPts.Min(p => p.Y));
            double maxY = Math.Max(1e-6, allPts.Max(p => p.Y));
            double rangeY = Math.Max(1e-6, maxY - minY);

            // Gridlines
            for (int g = 0; g <= 4; g++)
            {
                double frac = g / 4.0;
                double gy = padT + plotH - frac * plotH;
                canvas.Children.Add(new Line { X1 = padL, Y1 = gy, X2 = padL + plotW, Y2 = gy, Stroke = gridLine, StrokeThickness = 1 });
                double gv = minY + frac * rangeY;
                var gt = new TextBlock { Text = $"{gv:0.#}", FontSize = 9, Foreground = textMuted };
                Canvas.SetLeft(gt, 4);
                Canvas.SetTop(gt, gy - 7);
                canvas.Children.Add(gt);
            }

            int maxCount = Math.Max(1, chart.Series.Max(s => s.Points.Count));
            double minX = allPts.Min(p => p.X);
            double maxX = Math.Max(minX + 1e-6, allPts.Max(p => p.X));

            for (int si = 0; si < chart.Series.Count; si++)
            {
                var s = chart.Series[si];
                var brush = SeriesBrush(s.ColorHex, si);

                if (chart.Kind == ChartKind.Bar)
                {
                    double groupW = plotW / Math.Max(1, maxCount);
                    double barW = Math.Max(4, (groupW * 0.72) / chart.Series.Count);
                    for (int pi = 0; pi < s.Points.Count; pi++)
                    {
                        var pt = s.Points[pi];
                        double bh = Math.Max(2, ((pt.Y - minY) / rangeY) * plotH);
                        double bx = padL + pi * groupW + groupW * 0.14 + si * barW;
                        double by = padT + plotH - bh;
                        var rect = new Rectangle { Width = barW - 1, Height = bh, RadiusX = 3, RadiusY = 3, Fill = brush, ToolTip = $"{s.Name} · {pt.Label}: {pt.Y:0.##}" };
                        Canvas.SetLeft(rect, bx);
                        Canvas.SetTop(rect, by);
                        canvas.Children.Add(rect);
                    }
                }
                else
                {
                    var poly = new Polyline { Stroke = brush, StrokeThickness = 2 };
                    var areaPts = new PointCollection();
                    if (chart.Kind == ChartKind.Area) areaPts.Add(new Point(padL, padT + plotH));

                    for (int pi = 0; pi < s.Points.Count; pi++)
                    {
                        var pt = s.Points[pi];
                        double px = chart.Kind == ChartKind.Scatter
                            ? padL + ((pt.X - minX) / (maxX - minX)) * plotW
                            : padL + (s.Points.Count == 1 ? plotW * 0.5 : (pi / (double)(s.Points.Count - 1)) * plotW);
                        double py = padT + plotH - ((pt.Y - minY) / rangeY) * plotH;

                        if (chart.Kind != ChartKind.Scatter)
                        {
                            poly.Points.Add(new Point(px, py));
                            areaPts.Add(new Point(px, py));
                        }

                        var dot = new Ellipse { Width = 7, Height = 7, Fill = brush, ToolTip = $"{s.Name} · {pt.Label}: {pt.Y:0.##}" };
                        Canvas.SetLeft(dot, px - 3.5);
                        Canvas.SetTop(dot, py - 3.5);
                        canvas.Children.Add(dot);
                    }

                    if (chart.Kind == ChartKind.Area && areaPts.Count > 1)
                    {
                        areaPts.Add(new Point(areaPts[^1].X, padT + plotH));
                        var fillBrush = brush.Clone();
                        fillBrush.Opacity = 0.2;
                        canvas.Children.Add(new Polygon { Points = areaPts, Fill = fillBrush });
                    }

                    if (chart.Kind != ChartKind.Scatter) canvas.Children.Add(poly);
                }
            }

            // X labels from series 0
            var firstPts = chart.Series[0].Points;
            int step = Math.Max(1, firstPts.Count / 8);
            for (int i = 0; i < firstPts.Count; i += step)
            {
                double x = chart.Kind == ChartKind.Bar
                    ? padL + (i + 0.5) * (plotW / Math.Max(1, firstPts.Count))
                    : padL + (firstPts.Count == 1 ? plotW * 0.5 : (i / (double)(firstPts.Count - 1)) * plotW);
                var lbl = new TextBlock { Text = firstPts[i].Label, FontSize = 9, Foreground = textMuted };
                Canvas.SetLeft(lbl, x - 14);
                Canvas.SetTop(lbl, padT + plotH + 6);
                canvas.Children.Add(lbl);
            }

            if (!string.IsNullOrWhiteSpace(chart.XLabel))
            {
                var xl = new TextBlock { Text = chart.XLabel, FontSize = 9.5, Foreground = textPrimary };
                Canvas.SetLeft(xl, padL + plotW * 0.45);
                Canvas.SetTop(xl, h - 14);
                canvas.Children.Add(xl);
            }
        }

        private static SolidColorBrush SeriesBrush(string? hex, int index)
        {
            string cHex = !string.IsNullOrWhiteSpace(hex) ? hex! : PaletteHex[index % PaletteHex.Length];
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cHex));
                b.Freeze();
                return b;
            }
            catch
            {
                return Brushes.CornflowerBlue;
            }
        }

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
