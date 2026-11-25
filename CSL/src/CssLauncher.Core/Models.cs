namespace CssLauncher.Core;

public enum VersionStatus { Confirmed, UnknownBuild, ManifestEmpty, GameNotFound }
public enum MismatchKind { Missing, HashDiffers }
public record GameCandidate(string RootPath, string Source);
public record FileMismatch(string Path, MismatchKind Kind, string? Expected, string? Actual);
public record VersionCheckResult(VersionStatus Status, IReadOnlyList<FileMismatch> Mismatches, string? BuildId = null, string? ExecutablePath = null, string? Message = null, string? ClientVersion = null);
public record ManifestFile(string Path, string Sha256);
public record ManifestVariant(string Id, string? Description, IReadOnlyList<ManifestFile> Files);
public record VersionManifest(int SchemaVersion, string Game, string Version, IReadOnlyList<int> KnownVersions, IReadOnlyList<int>? KnownBuilds = null, bool EnableSha256Checks = false, IReadOnlyList<ManifestVariant>? Variants = null);
public record ExecutableDetection(string? PreferredPath, IReadOnlyList<string> FoundPaths);
public record LaunchOptions(bool Novid = true, bool Console = true, bool Windowed = false, bool Noborder = false, int? Width = null, int? Height = null, string Extra = "", bool Steam = true, string Executable = "");

public record LauncherSettings(string GamePath = "", LaunchOptions? LaunchOptions = null, bool DiagnosticMode = false)
{
    public int SchemaVersion { get; init; } = 1;
    public string Executable { get; init; } = "";

    public LaunchOptions EffectiveLaunchOptions => LaunchOptions ?? new();
}
