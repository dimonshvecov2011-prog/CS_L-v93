using CssLauncher.Core;

namespace CssLauncher.Game;

public static class ProfileLaunchOptionsBuilder
{
    public static LaunchOptions Build(
        LauncherProfile profile,
        bool steam,
        bool? novidOverride = null,
        bool? consoleOverride = null,
        string executable = "") =>
        new(
            Novid: novidOverride ?? profile.Novid,
            Console: consoleOverride ?? profile.Console,
            Windowed: profile.Windowed,
            Noborder: profile.Noborder,
            Extra: profile.Extra,
            Steam: steam,
            Executable: executable);
}
