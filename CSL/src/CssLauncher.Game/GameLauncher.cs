using System.Diagnostics;
using CssLauncher.Core;

namespace CssLauncher.Game;

public sealed class GameLauncher : IGameLauncher
{
    public async Task<int> LaunchAsync(string gameRoot, string profilePath, LaunchOptions options, Action<string>? log = null, CancellationToken ct = default)
    {
        var detected = GameExecutableDetector.Detect(gameRoot);
        foreach (var path in detected.FoundPaths) log?.Invoke($"Найден исполняемый файл: {path}");
        var exe = string.IsNullOrWhiteSpace(options.Executable) ? detected.PreferredPath : Path.Combine(gameRoot, options.Executable);
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) throw new FileNotFoundException("Не найден исполняемый файл cstrike.exe или hl2.exe.", exe);
        if (options.Steam && !IsSteamRunning()) log?.Invoke("ПРЕДУПРЕЖДЕНИЕ: Steam не запущен. Игра может потребовать запущенный Steam.");
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = gameRoot, UseShellExecute = false };
        if (options.Steam) psi.ArgumentList.Add("-steam");
        psi.ArgumentList.Add("-game"); psi.ArgumentList.Add(profilePath);
        if (options.Novid) psi.ArgumentList.Add("-novid"); if (options.Console) psi.ArgumentList.Add("-console"); if (options.Windowed) psi.ArgumentList.Add("-windowed"); if (options.Noborder) psi.ArgumentList.Add("-noborder");
        if (options.Width is not null) { psi.ArgumentList.Add("-w"); psi.ArgumentList.Add(options.Width.Value.ToString()); }
        if (options.Height is not null) { psi.ArgumentList.Add("-h"); psi.ArgumentList.Add(options.Height.Value.ToString()); }
        foreach (var arg in SplitExtra(options.Extra)) psi.ArgumentList.Add(arg);
        log?.Invoke("Запуск: " + exe + " " + string.Join(" ", psi.ArgumentList.Select(Quote)));
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить игру.");
        await process.WaitForExitAsync(ct); log?.Invoke($"Игра завершила работу с кодом {process.ExitCode}."); return process.ExitCode;
    }
    public static bool IsSteamRunning() => Process.GetProcessesByName("steam").Length > 0;
    public static IEnumerable<string> SplitExtra(string extra) => string.IsNullOrWhiteSpace(extra) ? Array.Empty<string>() : System.Text.RegularExpressions.Regex.Matches(extra, @"[^\s""']+|""([^""]*)""|'([^']*)'").Select(m => m.Value.Trim('"', '\''));
    private static string Quote(string x) => x.Contains(' ') ? '"' + x + '"' : x;
}
