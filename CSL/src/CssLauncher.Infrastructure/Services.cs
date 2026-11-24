using System.Text.Json; using CssLauncher.Core;
namespace CssLauncher.Infrastructure;
public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _path; public JsonSettingsService(string? appData = null) => _path = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher", "settings.json");
    public async Task<LauncherSettings> LoadAsync(CancellationToken ct = default) { try { if (!File.Exists(_path)) { var d = new LauncherSettings(); await SaveAsync(d, ct); return d; } return JsonSerializer.Deserialize<LauncherSettings>(await File.ReadAllTextAsync(_path, ct), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new LauncherSettings(); } catch { return new LauncherSettings(); } }
    public async Task SaveAsync(LauncherSettings settings, CancellationToken ct = default) { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), ct); }
}
public sealed class FileLogger : IAppLogger, IDisposable
{
    private readonly StreamWriter _writer; public event Action<string>? Message; public FileLogger(string? appData = null) { var dir = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher", "logs"); Directory.CreateDirectory(dir); _writer = File.AppendText(Path.Combine(dir, $"launcher-{DateTime.UtcNow:yyyyMMdd}.log")); }
    public void Info(string m) => Write("INFO", m); public void Warning(string m) => Write("WARN", m); public void Error(string m, Exception? e = null) => Write("ERROR", m + (e is null ? "" : " " + e.Message)); private void Write(string level, string m) { var line = $"[{DateTime.Now:O}] [{level}] {m}"; _writer.WriteLine(line); _writer.Flush(); Message?.Invoke(line); } public void Dispose() => _writer.Dispose();
}
