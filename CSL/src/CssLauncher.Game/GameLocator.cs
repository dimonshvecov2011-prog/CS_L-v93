using Microsoft.Win32;
using CssLauncher.Core;

namespace CssLauncher.Game;

public sealed class GameLocator : IGameLocator
{
    private readonly string? _savedPath;
    public GameLocator(string? savedPath = null) => _savedPath = savedPath;

    public Task<IReadOnlyList<GameCandidate>> FindCandidatesAsync(CancellationToken ct)
    {
        var result = new List<GameCandidate>();
        AddIfValid(result, _savedPath, "Settings");
        foreach (var steam in ReadSteamRoots())
        {
            AddLibrary(result, steam, "Registry");
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf)) foreach (var lib in new LibraryFoldersVdfParser().Parse(File.ReadAllText(vdf))) AddLibrary(result, lib, "Library");
        }
        return Task.FromResult<IReadOnlyList<GameCandidate>>(result);
    }

    private static void AddLibrary(List<GameCandidate> result, string root, string source) => AddIfValid(result, Path.Combine(root, "steamapps", "common", "Counter-Strike Source"), source);
    private static void AddIfValid(List<GameCandidate> result, string? path, string source) { if (string.IsNullOrWhiteSpace(path)) return; if (Directory.Exists(path) && !result.Any(x => string.Equals(x.RootPath, path, StringComparison.OrdinalIgnoreCase))) result.Add(new(path, source)); }

    private static IReadOnlyList<string> ReadSteamRoots()
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows()) return result;
#pragma warning disable CA1416 // Registry access is guarded by OperatingSystem.IsWindows().
        var values = new[] { (RegistryHive.CurrentUser, @"Software\Valve\Steam"), (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam") };
        foreach (var (hive, keyName) in values)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(keyName);
                foreach (var name in new[] { "SteamPath", "InstallPath" })
                    if (key?.GetValue(name) is string path && Directory.Exists(path) && !result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
            }
            catch (Exception) { }
        }
#pragma warning restore CA1416
        return result;
    }
}
