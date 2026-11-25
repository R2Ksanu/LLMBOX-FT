using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using LlmodelBox.Services;

namespace LlmodelBox.Controls
{
    // Live, theme-aware visualization of the agent pipeline. Fixed nodes wired in a
    // brain-like graph; real AgentActivityBus signals flash the node and fire a pulse
    // along every incoming edge. An ambient timer keeps idle synapses gently breathing.
    public partial class NeuralNetVisualizer : UserControl
    {
        private sealed class NodeVisual
        {
            public AgentNode Node;
            public Point Center;
            public double Radius;
            public Ellipse Glow = null!;
            public Ellipse Core = null!;
            public TextBlock Label = null!;
            public Color BaseColor;
        }

        private readonly Dictionary<AgentNode, NodeVisual> _nodes = new();
        private readonly List<(AgentNode From, AgentNode To, Line Edge)> _edges = new();
        private readonly DispatcherTimer _ambient = new() { Interval = TimeSpan.FromMilliseconds(1500) };
        private int _signalCount;
        private bool _built;
        private readonly Random _rng = new();

        // Layout on the 960x540 canvas: a left-to-right flow with vertical offshoots.
        private static readonly Dictionary<AgentNode, Point> Layout = new()
        {
            { AgentNode.Prompt,     new Point(90, 270) },
            { AgentNode.Memory,     new Point(250, 110) },
            { AgentNode.Context,    new Point(250, 430) },
            { AgentNode.Model,      new Point(470, 270) },
            { AgentNode.Reasoning,  new Point(680, 110) },
            { AgentNode.Compaction, new Point(680, 430) },
            { AgentNode.Response,   new Point(870, 270) },
            { AgentNode.Tokens,     new Point(680, 270) },
        };

        private static readonly (AgentNode, AgentNode)[] Wiring =
        {
            (AgentNode.Prompt, AgentNode.Memory),
            (AgentNode.Prompt, AgentNode.Context),
            (AgentNode.Memory, AgentNode.Model),
            (AgentNode.Context, AgentNode.Model),
            (AgentNode.Model, AgentNode.Reasoning),
            (AgentNode.Model, AgentNode.Compaction),
            (AgentNode.Model, AgentNode.Tokens),
            (AgentNode.Reasoning, AgentNode.Response),
            (AgentNode.Compaction, AgentNode.Response),
            (AgentNode.Tokens, AgentNode.Response),
        };

        public NeuralNetVisualizer()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }
        private Color Brush(string key, string fallback)
        {
            if (TryFindResource(key) is SolidColorBrush b) return b.Color;
            try { return (Color)ColorConverter.ConvertFromString(fallback); } catch { return Colors.Gray; }
        }

        private Color ColorFor(AgentNode n) => n switch
        {
            AgentNode.Prompt => Brush("BrushAccent", "#3f6fd1"),
            AgentNode.Memory => Brush("BrushSuccess", "#5fd08a"),
            AgentNode.Context => Brush("BrushAccentLight", "#7fa8f0"),
            AgentNode.Model => Brush("BrushWarning", "#e0b354"),
            AgentNode.Reasoning => Brush("BrushAccentLight", "#7fa8f0"),
            AgentNode.Compaction => Brush("BrushWarning", "#e0b354"),
            AgentNode.Response => Brush("BrushSuccess", "#5fd08a"),
            AgentNode.Tokens => Brush("BrushAccent", "#3f6fd1"),
            _ => Brush("BrushTextSecondary", "#c9ced9"),
        };

        private static string Title(AgentNode n) => n switch
        {
            AgentNode.Prompt => "PROMPT",
            AgentNode.Memory => "MEMORY",
            AgentNode.Context => "CONTEXT",
            AgentNode.Model => "MODEL",
            AgentNode.Reasoning => "REASONING",
            AgentNode.Compaction => "COMPACT",
            AgentNode.Response => "RESPONSE",
            AgentNode.Tokens => "TOKENS",
            _ => n.ToString().ToUpperInvariant(),
        };

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_built) { BuildGraph(); _built = true; }
            AgentActivityBus.SignalEmitted += OnSignal;
            _ambient.Tick += Ambient_Tick;
            _ambient.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            AgentActivityBus.SignalEmitted -= OnSignal;
            _ambient.Tick -= Ambient_Tick;
            _ambient.Stop();
        }

        private void BuildGraph()
        {
            BrainCanvas.Children.Clear();
            _nodes.Clear();
            _edges.Clear();

            DrawBackdrop();

            var edgeColor = Brush("BrushAccent", "#3f6fd1");
            foreach (var (from, to) in Wiring)
            {
                var a = Layout[from];
                var b = Layout[to];
                // Trim endpoints to the node rim so edges connect ring-to-ring, not center-to-center.
                var (p1, p2) = TrimToRim(a, b, 42);
                var line = new Line
                {
                    X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                    Stroke = new SolidColorBrush(Color.FromArgb(90, edgeColor.R, edgeColor.G, edgeColor.B)),
                    StrokeThickness = 1.6,
                    StrokeDashArray = new DoubleCollection { 5, 6 },
                    StrokeDashCap = PenLineCap.Round,
                    Opacity = 0.7,
                    IsHitTestVisible = false
                };
                BrainCanvas.Children.Add(line);
                _edges.Add((from, to, line));

                // Continuous "marching ants" flow so idle synapses always feel alive.
                var flow = new DoubleAnimation(11, 0, TimeSpan.FromMilliseconds(1100 + _rng.Next(500)))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                line.BeginAnimation(Shape.StrokeDashOffsetProperty, flow);
            }

            foreach (var kv in Layout)
            {
                BuildNode(kv.Key, kv.Value);
            }
        }

        // Faint dotted grid + central vignette so the graph sits on texture instead of a flat void.
        private void DrawBackdrop()
        {
            var dot = Brush("BrushBorder", "#2a2a38");
            for (double x = 40; x < 960; x += 48)
            {
                for (double y = 40; y < 540; y += 48)
                {
                    var d = new Ellipse
                    {
                        Width = 2, Height = 2,
                        Fill = new SolidColorBrush(Color.FromArgb(40, dot.R, dot.G, dot.B)),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(d, x);
                    Canvas.SetTop(d, y);
                    BrainCanvas.Children.Add(d);
                }
            }

            var accent = Brush("BrushAccent", "#3f6fd1");
            var halo = new Ellipse
            {
                Width = 620, Height = 620,
                Fill = new RadialGradientBrush(Color.FromArgb(30, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(halo, 480 - 310);
            Canvas.SetTop(halo, 270 - 310);
            BrainCanvas.Children.Add(halo);
        }

        // Returns the segment endpoints pulled back by `rim` px from each node center.
        private static (Point, Point) TrimToRim(Point a, Point b, double rim)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return (a, b);
            double ux = dx / len, uy = dy / len;
            return (new Point(a.X + ux * rim, a.Y + uy * rim), new Point(b.X - ux * rim, b.Y - uy * rim));
        }

        private void BuildNode(AgentNode node, Point center)
        {
            const double r = 40;
            var col = ColorFor(node);

            var glow = new Ellipse
            {
                Width = r * 2.6, Height = r * 2.6,
                Fill = new RadialGradientBrush(Color.FromArgb(120, col.R, col.G, col.B), Color.FromArgb(0, col.R, col.G, col.B)),
                Opacity = 0.15,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(glow, center.X - r * 1.3);
            Canvas.SetTop(glow, center.Y - r * 1.3);
            BrainCanvas.Children.Add(glow);

            var core = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Fill = new SolidColorBrush(Color.FromArgb(46, col.R, col.G, col.B)),
                Stroke = new SolidColorBrush(col),
                StrokeThickness = 2
            };
            Canvas.SetLeft(core, center.X - r);
            Canvas.SetTop(core, center.Y - r);
            BrainCanvas.Children.Add(core);

            var label = new TextBlock
            {
                Text = Title(node),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Brush("BrushTextPrimary", "#e8ebf2")),
                TextAlignment = TextAlignment.Center,
                Width = r * 2
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, center.X - r);
            Canvas.SetTop(label, center.Y - 8);
            BrainCanvas.Children.Add(label);

            _nodes[node] = new NodeVisual
            {
                Node = node, Center = center, Radius = r,
                Glow = glow, Core = core, Label = label, BaseColor = col
            };
        }

        private void Ambient_Tick(object? sender, EventArgs e)
        {
            if (_nodes.Count == 0) return;
            // Gently breathe a random node so an idle brain still feels alive.
            var keys = new List<AgentNode>(_nodes.Keys);
            var pick = _nodes[keys[_rng.Next(keys.Count)]];
            var anim = new DoubleAnimation(0.32, 0.12, TimeSpan.FromMilliseconds(1400))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            pick.Glow.BeginAnimation(OpacityProperty, anim);

            // Occasionally let a faint synapse fire along a random wire so the graph
            // reads as a living network even before any real signal arrives.
            if (_edges.Count > 0 && _rng.NextDouble() < 0.85)
            {
                var (from, to, _) = _edges[_rng.Next(_edges.Count)];
                FireAmbientPulse(from, to);
            }
        }

        // A dim, small drifting spark along an edge — the idle counterpart to FirePulse.
        private void FireAmbientPulse(AgentNode from, AgentNode to)
        {
            if (!_nodes.ContainsKey(from) || !_nodes.ContainsKey(to)) return;
            var a = _nodes[from].Center;
            var b = _nodes[to].Center;
            var col = _nodes[to].BaseColor;

            var dot = new Ellipse
            {
                Width = 6, Height = 6,
                Opacity = 0.55,
                Fill = new SolidColorBrush(Color.FromArgb(180, col.R, col.G, col.B)),
                IsHitTestVisible = false
            };
            BrainCanvas.Children.Add(dot);

            var dur = TimeSpan.FromMilliseconds(900 + _rng.Next(500));
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var ax = new DoubleAnimation(a.X - 3, b.X - 3, dur) { EasingFunction = ease };
            var ay = new DoubleAnimation(a.Y - 3, b.Y - 3, dur) { EasingFunction = ease };
            ax.Completed += (_, _) => BrainCanvas.Children.Remove(dot);
            dot.BeginAnimation(Canvas.LeftProperty, ax);
            dot.BeginAnimation(Canvas.TopProperty, ay);
        }

        private void OnSignal(AgentSignal sig)
        {
            Dispatcher.BeginInvoke(new Action(() => HandleSignal(sig)));
        }

        private void HandleSignal(AgentSignal sig)
        {
            _signalCount++;
            TxtSignalCount.Text = _signalCount == 1 ? "1 signal" : $"{_signalCount:N0} signals";
            TxtState.Text = Title(sig.Node).ToLowerInvariant();
            SetStateColor(sig.Kind);

            FlashNode(sig.Node, sig.Kind);
            foreach (var (from, to, edge) in _edges)
            {
                if (to == sig.Node) FirePulse(from, to, sig.Kind);
            }
            AppendLogRow(sig);
        }

        private void SetStateColor(string kind)
        {
            var c = kind switch
            {
                "error" => Brush("BrushDanger", "#e06c6c"),
                "warn" => Brush("BrushWarning", "#e0b354"),
                "active" => Brush("BrushAccentLight", "#7fa8f0"),
                _ => Brush("BrushSuccess", "#5fd08a"),
            };
            PulseDot.Fill = new SolidColorBrush(c);
        }

        private void FlashNode(AgentNode node, string kind)
        {
            if (!_nodes.TryGetValue(node, out var nv)) return;
            var accent = kind switch
            {
                "error" => Brush("BrushDanger", "#e06c6c"),
                "warn" => Brush("BrushWarning", "#e0b354"),
                _ => nv.BaseColor,
            };
            nv.Core.Stroke = new SolidColorBrush(accent);
            nv.Glow.Fill = new RadialGradientBrush(Color.FromArgb(150, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B));

            nv.Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0.14, TimeSpan.FromMilliseconds(750))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            var scale = new ScaleTransform(1, 1, nv.Radius, nv.Radius);
            nv.Core.RenderTransform = scale;
            var pop = new DoubleAnimation(1.0, 1.18, TimeSpan.FromMilliseconds(140))
            {
                AutoReverse = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        private void FirePulse(AgentNode from, AgentNode to, string kind)
        {
            if (!_nodes.ContainsKey(from) || !_nodes.ContainsKey(to)) return;
            var a = _nodes[from].Center;
            var b = _nodes[to].Center;
            var col = kind == "error" ? Brush("BrushDanger", "#e06c6c") : _nodes[to].BaseColor;

            var dot = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(col),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = col, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9
                }
            };
            BrainCanvas.Children.Add(dot);

            var dur = TimeSpan.FromMilliseconds(520);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var ax = new DoubleAnimation(a.X - 5, b.X - 5, dur) { EasingFunction = ease };
            var ay = new DoubleAnimation(a.Y - 5, b.Y - 5, dur) { EasingFunction = ease };
            ax.Completed += (_, _) => BrainCanvas.Children.Remove(dot);
            dot.BeginAnimation(Canvas.LeftProperty, ax);
            dot.BeginAnimation(Canvas.TopProperty, ay);
        }

        private void AppendLogRow(AgentSignal sig)
        {
            var col = sig.Kind switch
            {
                "error" => Brush("BrushDanger", "#e06c6c"),
                "warn" => Brush("BrushWarning", "#e0b354"),
                "active" => Brush("BrushAccentLight", "#7fa8f0"),
                _ => ColorFor(sig.Node),
            };

            var row = new Border { Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(5) };
            row.Background = new SolidColorBrush(Color.FromArgb(30, col.R, col.G, col.B));
            row.BorderThickness = new Thickness(0, 0, 0, 0);

            var stack = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = Title(sig.Node),
                FontFamily = new FontFamily("Consolas"), FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(col)
            });
            head.Children.Add(new TextBlock
            {
                Text = "  " + sig.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
                FontFamily = new FontFamily("Consolas"), FontSize = 9.5,
                Foreground = new SolidColorBrush(Brush("BrushTextDim", "#6b7280"))
            });
            if (sig.Tokens.HasValue)
            {
                head.Children.Add(new TextBlock
                {
                    Text = $"  {sig.Tokens.Value:N0} tok",
                    FontFamily = new FontFamily("Consolas"), FontSize = 9.5, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Brush("BrushAccentLight", "#7fa8f0")),
                    HorizontalAlignment = HorizontalAlignment.Right
                });
            }
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock
            {
                Text = sig.Detail,
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Brush("BrushTextSecondary", "#c9ced9")),
                Margin = new Thickness(0, 1, 0, 0)
            });
            row.Child = stack;

            SignalLog.Items.Insert(0, row);
            while (SignalLog.Items.Count > 120) SignalLog.Items.RemoveAt(SignalLog.Items.Count - 1);
        }

        public void ReplayRecent()
        {
            SignalLog.Items.Clear();
            var recent = AgentActivityBus.Recent;
            _signalCount = recent.Count;
            foreach (var sig in recent)
            {
                AppendLogRow(sig);
            }
            TxtSignalCount.Text = _signalCount == 1 ? "1 signal" : $"{_signalCount:N0} signals";
        }

        private void BtnClearNeural_Click(object sender, RoutedEventArgs e)
        {
            AgentActivityBus.Clear();
            SignalLog.Items.Clear();
            _signalCount = 0;
            TxtSignalCount.Text = "0 signals";
            TxtState.Text = "idle";
        }

        private bool _is3D;
        private void BtnToggle3D_Click(object sender, RoutedEventArgs e)
        {
            _is3D = !_is3D;
            Graph2DHost.Visibility = _is3D ? Visibility.Collapsed : Visibility.Visible;
            Graph3DHost.Visibility = _is3D ? Visibility.Visible : Visibility.Collapsed;
            BtnToggle3D.Content = _is3D ? "2D" : "3D";
        }
    }
}
