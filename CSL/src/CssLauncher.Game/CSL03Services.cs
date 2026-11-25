using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CssLauncher.Core;

namespace CssLauncher.Game;

public sealed class ProfileCatalogService
{
    private const int MaxProfileBackups = 5;
    private static readonly Regex SearchPathLine = new(@"^\s*[A-Za-z][A-Za-z0-9+_]*\s+(?:""([^""]+)""|([^\s]+))", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex FileSystemBlock = new(@"(?im)^\s*""?FileSystem""?\s*\{", RegexOptions.Compiled);
    private static readonly Regex SearchPathsBlock = new(@"(?im)^\s*""?SearchPaths""?\s*\{", RegexOptions.Compiled);
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

    public IReadOnlyList<ProfileBackupInfo> GetBackups(string id)
    {
        EnsureNoReparsePoints(_root);
        var directory = GetBackupDirectory(id);
        EnsureNoReparsePoints(directory);
        if (!Directory.Exists(directory)) return Array.Empty<ProfileBackupInfo>();
        var paths = Directory.GetDirectories(directory);
        foreach (var path in paths) EnsureNoReparsePointsRecursive(path);
        return paths
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Select(path => new ProfileBackupInfo(path, Directory.GetLastWriteTimeUtc(path)))
            .ToArray();
    }

    public Task<string> PrepareAsync(string gameRoot, LauncherProfile profile, string templatePath, CancellationToken ct = default) =>
        PrepareCoreAsync(gameRoot, profile, async token =>
        {
            if (!File.Exists(templatePath)) throw new FileNotFoundException("Не найден шаблон gameinfo.", templatePath);
            return await File.ReadAllTextAsync(templatePath, token);
        }, preserveExistingData: true, reuseIfCurrent: true, ct: ct);

    public Task<string> PrepareFromGameInfoAsync(string gameRoot, LauncherProfile profile, CancellationToken ct = default) =>
        PrepareCoreAsync(gameRoot, profile, token => ReadOfficialGameInfoAsync(gameRoot, token),
            preserveExistingData: true, reuseIfCurrent: true, ct: ct);

    private async Task<string> PrepareCoreAsync(string gameRoot, LauncherProfile profile,
        Func<CancellationToken, Task<string>> gameInfoFactory,
        bool preserveExistingData, bool reuseIfCurrent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            throw new DirectoryNotFoundException("Папка игры не найдена.");
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        var builtInProfile = Get(profile.Id);
        var path = GetPath(builtInProfile.Id);
        var parent = Path.GetDirectoryName(path)!;
        EnsureNoReparsePoints(parent);
        Directory.CreateDirectory(parent);
        EnsureNoReparsePoints(parent);
        EnsureNoReparsePoints(path);
        EnsureNoReparsePoints(GetBackupDirectory(builtInProfile.Id));
        var staging = path + ".staging-" + Guid.NewGuid().ToString("N");
        string? backup = null;
        try
        {
            var gameInfo = (await gameInfoFactory(ct)).Replace("__PROFILE__", path.Replace("\\", "/"), StringComparison.Ordinal);
            var normalizedRoot = Path.GetFullPath(gameRoot).Replace("\\", "/");
            if (gameInfo.Contains("{GAME_ROOT}", StringComparison.Ordinal))
                gameInfo = gameInfo.Replace("{GAME_ROOT}", normalizedRoot, StringComparison.Ordinal);
            if (gameInfo.Contains("{GAME_ROOT}", StringComparison.Ordinal))
                throw new InvalidDataException("В gameinfo.txt осталась неподставленная переменная {GAME_ROOT}.");
            var profileJson = JsonSerializer.Serialize(builtInProfile, new JsonSerializerOptions { WriteIndented = true });
            if (reuseIfCurrent && Directory.Exists(path) && ValidateProfile(path, gameRoot).Count == 0 &&
                File.Exists(Path.Combine(path, "profile.json")) &&
                string.Equals(await File.ReadAllTextAsync(Path.Combine(path, "gameinfo.txt"), ct), gameInfo, StringComparison.Ordinal) &&
                string.Equals(await File.ReadAllTextAsync(Path.Combine(path, "profile.json"), ct), profileJson, StringComparison.Ordinal))
                return path;

            Directory.CreateDirectory(Path.Combine(staging, "custom"));
            if (preserveExistingData && Directory.Exists(path)) CopyPreservedProfileContents(path, staging, ct);
            await WriteAtomicallyAsync(Path.Combine(staging, "gameinfo.txt"), gameInfo, ct);
            await WriteAtomicallyAsync(Path.Combine(staging, "profile.json"), profileJson, ct);
            var validation = ValidateProfile(staging, gameRoot, path);
            if (validation.Count > 0)
                throw new InvalidDataException("Сгенерированный профиль не прошёл проверку SearchPaths:\n" + string.Join("\n", validation));

            if (Directory.Exists(path))
            {
                backup = GetNewBackupPath(builtInProfile.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                EnsureNoReparsePoints(Path.GetDirectoryName(backup)!);
                await MoveDirectoryWithRetriesAsync(path, backup, ct);
            }
            try
            {
                await MoveDirectoryWithRetriesAsync(staging, path, ct);
                var finalErrors = ValidateProfile(path, gameRoot);
                if (finalErrors.Count > 0)
                    throw new InvalidDataException("Профиль после установки не прошёл проверку:\n" + string.Join("\n", finalErrors));
            }
            catch
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                if (backup is not null && Directory.Exists(backup))
                    await MoveDirectoryWithRetriesAsync(backup, path, CancellationToken.None);
                throw;
            }
            PruneBackups(builtInProfile.Id);
            return path;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Нет доступа для создания или резервного копирования профиля CS:L. Проверьте права на %AppData%\\CssLauncher.", ex);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public async Task<string> RollbackAsync(string id, string? backupPath = null, string? gameRoot = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var profile = Get(id);
        var backups = GetBackups(profile.Id);
        var selected = backupPath is null
            ? backups.FirstOrDefault()?.Path
            : backups.FirstOrDefault(item => Path.GetFullPath(item.Path).Equals(Path.GetFullPath(backupPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))?.Path;
        if (selected is null) throw new DirectoryNotFoundException("Резервная копия профиля не найдена.");
        EnsureNoReparsePointsRecursive(selected);
        var selectedErrors = ValidateProfile(selected, gameRoot ?? string.Empty);
        if (selectedErrors.Count > 0)
            throw new InvalidDataException("Выбранная резервная копия профиля не прошла проверку:\n" + string.Join("\n", selectedErrors));
        var target = GetPath(profile.Id);
        EnsureNoReparsePoints(target);
        EnsureNoReparsePoints(GetBackupDirectory(profile.Id));
        var currentBackup = Directory.Exists(target) ? GetNewBackupPath(profile.Id) : null;
        if (currentBackup is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(currentBackup)!);
            EnsureNoReparsePoints(Path.GetDirectoryName(currentBackup)!);
            await MoveDirectoryWithRetriesAsync(target, currentBackup, ct);
        }
        try
        {
            await MoveDirectoryWithRetriesAsync(selected, target, ct);
            var errors = ValidateProfile(target, gameRoot ?? string.Empty);
            if (errors.Count > 0)
                throw new InvalidDataException("Восстановленный профиль не прошёл проверку:\n" + string.Join("\n", errors));
            PruneBackups(profile.Id);
            return target;
        }
        catch (Exception updateError)
        {
            try
            {
                if (Directory.Exists(target) && !Directory.Exists(selected))
                    await MoveDirectoryWithRetriesAsync(target, selected, CancellationToken.None);
                if (!Directory.Exists(target) && currentBackup is not null && Directory.Exists(currentBackup))
                    await MoveDirectoryWithRetriesAsync(currentBackup, target, CancellationToken.None);
            }
            catch (Exception restoreError)
            {
                throw new AggregateException("Проверка или откат резервной копии профиля завершились ошибкой; сохранённые каталоги не удаляйте.",
                    updateError, restoreError);
            }
            throw;
        }
    }

    public Task<string> ResetToFactoryAsync(string gameRoot, string id, string templatePath, CancellationToken ct = default) =>
        PrepareCoreAsync(gameRoot, Get(id), async token =>
        {
            if (!File.Exists(templatePath)) throw new FileNotFoundException("Не найден шаблон gameinfo.", templatePath);
            return await File.ReadAllTextAsync(templatePath, token);
        }, preserveExistingData: false, reuseIfCurrent: false, ct);

    public Task<string> ResetToFactoryFromGameInfoAsync(string gameRoot, string id, CancellationToken ct = default) =>
        PrepareCoreAsync(gameRoot, Get(id), token => ReadOfficialGameInfoAsync(gameRoot, token),
            preserveExistingData: false, reuseIfCurrent: false, ct: ct);

    public async Task ResetAllToFactoryAsync(string? gameRoot, string templatePath, CancellationToken ct = default)
    {
        foreach (var profile in BuiltInProfiles)
        {
            ct.ThrowIfCancellationRequested();
            var current = GetPath(profile.Id);
            EnsureNoReparsePoints(_root);
            EnsureNoReparsePoints(current);
            EnsureNoReparsePoints(GetBackupDirectory(profile.Id));
            if (!string.IsNullOrWhiteSpace(gameRoot) && Directory.Exists(gameRoot))
            {
                await ResetToFactoryAsync(gameRoot, profile.Id, templatePath, ct);
                continue;
            }
            if (!Directory.Exists(current)) continue;
            var backup = GetNewBackupPath(profile.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            EnsureNoReparsePoints(Path.GetDirectoryName(backup)!);
            await MoveDirectoryWithRetriesAsync(current, backup, ct);
            PruneBackups(profile.Id);
        }
    }

    public async Task ResetAllToFactoryFromGameInfoAsync(string gameRoot, CancellationToken ct = default)
    {
        foreach (var profile in BuiltInProfiles)
        {
            ct.ThrowIfCancellationRequested();
            await ResetToFactoryFromGameInfoAsync(gameRoot, profile.Id, ct);
        }
    }

    public IReadOnlyList<string> ValidateProfile(string profilePath, string gameRoot, string? expectedProfilePath = null)
    {
        EnsureNoReparsePointsRecursive(profilePath);
        var errors = new List<string>();
        var gameInfoPath = Path.Combine(profilePath, "gameinfo.txt");
        if (!File.Exists(gameInfoPath)) return new[] { "gameinfo.txt отсутствует." };
        var text = File.ReadAllText(gameInfoPath);
        var filesystemMatch = FileSystemBlock.Match(text);
        if (!filesystemMatch.Success) return new[] { "В gameinfo.txt отсутствует блок FileSystem." };
        var filesystemOpen = filesystemMatch.Index + filesystemMatch.Length - 1;
        var filesystemBody = ExtractBalancedBlock(text, filesystemOpen);
        if (filesystemBody is null) return new[] { "Блок FileSystem в gameinfo.txt не закрыт корректно." };
        var searchPathsMatch = SearchPathsBlock.Match(filesystemBody);
        if (!searchPathsMatch.Success) return new[] { "В блоке FileSystem отсутствует SearchPaths." };
        var searchPathsOpen = searchPathsMatch.Index + searchPathsMatch.Length - 1;
        var searchPathsBody = ExtractBalancedBlock(filesystemBody, searchPathsOpen);
        if (searchPathsBody is null) return new[] { "Блок SearchPaths в gameinfo.txt не закрыт корректно." };
        var searchPaths = SearchPathLine.Matches(searchPathsBody)
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).ToArray();
        if (searchPaths.Length == 0) return new[] { "В gameinfo.txt не найден блок SearchPaths." };
        foreach (var entry in searchPaths)
        {
            var entryForValidation = entry;
            if (expectedProfilePath is not null)
            {
                var expectedProfileText = Path.GetFullPath(expectedProfilePath).Replace('\\', '/').TrimEnd('/');
                var stagedProfileText = Path.GetFullPath(profilePath).Replace('\\', '/').TrimEnd('/');
                entryForValidation = entryForValidation.Replace(expectedProfileText, stagedProfileText, StringComparison.OrdinalIgnoreCase);
            }
            var expanded = entryForValidation.Replace("|gameinfo_path|", Path.Combine(Path.GetFullPath(gameRoot), "cstrike") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                .Replace("|all_source_engine_paths|", Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                .Replace("{GAME_ROOT}", Path.GetFullPath(gameRoot), StringComparison.OrdinalIgnoreCase)
                .Replace('/', Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(expanded))
                expanded = Path.Combine(Path.GetFullPath(gameRoot), expanded);
            var wildcard = expanded.IndexOf('*');
            if (wildcard >= 0) expanded = expanded[..wildcard].TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(expanded);
            var downloadPath = Path.Combine(Path.GetFullPath(gameRoot), "cstrike", "download");
            if (full.Equals(downloadPath, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(full)) continue;
            var profileEquivalent = TryMapToProfilePath(full, expectedProfilePath ?? profilePath);
            if (!SearchPathExists(full) && (profileEquivalent is null || !SearchPathExists(profileEquivalent)))
                errors.Add($"Не найден SearchPath: {entry}");
        }
        return errors;
    }

    private static bool SearchPathExists(string fullPath)
    {
        if (File.Exists(fullPath) || Directory.Exists(fullPath)) return true;
        if (!fullPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)) return false;
        var directoryVariant = Path.Combine(Path.GetDirectoryName(fullPath)!, Path.GetFileNameWithoutExtension(fullPath) + "_dir.vpk");
        return File.Exists(directoryVariant);
    }

    private static string? TryMapToProfilePath(string fullPath, string profilePath)
    {
        var expectedRoot = Path.GetFullPath(profilePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = Path.GetFullPath(fullPath);
        if (normalized.Equals(expectedRoot, StringComparison.OrdinalIgnoreCase)) return expectedRoot;
        var prefix = expectedRoot + Path.DirectorySeparatorChar;
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(profilePath, normalized[prefix.Length..])
            : null;
    }

    private static async Task<string> ReadOfficialGameInfoAsync(string gameRoot, CancellationToken ct)
    {
        var sourcePath = Path.Combine(gameRoot, "cstrike", "gameinfo.txt");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Не найден официальный cstrike\\gameinfo.txt. Профиль не создан, запуск отменён.", sourcePath);

        var source = await File.ReadAllTextAsync(sourcePath, ct);
        var filesystemMatch = FileSystemBlock.Match(source);
        if (!filesystemMatch.Success)
            throw new InvalidDataException("В официальном cstrike\\gameinfo.txt отсутствует блок FileSystem. Профиль не создан.");
        var filesystemBody = ExtractBalancedBlock(source, filesystemMatch.Index + filesystemMatch.Length - 1)
            ?? throw new InvalidDataException("Блок FileSystem в официальном cstrike\\gameinfo.txt не закрыт.");
        var searchPathsMatch = SearchPathsBlock.Match(filesystemBody);
        var searchPathsBody = searchPathsMatch.Success
            ? ExtractBalancedBlock(filesystemBody, searchPathsMatch.Index + searchPathsMatch.Length - 1)
            : null;
        if (searchPathsBody is null)
            throw new InvalidDataException("В официальном cstrike\\gameinfo.txt отсутствует блок SearchPaths. Профиль не создан.");

        var gameRootPath = Path.GetFullPath(gameRoot).Replace("\\", "/").TrimEnd('/');
        var cstrikePath = gameRootPath + "/cstrike";
        var lines = new List<string> { "            game+mod+custom_mod   \"__PROFILE__/custom/*\"" };
        foreach (Match match in SearchPathLine.Matches(searchPathsBody))
        {
            var line = match.Value.Trim();
            if (line.Contains("custom/*", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("default_write_path", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("mod_write", StringComparison.OrdinalIgnoreCase)) continue;
            var sourcePathValue = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            var resolvedPath = sourcePathValue
                .Replace("|gameinfo_path|", cstrikePath + "/", StringComparison.OrdinalIgnoreCase)
                .Replace("|all_source_engine_paths|", gameRootPath + "/", StringComparison.OrdinalIgnoreCase);
            if (!resolvedPath.StartsWith('/', StringComparison.Ordinal) &&
                !Regex.IsMatch(resolvedPath, @"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant))
                resolvedPath = gameRootPath + "/" + resolvedPath.TrimStart('/');
            var rendered = line.Replace(sourcePathValue, resolvedPath, StringComparison.Ordinal);
            if (!match.Groups[1].Success && resolvedPath.Contains(' '))
                rendered = rendered.Replace(resolvedPath, $"\"{resolvedPath}\"", StringComparison.Ordinal);
            lines.Add("            " + rendered);
        }
        lines.Add("            mod+mod_write+default_write_path  \"__PROFILE__\"");
        return "\"GameInfo\"\n{\n    game \"CS:S Launcher Profile\"\n    title \"\"\n    type multiplayer_only\n\n    FileSystem\n    {\n        SteamAppId 240\n        SearchPaths\n        {\n" + string.Join(Environment.NewLine, lines) + "\n        }\n    }\n}\n";
    }

    private static string? ExtractBalancedBlock(string text, int openingBrace)
    {
        if (openingBrace < 0 || openingBrace >= text.Length || text[openingBrace] != '{') return null;
        var depth = 0;
        var quoted = false;
        var escaped = false;
        var lineComment = false;
        for (var index = openingBrace; index < text.Length; index++)
        {
            var character = text[index];
            if (lineComment)
            {
                if (character is '\r' or '\n') lineComment = false;
                continue;
            }
            if (quoted)
            {
                if (escaped) { escaped = false; continue; }
                if (character == '\\') { escaped = true; continue; }
                if (character == '"') quoted = false;
                continue;
            }
            if (character == '"') { quoted = true; continue; }
            if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                lineComment = true;
                index++;
                continue;
            }
            if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return text[(openingBrace + 1)..index];
        }
        return null;
    }

    private static void CopyPreservedProfileContents(string source, string destination, CancellationToken ct)
    {
        EnsureNoReparsePointsRecursive(source);
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, entry);
                if (relative.Equals("gameinfo.txt", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("profile.json", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(destination, relative);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Ссылки внутри профиля запрещены: {entry}");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    pending.Push(entry);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(entry, target, overwrite: false);
                }
            }
        }
    }

    private static async Task MoveDirectoryWithRetriesAsync(string source, string destination, CancellationToken ct)
    {
        var delays = new[] { 50, 150, 350 };
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException) when (attempt < delays.Length)
            {
                await Task.Delay(delays[attempt], ct);
            }
            catch (UnauthorizedAccessException) when (attempt < delays.Length)
            {
                await Task.Delay(delays[attempt], ct);
            }
        }
    }

    private static async Task MoveFileWithRetriesAsync(string source, string destination, CancellationToken ct)
    {
        var delays = new[] { 40, 120, 300 };
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < delays.Length)
            {
                await Task.Delay(delays[attempt], ct);
            }
            catch (UnauthorizedAccessException) when (attempt < delays.Length)
            {
                await Task.Delay(delays[attempt], ct);
            }
        }
    }

    private string GetBackupDirectory(string id) => Path.Combine(_root, "..", "backups", "profiles", Get(id).Id);

    private static void EnsureNoReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var component in full[current.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Ссылки файловой системы в каталоге профилей запрещены: {current}");
        }
    }

    private static void EnsureNoReparsePointsRecursive(string path)
    {
        EnsureNoReparsePoints(path);
        if (!Directory.Exists(path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Ссылки внутри каталогов профилей запрещены: {entry}");
            if ((attributes & FileAttributes.Directory) != 0) EnsureNoReparsePointsRecursive(entry);
        }
    }

    private string GetNewBackupPath(string id)
    {
        var directory = GetBackupDirectory(id);
        return Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
    }

    private void PruneBackups(string id)
    {
        var backups = GetBackups(id);
        foreach (var stale in backups.Skip(MaxProfileBackups))
        {
            try { Directory.Delete(stale.Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task WriteAtomicallyAsync(string path, string contents, CancellationToken ct)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                await writer.WriteAsync(contents.AsMemory(), ct);
                await writer.FlushAsync(ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            await MoveFileWithRetriesAsync(temporary, path, ct);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed record ProfileBackupInfo(string Path, DateTime CreatedUtc);

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
        var temporaryArchive = archive + ".tmp";
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
        var inventoryFiles = new List<string>();
        foreach (var inventoryRoot in new[] { Path.Combine(backups, "settings"), Path.Combine(backups, "profiles") })
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
