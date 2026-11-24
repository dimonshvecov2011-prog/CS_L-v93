namespace CssLauncher.Core;

public sealed record ModManifest(
    int SchemaVersion,
    string Channel,
    IReadOnlyList<ModPackage> Mods);

public sealed record ModPackage(
    string Id,
    string Name,
    string Version,
    string Description,
    string DownloadUrl,
    long Size,
    string Sha256,
    bool EnabledByDefault = false);

public sealed record ModInstallResult(string Id, string Version, string InstallPath, bool Enabled);
