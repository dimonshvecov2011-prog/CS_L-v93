using System.Text;
using System.Text.Json;
using CssLauncher.Core;

namespace CssLauncher.Infrastructure;

public sealed class SettingsCorruptedException : Exception
{
    public string BackupPath { get; }

    public SettingsCorruptedException(string backupPath, Exception innerException)
        : base($"Файл настроек повреждён; исходник сохранён как {backupPath}.", innerException) =>
        BackupPath = backupPath;
}

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
    private const long MaxSettingsBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;

    public JsonSettingsService(string? appData = null) =>
        _path = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher", "settings.json");

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
        catch (JsonException ex)
        {
            var backupPath = GetBackupPath();
            File.Move(_path, backupPath);
            throw new SettingsCorruptedException(backupPath, ex);
        }
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion != CurrentSchemaVersion)
            throw new UnsupportedSettingsSchemaException(settings.SchemaVersion);

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"settings.json.tmp-{Guid.NewGuid():N}");

        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(output, settings, JsonOptions, ct);
                await output.FlushAsync(ct);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private string GetBackupPath()
    {
        var backup = _path + ".bak";
        if (!File.Exists(backup)) return backup;
        return _path + $".bak-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
    }
}

public sealed class FileLogger : IAppLogger, IDisposable
{
    private const long MaxLogFileBytes = 5L * 1024 * 1024;
    private const int MaxLogFileCount = 14;
    private readonly string _directory;
    private string _path;
    private StreamWriter _writer;
    private readonly object _sync = new();
    private bool _disposed;

    public event Action<string>? Message;

    public FileLogger(string? appData = null)
    {
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

    private string GetDailyPath() => Path.Combine(_directory, $"launcher-{DateTime.UtcNow:yyyyMMdd}.log");

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
