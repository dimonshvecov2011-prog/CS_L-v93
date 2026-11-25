using Microsoft.Win32;
using CssLauncher.Core;

namespace CssLauncher.Game;

public sealed class GameLocator : IGameLocator
{
    private readonly string? _savedPath;
    private readonly IReadOnlyList<string>? _steamRoots;
    private readonly Action<Exception>? _logError;

    public GameLocator(string? savedPath = null, IEnumerable<string>? steamRoots = null, Action<Exception>? logError = null)
    {
        _savedPath = savedPath;
        _steamRoots = steamRoots?.ToArray();
        _logError = logError;
    }

    public async Task<IReadOnlyList<GameCandidate>> FindCandidatesAsync(CancellationToken ct)
    {
        var result = new List<GameCandidate>();
        AddIfValid(result, _savedPath, "Settings");

        foreach (var steam in _steamRoots ?? ReadSteamRoots(_logError))
        {
            ct.ThrowIfCancellationRequested();
            AddLibrary(result, steam, "Registry");

            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            try
            {
                var text = await File.ReadAllTextAsync(vdf, ct);
                foreach (var library in new LibraryFoldersVdfParser().Parse(text))
                {
                    ct.ThrowIfCancellationRequested();
                    AddLibrary(result, library, "Library");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logError?.Invoke(ex);
            }
        }

        return result;
    }

    private static void AddLibrary(List<GameCandidate> result, string root, string source) =>
        AddIfValid(result, Path.Combine(root, "steamapps", "common", "Counter-Strike Source"), source);

    private static void AddIfValid(List<GameCandidate> result, string? path, string source)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        var cstrike = Path.Combine(path, "cstrike");
        if (!Directory.Exists(cstrike) || !File.Exists(Path.Combine(cstrike, "gameinfo.txt"))) return;
        if (GameExecutableDetector.Detect(path).FoundPaths.Count == 0) return;

        if (!result.Any(candidate => string.Equals(candidate.RootPath, path, StringComparison.OrdinalIgnoreCase)))
            result.Add(new GameCandidate(path, source));
    }

    private static IReadOnlyList<string> ReadSteamRoots(Action<Exception>? logError)
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows()) return result;

#pragma warning disable CA1416 // Registry access is guarded by OperatingSystem.IsWindows().
        var values = new[]
        {
            (RegistryHive.CurrentUser, @"Software\Valve\Steam"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam")
        };

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var (hive, keyName) in values)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(keyName);
                foreach (var name in new[] { "SteamPath", "InstallPath" })
                {
                    if (key?.GetValue(name) is string path && Directory.Exists(path) &&
                        !result.Contains(path, StringComparer.OrdinalIgnoreCase))
                        result.Add(path);
                }
            }
            catch (Exception ex)
            {
                logError?.Invoke(ex);
            }
        }
#pragma warning restore CA1416

        return result;
    }
}
