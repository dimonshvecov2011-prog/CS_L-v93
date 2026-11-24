using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CssLauncher.Core;

namespace CssLauncher.Game;

public sealed class ModPackageService
{
    private readonly string _dataRoot;
    private readonly HttpClient _http;

    public ModPackageService(string? appData = null, HttpClient? http = null)
    {
        _dataRoot = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher");
        _http = http ?? new HttpClient();
    }

    public async Task<ModManifest> ReadManifestAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ModManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct)
            ?? throw new InvalidDataException("Манифест модов пуст.");
    }

    public async Task<ModInstallResult> DownloadAndInstallAsync(ModPackage mod, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mod.Id) || mod.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("Некорректный идентификатор мода.");
        if (!Uri.TryCreate(mod.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) throw new InvalidDataException("Разрешены только HTTP(S)-ссылки.");
        var downloads = Path.Combine(_dataRoot, "downloads");
        var modsRoot = Path.Combine(_dataRoot, "mods", mod.Id);
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(modsRoot);
        var archive = Path.Combine(downloads, $"{mod.Id}-{mod.Version}.zip");
        await DownloadAsync(uri, archive, mod.Size, progress, ct);
        if (!string.Equals(await Sha256Async(archive, ct), mod.Sha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(archive); throw new InvalidDataException($"SHA-256 мода {mod.Id} не совпадает с манифестом."); }
        var versionRoot = Path.Combine(modsRoot, mod.Version);
        var staging = Path.Combine(modsRoot, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ExtractZipSafely(archive, staging);
            if (Directory.Exists(versionRoot)) Directory.Delete(versionRoot, true);
            Directory.Move(staging, versionRoot);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        if (mod.EnabledByDefault) await SetEnabledAsync(mod, versionRoot, true, ct);
        return new ModInstallResult(mod.Id, mod.Version, versionRoot, mod.EnabledByDefault);
    }

    public Task SetEnabledAsync(ModPackage mod, string installedVersionPath, bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = Path.Combine(_dataRoot, "profiles", "default", "custom", mod.Id);
        if (!enabled) { if (Directory.Exists(target)) Directory.Delete(target, true); return Task.CompletedTask; }
        if (!Directory.Exists(installedVersionPath)) throw new DirectoryNotFoundException(installedVersionPath);
        var temp = target + ".staging";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        CopyDirectory(installedVersionPath, temp);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(temp, target);
        return Task.CompletedTask;
    }

    public Task RollbackAsync(ModPackage mod, string version, CancellationToken ct = default)
    {
        var source = Path.Combine(_dataRoot, "mods", mod.Id, version);
        return SetEnabledAsync(mod with { Version = version }, source, true, ct);
    }

    private async Task DownloadAsync(Uri uri, string destination, long expectedSize, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(destination);
        var buffer = new byte[81920]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0) { await output.WriteAsync(buffer.AsMemory(0, read), ct); total += read; if (expectedSize > 0) progress?.Report(Math.Min(1d, (double)total / expectedSize)); }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ExtractZipSafely(string archive, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var full = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Архив содержит небезопасный путь.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(full); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
