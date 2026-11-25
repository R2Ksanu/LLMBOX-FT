using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace LlmodelBox
{
    public partial class SplashWindow : Window
    {
        private static readonly Color AccentColor = Color.FromRgb(0x3b, 0x82, 0xf6);
        private static readonly Color DoneColor = Color.FromRgb(0x22, 0xc5, 0x5e);
        private static readonly Color PendingDotColor = Color.FromRgb(0x2a, 0x2a, 0x32);
        private static readonly Color PendingTextColor = Color.FromRgb(0x52, 0x52, 0x5b);
        private static readonly Color ActiveTextColor = Color.FromRgb(0xfa, 0xfa, 0xfa);
        private static readonly Color DoneTextColor = Color.FromRgb(0xa1, 0xa1, 0xaa);

        private Ellipse[] _stepDots = Array.Empty<Ellipse>();
        private TextBlock[] _stepTexts = Array.Empty<TextBlock>();

        public SplashWindow()
        {
            InitializeComponent();
            _stepDots = new[] { StepDot0, StepDot1, StepDot2, StepDot3, StepDot4, StepDot5 };
            _stepTexts = new[] { StepText0, StepText1, StepText2, StepText3, StepText4, StepText5 };

            Loaded += (_, __) =>
            {
                TryLoadArt();
                if (Resources["IntroStoryboard"] is Storyboard sb) sb.Begin();
            };
        }

        // Splash artwork placed by the user at src/loaderSplash.jpg (built as a WPF Resource).
        // Falls back to the optional Assets art, then to the gradient + vector motif if neither decodes.
        private void TryLoadArt()
        {
            string[] candidates =
            {
                "pack://application:,,,/loaderSplash.jpg",
                "pack://application:,,,/Assets/llmodelbox-splash-art.png",
            };

            foreach (var uri in candidates)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(uri, UriKind.Absolute);
                    bmp.EndInit();
                    ArtImage.Source = bmp;
                    ArtImage.Opacity = 1.0;
                    return;
                }
                catch
                {
                    // try next candidate
                }
            }

            ArtImage.Source = null;
            ArtImage.Opacity = 0.0;
        }

        // Updates the progress bar, status line and step-row highlight. Safe from any thread.
        public void SetProgress(double percent, string stage)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetProgress(percent, stage));
                return;
            }

            percent = Math.Max(0.0, Math.Min(100.0, percent));

            double trackWidth = ProgressTrack.ActualWidth > 0 ? ProgressTrack.ActualWidth : 340.0;
            double targetWidth = trackWidth * percent / 100.0;
            var widthAnim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            ProgressFill.BeginAnimation(WidthProperty, widthAnim);

            if (!string.IsNullOrEmpty(stage)) StatusText.Text = stage;
            PercentText.Text = $"{(int)Math.Round(percent)}%";

            int active = StepIndexForPercent(percent);
            for (int i = 0; i < _stepDots.Length; i++)
            {
                bool complete = percent >= 100.0 || i < active;
                if (complete)
                {
                    _stepDots[i].Fill = new SolidColorBrush(DoneColor);
                    _stepTexts[i].Foreground = new SolidColorBrush(DoneTextColor);
                }
                else if (i == active)
                {
                    _stepDots[i].Fill = new SolidColorBrush(AccentColor);
                    _stepTexts[i].Foreground = new SolidColorBrush(ActiveTextColor);
                }
                else
                {
                    _stepDots[i].Fill = new SolidColorBrush(PendingDotColor);
                    _stepTexts[i].Foreground = new SolidColorBrush(PendingTextColor);
                }
            }
        }

        // Marks a specific step row as completed. Safe from any thread.
        public void SetStepDone(int index)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetStepDone(index));
                return;
            }
            if (index < 0 || index >= _stepDots.Length) return;
            _stepDots[index].Fill = new SolidColorBrush(DoneColor);
            _stepTexts[index].Foreground = new SolidColorBrush(DoneTextColor);
        }

        private static int StepIndexForPercent(double percent)
        {
            if (percent < 15.0) return 0;
            if (percent < 30.0) return 1;
            if (percent < 50.0) return 2;
            if (percent < 70.0) return 3;
            if (percent < 85.0) return 4;
            return 5;
        }

        // Fades the splash out, then invokes the continuation on the UI thread.
        public void FadeOutAndClose(Action onClosed)
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fade.Completed += (_, __) =>
            {
                onClosed();
                Close();
            };
            BeginAnimation(OpacityProperty, fade);
        }
    }
}
