using System.Security.Cryptography;
using System.Text.Json;
using CssLauncher.Core;

namespace CssLauncher.Game;

public sealed class VersionChecker : IVersionChecker
{
    private readonly string _manifestPath;
    private readonly AppManifestParser _appManifestParser = new();
    private readonly SteamInfParser _steamInfParser = new();
    public VersionChecker(string manifestPath) => _manifestPath = manifestPath;

    public async Task<VersionCheckResult> CheckAsync(string gameRoot, CancellationToken ct = default)
    {
        var executable = GameExecutableDetector.Detect(gameRoot);
        if (!Directory.Exists(gameRoot) || executable.FoundPaths.Count == 0 || !Directory.Exists(Path.Combine(gameRoot, "cstrike")) || !File.Exists(Path.Combine(gameRoot, "cstrike", "gameinfo.txt")))
            return new(VersionStatus.GameNotFound, Array.Empty<FileMismatch>(), ExecutablePath: executable.PreferredPath, Message: "Не найден cstrike.exe/hl2.exe или обязательные файлы CS:S.");

        var manifest = JsonSerializer.Deserialize<VersionManifest>(await File.ReadAllTextAsync(_manifestPath, ct), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest is null || manifest.KnownVersions is not { Count: > 0 })
            return new(VersionStatus.ManifestEmpty, Array.Empty<FileMismatch>(), ExecutablePath: executable.PreferredPath, Message: "Список совместимых ClientVersion пуст.");

        var appManifestPath = SteamPathHelper.GetAppManifestPath(gameRoot);
        var buildId = _appManifestParser.ParseBuildIdFromFile(appManifestPath);
        var steamInfPath = Path.Combine(gameRoot, "cstrike", "steam.inf");
        var clientVersion = _steamInfParser.ParseClientVersionFromFile(steamInfPath);
        var known = clientVersion is not null && manifest.KnownVersions.Contains(int.TryParse(clientVersion, out var value) ? value : -1);
        var mismatches = new List<FileMismatch>();
        if (manifest.EnableSha256Checks)
        {
            var variants = manifest.Variants?.Where(variant => variant.Files.Count > 0 && variant.Files.All(file => IsValidSha256(file.Sha256))).ToList();
            if (variants is not { Count: > 0 })
                return new(VersionStatus.ManifestEmpty, Array.Empty<FileMismatch>(), buildId, executable.PreferredPath,
                    "Проверка SHA-256 включена, но в манифесте нет вариантов с корректными хэшами.", clientVersion);
            mismatches.AddRange(await CheckHashesAsync(gameRoot, variants, ct));
        }
        var message = clientVersion is null
            ? "cstrike/steam.inf не найден или не содержит ClientVersion/PatchVersion. Продолжение возможно только в диагностическом режиме или после явного подтверждения."
            : known ? $"ClientVersion {clientVersion} подтверждён; Steam buildid: {buildId ?? "не определён"}." : $"Установлена ClientVersion {clientVersion}, встроенные расширения CS:L проверены для версии {manifest.Version}. Возможна несовместимость. Steam buildid: {buildId ?? "не определён"}.";
        return new(known ? VersionStatus.Confirmed : VersionStatus.UnknownBuild, mismatches, buildId, executable.PreferredPath, message, clientVersion);
    }

    private static async Task<IReadOnlyList<FileMismatch>> CheckHashesAsync(string root, IReadOnlyList<ManifestVariant> variants, CancellationToken ct)
    {
        var valid = variants.Where(v => v.Files.Count > 0 && v.Files.All(f => IsValidSha256(f.Sha256))).ToList();
        if (valid.Count == 0) return Array.Empty<FileMismatch>();
        List<FileMismatch>? best = null;
        foreach (var variant in valid)
        {
            var current = new List<FileMismatch>();
            foreach (var file in variant.Files)
            {
                if (!IsSafeRelativePath(file.Path)) { current.Add(new(file.Path, MismatchKind.Missing, file.Sha256, null)); continue; }
                var full = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) current.Add(new(file.Path, MismatchKind.Missing, file.Sha256, null));
                else { await using var stream = File.OpenRead(full); var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant(); if (!actual.Equals(file.Sha256.Trim(), StringComparison.OrdinalIgnoreCase)) current.Add(new(file.Path, MismatchKind.HashDiffers, file.Sha256, actual)); }
            }
            if (best is null || current.Count < best.Count) best = current;
        }
        return best is null ? Array.Empty<FileMismatch>() : (IReadOnlyList<FileMismatch>)best;
    }

    public static bool IsSafeRelativePath(string path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains("..", StringComparison.Ordinal) && !path.Contains(':') && !path.StartsWith('/') && !path.StartsWith('\\');

    private static bool IsValidSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
