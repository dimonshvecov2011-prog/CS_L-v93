using System.Text.RegularExpressions;
using CssLauncher.Core;

namespace CssLauncher.Game;

public sealed class AppManifestParser
{
    private static readonly Regex BuildIdRegex = new("\\\"buildid\\\"\\s+\\\"(?<id>\\d+)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public string? ParseBuildId(string text) => BuildIdRegex.Match(text).Groups["id"].Value is { Length: > 0 } id ? id : null;
    public string? ParseBuildIdFromFile(string path) => File.Exists(path) ? ParseBuildId(File.ReadAllText(path)) : null;
}

public sealed class SteamInfParser
{
    public IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return result;
    }
    public string? ParseClientVersion(string text)
    {
        var values = Parse(text);
        if (values.TryGetValue("ClientVersion", out var client)) return client;
        if (values.TryGetValue("PatchVersion", out var patch)) return patch;
        return null;
    }
    public string? ParseClientVersionFromFile(string path) => File.Exists(path) ? ParseClientVersion(File.ReadAllText(path)) : null;
}

public static class GameExecutableDetector
{
    public static ExecutableDetection Detect(string gameRoot)
    {
        var found = new[] { "cstrike.exe", "hl2.exe" }.Select(x => Path.Combine(gameRoot, x)).Where(File.Exists).ToArray();
        return new(found.FirstOrDefault(), found);
    }
}
