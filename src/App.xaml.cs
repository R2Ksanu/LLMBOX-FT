using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LlmodelBox
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
            };

            DispatcherUnhandledException += (s, args) =>
            {
                LogCrash("DispatcherUnhandledException", args.Exception);
                args.Handled = false;
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                LogCrash("UnobservedTaskException", args.Exception);
            };

            ShowSplashThenMain();
        }

        // Shows the split-screen splash and drives it from the real startup load, then hands off to
        // the main window. Guarded by a minimum show time and a maximum timeout so it can never hang.
        private void ShowSplashThenMain()
        {
            var splash = new SplashWindow();
            splash.Topmost = true;
            splash.Show();

            splash.SetProgress(5, "Starting up");
            splash.SetProgress(15, "Preparing workspace");

            var startedAt = DateTime.UtcNow;
            const double MinShowMs = 900.0;
            const double MaxTimeoutMs = 12000.0;

            var main = new MainWindow();
            main.StartupProgress += (pct, stage) => splash.SetProgress(pct, stage);

            bool handedOff = false;
            DispatcherTimer? timeoutTimer = null;

            void HandOff()
            {
                if (handedOff) return;
                handedOff = true;
                timeoutTimer?.Stop();

                void Finish()
                {
                    splash.SetProgress(100, "Ready");
                    splash.FadeOutAndClose(() =>
                    {
                        MainWindow = main;
                        // Bring the window back on-screen (it loaded off-screen — see below).
                        var wa = SystemParameters.WorkArea;
                        main.Left = wa.Left + Math.Max(0, (wa.Width - main.Width) / 2);
                        main.Top = wa.Top + Math.Max(0, (wa.Height - main.Height) / 2);
                        main.Opacity = 1.0;
                        main.Activate();
                    });
                }

                double elapsed = (DateTime.UtcNow - startedAt).TotalMilliseconds;
                double remaining = MinShowMs - elapsed;
                if (remaining > 0)
                {
                    var minTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(remaining) };
                    minTimer.Tick += (_, __) => { minTimer.Stop(); Finish(); };
                    minTimer.Start();
                }
                else
                {
                    Finish();
                }
            }

            // Real completion: the (async) startup load raises StartupCompleted after all stages
            // finish. We cannot use Loaded here because MainWindow_Loaded is async-void and returns
            // at its first await, which would trigger the handoff before the load is done.
            main.StartupCompleted += () => HandOff();

            // Safety net: force completion even if load never signals, so startup can never hang.
            timeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MaxTimeoutMs) };
            timeoutTimer.Tick += (_, __) => { timeoutTimer.Stop(); HandOff(); };
            timeoutTimer.Start();

            // MainWindow has no AllowsTransparency, so Window.Opacity is IGNORED by WPF — a hidden-by-
            // opacity window still paints its opaque black background. That black rectangle was showing
            // behind the splash. Instead we load the window OFF-SCREEN (it still fires Loaded and runs
            // the real startup load) and move it to center only when the splash fades out.
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Left = -32000;
            main.Top = -32000;
            main.ShowActivated = false;
            main.Show();
        }

        private static void LogCrash(string source, Exception? ex)
        {
            try
            {
                string msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex?.GetType()}: {ex?.Message}\r\n{ex?.StackTrace}\r\n";
                if (ex?.InnerException != null)
                {
                    msg += $"InnerException: {ex.InnerException.GetType()}: {ex.InnerException.Message}\r\n{ex.InnerException.StackTrace}\r\n";
                }
                File.AppendAllText(Path.Combine(LlmodelBox.Common.AppConstants.DataDirectory, "crash.log"), msg);
            }
            catch { }
        }
    }
}

