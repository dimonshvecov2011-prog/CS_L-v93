using System.Windows;
using System.Security.Principal;
using CssLauncher.Infrastructure;
using WpfMessageBox = System.Windows.MessageBox;

namespace CssLauncher.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var identity = Environment.UserDomainName + "\\" + Environment.UserName;
        if (OperatingSystem.IsWindows()) identity = WindowsIdentity.GetCurrent().User?.Value ?? identity;
        var mutexName = CrashRecoveryMarker.GetPerUserMutexName(identity);
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            if (!createdNew)
            {
                _instanceMutex.Dispose();
                _instanceMutex = null;
                WpfMessageBox.Show("CS:L уже запущен для этого пользователя.", "CS:L", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }

            var marker = new CrashRecoveryMarker();
            var previousRunWasUnclean = marker.BeginRun();
            var safeMode = CrashRecoveryMarker.IsSafeModeRequested(e.Args);
            var window = new MainWindow(safeMode, previousRunWasUnclean, marker);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            _instanceMutex?.Dispose();
            _instanceMutex = null;
            WpfMessageBox.Show($"Не удалось безопасно запустить CS:L:\n{ex.Message}", "CS:L — запуск", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
        base.OnExit(e);
    }
}
