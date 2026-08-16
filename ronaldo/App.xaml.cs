using System;
using System.Threading.Tasks;
using System.Windows;

namespace ronaldo
{
    public partial class App : Application
    {
        /// <summary>How long the splash stays up once everything is ready.</summary>
        private static readonly TimeSpan SplashDuration = TimeSpan.FromSeconds(1);

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Keep the app alive across the splash-to-main handover, which would otherwise
            // look like the last window closing.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var splash = new SplashWindow();
            splash.Show();

            var minimumVisible = Task.Delay(SplashDuration);

            splash.SetStatus("Loading settings...");
            var settings = AppSettings.Load();

            splash.SetStatus("Starting up...");
            var main = new MainWindow(settings);

            await minimumVisible;

            main.Show();
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            splash.Close();
        }
    }
}
