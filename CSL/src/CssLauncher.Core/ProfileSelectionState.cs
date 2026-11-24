namespace CssLauncher.Core;

/// <summary>Single source of truth for the currently selected launcher profile.</summary>
public sealed class ProfileSelectionState
{
    private readonly IReadOnlyList<LauncherProfile> _profiles;

    public LauncherProfile ActiveProfile { get; private set; }

    public ProfileSelectionState(IReadOnlyList<LauncherProfile> profiles, string? initialId = null)
    {
        if (profiles.Count == 0) throw new ArgumentException("At least one profile is required.", nameof(profiles));
        _profiles = profiles;
        ActiveProfile = Find(initialId) ?? Find("classic") ?? profiles[0];
    }

    public LauncherProfile Select(string? id)
    {
        ActiveProfile = Find(id) ?? Find("classic") ?? _profiles[0];
        return ActiveProfile;
    }

    private LauncherProfile? Find(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : _profiles.FirstOrDefault(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
