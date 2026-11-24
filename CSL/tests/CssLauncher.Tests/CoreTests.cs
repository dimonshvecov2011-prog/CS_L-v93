using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CssLauncher.Core;
using CssLauncher.Game;
using CssLauncher.Infrastructure;
using Xunit;

namespace CssLauncher.Tests;

public class CoreTests
{
    [Fact]
    public void ParsesLibraryFolders()
    {
        var result = new LibraryFoldersVdfParser().Parse("\"path\" \"C:\\\\SteamLibrary\"\n\"path\" \"D:\\\\Games\"");
        Assert.Equal(2, result.Count);
        Assert.Contains("C:\\SteamLibrary", result);
    }

    [Fact]
    public void ParsesAppManifestBuildId() =>
        Assert.Equal("11003710", new AppManifestParser().ParseBuildId("\"appid\" \"240\"\n\"buildid\" \"11003710\""));

    [Fact]
    public void ResolvesSteamAppManifestFromGameRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Steam", "steamapps", "common", "Counter-Strike Source");
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "..", "..", "appmanifest_240.acf")), SteamPathHelper.GetAppManifestPath(root));
    }

    [Fact]
    public void ParsesSteamInfClientVersion()
    {
        var values = new SteamInfParser().Parse("PatchVersion=11003710\nClientVersion=11003710\nServerVersion=11003710");
        Assert.Equal("11003710", values["ClientVersion"]);
        Assert.Equal("11003710", new SteamInfParser().ParseClientVersion("PatchVersion=11003710\nClientVersion=11003710"));
    }

    [Fact]
    public void DetectsBothExecutablesAndPrefersCstrikeByDefault()
    {
        var root = TempDirectory("CssExe");
        try
        {
            File.WriteAllText(Path.Combine(root, "cstrike.exe"), "");
            File.WriteAllText(Path.Combine(root, "hl2.exe"), "");
            var result = GameExecutableDetector.Detect(root);
            Assert.Equal(Path.Combine(root, "cstrike.exe"), result.PreferredPath);
            Assert.Equal(2, result.FoundPaths.Count);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void SavedExecutableChoiceIsRestoredByName()
    {
        var root = TempDirectory("CssExeChoice");
        try
        {
            File.WriteAllText(Path.Combine(root, "cstrike.exe"), "");
            File.WriteAllText(Path.Combine(root, "hl2.exe"), "");
            var detection = GameExecutableDetector.Detect(root);
            Assert.Equal(Path.Combine(root, "hl2.exe"), GameExecutableDetector.Select(detection, "hl2.exe"));
            Assert.Equal(Path.Combine(root, "cstrike.exe"), GameExecutableDetector.Select(detection, "missing.exe"));
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void DetectsNoExecutable()
    {
        var root = TempDirectory("CssExeNone");
        try
        {
            var result = GameExecutableDetector.Detect(root);
            Assert.Null(result.PreferredPath);
            Assert.Empty(result.FoundPaths);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task SearchSkipsEmptyFirstSteamLibraryAndFindsValidSecond()
    {
        var temp = TempDirectory("CssLibraries");
        try
        {
            var firstLibrary = Path.Combine(temp, "library-one");
            var secondLibrary = Path.Combine(temp, "library-two");
            Directory.CreateDirectory(firstLibrary);
            var validRoot = Path.Combine(secondLibrary, "steamapps", "common", "Counter-Strike Source");
            Directory.CreateDirectory(Path.Combine(validRoot, "cstrike"));
            await File.WriteAllTextAsync(Path.Combine(validRoot, "cstrike", "gameinfo.txt"), "info");
            await File.WriteAllTextAsync(Path.Combine(validRoot, "hl2.exe"), "game");

            var candidates = await new GameLocator(steamRoots: new[] { firstLibrary, secondLibrary })
                .FindCandidatesAsync(CancellationToken.None);

            var candidate = Assert.Single(candidates);
            Assert.Equal(validRoot, candidate.RootPath);
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task LocatorRejectsFolderWithoutGameInfoEvenWhenExeExists()
    {
        var temp = TempDirectory("CssIncomplete");
        try
        {
            var root = Path.Combine(temp, "steamapps", "common", "Counter-Strike Source");
            Directory.CreateDirectory(Path.Combine(root, "cstrike"));
            File.WriteAllText(Path.Combine(root, "cstrike.exe"), "game");
            var candidates = await new GameLocator(steamRoots: new[] { temp }).FindCandidatesAsync(CancellationToken.None);
            Assert.Empty(candidates);
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public void LaunchGateRejectsRepeatUntilReleased()
    {
        var gate = new LaunchGate();
        Assert.True(gate.TryBegin());
        Assert.True(gate.IsLaunching);
        Assert.False(gate.TryBegin());
        gate.Complete();
        Assert.False(gate.IsLaunching);
        Assert.True(gate.TryBegin());
        gate.Complete();
    }

    [Fact]
    public void RejectsUnsafeManifestPath()
    {
        Assert.False(VersionChecker.IsSafeRelativePath("../secret"));
        Assert.False(VersionChecker.IsSafeRelativePath("C:\\secret"));
        Assert.False(VersionChecker.IsSafeRelativePath("/absolute"));
        Assert.False(VersionChecker.IsSafeRelativePath("foo:bar"));
        Assert.True(VersionChecker.IsSafeRelativePath("cstrike/gameinfo.txt"));
    }

    [Fact]
    public void SplitsExtraArgumentsWithQuotes()
    {
        var args = GameLauncher.SplitExtra("-foo \"hello world\" -bar").ToArray();
        Assert.Equal(new[] { "-foo", "hello world", "-bar" }, args);
    }

    [Fact]
    public void ProfileLaunchBuilderAppliesAllProfileFieldsAndOverrides()
    {
        var profile = new LauncherProfile("test", "Test", "description", Novid: false, Console: true,
            Windowed: true, Noborder: true, Extra: "-custom value");
        var defaults = ProfileLaunchOptionsBuilder.Build(profile, steam: true, executable: "hl2.exe");
        Assert.False(defaults.Novid);
        Assert.True(defaults.Console);
        Assert.True(defaults.Windowed);
        Assert.True(defaults.Noborder);
        Assert.Equal("-custom value", defaults.Extra);
        Assert.True(defaults.Steam);
        Assert.Equal("hl2.exe", defaults.Executable);

        var overrides = ProfileLaunchOptionsBuilder.Build(profile, steam: false, novidOverride: true, consoleOverride: false);
        Assert.True(overrides.Novid);
        Assert.False(overrides.Console);
        Assert.True(overrides.Windowed);
        Assert.True(overrides.Noborder);
        Assert.Equal("-custom value", overrides.Extra);
        Assert.False(overrides.Steam);
    }

    [Fact]
    public void UnknownProfileIdFallsBackToClassic()
    {
        var temp = TempDirectory("CssProfileCatalog");
        try
        {
            var service = new ProfileCatalogService(temp);
            Assert.Equal("classic", service.Get("not-a-profile").Id);
            Assert.Equal("classic", new ProfileSelectionState(ProfileCatalogService.BuiltInProfiles, "not-a-profile").ActiveProfile.Id);
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public void OverviewAndProfilesSelectionsShareOneActiveProfileState()
    {
        var state = new ProfileSelectionState(ProfileCatalogService.BuiltInProfiles, "classic");

        // Both WPF selection handlers route through SetActiveProfile -> Select(id).
        Assert.Equal("modern", state.Select("modern").Id);
        Assert.Equal("modern", state.ActiveProfile.Id);
        Assert.Equal("competitive", state.Select("competitive").Id);
        Assert.Equal("competitive", state.ActiveProfile.Id);
    }

    [Fact]
    public async Task ProfileCatalogServicePreparesTheProfileUsedByTheWindow()
    {
        var app = TempDirectory("CssCatalogProfileApp");
            var game = TempDirectory("Игра с пробелом");
        try
        {
            var template = Path.Combine(app, "gameinfo.template.txt");
            await File.WriteAllTextAsync(template, "game-root={GAME_ROOT}");
            var service = new ProfileCatalogService(app);
            var profile = service.Get("modern");
            var profilePath = await service.PrepareAsync(game, profile, template);

            Assert.True(Directory.Exists(Path.Combine(profilePath, "custom")));
            Assert.Contains(game.Replace("\\", "/"), await File.ReadAllTextAsync(Path.Combine(profilePath, "gameinfo.txt")));
            Assert.Contains("modern", await File.ReadAllTextAsync(Path.Combine(profilePath, "profile.json")));
            Assert.False(File.Exists(Path.Combine(game, "gameinfo.txt")));
        }
        finally { DeleteDirectory(app); DeleteDirectory(game); }
    }

    [Fact]
    public async Task SettingsRoundTripPersistsProfileAndExecutableAtomically()
    {
        var temp = TempDirectory("CssSettingsRoundTrip");
        try
        {
            var service = new JsonSettingsService(temp);
            var settings = new LauncherSettings("D:/Games/CSS", new LaunchOptions(
                Novid: false, Console: true, Windowed: true, Noborder: true, Extra: "-freq 144", Steam: true, Executable: "hl2.exe"), true)
            {
                SchemaVersion = 1,
                ProfileId = "competitive",
                Executable = "hl2.exe"
            };
            await service.SaveAsync(settings);
            var loaded = await service.LoadAsync();

            Assert.Equal(settings.GamePath, loaded.GamePath);
            Assert.Equal("competitive", loaded.ProfileId);
            Assert.Equal("hl2.exe", loaded.Executable);
            Assert.False(loaded.EffectiveLaunchOptions.Novid);
            Assert.True(loaded.EffectiveLaunchOptions.Windowed);
            Assert.True(loaded.DiagnosticMode);
            Assert.Empty(Directory.GetFiles(Path.Combine(temp, "CssLauncher"), "settings.json.tmp-*"));
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task MissingSettingsReturnAndPersistDefaults()
    {
        var temp = TempDirectory("CssSettingsMissing");
        try
        {
            var settings = await new JsonSettingsService(temp).LoadAsync();
            Assert.Equal("classic", settings.ProfileId);
            Assert.True(File.Exists(Path.Combine(temp, "CssLauncher", "settings.json")));
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task CorruptSettingsArePreservedAsBackupAndReported()
    {
        var temp = TempDirectory("CssSettingsCorrupt");
        try
        {
            var settingsPath = Path.Combine(temp, "CssLauncher", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            const string corrupted = "{\"GamePath\":";
            await File.WriteAllTextAsync(settingsPath, corrupted);

            var exception = await Assert.ThrowsAsync<SettingsCorruptedException>(() => new JsonSettingsService(temp).LoadAsync());

            Assert.Equal(settingsPath + ".bak", exception.BackupPath);
            Assert.False(File.Exists(settingsPath));
            Assert.Equal(corrupted, await File.ReadAllTextAsync(exception.BackupPath));
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task KnownClientVersionIsConfirmed()
    {
        var library = TempDirectory("CssKnownVersion");
        try
        {
            var root = await CreateGameRoot(library, "11003710");
            await File.WriteAllTextAsync(Path.Combine(library, "steamapps", "appmanifest_240.acf"), "\"buildid\" \"25338318\"");
            var manifest = await WriteVersionManifest(library, new VersionManifest(2, "CS:S", "11003710", new[] { 11003710 }, new[] { 25338318 }));
            var result = await new VersionChecker(manifest).CheckAsync(root);
            Assert.Equal(VersionStatus.Confirmed, result.Status);
            Assert.Equal("11003710", result.ClientVersion);
            Assert.Equal("25338318", result.BuildId);
        }
        finally { DeleteDirectory(library); }
    }

    [Fact]
    public async Task UnknownClientVersionReturnsWarningStatus()
    {
        var library = TempDirectory("CssUnknownVersion");
        try
        {
            var root = await CreateGameRoot(library, "99999999", executable: "hl2.exe");
            await File.WriteAllTextAsync(Path.Combine(library, "steamapps", "appmanifest_240.acf"), "\"buildid\" \"25338318\"");
            var manifest = await WriteVersionManifest(library, new VersionManifest(2, "CS:S", "11003710", new[] { 11003710 }, new[] { 25338318 }));
            var result = await new VersionChecker(manifest).CheckAsync(root);
            Assert.Equal(VersionStatus.UnknownBuild, result.Status);
            Assert.Contains("ClientVersion 99999999", result.Message);
        }
        finally { DeleteDirectory(library); }
    }

    [Fact]
    public async Task EmptyVersionListReturnsManifestEmpty()
    {
        var root = TempDirectory("CssEmptyVersion");
        try
        {
            await CreateGameRoot(root, "11003710", executable: "hl2.exe");
            var manifest = await WriteVersionManifest(root, new VersionManifest(2, "CS:S", "current", Array.Empty<int>()));
            var result = await new VersionChecker(manifest).CheckAsync(Path.Combine(root, "steamapps", "common", "Counter-Strike Source"));
            Assert.Equal(VersionStatus.ManifestEmpty, result.Status);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task ShaMismatchIsReportedForExplicitConfirmation()
    {
        var library = TempDirectory("CssHashMismatch");
        try
        {
            var root = await CreateGameRoot(library, "11003710");
            var contentPath = Path.Combine(root, "cstrike", "payload.dat");
            await File.WriteAllTextAsync(contentPath, "actual content");
            var variant = new ManifestVariant("base", "Base files", new[] { new ManifestFile("cstrike/payload.dat", new string('0', 64)) });
            var manifest = await WriteVersionManifest(library, new VersionManifest(2, "CS:S", "11003710", new[] { 11003710 },
                EnableSha256Checks: true, Variants: new[] { variant }));

            var result = await new VersionChecker(manifest).CheckAsync(root);

            Assert.Equal(VersionStatus.Confirmed, result.Status);
            Assert.Single(result.Mismatches);
            Assert.Equal(MismatchKind.HashDiffers, result.Mismatches[0].Kind);
            Assert.True(HashMismatchPrompt.IsRequired(result));
            Assert.Contains("cstrike/payload.dat", HashMismatchPrompt.Format(result));
            Assert.Contains("ожидался", HashMismatchPrompt.Format(result));
        }
        finally { DeleteDirectory(library); }
    }

    [Fact]
    public async Task EnabledShaChecksWithoutValidManifestAreNotSilentlySkipped()
    {
        var library = TempDirectory("CssHashEmpty");
        try
        {
            var root = await CreateGameRoot(library, "11003710");
            var invalidVariant = new ManifestVariant("broken", null, new[] { new ManifestFile("cstrike/payload.dat", "not-a-hash") });
            var manifest = await WriteVersionManifest(library, new VersionManifest(2, "CS:S", "11003710", new[] { 11003710 },
                EnableSha256Checks: true, Variants: new[] { invalidVariant }));

            var result = await new VersionChecker(manifest).CheckAsync(root);

            Assert.Equal(VersionStatus.ManifestEmpty, result.Status);
            Assert.Contains("SHA-256", result.Message);
        }
        finally { DeleteDirectory(library); }
    }

    [Fact]
    public async Task DisabledShaChecksDoNotComputeMismatches()
    {
        var library = TempDirectory("CssHashDisabled");
        try
        {
            var root = await CreateGameRoot(library, "11003710");
            await File.WriteAllTextAsync(Path.Combine(root, "cstrike", "payload.dat"), "actual");
            var variant = new ManifestVariant("base", null, new[] { new ManifestFile("cstrike/payload.dat", new string('0', 64)) });
            var manifest = await WriteVersionManifest(library, new VersionManifest(2, "CS:S", "11003710", new[] { 11003710 },
                EnableSha256Checks: false, Variants: new[] { variant }));

            var result = await new VersionChecker(manifest).CheckAsync(root);

            Assert.Equal(VersionStatus.Confirmed, result.Status);
            Assert.Empty(result.Mismatches);
        }
        finally { DeleteDirectory(library); }
    }

    [Fact]
    public async Task DiagnosticArchiveIncludesSteamManifestFromLibraryRoot()
    {
        var library = TempDirectory("CssDiagLibrary");
        var app = TempDirectory("CssDiagApp");
        try
        {
            var game = Path.Combine(library, "steamapps", "common", "Counter-Strike Source");
            Directory.CreateDirectory(Path.Combine(game, "cstrike"));
            Directory.CreateDirectory(Path.Combine(library, "steamapps"));
            await File.WriteAllTextAsync(Path.Combine(game, "cstrike", "steam.inf"), "ClientVersion=11003710");
            await File.WriteAllTextAsync(Path.Combine(library, "steamapps", "appmanifest_240.acf"), "\"buildid\" \"25338318\"");

            var result = await new DiagnosticArchiveService(app).CreateAsync(game);

            using var zip = ZipFile.OpenRead(result.ArchivePath);
            Assert.Contains(zip.Entries, entry => entry.FullName == "game/cstrike/steam.inf");
            Assert.Contains(zip.Entries, entry => entry.FullName == "external/appmanifest_240.acf");
            Assert.True(result.Size > 0);
        }
        finally { DeleteDirectory(library); DeleteDirectory(app); }
    }

    [Fact]
    public async Task UnsupportedSettingsSchemaIsPreservedWithoutBackupOrOverwrite()
    {
        var temp = TempDirectory("CssSettingsFutureSchema");
        try
        {
            var settingsPath = Path.Combine(temp, "CssLauncher", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            const string original = "{\"schemaVersion\":99,\"gamePath\":\"D:/keep\"}";
            await File.WriteAllTextAsync(settingsPath, original);

            var exception = await Assert.ThrowsAsync<UnsupportedSettingsSchemaException>(
                () => new JsonSettingsService(temp).LoadAsync());

            Assert.Equal(99, exception.FoundSchemaVersion);
            Assert.Equal(original, await File.ReadAllTextAsync(settingsPath));
            Assert.False(File.Exists(settingsPath + ".bak"));
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task LauncherUpdateManifestReadsCamelCaseJsonAndValidatesHttpsChannel()
    {
        var temp = TempDirectory("CssUpdateManifest");
        try
        {
            var path = Path.Combine(temp, "launcher-update.json");
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                version = "0.3.4",
                downloadUrl = "https://updates.example.invalid/CSL.zip",
                sha256 = new string('a', 64),
                size = 12,
                releaseNotes = "test"
            });
            await File.WriteAllTextAsync(path, json);

            var result = await new LauncherUpdateService(temp).CheckAsync(path, "0.3.3");

            Assert.True(result.Available);
            Assert.Equal("0.3.4", result.Update?.Version);
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task LauncherUpdateRejectsHttpManifestWithoutSendingRequest()
    {
        var temp = TempDirectory("CssUpdateHttp");
        try
        {
            var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var service = new LauncherUpdateService(temp, new HttpClient(handler));

            var result = await service.CheckAsync("http://updates.example.invalid/manifest.json", "0.3.3");

            Assert.False(result.Available);
            Assert.Contains("HTTPS", result.Message);
            Assert.Equal(0, handler.RequestCount);
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task LauncherUpdateCheckPropagatesCallerCancellation()
    {
        var temp = TempDirectory("CssUpdateCancel");
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new CancellableHttpMessageHandler(entered);
            var service = new LauncherUpdateService(temp, new HttpClient(handler));
            using var cancellation = new CancellationTokenSource();
            var check = service.CheckAsync("https://updates.example.invalid/manifest.json", "0.3.3", cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task LauncherUpdateDownloadsOnlyExactHttpsBytesWithMatchingSha256()
    {
        var temp = TempDirectory("CssUpdateDownload");
        try
        {
            var payload = Encoding.UTF8.GetBytes("verified update bytes");
            var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
            var service = new LauncherUpdateService(temp, new HttpClient(handler));
            var manifest = new LauncherUpdateManifest(1, "0.3.4", "https://updates.example.invalid/CSL.zip",
                Convert.ToHexString(SHA256.HashData(payload)), payload.Length);

            var downloaded = await service.DownloadAsync(manifest);

            Assert.Equal(payload, await File.ReadAllBytesAsync(downloaded));
            Assert.Equal(1, handler.RequestCount);
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task LauncherUpdateRejectsHttpDownloadAndSizeMismatch()
    {
        var temp = TempDirectory("CssUpdateReject");
        try
        {
            var payload = Encoding.UTF8.GetBytes("payload");
            var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
            var service = new LauncherUpdateService(temp, new HttpClient(handler));
            var insecure = new LauncherUpdateManifest(1, "0.3.4", "http://updates.example.invalid/CSL.zip",
                new string('0', 64), payload.Length);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(insecure));
            Assert.Equal(0, handler.RequestCount);

            var wrongSize = new LauncherUpdateManifest(1, "0.3.4", "https://updates.example.invalid/CSL.zip",
                new string('0', 64), payload.Length - 1);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(wrongSize));
            Assert.Empty(Directory.GetFiles(Path.Combine(temp, "CssLauncher", "updates"), "*.zip"));
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task ServerListRejectsHttpSourcesAndInvalidPorts()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = new ServerListService(new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync("http://servers.example.invalid/list.json"));
        Assert.Equal(0, handler.RequestCount);

        var temp = TempDirectory("CssServerListInvalid");
        try
        {
            var path = Path.Combine(temp, "servers.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ServerListManifest(1,
                "2026-11-24T00:00:00Z", new[] { new ServerEntry("Bad server", "localhost", 70000) })));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(path));
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task GameLauncherRejectsExecutablePathOutsideGameRoot()
    {
        var temp = TempDirectory("CssLaunchPathSafety");
        try
        {
            var game = Path.Combine(temp, "game");
            var profile = Path.Combine(temp, "profile");
            Directory.CreateDirectory(game);
            Directory.CreateDirectory(profile);
            await File.WriteAllTextAsync(Path.Combine(game, "cstrike.exe"), "not launched");
            await File.WriteAllTextAsync(Path.Combine(temp, "outside.exe"), "not launched");

            await Assert.ThrowsAsync<ArgumentException>(() => new GameLauncher().LaunchAsync(
                game, profile, new LaunchOptions(Executable: "../outside.exe", Steam: false)));
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task GameLauncherRejectsExecutableSymlinkOutsideGameRoot()
    {
        var temp = TempDirectory("CssLaunchSymlinkSafety");
        try
        {
            var game = Path.Combine(temp, "game");
            var profile = Path.Combine(temp, "profile");
            var outside = Path.Combine(temp, "outside.exe");
            Directory.CreateDirectory(game);
            Directory.CreateDirectory(profile);
            await File.WriteAllTextAsync(outside, "not launched");
            try { File.CreateSymbolicLink(Path.Combine(game, "cstrike.exe"), outside); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }

            var error = await Assert.ThrowsAsync<ArgumentException>(() => new GameLauncher().LaunchAsync(
                game, profile, new LaunchOptions(Executable: "cstrike.exe", Steam: false)));
            Assert.Contains("ссылки", error.Message);
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task KnownClientVersionRemainsConfirmedWhenInformationalSteamBuildDiffers()
    {
        var library = TempDirectory("CssKnownVersionUnknownBuild");
        try
        {
            var root = await CreateGameRoot(library, "11003710");
            await File.WriteAllTextAsync(Path.Combine(library, "steamapps", "appmanifest_240.acf"), "\"buildid\" \"99999999\"");
            var manifest = await WriteVersionManifest(library,
                new VersionManifest(2, "CS:S", "11003710", new[] { 11003710 }, new[] { 25338318 }));

            var result = await new VersionChecker(manifest).CheckAsync(root);

            Assert.Equal(VersionStatus.Confirmed, result.Status);
            Assert.Contains("информационное", result.Message);
        }
        finally { DeleteDirectory(library); }
    }

    [Fact]
    public void HashMismatchPromptClampsNegativeEntryLimit()
    {
        var result = new VersionCheckResult(VersionStatus.Confirmed,
            new[] { new FileMismatch("cstrike/file.dat", MismatchKind.Missing, "hash", null) });

        Assert.Contains("ещё 1", HashMismatchPrompt.Format(result, -1));
    }

    [Fact]
    public async Task LoggerWritesFullExceptionAndIsolatesThrowingSubscribers()
    {
        var temp = TempDirectory("CssLogger");
        try
        {
            using var logger = new FileLogger(temp);
            var observed = 0;
            logger.Message += _ => throw new InvalidOperationException("subscriber failure");
            logger.Message += _ => observed++;
            logger.Error("operation failed", new InvalidOperationException("exception details"));

            var logFile = Assert.Single(Directory.GetFiles(Path.Combine(temp, "CssLauncher", "logs"), "*.log"));
            var text = await File.ReadAllTextAsync(logFile);
            Assert.Contains("InvalidOperationException", text);
            Assert.Contains("exception details", text);
            Assert.Equal(1, observed);
        }
        finally { DeleteDirectory(temp); }
    }

    [Fact]
    public async Task LoggerTruncatesSingleOversizedEntryToConfiguredFileLimit()
    {
        var temp = TempDirectory("CssLoggerLimit");
        try
        {
            using var logger = new FileLogger(temp);
            logger.Info(new string('Ж', 3_000_000));

            var logFile = Assert.Single(Directory.GetFiles(Path.Combine(temp, "CssLauncher", "logs"), "*.log"));
            Assert.True(new FileInfo(logFile).Length <= 5L * 1024 * 1024);
            Assert.Contains("[log entry truncated]", await File.ReadAllTextAsync(logFile));
        }
        finally { DeleteDirectory(temp); }
    }

    private static string TempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static async Task<string> CreateGameRoot(string libraryRoot, string clientVersion, string executable = "cstrike.exe")
    {
        var root = Path.Combine(libraryRoot, "steamapps", "common", "Counter-Strike Source");
        Directory.CreateDirectory(Path.Combine(root, "cstrike"));
        await File.WriteAllTextAsync(Path.Combine(root, executable), "game");
        await File.WriteAllTextAsync(Path.Combine(root, "cstrike", "gameinfo.txt"), "info");
        await File.WriteAllTextAsync(Path.Combine(root, "cstrike", "steam.inf"), $"ClientVersion={clientVersion}\nPatchVersion={clientVersion}");
        return root;
    }

    private static async Task<string> WriteVersionManifest(string directory, VersionManifest manifest)
    {
        var path = Path.Combine(directory, "version-manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _createResponse;
        public int RequestCount { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> createResponse) =>
            _createResponse = createResponse;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = _createResponse(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class CancellableHttpMessageHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered;

        public CancellableHttpMessageHandler(TaskCompletionSource entered) => _entered = entered;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
