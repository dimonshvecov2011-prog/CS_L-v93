namespace CssLauncher.Core;

public sealed record ValidationResult(bool IsValid, string ErrorMessage = "");

public static class HudSafetyValidator
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".res", ".txt", ".png", ".jpg", ".jpeg", ".tga", ".vmt", ".vtf"
    };

    public static ValidationResult Validate(string hudDirectory, string gameFileSystemRoot)
    {
        if (string.IsNullOrWhiteSpace(hudDirectory) || string.IsNullOrWhiteSpace(gameFileSystemRoot))
            return Invalid("Каталог HUD и корень файловой системы игры обязательны.");
        if (!Directory.Exists(hudDirectory))
            return Invalid("Каталог HUD не найден.");
        if (!Directory.Exists(gameFileSystemRoot))
            return Invalid("Корень файловой системы игры не найден.");

        string hudRoot;
        string gameRoot;
        try
        {
            hudRoot = Path.GetFullPath(hudDirectory);
            gameRoot = Path.GetFullPath(gameFileSystemRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return Invalid($"Некорректный путь: {ex.Message}");
        }

        // The only allowed location inside the Steam game root is our own custom package.
        // Every original Valve directory remains protected.
        if (SamePath(gameRoot, hudRoot))
            return Invalid("Нельзя использовать корень файловой системы игры как каталог HUD.");
        if (IsInside(gameRoot, hudRoot) &&
            !IsInside(Path.Combine(gameRoot, "cstrike", "custom", "csl_hud"), hudRoot) &&
            !SamePath(Path.Combine(gameRoot, "cstrike", "custom", "csl_hud"), hudRoot))
            return Invalid("HUD нельзя размещать внутри защищённых файлов игры.");

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(hudRoot, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Invalid($"Не удалось прочитать каталог HUD: {ex.Message}");
        }

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(hudRoot, file).Replace('\\', '/');
            if (relative.Contains("..", StringComparison.Ordinal) || relative.Contains(':'))
                return Invalid($"Обнаружен небезопасный относительный путь: {relative}");

            var normalized = relative.ToLowerInvariant();
            if (normalized.StartsWith("resource/clientui", StringComparison.Ordinal) ||
                normalized.StartsWith("resource/valve_", StringComparison.Ordinal))
                return Invalid($"Запрещён системный ресурс HUD: {relative}");

            if (!AllowedExtensions.Contains(Path.GetExtension(relative)))
                return Invalid($"Расширение файла HUD не разрешено: {relative}");

            if (IsInside(gameRoot, Path.GetFullPath(file)) &&
                !IsInside(Path.Combine(gameRoot, "cstrike", "custom", "csl_hud"), Path.GetFullPath(file)))
                return Invalid($"HUD пытается изменить защищённый файл игры: {relative}");
        }

        return new(true);
    }

    private static ValidationResult Invalid(string message) => new(false, message);

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string parent, string child)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var normalizedChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        return normalizedChild.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
