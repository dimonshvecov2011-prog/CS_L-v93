using System.Security.Principal;

namespace CssLauncher.Core;

/// <summary>
/// Owns a per-user mutex so two CS:L windows cannot run at the same time.
/// </summary>
public sealed class SingleInstanceManager
{
    private Mutex? _mutex;
    private bool _ownsMutex;

    public bool IsSafeMode { get; private set; }

    public bool Initialize(string[]? args)
    {
        IsSafeMode = args?.Any(argument =>
            string.Equals(argument, "--safe-mode", StringComparison.OrdinalIgnoreCase)) == true;

        var sid = GetCurrentUserSid();
        var mutexName = $"Local\\CSL-Launcher-{sid}";
        _mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return createdNew;
    }

    public void Release()
    {
        if (_mutex is null) return;
        try
        {
            if (_ownsMutex) _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex may already have been abandoned or released during shutdown.
        }
        finally
        {
            _mutex.Dispose();
            _mutex = null;
            _ownsMutex = false;
        }
    }

    private static string GetCurrentUserSid()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            }
            catch (PlatformNotSupportedException)
            {
                // Keep tests and diagnostics usable on non-Windows build hosts.
            }
        }
        return Environment.UserName;
    }
}
