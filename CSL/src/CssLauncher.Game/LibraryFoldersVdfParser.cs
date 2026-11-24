using System.Text.RegularExpressions;
namespace CssLauncher.Game;
public sealed class LibraryFoldersVdfParser
{
    private static readonly Regex PathRegex = new("\\\"path\\\"\\s+\\\"(?<path>.*?)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public IReadOnlyList<string> Parse(string text)
    {
        var result = new List<string>();
        foreach (Match m in PathRegex.Matches(text)) { var path = m.Groups["path"].Value.Replace("\\\\", "\\"); if (!string.IsNullOrWhiteSpace(path) && !result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path); }
        return result;
    }
}
