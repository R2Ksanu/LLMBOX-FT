using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace LlmodelBox.Infrastructure.Editor
{
    public enum TextMarkerType
    {
        None,
        Squiggle,
        Underline,
        Background,
        AiAddedLine
    }

    public interface ITextMarker
    {
        int StartOffset { get; }
        int EndOffset { get; }
        int Length { get; }
        Color? BackgroundColor { get; set; }
        Color? MarkerColor { get; set; }
        TextMarkerType MarkerType { get; set; }
        string? ToolTipText { get; set; }
        object? Tag { get; set; }
        void Delete();
        bool IsDeleted { get; }
    }

    public sealed class TextMarker : TextSegment, ITextMarker
    {
        private readonly TextMarkerService _service;

        public TextMarker(TextMarkerService service, int startOffset, int length)
        {
            _service = service;
            StartOffset = startOffset;
            Length = length;
        }

        public Color? BackgroundColor { get; set; }
        public Color? MarkerColor { get; set; }
        public TextMarkerType MarkerType { get; set; }
        public string? ToolTipText { get; set; }
        public object? Tag { get; set; }
        public bool IsDeleted { get; private set; }

        public void Delete()
        {
            IsDeleted = true;
            _service.Remove(this);
        }
    }

    /// <summary>
    /// AvalonEdit background renderer & TextSegment tracker supporting diagnostic squiggles,
    /// search highlights, and AI inline-change diff bars without duplicating editor state.
    /// </summary>
    public sealed class TextMarkerService : IBackgroundRenderer, ITextViewConnect
    {
        private TextDocument _document;
        private TextSegmentCollection<TextMarker> _markers;
        private readonly List<TextView> _textViews = new();

        public TextMarkerService(TextDocument document)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _markers = new TextSegmentCollection<TextMarker>(_document);
        }

        public KnownLayer Layer => KnownLayer.Selection;

        public void SetDocument(TextDocument document)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _markers = new TextSegmentCollection<TextMarker>(_document);
            Redraw();
        }

        public void AddToTextView(TextView textView)
        {
            if (textView != null && !_textViews.Contains(textView))
                _textViews.Add(textView);
        }

        public void RemoveFromTextView(TextView textView)
        {
            if (textView != null)
                _textViews.Remove(textView);
        }

        public ITextMarker Create(int startOffset, int length)
        {
            int textLen = _document.TextLength;
            int safeStart = Math.Clamp(startOffset, 0, textLen);
            int safeLen = Math.Clamp(length, 0, Math.Max(0, textLen - safeStart));
            var marker = new TextMarker(this, safeStart, safeLen);
            _markers.Add(marker);
            Redraw();
            return marker;
        }

        public void Remove(ITextMarker marker)
        {
            if (marker is TextMarker m && _markers.Remove(m))
                Redraw();
        }

        public void RemoveAll(Predicate<ITextMarker>? predicate = null)
        {
            if (predicate == null)
            {
                _markers.Clear();
                Redraw();
                return;
            }

            foreach (var m in _markers.Where(x => predicate(x)).ToList())
            {
                _markers.Remove(m);
            }
            Redraw();
        }

        public IEnumerable<ITextMarker> GetMarkersAtOffset(int offset)
            => _markers.FindSegmentsContaining(offset);

        public IReadOnlyList<ITextMarker> AllMarkers => _markers.ToList();

        public void Redraw()
        {
            foreach (var view in _textViews)
            {
                view.InvalidateLayer(Layer);
            }
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_markers.Count == 0 || !textView.VisualLinesValid) return;
            var visualLines = textView.VisualLines;
            if (visualLines.Count == 0) return;

            int viewStart = visualLines[0].FirstDocumentLine.Offset;
            int viewEnd = visualLines[^1].LastDocumentLine.EndOffset;

            foreach (var marker in _markers.FindOverlappingSegments(viewStart, Math.Max(0, viewEnd - viewStart)))
            {
                if (marker.IsDeleted) continue;

                if (marker.BackgroundColor.HasValue)
                {
                    var fill = new SolidColorBrush(marker.BackgroundColor.Value);
                    fill.Freeze();
                    var geoBuilder = new BackgroundGeometryBuilder
                    {
                        AlignToWholePixels = true,
                        CornerRadius = 3
                    };
                    if (marker.MarkerType == TextMarkerType.AiAddedLine)
                    {
                        // Full-width subtle emerald band + bright left accent bar
                        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
                        {
                            var fullLineRect = new Rect(0, rect.Top, Math.Max(textView.ActualWidth, rect.Width + 16), rect.Height);
                            drawingContext.DrawRectangle(fill, null, fullLineRect);
                            if (marker.MarkerColor.HasValue)
                            {
                                var accent = new SolidColorBrush(marker.MarkerColor.Value);
                                accent.Freeze();
                                drawingContext.DrawRectangle(accent, null, new Rect(0, rect.Top, 3, rect.Height));
                            }
                        }
                        continue;
                    }

                    geoBuilder.AddSegment(textView, marker);
                    var geo = geoBuilder.CreateGeometry();
                    if (geo != null)
                        drawingContext.DrawGeometry(fill, null, geo);
                }

                if (marker.MarkerType == TextMarkerType.Squiggle && marker.MarkerColor.HasValue)
                {
                    var pen = new Pen(new SolidColorBrush(marker.MarkerColor.Value), 1.15);
                    pen.Freeze();
                    foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
                    {
                        double w = Math.Max(rect.Width, 6);
                        var segRect = new Rect(rect.Left, rect.Top, w, rect.Height);
                        DrawSquiggle(drawingContext, pen, segRect);
                    }
                }
                else if (marker.MarkerType == TextMarkerType.Underline && marker.MarkerColor.HasValue)
                {
                    var pen = new Pen(new SolidColorBrush(marker.MarkerColor.Value), 1.2);
                    pen.Freeze();
                    foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
                    {
                        double w = Math.Max(rect.Width, 6);
                        drawingContext.DrawLine(pen, new Point(rect.Left, rect.Bottom - 1), new Point(rect.Left + w, rect.Bottom - 1));
                    }
                }
            }
        }

        private static void DrawSquiggle(DrawingContext drawingContext, Pen pen, Rect rect)
        {
            double y = rect.Bottom - 1.5;
            double startX = rect.Left;
            double endX = rect.Right;
            const double height = 2.2;
            const double step = 3.2;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(startX, y), false, false);
                bool up = true;
                for (double x = startX + step / 2; x <= endX + step / 2; x += step / 2)
                {
                    ctx.LineTo(new Point(Math.Min(x, endX), up ? y - height : y), true, false);
                    up = !up;
                }
            }
            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>
    /// Highlights matching (), [], {} brackets near the caret in AvalonEdit.
    /// </summary>
    public sealed class BracketHighlightRenderer : IBackgroundRenderer
    {
        private int _openOffset = -1;
        private int _closeOffset = -1;
        private Color _fillColor = Color.FromArgb(75, 59, 130, 246);
        private Color _strokeColor = Color.FromArgb(190, 96, 165, 250);

        public KnownLayer Layer => KnownLayer.Selection;

        public void SetPalette(Color fill, Color stroke)
        {
            _fillColor = fill;
            _strokeColor = stroke;
        }

        public void UpdateHighlight(int openOffset, int closeOffset, TextView? view)
        {
            if (_openOffset == openOffset && _closeOffset == closeOffset) return;
            _openOffset = openOffset;
            _closeOffset = closeOffset;
            view?.InvalidateLayer(Layer);
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_openOffset < 0 || _closeOffset < 0 || !textView.VisualLinesValid) return;

            var doc = textView.Document;
            if (doc == null || _openOffset >= doc.TextLength || _closeOffset >= doc.TextLength) return;

            var builder = new BackgroundGeometryBuilder
            {
                AlignToWholePixels = true,
                CornerRadius = 2
            };
            builder.AddSegment(textView, new TextSegment { StartOffset = _openOffset, Length = 1 });
            builder.AddSegment(textView, new TextSegment { StartOffset = _closeOffset, Length = 1 });

            var geo = builder.CreateGeometry();
            if (geo == null) return;

            var fill = new SolidColorBrush(_fillColor);
            fill.Freeze();
            var pen = new Pen(new SolidColorBrush(_strokeColor), 1.0);
            pen.Freeze();
            drawingContext.DrawGeometry(fill, pen, geo);
        }

        /// <summary>
        /// Pure helper returning the matching open/close offsets for brackets adjacent to caretOffset.
        /// </summary>
        public static (int OpenOffset, int CloseOffset) FindMatchingBracket(string text, int caretOffset)
        {
            if (string.IsNullOrEmpty(text)) return (-1, -1);

            int probe = -1;
            if (caretOffset < text.Length && IsBracket(text[caretOffset]))
                probe = caretOffset;
            else if (caretOffset > 0 && caretOffset - 1 < text.Length && IsBracket(text[caretOffset - 1]))
                probe = caretOffset - 1;

            if (probe < 0) return (-1, -1);

            char ch = text[probe];
            char pair = ch switch
            {
                '(' => ')',
                '[' => ']',
                '{' => '}',
                ')' => '(',
                ']' => '[',
                '}' => '{',
                _ => '\0'
            };
            if (pair == '\0') return (-1, -1);

            bool forward = ch is '(' or '[' or '{';
            int depth = 0;

            if (forward)
            {
                for (int i = probe; i < text.Length; i++)
                {
                    if (text[i] == ch) depth++;
                    else if (text[i] == pair)
                    {
                        depth--;
                        if (depth == 0) return (probe, i);
                    }
                }
            }
            else
            {
                for (int i = probe; i >= 0; i--)
                {
                    if (text[i] == ch) depth++;
                    else if (text[i] == pair)
                    {
                        depth--;
                        if (depth == 0) return (i, probe);
                    }
                }
            }

            return (-1, -1);
        }

        private static bool IsBracket(char c) => c is '(' or ')' or '[' or ']' or '{' or '}';
    }
}
