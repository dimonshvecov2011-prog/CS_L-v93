using System.Security.Cryptography;
using System.Text;

namespace CssLauncher.Infrastructure;

public sealed class CrashRecoveryMarker
{
    private readonly string _markerPath;

    public CrashRecoveryMarker(string? appData = null)
    {
        var root = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher");
        _markerPath = Path.Combine(root, "state", "unclean-start.marker");
    }

    public bool BeginRun()
    {
        var unclean = File.Exists(_markerPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
        var temporary = _markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, $"pid={Environment.ProcessId}{Environment.NewLine}utc={DateTimeOffset.UtcNow:O}{Environment.NewLine}", new UTF8Encoding(false));
            File.Move(temporary, _markerPath, overwrite: true);
            return unclean;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void CompleteRun()
    {
        try { if (File.Exists(_markerPath)) File.Delete(_markerPath); }
        catch (IOException ex) { throw new IOException("Не удалось очистить маркер незавершённого запуска CS:L.", ex); }
    }

    public static string GetPerUserMutexName(string userIdentity)
    {
        if (string.IsNullOrWhiteSpace(userIdentity)) throw new ArgumentException("Идентификатор пользователя не задан.", nameof(userIdentity));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userIdentity))).Substring(0, 24);
        return (OperatingSystem.IsWindows() ? "Global\\" : string.Empty) + "CSL_Launcher_" + hash;
    }

    public static bool IsSafeModeRequested(IEnumerable<string> arguments) =>
        arguments.Any(argument => argument.Equals("--safe-mode", StringComparison.OrdinalIgnoreCase));
}
