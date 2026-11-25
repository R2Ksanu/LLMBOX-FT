using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using LlmodelBox.Services;

namespace LlmodelBox.Controls
{
    // A walkable 3D view of the same agent pipeline. Node spheres float in space,
    // edges connect them, and real AgentActivityBus signals ignite a node's glow and
    // send a photon travelling down each incoming edge. Drag to orbit, scroll to zoom.
    public partial class NeuralNet3DVisualizer : UserControl
    {
        private sealed class Node3D
        {
            public AgentNode Node;
            public Point3D Center;
            public SolidColorBrush Emissive = null!;
            public Color BaseColor;
            public DateTime LastFire = DateTime.MinValue;
            public Border Card = null!;
            public TextBlock DetailText = null!;
        }

        private readonly Dictionary<AgentNode, Node3D> _nodes = new();
        private readonly DispatcherTimer _decay = new() { Interval = TimeSpan.FromMilliseconds(40) };
        private bool _built;

        // Orbit state
        private double _yaw = 0, _pitch = 0.35, _radius = 20;
        private bool _dragging;
        private Point _lastMouse;

        private static readonly Dictionary<AgentNode, Point3D> Layout = new()
        {
            { AgentNode.Prompt,     new Point3D(-6, 0, 0) },
            { AgentNode.Memory,     new Point3D(-3, 2.6, 1.6) },
            { AgentNode.Context,    new Point3D(-3, -2.6, -1.6) },
            { AgentNode.Model,      new Point3D(0, 0, 0) },
            { AgentNode.Reasoning,  new Point3D(3, 2.6, 1.6) },
            { AgentNode.Compaction, new Point3D(3, -2.6, -1.6) },
            { AgentNode.Tokens,     new Point3D(3, 0, -2.4) },
            { AgentNode.Response,   new Point3D(6, 0, 0) },
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

        public NeuralNet3DVisualizer()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += OnVisibleChanged;
        }
        // BODY_PLACEHOLDER
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
            _ => Colors.Gray,
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
            if (!_built) { BuildScene(); BuildLabels(); _built = true; }
            Viewport.SizeChanged -= Viewport_SizeChanged;
            Viewport.SizeChanged += Viewport_SizeChanged;
            UpdateCamera();
            Dispatcher.BeginInvoke(new Action(UpdateLabels), DispatcherPriority.Loaded);
            _decay.Tick -= Decay_Tick;
            _decay.Tick += Decay_Tick;
        }

        private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLabels();

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            AgentActivityBus.SignalEmitted -= OnSignal;
            _decay.Stop();
        }

        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Only listen and animate while actually on screen — keeps idle CPU near zero.
            if ((bool)e.NewValue)
            {
                AgentActivityBus.SignalEmitted -= OnSignal;
                AgentActivityBus.SignalEmitted += OnSignal;
                _decay.Start();
            }
            else
            {
                AgentActivityBus.SignalEmitted -= OnSignal;
                _decay.Stop();
            }
        }

        private void UpdateCamera()
        {
            double cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
            double cy = Math.Cos(_yaw), sy = Math.Sin(_yaw);
            var pos = new Point3D(_radius * cp * sy, _radius * sp, _radius * cp * cy);
            Cam.Position = pos;
            Cam.LookDirection = new Vector3D(-pos.X, -pos.Y, -pos.Z);
            UpdateLabels();
        }

        private static MeshGeometry3D Sphere(Point3D c, double r, int seg = 16)
        {
            var mesh = new MeshGeometry3D();
            for (int i = 0; i <= seg; i++)
            {
                double lat = Math.PI * i / seg;
                for (int j = 0; j <= seg; j++)
                {
                    double lon = 2 * Math.PI * j / seg;
                    double x = Math.Sin(lat) * Math.Cos(lon);
                    double y = Math.Cos(lat);
                    double z = Math.Sin(lat) * Math.Sin(lon);
                    mesh.Positions.Add(new Point3D(c.X + r * x, c.Y + r * y, c.Z + r * z));
                    mesh.Normals.Add(new Vector3D(x, y, z));
                }
            }
            for (int i = 0; i < seg; i++)
            {
                for (int j = 0; j < seg; j++)
                {
                    int a = i * (seg + 1) + j;
                    int b = a + seg + 1;
                    mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(a + 1);
                    mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 1); mesh.TriangleIndices.Add(a + 1);
                }
            }
            return mesh;
        }

        // A thin square-section tube from a to b, used to draw an edge.
        private static MeshGeometry3D Tube(Point3D a, Point3D b, double t)
        {
            var dir = b - a; dir.Normalize();
            var up = Math.Abs(dir.Y) > 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
            var side = Vector3D.CrossProduct(dir, up); side.Normalize();
            var vert = Vector3D.CrossProduct(dir, side); vert.Normalize();
            var mesh = new MeshGeometry3D();
            Point3D[] ends = { a, b };
            foreach (var p in ends)
            {
                mesh.Positions.Add(p + (side + vert) * t);
                mesh.Positions.Add(p + (side - vert) * t);
                mesh.Positions.Add(p + (-side - vert) * t);
                mesh.Positions.Add(p + (-side + vert) * t);
            }
            int[] quad = { 0, 1, 5, 5, 4, 0, 1, 2, 6, 6, 5, 1, 2, 3, 7, 7, 6, 2, 3, 0, 4, 4, 7, 3 };
            foreach (var idx in quad) mesh.TriangleIndices.Add(idx);
            return mesh;
        }

        private void BuildScene()
        {
            // Edges first (behind the nodes). Emissive-only so they read as clean glowing
            // wires from every camera angle instead of unlit black ribbons.
            var wire = Brush("BrushAccentLight", "#7fa8f0");
            var wireBrush = new SolidColorBrush(Color.FromArgb(150, wire.R, wire.G, wire.B));
            foreach (var (from, to) in Wiring)
            {
                var edgeMat = new EmissiveMaterial(wireBrush);
                var model = new GeometryModel3D(Tube(Layout[from], Layout[to], 0.03), edgeMat)
                {
                    BackMaterial = edgeMat
                };
                SceneRoot.Children.Add(model);
            }

            foreach (var kv in Layout)
            {
                var col = ColorFor(kv.Key);
                var emissive = new SolidColorBrush(Color.FromArgb(90, col.R, col.G, col.B));
                var mat = new MaterialGroup();
                mat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(200, col.R, col.G, col.B))));
                mat.Children.Add(new EmissiveMaterial(emissive));
                SceneRoot.Children.Add(new GeometryModel3D(Sphere(kv.Value, 0.85), mat));
                _nodes[kv.Key] = new Node3D { Node = kv.Key, Center = kv.Value, Emissive = emissive, BaseColor = col };
            }
        }

        private void BuildLabels()
        {
            LabelOverlay.Children.Clear();
            foreach (var kv in Layout)
            {
                var col = ColorFor(kv.Key);
                var card = new Border
                {
                    Width = 150,
                    Background = new SolidColorBrush(Color.FromArgb(150, 10, 10, 18)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(160, col.R, col.G, col.B)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 3, 7, 4),
                    IsHitTestVisible = false
                };
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = Title(kv.Key),
                    FontFamily = new FontFamily("Consolas"), FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(col), TextAlignment = TextAlignment.Center
                });
                var detail = new TextBlock
                {
                    Text = "idle", FontSize = 9.5, FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Brush("BrushTextMuted", "#8b93a7")),
                    TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 138
                };
                stack.Children.Add(detail);
                card.Child = stack;
                LabelOverlay.Children.Add(card);
                _nodes[kv.Key].Card = card;
                _nodes[kv.Key].DetailText = detail;
            }
        }

        // Projects each node's 3D center to 2D screen space and pins its label there.
        private void UpdateLabels()
        {
            if (_nodes.Count == 0) return;
            double W = Viewport.ActualWidth, H = Viewport.ActualHeight;
            if (W <= 0 || H <= 0) return;

            var P = Cam.Position;
            Vector3D f = Cam.LookDirection; f.Normalize();
            Vector3D r = Vector3D.CrossProduct(f, Cam.UpDirection); r.Normalize();
            Vector3D u = Vector3D.CrossProduct(r, f);
            double focalX = 1.0 / Math.Tan(Cam.FieldOfView * Math.PI / 180.0 / 2.0);
            double focalY = focalX * (W / H);

            foreach (var nv in _nodes.Values)
            {
                var d = nv.Center - P;
                double x = Vector3D.DotProduct(d, r);
                double y = Vector3D.DotProduct(d, u);
                double z = Vector3D.DotProduct(d, f);
                if (z <= 0.1) { nv.Card.Visibility = Visibility.Collapsed; continue; }
                nv.Card.Visibility = Visibility.Visible;
                double sx = (x * focalX / z * 0.5 + 0.5) * W;
                double sy = (0.5 - y * focalY / z * 0.5) * H;
                Canvas.SetLeft(nv.Card, sx - nv.Card.Width / 2);
                Canvas.SetTop(nv.Card, sy + 30); // sit just below the sphere
            }
        }

        private void Surface_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _lastMouse = e.GetPosition(this);
            InputSurface.CaptureMouse();
        }

        private void Surface_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            InputSurface.ReleaseMouseCapture();
        }

        private void Surface_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetPosition(this);
            _yaw += (p.X - _lastMouse.X) * 0.008;
            _pitch += (p.Y - _lastMouse.Y) * 0.008;
            _pitch = Math.Max(-1.4, Math.Min(1.4, _pitch));
            _lastMouse = p;
            UpdateCamera();
        }

        private void Surface_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _radius -= e.Delta * 0.012;
            _radius = Math.Max(8, Math.Min(45, _radius));
            UpdateCamera();
        }

        private void Decay_Tick(object? sender, EventArgs e)
        {
            // Ease every node's glow back toward its resting brightness.
            foreach (var nv in _nodes.Values)
            {
                byte a = nv.Emissive.Color.A;
                if (a > 90)
                {
                    byte na = (byte)Math.Max(90, a - 12);
                    nv.Emissive.Color = Color.FromArgb(na, nv.Emissive.Color.R, nv.Emissive.Color.G, nv.Emissive.Color.B);
                }
            }
        }

        private void OnSignal(AgentSignal sig)
        {
            Dispatcher.BeginInvoke(new Action(() => HandleSignal(sig)));
        }

        private void HandleSignal(AgentSignal sig)
        {
            if (_nodes.TryGetValue(sig.Node, out var nv))
            {
                var c = sig.Kind == "error" ? Brush("BrushDanger", "#e06c6c")
                      : sig.Kind == "warn" ? Brush("BrushWarning", "#e0b354")
                      : nv.BaseColor;
                nv.Emissive.Color = Color.FromArgb(255, c.R, c.G, c.B);
                if (nv.DetailText != null)
                {
                    nv.DetailText.Text = string.IsNullOrWhiteSpace(sig.Detail) ? "active" : sig.Detail;
                    nv.DetailText.Foreground = new SolidColorBrush(c);
                }
            }
            foreach (var (from, to) in Wiring)
            {
                if (to == sig.Node) FirePhoton(from, to, sig.Kind);
            }
        }

        private void FirePhoton(AgentNode from, AgentNode to, string kind)
        {
            if (!_nodes.ContainsKey(from) || !_nodes.ContainsKey(to)) return;
            var a = _nodes[from].Center;
            var b = _nodes[to].Center;
            var col = kind == "error" ? Brush("BrushDanger", "#e06c6c") : _nodes[to].BaseColor;

            var mat = new MaterialGroup();
            mat.Children.Add(new DiffuseMaterial(new SolidColorBrush(col)));
            mat.Children.Add(new EmissiveMaterial(new SolidColorBrush(col)));
            var move = new TranslateTransform3D(0, 0, 0);
            var model = new GeometryModel3D(Sphere(new Point3D(0, 0, 0), 0.22, 10), mat) { Transform = move };
            SceneRoot.Children.Add(model);

            var dur = TimeSpan.FromMilliseconds(560);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            move.BeginAnimation(TranslateTransform3D.OffsetXProperty, new DoubleAnimation(a.X, b.X, dur) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform3D.OffsetYProperty, new DoubleAnimation(a.Y, b.Y, dur) { EasingFunction = ease });
            var az = new DoubleAnimation(a.Z, b.Z, dur) { EasingFunction = ease };
            az.Completed += (_, _) => SceneRoot.Children.Remove(model);
            move.BeginAnimation(TranslateTransform3D.OffsetZProperty, az);
        }
    }
}
