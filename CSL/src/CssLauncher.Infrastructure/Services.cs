using System.Text;
using System.Text.Json;
using CssLauncher.Core;

namespace CssLauncher.Infrastructure;

public sealed class SettingsCorruptedException : Exception
{
    public string BackupPath { get; }
    public IReadOnlyList<SettingsBackupInfo> RecoverableBackups { get; }

    public SettingsCorruptedException(string backupPath, Exception innerException, IReadOnlyList<SettingsBackupInfo>? recoverableBackups = null)
        : base($"Файл настроек повреждён; исходник сохранён как {backupPath}.", innerException) =>
        (BackupPath, RecoverableBackups) = (backupPath, recoverableBackups ?? Array.Empty<SettingsBackupInfo>());
}

public sealed record SettingsBackupInfo(string Path, DateTime CreatedUtc, long SizeBytes);

public sealed class UnsupportedSettingsSchemaException : Exception
{
    public int FoundSchemaVersion { get; }

    public UnsupportedSettingsSchemaException(int foundSchemaVersion)
        : base($"Версия схемы настроек {foundSchemaVersion} не поддерживается этой сборкой. Файл настроек не изменён.") =>
        FoundSchemaVersion = foundSchemaVersion;
}

public sealed class JsonSettingsService : ISettingsService
{
    public const int CurrentSchemaVersion = 1;
    private const int MaxBackupCount = 5;
    private const long MaxSettingsBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly string _backupDirectory;

    public JsonSettingsService(string? appData = null)
    {
        var root = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher");
        _path = Path.Combine(root, "settings.json");
        _backupDirectory = Path.Combine(root, "backups", "settings");
    }

    public async Task<LauncherSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            var defaults = new LauncherSettings();
            await SaveAsync(defaults, ct);
            return defaults;
        }

        try
        {
            var info = new FileInfo(_path);
            if (info.Length > MaxSettingsBytes) throw new InvalidDataException("Файл настроек превышает допустимый размер 1 МБ.");
            await using var input = File.OpenRead(_path);
            var settings = await JsonSerializer.DeserializeAsync<LauncherSettings>(input, JsonOptions, ct)
                ?? throw new JsonException("Файл настроек содержит пустой JSON.");
            if (settings.SchemaVersion != CurrentSchemaVersion)
                throw new UnsupportedSettingsSchemaException(settings.SchemaVersion);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            await CreateSnapshotAsync(_path, ct);
            var backupPath = GetBackupPath();
            await ReplaceWithRetriesAsync(_path, backupPath, ct);
            throw new SettingsCorruptedException(backupPath, ex, await GetRecoverableBackupsAsync(ct));
        }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion != CurrentSchemaVersion)
            throw new UnsupportedSettingsSchemaException(settings.SchemaVersion);

        var directory = Path.GetDirectoryName(_path)!;
        EnsureNoReparsePoints(directory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(directory);
        var temporaryPath = Path.Combine(directory, $"settings.json.tmp-{Guid.NewGuid():N}");

        try
        {
            if (File.Exists(_path)) await CreateSnapshotAsync(_path, ct);
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(output, settings, JsonOptions, ct);
                if (output.Length > MaxSettingsBytes)
                    throw new InvalidDataException("Сериализованные настройки превышают допустимый размер 1 МБ; текущий файл сохранён без изменений.");
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            await ReplaceWithRetriesAsync(temporaryPath, _path, ct);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Нет доступа для безопасного сохранения settings.json. Проверьте права доступа к папке данных CS:L.", ex);
        }
        catch (IOException ex) when (ex.Message.Contains("space", StringComparison.OrdinalIgnoreCase) || ex.HResult == unchecked((int)0x80070070))
        {
            throw new IOException("Недостаточно места для сохранения настроек CS:L. Освободите место на диске и повторите попытку.", ex);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<IReadOnlyList<SettingsBackupInfo>> GetRecoverableBackupsAsync(CancellationToken ct = default)
    {
        EnsureNoReparsePoints(_backupDirectory);
        if (!Directory.Exists(_backupDirectory)) return Array.Empty<SettingsBackupInfo>();
        var result = new List<SettingsBackupInfo>();
        foreach (var file in Directory.GetFiles(_backupDirectory, "settings-*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                if (info.Length is <= 0 or > MaxSettingsBytes) continue;
                await using var input = File.OpenRead(file);
                var settings = await JsonSerializer.DeserializeAsync<LauncherSettings>(input, JsonOptions, ct);
                if (settings?.SchemaVersion != CurrentSchemaVersion) continue;
                result.Add(new SettingsBackupInfo(file, info.LastWriteTimeUtc, info.Length));
            }
            catch (JsonException) { }
        }
        return result;
    }

    public async Task<LauncherSettings> RestoreBackupAsync(string backupPath, CancellationToken ct = default)
    {
        EnsureNoReparsePoints(_backupDirectory);
        var fullBackupDirectory = Path.GetFullPath(_backupDirectory) + Path.DirectorySeparatorChar;
        var fullBackupPath = Path.GetFullPath(backupPath);
        if (!fullBackupPath.StartsWith(fullBackupDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !File.Exists(fullBackupPath))
            throw new InvalidDataException("Выбранная копия настроек не находится в каталоге резервных копий CS:L.");
        var info = new FileInfo(fullBackupPath);
        if (info.Length is <= 0 or > MaxSettingsBytes) throw new InvalidDataException("Размер выбранной копии настроек недопустим.");
        await using var input = File.OpenRead(fullBackupPath);
        var settings = await JsonSerializer.DeserializeAsync<LauncherSettings>(input, JsonOptions, ct)
            ?? throw new JsonException("Выбранная копия настроек пуста.");
        if (settings.SchemaVersion != CurrentSchemaVersion)
            throw new UnsupportedSettingsSchemaException(settings.SchemaVersion);
        await SaveAsync(settings, ct);
        return settings;
    }

    private async Task CreateSnapshotAsync(string source, CancellationToken ct)
    {
        EnsureNoReparsePoints(source);
        EnsureNoReparsePoints(_backupDirectory);
        Directory.CreateDirectory(_backupDirectory);
        EnsureNoReparsePoints(_backupDirectory);
        var name = $"settings-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json";
        var target = Path.Combine(_backupDirectory, name);
        var temporary = target + ".tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await input.CopyToAsync(output, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            await ReplaceWithRetriesAsync(temporary, target, ct);
            PruneSnapshots();
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task ReplaceWithRetriesAsync(string source, string target, CancellationToken ct)
    {
        var delays = new[] { 40, 120, 300 };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
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

    private void PruneSnapshots()
    {
        var snapshots = Directory.GetFiles(_backupDirectory, "settings-*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        foreach (var stale in snapshots.Skip(MaxBackupCount))
        {
            try { File.Delete(stale); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string GetBackupPath()
    {
        var backup = _path + ".bak";
        if (!File.Exists(backup)) return backup;
        return _path + $".bak-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
    }

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
                throw new InvalidDataException($"Символьные ссылки в каталоге данных CS:L запрещены: {current}");
        }
    }
}

public sealed class FileLogger : IAppLogger, IDisposable
{
    private const long MaxLogFileBytes = 5L * 1024 * 1024;
    private const int MaxLogFileCount = 14;
    private readonly string _directory;
    private readonly Func<DateTime> _utcNow;
    private string _path;
    private StreamWriter _writer;
    private readonly object _sync = new();
    private bool _disposed;

    public event Action<string>? Message;

    public FileLogger(string? appData = null, Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _directory = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher", "logs");
        Directory.CreateDirectory(_directory);
        _path = GetDailyPath();
        PruneOldLogs();
        _writer = File.AppendText(_path);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);
    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", message + (exception is null ? "" : Environment.NewLine + exception));

    private void Write(string level, string message)
    {
        var prefix = $"[{DateTime.Now:O}] [{level}] ";
        var line = prefix + message;
        var newlineBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);
        var maxLineBytes = MaxLogFileBytes - newlineBytes;
        if (Encoding.UTF8.GetByteCount(line) > maxLineBytes)
        {
            const string marker = " [log entry truncated]";
            var messageBudget = (int)Math.Max(0, maxLineBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(marker));
            line = prefix + TruncateUtf8(message, messageBudget) + marker;
        }
        var entryBytes = Encoding.UTF8.GetByteCount(line) + newlineBytes;
        lock (_sync)
        {
            if (_disposed) return;
            SwitchDailyFileIfNeeded();
            RotateIfNeeded(entryBytes);
            _writer.WriteLine(line);
            _writer.Flush();
        }
        Publish(line);
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes) break;
            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return builder.ToString();
    }

    private void RotateIfNeeded(long incomingBytes)
    {
        if (_writer.BaseStream.Length + incomingBytes <= MaxLogFileBytes) return;
        _writer.Dispose();
        var rotated = _path + $".{DateTime.UtcNow:HHmmss}-{Guid.NewGuid():N}";
        File.Move(_path, rotated);
        _writer = File.AppendText(_path);
        PruneOldLogs();
    }

    private string GetDailyPath() => Path.Combine(_directory, $"launcher-{_utcNow():yyyyMMdd}.log");

    private void SwitchDailyFileIfNeeded()
    {
        var nextPath = GetDailyPath();
        if (string.Equals(nextPath, _path, StringComparison.OrdinalIgnoreCase)) return;
        _writer.Dispose();
        _path = nextPath;
        _writer = File.AppendText(_path);
        PruneOldLogs();
    }

    private void PruneOldLogs()
    {
        var files = Directory.GetFiles(_directory, "launcher-*.log*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        foreach (var file in files.Skip(MaxLogFileCount))
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Publish(string line)
    {
        var handlers = Message;
        if (handlers is null) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(line); }
            catch { /* A UI subscriber must not break application logging. */ }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}
