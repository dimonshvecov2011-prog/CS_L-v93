using System.Text; using CssLauncher.Core;
namespace CssLauncher.Game;
public sealed class ProfileService : IProfileService
{
    private readonly string _template; private readonly string _profile;
    public ProfileService(string templatePath, string? appData = null) { _template = templatePath; _profile = Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher", "profiles", "default"); }
    public async Task<string> CreateOrUpdateAsync(string gameRoot, CancellationToken ct = default)
    {
        var root = Directory.GetParent(Directory.GetParent(_profile)!.FullName)!.FullName;
        Directory.CreateDirectory(_profile);
        Directory.CreateDirectory(Path.Combine(_profile, "custom"));
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        Directory.CreateDirectory(Path.Combine(root, "downloads"));
        Directory.CreateDirectory(Path.Combine(root, "backups"));
        var text = await File.ReadAllTextAsync(_template, ct);
        text = text.Replace("{GAME_ROOT}", gameRoot.Replace("\\", "/"));
        await File.WriteAllTextAsync(Path.Combine(_profile, "gameinfo.txt"), text, new UTF8Encoding(false), ct);
        return _profile;
    }
}
