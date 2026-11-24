namespace CssLauncher.Core;

public interface IGameLocator { Task<IReadOnlyList<GameCandidate>> FindCandidatesAsync(CancellationToken ct); }
public interface IVersionChecker { Task<VersionCheckResult> CheckAsync(string gameRoot, CancellationToken ct = default); }
public interface IGameLauncher { Task<int> LaunchAsync(string gameRoot, string profilePath, LaunchOptions options, Action<string>? log = null, CancellationToken ct = default); }
public interface ISettingsService { Task<LauncherSettings> LoadAsync(CancellationToken ct = default); Task SaveAsync(LauncherSettings settings, CancellationToken ct = default); }
public interface IAppLogger { void Info(string message); void Warning(string message); void Error(string message, Exception? exception = null); event Action<string>? Message; }
