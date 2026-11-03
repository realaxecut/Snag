using System.Windows;
using System.Windows.Threading;
using Snag.Services;

namespace Snag;

public partial class App : Application
{
    /// <summary>
    /// Started with --dry-run-shutdown: the countdown runs as normal, but the PC is never turned off.
    /// Handy for trying the feature out.
    /// </summary>
    public static bool DryRunShutdown { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DryRunShutdown = e.Args.Any(a => string.Equals(a, "--dry-run-shutdown", StringComparison.OrdinalIgnoreCase));

        // Applied in code (not XAML) so the Fluent dictionaries are added after our own resources load.
        var settings = SettingsStore.Load();
        ThemeManager.SetCustomThemes(SettingsStore.CustomThemes(settings));
        ThemeManager.Apply(settings.Theme, settings.Appearance);
        DispatcherUnhandledException += OnUnhandledException;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show(e.Exception.Message, "Snag ran into a problem", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
