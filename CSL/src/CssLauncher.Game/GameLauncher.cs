using System.Diagnostics;
using CssLauncher.Core;

namespace CssLauncher.Game;

public sealed class GameLauncher : IGameLauncher
{
    private static readonly string[] AllowedExecutables = ["cstrike.exe", "hl2.exe"];

    public async Task<int> LaunchAsync(string gameRoot, string profilePath, LaunchOptions options,
        Action<string>? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            throw new DirectoryNotFoundException("Папка игры не найдена.");
        if (string.IsNullOrWhiteSpace(profilePath) || !Directory.Exists(profilePath))
            throw new DirectoryNotFoundException("Папка профиля запуска не найдена.");
        if (options.Width is <= 0 or > 16384) throw new ArgumentOutOfRangeException(nameof(options.Width));
        if (options.Height is <= 0 or > 16384) throw new ArgumentOutOfRangeException(nameof(options.Height));

        var normalizedRoot = Path.GetFullPath(gameRoot);
        var resolvedRoot = ResolveDirectoryPath(normalizedRoot);
        var detected = GameExecutableDetector.Detect(normalizedRoot);
        foreach (var path in detected.FoundPaths) log?.Invoke($"Найден исполняемый файл: {path}");

        var requested = options.Executable?.Trim() ?? "";
        string? exe;
        if (requested.Length == 0)
        {
            exe = detected.PreferredPath;
        }
        else
        {
            if (requested.Contains('/') || requested.Contains('\\') ||
                !AllowedExecutables.Any(name => name.Equals(requested, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Для запуска допустим только cstrike.exe или hl2.exe из папки игры.", nameof(options));
            exe = detected.FoundPaths.FirstOrDefault(path =>
                Path.GetFileName(path).Equals(requested, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new FileNotFoundException("Не найден выбранный исполняемый файл cstrike.exe или hl2.exe в папке игры.", exe);
        exe = ResolveFilePath(exe);
        var executableDirectory = Path.GetDirectoryName(exe);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(executableDirectory, resolvedRoot, pathComparison))
            throw new ArgumentException("Исполняемый файл должен находиться непосредственно в папке игры; ссылки за её пределы запрещены.", nameof(options));
        if (options.Steam && !IsSteamRunning())
            log?.Invoke("ПРЕДУПРЕЖДЕНИЕ: Steam не запущен. Игра может потребовать запущенный Steam.");

        var psi = new ProcessStartInfo(exe) { WorkingDirectory = normalizedRoot, UseShellExecute = false };
        if (options.Steam) psi.ArgumentList.Add("-steam");
        psi.ArgumentList.Add("-game");
        psi.ArgumentList.Add(Path.GetFullPath(profilePath));
        if (options.Novid) psi.ArgumentList.Add("-novid");
        if (options.Console) psi.ArgumentList.Add("-console");
        if (options.Windowed) psi.ArgumentList.Add("-windowed");
        if (options.Noborder) psi.ArgumentList.Add("-noborder");
        if (options.Width is not null) { psi.ArgumentList.Add("-w"); psi.ArgumentList.Add(options.Width.Value.ToString()); }
        if (options.Height is not null) { psi.ArgumentList.Add("-h"); psi.ArgumentList.Add(options.Height.Value.ToString()); }
        foreach (var arg in SplitExtra(options.Extra)) psi.ArgumentList.Add(arg);

        log?.Invoke("Запуск: " + exe + " " + string.Join(" ", psi.ArgumentList.Select(Quote)));
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить игру.");
        await process.WaitForExitAsync(ct);
        log?.Invoke($"Игра завершила работу с кодом {process.ExitCode}.");
        return process.ExitCode;
    }

    public static bool IsSteamRunning()
    {
        var processes = Process.GetProcessesByName("steam");
        try
        {
            foreach (var process in processes)
            {
                try { if (!process.HasExited) return true; }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return false;
    }

    public static IEnumerable<string> SplitExtra(string extra) =>
        string.IsNullOrWhiteSpace(extra)
            ? Array.Empty<string>()
            : System.Text.RegularExpressions.Regex.Matches(extra, @"[^\s""']+|""([^""]*)""|'([^']*)'")
                .Select(match => match.Value.Trim('"', '\''));

    private static string ResolveDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath) ?? throw new IOException("Не удалось определить корень пути игры.");
        var current = pathRoot;
        var segments = fullPath[pathRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (directory.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                current = Path.GetFullPath(target.FullName);
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static string ResolveFilePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new IOException("У исполняемого файла отсутствует родительский каталог.");
        var resolvedParent = ResolveDirectoryPath(parent);
        var file = new FileInfo(Path.Combine(resolvedParent, Path.GetFileName(fullPath)));
        var target = file.ResolveLinkTarget(returnFinalTarget: true) ?? file;
        return Path.GetFullPath(target.FullName);
    }

    private static string Quote(string value) => value.Contains(' ') ? '"' + value + '"' : value;
}
