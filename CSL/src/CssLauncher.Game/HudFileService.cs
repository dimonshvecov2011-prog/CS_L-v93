using System.Security.Cryptography;
using System.Text.Json;
using CssLauncher.Core;

namespace CssLauncher.Game;

public enum HudInstallState { NotInstalled, Installed, NeedsUpdate, UnsupportedBuild, Corrupt, Error }

public sealed record HudFileStatus(string RelativePath, string ExpectedSha256, string? ActualSha256, bool IsValid);

public sealed record HudInspectionResult(
    HudInstallState State,
    string? ClientVersion,
    IReadOnlyList<HudFileStatus> Files,
    string Message);

/// <summary>
/// Installs only the CS:L-owned cstrike/custom/csl_hud directory.
/// It never edits gameinfo.txt, VPKs, DLLs or any sibling custom directory.
/// </summary>
public sealed class HudFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _packageRoot;
    private readonly IReadOnlySet<string> _supportedClientVersions;
    private readonly IReadOnlyList<HudPackageFile> _files;

    public HudFileService(string packageRoot, IEnumerable<string>? supportedClientVersions = null)
    {
        _packageRoot = Path.GetFullPath(packageRoot);
        _supportedClientVersions = new HashSet<string>(supportedClientVersions ?? ["11003710"], StringComparer.OrdinalIgnoreCase);
        _files = LoadPackage(_packageRoot);
    }

    public string GetTargetDirectory(string gameRoot) =>
        Path.Combine(Path.GetFullPath(gameRoot), "cstrike", "custom", "csl_hud");

    public HudInspectionResult Inspect(string gameRoot)
    {
        try
        {
            var root = Path.GetFullPath(gameRoot);
            var steamInf = Path.Combine(root, "cstrike", "steam.inf");
            var clientVersion = File.Exists(steamInf)
                ? new SteamInfParser().ParseClientVersion(File.ReadAllText(steamInf))
                : null;
            if (clientVersion is null || !_supportedClientVersions.Contains(clientVersion))
                return new(HudInstallState.UnsupportedBuild, clientVersion, [],
                    $"Сборка CS:S {clientVersion ?? "неизвестна"} не входит в список совместимых сборок HUD.");

            var target = GetTargetDirectory(root);
            if (!Directory.Exists(target))
                return new(HudInstallState.NotInstalled, clientVersion, [], "HUD не установлен.");
            RejectLink(target);

            var statuses = _files.Select(file =>
            {
                var path = ResolveOwnedPath(target, file.RelativePath);
                var actual = File.Exists(path) ? HashFile(path) : null;
                return new HudFileStatus(file.RelativePath, file.Sha256, actual,
                    actual is not null && actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase));
            }).ToArray();
            var state = statuses.All(x => x.IsValid) ? HudInstallState.Installed : HudInstallState.Corrupt;
            return new(state, clientVersion, statuses,
                state == HudInstallState.Installed ? "HUD установлен." : "HUD установлен, но файлы повреждены или устарели.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(HudInstallState.Error, null, [], $"Не удалось проверить HUD: {ex.Message}");
        }
    }

    public HudInspectionResult InstallOrUpdate(string gameRoot)
    {
        var inspection = Inspect(gameRoot);
        if (inspection.State == HudInstallState.UnsupportedBuild)
            throw new InvalidOperationException(inspection.Message);

        var root = Path.GetFullPath(gameRoot);
        var customRoot = Path.Combine(root, "cstrike", "custom");
        var target = GetTargetDirectory(root);
        Directory.CreateDirectory(customRoot);
        RejectLink(customRoot);
        if (Directory.Exists(target))
        {
            RejectLink(target);
            var safety = HudSafetyValidator.Validate(target, root);
            if (!safety.IsValid) throw new InvalidDataException(safety.ErrorMessage);
        }

        var staging = Path.Combine(customRoot, ".csl_hud.staging-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(customRoot, ".csl_hud.backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyPackage(staging);
            var hadTarget = Directory.Exists(target);
            if (hadTarget) Directory.Move(target, backup);
            try
            {
                Directory.Move(staging, target);
                var verified = Inspect(root);
                if (verified.State is not HudInstallState.Installed)
                    throw new InvalidDataException($"Проверка после установки HUD не прошла: {verified.Message}");
                if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
                return verified;
            }
            catch
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                if (Directory.Exists(backup)) Directory.Move(backup, target);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        }
    }

    public void Remove(string gameRoot)
    {
        var root = Path.GetFullPath(gameRoot);
        var target = GetTargetDirectory(root);
        if (!Directory.Exists(target)) return;
        RejectLink(target);
        Directory.Delete(target, recursive: true);
    }

    private void CopyPackage(string staging)
    {
        Directory.CreateDirectory(staging);
        foreach (var file in _files)
        {
            var source = ResolveOwnedPath(_packageRoot, file.RelativePath);
            if (!File.Exists(source)) throw new FileNotFoundException("Файл пакета HUD не найден.", source);
            var destination = ResolveOwnedPath(staging, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
    }

    private static IReadOnlyList<HudPackageFile> LoadPackage(string packageRoot)
    {
        var manifestPath = Path.Combine(packageRoot, "hud-package.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Не найден manifest файлового HUD.", manifestPath);
        var manifest = JsonSerializer.Deserialize<HudPackageManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("Manifest файлового HUD пуст.");
        if (manifest.SchemaVersion != 1 || manifest.Files is null || manifest.Files.Count == 0)
            throw new InvalidDataException("Manifest файлового HUD имеет неподдерживаемую схему.");
        return manifest.Files.Select(file =>
        {
            var relative = NormalizeRelative(file.Path);
            if (file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Некорректный SHA-256 в manifest HUD: {relative}");
            return new HudPackageFile(relative, file.Sha256);
        }).ToArray();
    }

    private static string ResolveOwnedPath(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Путь HUD выходит за пределы собственного каталога: {relative}");
        return full;
    }

    private static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new InvalidDataException("Путь файла HUD должен быть относительным.");
        var normalized = path.Replace('\\', '/');
        if (normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Небезопасный путь файла HUD: {path}");
        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static void RejectLink(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (info.ResolveLinkTarget(returnFinalTarget: true) is not null)
            throw new InvalidDataException($"HUD-каталог является ссылкой и не может быть изменён: {directory}");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record HudPackageFile(string RelativePath, string Sha256);
    private sealed record HudPackageManifest(int SchemaVersion, IReadOnlyList<HudManifestFile> Files);
    private sealed record HudManifestFile(string Path, string Sha256);
}
