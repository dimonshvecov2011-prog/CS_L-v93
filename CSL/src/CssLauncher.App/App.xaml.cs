using System.Windows;
using CssLauncher.Core;
using CssLauncher.Infrastructure;
using WpfMessageBox = System.Windows.MessageBox;

namespace CssLauncher.App;

public partial class App : System.Windows.Application
{
    private readonly SingleInstanceManager _singleInstance = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (!_singleInstance.Initialize(e.Args))
            {
                WpfMessageBox.Show("Лаунчер уже запущен", "CS:L", MessageBoxButton.OK, MessageBoxImage.Information);
                Environment.Exit(0);
                return;
            }

            var marker = new CrashRecoveryMarker();
            var previousRunWasUnclean = marker.BeginRun();
            var safeMode = _singleInstance.IsSafeMode;
            // Safe mode is intentionally handled by MainWindow: it disables game launch,
            // HUD operations and network/update actions while recovery UI remains available.
            var window = new MainWindow(safeMode, previousRunWasUnclean, marker);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            _singleInstance.Release();
            WpfMessageBox.Show($"Не удалось безопасно запустить CS:L:\n{ex.Message}", "CS:L — запуск", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance.Release();
        base.OnExit(e);
    }
}
