using System;
using System.Threading;
using System.Windows;

namespace Kairos
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Only allow one copy of the window to run at a time.
            _singleInstance = new Mutex(true, @"Local\Kairos.SingleInstance", out bool isNew);
            if (!isNew)
            {
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
            var window = new MainWindow(settings);
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
