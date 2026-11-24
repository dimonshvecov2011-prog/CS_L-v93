using CssLauncher.Core;

namespace CssLauncher.Game;

public sealed class ModCatalogService
{
    private readonly string _root;
    private readonly ModPackageService _packages;
    public ModCatalogService(string? appData = null, HttpClient? http = null)
    {
        _root = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher");
        _packages = new ModPackageService(appData, http);
    }
    public async Task<IReadOnlyList<InstalledMod>> ListAsync(ModManifest manifest, string profileId = "default", CancellationToken ct = default)
    {
        var result = new List<InstalledMod>();
        foreach (var mod in manifest.Mods)
        {
            ct.ThrowIfCancellationRequested();
            var modRoot = Path.Combine(_root, "mods", mod.Id);
            var versions = Directory.Exists(modRoot) ? Directory.GetDirectories(modRoot).Where(x => !Path.GetFileName(x).StartsWith(".")).OrderByDescending(x => x).ToArray() : Array.Empty<string>();
            var version = versions.Select(Path.GetFileName).FirstOrDefault() ?? mod.Version;
            var enabled = Directory.Exists(Path.Combine(_root, "profiles", profileId, "custom", mod.Id));
            result.Add(new InstalledMod(mod.Id, mod.Name, version, mod.Description, Path.Combine(modRoot, version), enabled));
        }
        return await Task.FromResult(result);
    }
    public async Task<ModInstallResult> InstallAsync(ModPackage mod, IProgress<double>? progress = null, CancellationToken ct = default) => await _packages.DownloadAndInstallAsync(mod, progress, ct);
    public Task SetEnabledAsync(ModManifest manifest, string id, bool enabled, string profileId = "default", CancellationToken ct = default)
    {
        var mod = manifest.Mods.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException(id);
        var root = Path.Combine(_root, "mods", id);
        var version = Directory.Exists(root) ? Directory.GetDirectories(root).OrderByDescending(x => x).FirstOrDefault() : null;
        if (version is null) throw new DirectoryNotFoundException($"Мод {id} не установлен.");
        if (profileId != "default")
        {
            var target = Path.Combine(_root, "profiles", profileId, "custom", id);
            if (!enabled) { if (Directory.Exists(target)) Directory.Delete(target, true); return Task.CompletedTask; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); CopyDirectory(version, target); return Task.CompletedTask;
        }
        return _packages.SetEnabledAsync(mod with { Version = Path.GetFileName(version) }, version, enabled, ct);
    }
    private static void CopyDirectory(string source, string destination)
    { if (Directory.Exists(destination)) Directory.Delete(destination, true); Directory.CreateDirectory(destination); foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) { var target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true); } }
}
