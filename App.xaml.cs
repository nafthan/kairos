using System;
using System.Threading;
using System.Windows;

namespace Kairos
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;
        private EventWaitHandle? _showSignal;
        private const string ShowSignalName = @"Local\Kairos.Show";

        protected override void OnStartup(StartupEventArgs e)
        {
            // Only allow one copy of the window to run at a time.
            _singleInstance = new Mutex(true, @"Local\Kairos.SingleInstance", out bool isNew);
            if (!isNew)
            {
                // Kairos is already running (perhaps waiting in the background for Spotify): bring it forward.
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                    signal.Set();
                }
                catch { }
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, args) =>
            {
                // Keep running on unexpected errors rather than vanishing from the screen.
                System.Diagnostics.Debug.WriteLine(args.Exception);
                args.Handled = true;
            };

            base.OnStartup(e);

            var settings = Settings.Load();
            LocalLyrics.MoveFromOldDefault(settings);
            var window = new MainWindow(settings);
            MainWindow = window;

            // Listen for "show yourself" from a second launch of Kairos.
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            var listener = new Thread(() =>
            {
                while (true)
                {
                    try { _showSignal.WaitOne(); }
                    catch { return; }
                    Dispatcher.BeginInvoke(new Action(window.BringBack));
                }
            }) { IsBackground = true, Name = "Kairos show signal" };
            listener.Start();

            // Waiting for Spotify: start quietly in the background instead of showing an empty window.
            bool waitForSpotify = settings.OnSpotifyClose == "Hide" && !settings.OpenSpotifyOnStart && !SpotifyApp.IsRunning();
            if (waitForSpotify) window.StartHidden();
            else if (settings.MiniMode) window.StartInMini();
            else window.Show();

            SessionEnding += (_, _) => window.WindowsIsShuttingDown = true;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstance?.Dispose();
            _showSignal?.Dispose();
            base.OnExit(e);
        }
    }
}
