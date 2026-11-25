using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CssLauncher.Core;

namespace CssLauncher.Game;

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
            if (string.IsNullOrWhiteSpace(manifest.DownloadUrl) && string.IsNullOrWhiteSpace(manifest.Sha256) && manifest.Size == 0)
                return new(false, currentVersion, manifest, "Канал обновлений CS:L отключён; автоматическая установка не выполняется.");
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

public sealed record LauncherUpdateTransactionResult(string InstallDirectory, string? BackupDirectory, bool LaunchConfirmed);

/// <summary>
/// Isolated transactional deployment engine for a launcher package. The live update feed remains disabled;
/// real activation must be hosted by an external bootstrapper so the running executable is never replaced in place.
/// </summary>
public sealed class LauncherUpdateTransactionService
{
    private readonly TimeSpan _confirmationTimeout;

    public LauncherUpdateTransactionService(TimeSpan? confirmationTimeout = null) =>
        _confirmationTimeout = confirmationTimeout ?? TimeSpan.FromSeconds(90);

    public async Task<LauncherUpdateTransactionResult> ApplyAsync(
        string packagePath,
        string installDirectory,
        LauncherUpdateManifest manifest,
        Func<string, CancellationToken, Task<bool>> confirmLaunchAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(confirmLaunchAsync);
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Пакет обновления не найден.", packagePath);
        if (manifest.SchemaVersion != 1 || !Version.TryParse(manifest.Version, out _) || manifest.Size <= 0 ||
            manifest.Size > LauncherUpdateService.MaxDownloadBytes || manifest.Sha256 is not { Length: 64 } ||
            !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Манифест транзакции обновления некорректен.");

        await using var packageStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, useAsync: true);
        if (packageStream.Length != manifest.Size) throw new InvalidDataException("Размер пакета не совпадает с манифестом.");
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, ct));
        if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 пакета не совпадает с манифестом.");
        packageStream.Position = 0;

        var target = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("Каталог установки не имеет родительской папки.");
        Directory.CreateDirectory(parent);
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(parent, $".{Path.GetFileName(target)}.staging-{id}");
        var backup = Path.Combine(parent, $"{Path.GetFileName(target)}.backup-{manifest.Version}-{id}");
        var hadPrevious = Directory.Exists(target);
        var previousMoved = false;
        var newInstalled = false;

        try
        {
            await ExtractVerifiedPackageAsync(packageStream, staging, ct);
            if (!File.Exists(Path.Combine(staging, "CSL.exe")))
                throw new InvalidDataException("Пакет не содержит корневой файл CSL.exe.");
            ct.ThrowIfCancellationRequested();

            if (hadPrevious)
            {
                Directory.Move(target, backup);
                previousMoved = true;
            }
            Directory.Move(staging, target);
            newInstalled = true;

            using var confirmationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            confirmationCts.CancelAfter(_confirmationTimeout);
            bool confirmed;
            try
            {
                confirmed = await confirmLaunchAsync(target, confirmationCts.Token)
                    .WaitAsync(_confirmationTimeout, ct);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"Новая версия не подтвердила успешный запуск за {_confirmationTimeout.TotalSeconds:0} секунд.", ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && confirmationCts.IsCancellationRequested)
            {
                throw new TimeoutException($"Новая версия не подтвердила успешный запуск за {_confirmationTimeout.TotalSeconds:0} секунд.", ex);
            }
            if (!confirmed) throw new InvalidOperationException("Новая версия не подтвердила успешный запуск.");
            return new LauncherUpdateTransactionResult(target, previousMoved ? backup : null, LaunchConfirmed: true);
        }
        catch (Exception updateError)
        {
            try
            {
                if (newInstalled && Directory.Exists(target))
                    Directory.Move(target, target + ".failed-" + id);
                if (previousMoved && Directory.Exists(backup)) Directory.Move(backup, target);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Обновление не подтвердило запуск, и автоматический откат также завершился ошибкой. Не удаляйте папки .backup-* и .failed-*.",
                    updateError, rollbackError);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task ExtractVerifiedPackageAsync(Stream packageStream, string staging, CancellationToken ct)
    {
        const int maxEntries = 20000;
        const long maxExpandedBytes = 1024L * 1024 * 1024;
        Directory.CreateDirectory(staging);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is 0 or > maxEntries) throw new InvalidDataException("Число файлов в пакете обновления недопустимо.");
        long total = 0;
        var root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || Path.IsPathRooted(name) ||
                name.Split('/').Any(part => part is ".." or ".") || name.Contains(':'))
                throw new InvalidDataException($"В пакете обнаружен небезопасный путь: {entry.FullName}");
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Ссылки на файлы в пакете обновления запрещены.");
            total = checked(total + entry.Length);
            if (total > maxExpandedBytes) throw new InvalidDataException("Распакованный пакет превышает 1 ГБ.");
            var destination = Path.GetFullPath(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Путь распаковки вышел за staging-каталог.");
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await source.CopyToAsync(output, ct);
            await output.FlushAsync(ct);
        }
    }
}

public sealed class DiagnosticArchiveService
{
    private readonly string _dataRoot;

    public DiagnosticArchiveService(string? appData = null) =>
        _dataRoot = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher");

    public Task<DiagnosticArchiveResult> CreateAsync(string? gameRoot, CancellationToken ct = default) =>
        Task.Run(() => CreateCore(gameRoot, ct), ct);

    private DiagnosticArchiveResult CreateCore(string? gameRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var backups = Path.Combine(_dataRoot, "backups");
        Directory.CreateDirectory(backups);
        var archiveId = Guid.NewGuid().ToString("N")[..8];
        var archive = Path.Combine(backups, $"csl-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}-{archiveId}.zip");
        var temporaryArchive = archive + ".tmp";
        var files = new List<string>();
        Add(files, Path.Combine(_dataRoot, "settings.json"));
        Add(files, Path.Combine(_dataRoot, "logs"), recursive: true);
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            Add(files, Path.Combine(gameRoot, "cstrike", "steam.inf"));
            Add(files, SteamPathHelper.GetAppManifestPath(gameRoot));
        }

        var uniqueFiles = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var inventoryFiles = new List<string>();
        foreach (var inventoryRoot in new[] { Path.Combine(backups, "settings") })
            inventoryFiles.AddRange(EnumerateSafeFiles(inventoryRoot));
        var inventory = inventoryFiles
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return $"{Path.GetRelativePath(backups, path)}\t{info.Length} bytes\t{info.LastWriteTimeUtc:O}";
            })
            .ToArray();
        try
        {
            using (var zip = ZipFile.Open(temporaryArchive, ZipArchiveMode.Create))
            {
                foreach (var file in uniqueFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(file, GetEntryName(file, gameRoot));
                }
                var inventoryEntry = zip.CreateEntry("backup-inventory.txt");
                using var inventoryStream = inventoryEntry.Open();
                using var writer = new StreamWriter(inventoryStream, new UTF8Encoding(false));
                writer.WriteLine("Backup inventory only; backup contents are not included.");
                foreach (var line in inventory) writer.WriteLine(line);
            }
            File.Move(temporaryArchive, archive, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
        }
        return new DiagnosticArchiveResult(archive, uniqueFiles.Count + 1, new FileInfo(archive).Length);
    }

    private static void Add(List<string> files, string path, bool recursive = false)
    {
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) files.Add(path);
        }
        else if (recursive)
        {
            files.AddRange(EnumerateSafeFiles(path));
        }
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
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
