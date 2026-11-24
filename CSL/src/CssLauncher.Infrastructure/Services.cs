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

public sealed class JsonSettingsService : ISettingsService
{
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
            await using var input = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<LauncherSettings>(input, JsonOptions, ct)
                ?? throw new JsonException("Файл настроек содержит пустой JSON.");
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
    private readonly StreamWriter _writer;
    private readonly object _sync = new();
    private bool _disposed;

    public event Action<string>? Message;

    public FileLogger(string? appData = null)
    {
        var directory = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher", "logs");
        Directory.CreateDirectory(directory);
        _writer = File.AppendText(Path.Combine(directory, $"launcher-{DateTime.UtcNow:yyyyMMdd}.log"));
    }

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message + (exception is null ? "" : " " + exception.Message));

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:O}] [{level}] {message}";
        lock (_sync)
        {
            if (_disposed) return;
            _writer.WriteLine(line);
            _writer.Flush();
        }
        Message?.Invoke(line);
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
