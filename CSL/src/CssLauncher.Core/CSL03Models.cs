namespace CssLauncher.Core;

public sealed record ServerEntry(
    string Name,
    string Address,
    int Port = 27015,
    string Map = "—",
    int Players = 0,
    int MaxPlayers = 0,
    string Mode = "Classic",
    string? Region = null,
    bool Vac = false)
{
    public string VacLabel => Vac ? "VAC" : "без VAC";
    public string DisplayName => $"{Name}  •  {Players}/{MaxPlayers}  •  {Map}  •  {VacLabel}";
}

public sealed record ServerListManifest(
    int SchemaVersion,
    string UpdatedAt,
    IReadOnlyList<ServerEntry> Servers);

public sealed record LauncherUpdateManifest(
    int SchemaVersion,
    string Version,
    string DownloadUrl,
    string Sha256,
    long Size,
    string ReleaseNotes = "");

public sealed record UpdateCheckResult(bool Available, string CurrentVersion, LauncherUpdateManifest? Update, string Message);

public sealed record DiagnosticArchiveResult(string ArchivePath, int FilesIncluded, long Size);
