namespace CssLauncher.Core;

public static class HashMismatchPrompt
{
    public static bool IsRequired(VersionCheckResult? result) => result?.Mismatches.Count > 0;

    public static string Format(VersionCheckResult result, int maxEntries = 12)
    {
        var entries = result.Mismatches.Take(maxEntries).Select(mismatch => mismatch.Kind switch
        {
            MismatchKind.Missing => $"• {mismatch.Path}: файл отсутствует",
            MismatchKind.HashDiffers => $"• {mismatch.Path}: SHA-256 не совпадает (ожидался {mismatch.Expected}, получен {mismatch.Actual})",
            _ => $"• {mismatch.Path}: проверка не пройдена"
        });

        var remaining = result.Mismatches.Count - Math.Min(result.Mismatches.Count, maxEntries);
        var text = string.Join(Environment.NewLine, entries);
        if (remaining > 0) text += $"{Environment.NewLine}… и ещё {remaining} файл(ов).";
        return text;
    }
}
