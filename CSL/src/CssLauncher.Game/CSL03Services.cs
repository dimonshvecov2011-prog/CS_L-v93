using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CssLauncher.Core;

namespace CssLauncher.Game;

public sealed class ProfileCatalogService
{
    public static IReadOnlyList<LauncherProfile> BuiltInProfiles { get; } = new[]
    {
        new LauncherProfile("classic", "Classic", "Оригинальный опыт CS:S без лишних изменений."),
        new LauncherProfile("modern", "Modern", "Чистый современный интерфейс и комфортный запуск.", Extra: "-heapsize 2097152"),
        new LauncherProfile("competitive", "Competitive", "Минимум визуального шума для соревновательной игры.", Extra: "-freq 144 +cl_forcepreload 1")
    };

    private readonly string _root;
    public ProfileCatalogService(string? appData = null) => _root = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher", "profiles");
    public LauncherProfile Get(string id) => BuiltInProfiles.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? BuiltInProfiles[0];
    public string GetPath(string id) => Path.Combine(_root, Get(id).Id);
    public async Task<string> PrepareAsync(string gameRoot, LauncherProfile profile, string templatePath, CancellationToken ct = default)
    {
        var path = GetPath(profile.Id);
        Directory.CreateDirectory(Path.Combine(path, "custom"));
        var gameInfo = (await File.ReadAllTextAsync(templatePath, ct)).Replace("{GAME_ROOT}", gameRoot.Replace("\\", "/"));
        await File.WriteAllTextAsync(Path.Combine(path, "gameinfo.txt"), gameInfo, ct);
        await File.WriteAllTextAsync(Path.Combine(path, "profile.json"), JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }), ct);
        return path;
    }
}

public sealed class ServerListService
{
    private readonly HttpClient _http;
    public ServerListService(HttpClient? http = null) => _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    public async Task<ServerListManifest> LoadAsync(string source, CancellationToken ct = default)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return await _http.GetFromJsonAsync<ServerListManifest>(uri, cancellationToken: ct) ?? new(1, DateTime.UtcNow.ToString("O"), Array.Empty<ServerEntry>());
        await using var stream = File.OpenRead(source);
        return await JsonSerializer.DeserializeAsync<ServerListManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct) ?? new(1, DateTime.UtcNow.ToString("O"), Array.Empty<ServerEntry>());
    }
}

public sealed class LauncherUpdateService
{
    private readonly HttpClient _http;
    private readonly string _updates;
    public LauncherUpdateService(string? appData = null, HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _updates = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher", "updates");
    }
    public async Task<UpdateCheckResult> CheckAsync(string manifestSource, string currentVersion, CancellationToken ct = default)
    {
        try
        {
            LauncherUpdateManifest? manifest;
            if (Uri.TryCreate(manifestSource, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") manifest = await _http.GetFromJsonAsync<LauncherUpdateManifest>(uri, cancellationToken: ct);
            else await using (var stream = File.OpenRead(manifestSource)) manifest = await JsonSerializer.DeserializeAsync<LauncherUpdateManifest>(stream, cancellationToken: ct);
            if (manifest is null) return new(false, currentVersion, null, "Манифест обновления пуст.");
            var available = Version.TryParse(manifest.Version, out var remote) && Version.TryParse(currentVersion, out var local) && remote > local;
            if (available && (string.IsNullOrWhiteSpace(manifest.DownloadUrl) || manifest.Sha256.Length != 64))
                return new(false, currentVersion, manifest, $"Доступна версия {manifest.Version}, но канал обновлений ещё не настроен.");
            return new(available, currentVersion, manifest, available ? $"Доступна версия {manifest.Version}" : "Установлена последняя версия.");
        }
        catch (Exception ex) { return new(false, currentVersion, null, $"Проверка обновлений не удалась: {ex.Message}"); }
    }
    public async Task<string> DownloadAsync(LauncherUpdateManifest manifest, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("Некорректная ссылка обновления.");
        if (!Version.TryParse(manifest.Version, out var version)) throw new InvalidDataException("Некорректная версия обновления.");
        if (string.IsNullOrWhiteSpace(manifest.Sha256) || manifest.Sha256.Length != 64) throw new InvalidDataException("В манифесте отсутствует корректный SHA-256.");
        Directory.CreateDirectory(_updates); var target = Path.Combine(_updates, $"CSL-{version}.zip"); var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(temporary))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0) { await output.WriteAsync(buffer.AsMemory(0, read), ct); total += read; if (manifest.Size > 0) progress?.Report(Math.Min(1, (double)total / manifest.Size)); }
                await output.FlushAsync(ct);
                if (manifest.Size > 0 && total != manifest.Size) throw new InvalidDataException("Размер обновления не совпадает с манифестом.");
            }
            await using (var verify = File.OpenRead(temporary))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, ct));
                if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 обновления не совпадает.");
            }
            File.Move(temporary, target, true);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class DiagnosticArchiveService
{
    private readonly string _dataRoot;
    public DiagnosticArchiveService(string? appData = null) => _dataRoot = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher");
    public Task<DiagnosticArchiveResult> CreateAsync(string? gameRoot, string? profileId = null, CancellationToken ct = default) =>
        Task.Run(() => CreateCore(gameRoot, profileId, ct), ct);

    private DiagnosticArchiveResult CreateCore(string? gameRoot, string? profileId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.Combine(_dataRoot, "backups"));
        var archive = Path.Combine(_dataRoot, "backups", $"csl-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var files = new List<string>();
        Add(files, Path.Combine(_dataRoot, "settings.json")); Add(files, Path.Combine(_dataRoot, "logs"), true); Add(files, Path.Combine(_dataRoot, "profiles", profileId ?? "classic"), true);
        if (!string.IsNullOrWhiteSpace(gameRoot)) { Add(files, Path.Combine(gameRoot, "cstrike", "steam.inf")); Add(files, SteamPathHelper.GetAppManifestPath(gameRoot)); }
        var uniqueFiles = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) foreach (var file in uniqueFiles) { ct.ThrowIfCancellationRequested(); zip.CreateEntryFromFile(file, GetEntryName(file, gameRoot)); }
        return new DiagnosticArchiveResult(archive, uniqueFiles.Count, new FileInfo(archive).Length);
    }
    private static void Add(List<string> files, string path, bool recursive = false)
    { if (File.Exists(path)) files.Add(path); else if (recursive && Directory.Exists(path)) files.AddRange(Directory.GetFiles(path, "*", SearchOption.AllDirectories)); }
    private string GetEntryName(string file, string? gameRoot)
    {
        if (!string.IsNullOrWhiteSpace(gameRoot) && file.StartsWith(Path.GetFullPath(gameRoot), StringComparison.OrdinalIgnoreCase))
            return ("game/" + Path.GetRelativePath(gameRoot, file)).Replace('\\', '/');
        var relative = Path.GetRelativePath(_dataRoot, file);
        return relative.StartsWith("..", StringComparison.Ordinal) ? ("external/" + Path.GetFileName(file)) : relative.Replace('\\', '/');
    }
}
