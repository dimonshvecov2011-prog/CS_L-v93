using System.IO.Compression;
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

    public ProfileCatalogService(string? appData = null) =>
        _root = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher", "profiles");

    public LauncherProfile Get(string id) =>
        BuiltInProfiles.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? BuiltInProfiles[0];

    public string GetPath(string id) => Path.Combine(_root, Get(id).Id);

    public async Task<string> PrepareAsync(string gameRoot, LauncherProfile profile, string templatePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            throw new DirectoryNotFoundException("Папка игры не найдена.");
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (!File.Exists(templatePath)) throw new FileNotFoundException("Не найден шаблон gameinfo.", templatePath);

        var builtInProfile = Get(profile.Id);
        var path = GetPath(builtInProfile.Id);
        Directory.CreateDirectory(Path.Combine(path, "custom"));
        var normalizedRoot = Path.GetFullPath(gameRoot).Replace("\\", "/");
        var gameInfo = (await File.ReadAllTextAsync(templatePath, ct)).Replace("{GAME_ROOT}", normalizedRoot, StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(path, "gameinfo.txt"), gameInfo, ct);
        await File.WriteAllTextAsync(Path.Combine(path, "profile.json"),
            JsonSerializer.Serialize(builtInProfile, new JsonSerializerOptions { WriteIndented = true }), ct);
        return path;
    }
}

public sealed class ServerListService
{
    private const int MaxManifestBytes = 1 * 1024 * 1024;
    private const int MaxServerCount = 500;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;

    public ServerListService(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<ServerListManifest> LoadAsync(string source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Источник списка серверов не указан.", nameof(source));

        byte[] bytes;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Удалённый список серверов разрешено загружать только по HTTPS.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            EnsureHttpsResponse(response);
            if (response.Content.Headers.ContentLength is > MaxManifestBytes)
                throw new InvalidDataException("JSON-список серверов превышает допустимый размер 1 МБ.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            bytes = await ReadLimitedAsync(input, MaxManifestBytes, timeout.Token);
        }
        else
        {
            var info = new FileInfo(source);
            if (info.Length > MaxManifestBytes)
                throw new InvalidDataException("JSON-список серверов превышает допустимый размер 1 МБ.");
            await using var input = File.OpenRead(source);
            bytes = await ReadLimitedAsync(input, MaxManifestBytes, ct);
        }

        var manifest = JsonSerializer.Deserialize<ServerListManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("JSON-список серверов пуст.");
        ValidateManifest(manifest);
        return manifest;
    }

    private static void ValidateManifest(ServerListManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException($"Неподдерживаемая схема списка серверов: {manifest.SchemaVersion}.");
        if (!DateTimeOffset.TryParse(manifest.UpdatedAt, out _)) throw new InvalidDataException("В списке серверов некорректно указано время обновления.");
        if (manifest.Servers is null || manifest.Servers.Count > MaxServerCount)
            throw new InvalidDataException($"Список серверов должен содержать не более {MaxServerCount} записей.");

        foreach (var server in manifest.Servers)
        {
            if (server is null || string.IsNullOrWhiteSpace(server.Name) || server.Name.Length > 100 ||
                string.IsNullOrWhiteSpace(server.Address) || server.Address.Length > 255 ||
                server.Port is < 1 or > 65535 || server.Players < 0 || server.MaxPlayers < 0 ||
                (server.MaxPlayers > 0 && server.Players > server.MaxPlayers))
                throw new InvalidDataException("В списке серверов найдена запись с некорректными полями.");
        }
    }

    internal static void EnsureHttpsResponse(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Перенаправление на HTTP запрещено.");
    }

    internal static async Task<byte[]> ReadLimitedAsync(Stream input, int maxBytes, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (output.Length + read > maxBytes) throw new InvalidDataException($"Файл превышает допустимый размер {maxBytes / 1024} КБ.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return output.ToArray();
    }
}

public sealed class LauncherUpdateService
{
    public const long MaxDownloadBytes = 512L * 1024 * 1024;
    private const int MaxManifestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly string _updates;

    public LauncherUpdateService(string? appData = null, HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _updates = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher", "updates");
    }

    public async Task<UpdateCheckResult> CheckAsync(string manifestSource, string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var manifest = await ReadManifestAsync(manifestSource, timeout.Token);
            if (manifest is null) return new(false, currentVersion, null, "Манифест обновления пуст.");
            if (manifest.SchemaVersion != 1)
                return new(false, currentVersion, null, $"Неподдерживаемая схема манифеста обновлений: {manifest.SchemaVersion}.");
            if (!Version.TryParse(manifest.Version, out var remote) || !Version.TryParse(currentVersion, out var local))
                return new(false, currentVersion, null, "Версия в манифесте или установленной сборке некорректна.");
            if (remote <= local) return new(false, currentVersion, manifest, "Установлена последняя версия.");

            if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps || downloadUri.UserInfo.Length > 0)
                return new(false, currentVersion, manifest, $"Доступна версия {manifest.Version}, но безопасная HTTPS-ссылка обновления не настроена.");
            if (!IsValidSha256(manifest.Sha256) || manifest.Size <= 0 || manifest.Size > MaxDownloadBytes)
                return new(false, currentVersion, manifest, $"Доступна версия {manifest.Version}, но размер или SHA-256 в манифесте некорректен.");

            return new(true, currentVersion, manifest, $"Доступна версия {manifest.Version}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, currentVersion, null, $"Проверка обновлений не удалась: {ex.Message}");
        }
    }

    public async Task<string> DownloadAsync(LauncherUpdateManifest manifest, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
            throw new InvalidDataException("Ссылка обновления должна использовать HTTPS и не содержать учетные данные.");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Неподдерживаемая схема манифеста обновления.");
        if (!Version.TryParse(manifest.Version, out var version)) throw new InvalidDataException("Некорректная версия обновления.");
        if (!IsValidSha256(manifest.Sha256)) throw new InvalidDataException("В манифесте отсутствует корректный SHA-256.");
        if (manifest.Size <= 0 || manifest.Size > MaxDownloadBytes)
            throw new InvalidDataException($"Размер обновления должен быть больше нуля и не превышать {MaxDownloadBytes / (1024 * 1024)} МБ.");

        Directory.CreateDirectory(_updates);
        var target = Path.Combine(_updates, $"CSL-{version}.zip");
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            ServerListService.EnsureHttpsResponse(response);
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != manifest.Size)
                throw new InvalidDataException("Размер ответа не совпадает с размером в манифесте.");

            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
                {
                    if (total + read > manifest.Size || total + read > MaxDownloadBytes)
                        throw new InvalidDataException("Загружаемый файл превысил размер, указанный в манифесте.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    total += read;
                    progress?.Report((double)total / manifest.Size);
                }
                await output.FlushAsync(timeout.Token);
                if (total != manifest.Size) throw new InvalidDataException("Размер обновления не совпадает с манифестом.");
            }

            await using (var verify = File.OpenRead(temporary))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, timeout.Token));
                if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 обновления не совпадает.");
            }
            File.Move(temporary, target, overwrite: true);
            progress?.Report(1.0);
            return target;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<LauncherUpdateManifest?> ReadManifestAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Источник манифеста обновления не указан.", nameof(source));

        byte[] bytes;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Удалённый манифест обновлений разрешено загружать только по HTTPS.");
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            ServerListService.EnsureHttpsResponse(response);
            if (response.Content.Headers.ContentLength is > MaxManifestBytes)
                throw new InvalidDataException("Манифест обновлений превышает допустимый размер 1 МБ.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            bytes = await ServerListService.ReadLimitedAsync(input, MaxManifestBytes, ct);
        }
        else
        {
            var info = new FileInfo(source);
            if (info.Length > MaxManifestBytes) throw new InvalidDataException("Манифест обновлений превышает допустимый размер 1 МБ.");
            await using var input = File.OpenRead(source);
            bytes = await ServerListService.ReadLimitedAsync(input, MaxManifestBytes, ct);
        }
        return JsonSerializer.Deserialize<LauncherUpdateManifest>(bytes, JsonOptions);
    }

    private static bool IsValidSha256(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
}

public sealed class DiagnosticArchiveService
{
    private readonly string _dataRoot;

    public DiagnosticArchiveService(string? appData = null) =>
        _dataRoot = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher");

    public Task<DiagnosticArchiveResult> CreateAsync(string? gameRoot, string? profileId = null, CancellationToken ct = default) =>
        Task.Run(() => CreateCore(gameRoot, profileId, ct), ct);

    private DiagnosticArchiveResult CreateCore(string? gameRoot, string? profileId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var selectedProfile = ProfileCatalogService.BuiltInProfiles.FirstOrDefault(profile =>
            profile.Id.Equals(profileId ?? "classic", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Неизвестный профиль для диагностического архива.", nameof(profileId));

        var backups = Path.Combine(_dataRoot, "backups");
        Directory.CreateDirectory(backups);
        var archiveId = Guid.NewGuid().ToString("N")[..8];
        var archive = Path.Combine(backups, $"csl-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}-{archiveId}.zip");
        var files = new List<string>();
        Add(files, Path.Combine(_dataRoot, "settings.json"));
        Add(files, Path.Combine(_dataRoot, "logs"), recursive: true);
        Add(files, Path.Combine(_dataRoot, "profiles", selectedProfile.Id), recursive: true);
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            Add(files, Path.Combine(gameRoot, "cstrike", "steam.inf"));
            Add(files, SteamPathHelper.GetAppManifestPath(gameRoot));
        }

        var uniqueFiles = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var file in uniqueFiles)
            {
                ct.ThrowIfCancellationRequested();
                zip.CreateEntryFromFile(file, GetEntryName(file, gameRoot));
            }
        }
        return new DiagnosticArchiveResult(archive, uniqueFiles.Count, new FileInfo(archive).Length);
    }

    private static void Add(List<string> files, string path, bool recursive = false)
    {
        if (File.Exists(path)) files.Add(path);
        else if (recursive && Directory.Exists(path)) files.AddRange(Directory.GetFiles(path, "*", SearchOption.AllDirectories));
    }

    private string GetEntryName(string file, string? gameRoot)
    {
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            var root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullFile = Path.GetFullPath(file);
            if (fullFile.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return ("game/" + Path.GetRelativePath(root, fullFile)).Replace('\\', '/');
        }

        var relative = Path.GetRelativePath(_dataRoot, file);
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? "external/" + Path.GetFileName(file)
            : relative.Replace('\\', '/');
    }
}
