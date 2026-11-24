using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;
using CssLauncher.Core;
using CssLauncher.Game;
using CssLauncher.Infrastructure;

namespace CssLauncher.App;

public partial class MainWindow : Window
{
    private readonly FileLogger _logger = new();
    private readonly JsonSettingsService _settings = new();
    private readonly string _root = AppContext.BaseDirectory;
    private readonly ProfileCatalogService _profiles = new();
    private readonly ServerListService _servers = new();
    private readonly LauncherUpdateService _updates = new();
    private readonly DiagnosticArchiveService _diagnostics = new();
    private readonly LaunchGate _launchGate = new();
    private readonly ProfileSelectionState _profileSelection = new(ProfileCatalogService.BuiltInProfiles);
    private VersionCheckResult? _check;
    private LauncherProfile _activeProfile => _profileSelection.ActiveProfile;
    private CancellationTokenSource? _installCts;
    private string _gamePath = "";
    private string _selectedExecutable = "";
    private string _savedExecutable = "";
    private bool _syncingProfile;
    private bool _syncingExecutable;
    private bool _closing;
    private bool _closeApproved;
    private bool _settingsLoadFailed;

    public MainWindow()
    {
        InitializeComponent();
        _logger.Message += message => Dispatcher.BeginInvoke(new Action(() =>
            HintText.Text = message.Length > 100 ? message[..100] : message));
        Loaded += async (_, _) => await LoadAsync();
        Closing += MainWindow_Closing;
    }

    private async Task LoadAsync()
    {
        ProfileBox.ItemsSource = ProfileCatalogService.BuiltInProfiles;
        ProfilesList.ItemsSource = ProfileCatalogService.BuiltInProfiles;

        LauncherSettings settings;
        try
        {
            settings = await _settings.LoadAsync();
        }
        catch (SettingsCorruptedException ex)
        {
            _logger.Warning($"Повреждённый settings.json перемещён в {ex.BackupPath}.");
            WpfMessageBox.Show($"Файл настроек был повреждён и сохранён как резервная копия:\n{ex.BackupPath}\n\nБудут загружены настройки по умолчанию.",
                "CS:L — восстановление настроек", MessageBoxButton.OK, MessageBoxImage.Warning);
            settings = new LauncherSettings();
        }
        catch (Exception ex)
        {
            _settingsLoadFailed = true;
            _logger.Error("Не удалось загрузить настройки.", ex);
            WpfMessageBox.Show($"Не удалось прочитать настройки. Будут использованы значения по умолчанию.\n\n{ex.Message}",
                "CS:L — настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
            settings = new LauncherSettings();
        }

        _savedExecutable = !string.IsNullOrWhiteSpace(settings.Executable)
            ? settings.Executable
            : settings.EffectiveLaunchOptions.Executable;
        DiagnosticBox.IsChecked = settings.DiagnosticMode;
        SteamBox.IsChecked = settings.EffectiveLaunchOptions.Steam;

        var profile = _profiles.Get(settings.ProfileId);
        SetActiveProfile(profile, applyProfileDefaults: settings.LaunchOptions is null);
        if (settings.LaunchOptions is not null)
        {
            NovidBox.IsChecked = settings.EffectiveLaunchOptions.Novid;
            ConsoleBox.IsChecked = settings.EffectiveLaunchOptions.Console;
        }

        SetPathText(settings.GamePath);
        if (!string.IsNullOrWhiteSpace(settings.GamePath) && Directory.Exists(settings.GamePath) &&
            await CheckPathAsync(settings.GamePath))
            return;

        await AutoDetectAsync();
    }

    private void SetPathText(string? path)
    {
        _gamePath = path ?? "";
        PathText.Text = string.IsNullOrWhiteSpace(_gamePath)
            ? "Укажите папку или установите игру через Steam"
            : _gamePath;
    }

    private Task SaveAsync()
    {
        if (_settingsLoadFailed)
            throw new IOException("Нельзя безопасно перезаписать настройки: исходный settings.json не удалось прочитать.");
        var settings = new LauncherSettings(
            _gamePath,
            BuildOptions(),
            DiagnosticBox.IsChecked == true)
        {
            ProfileId = _activeProfile.Id,
            Executable = string.IsNullOrWhiteSpace(_selectedExecutable) ? _savedExecutable : Path.GetFileName(_selectedExecutable)
        };
        return _settings.SaveAsync(settings);
    }

    private async Task AutoDetectAsync()
    {
        StatusText.Text = "Ищем CS:S в библиотеках Steam...";
        BuildText.Text = "ClientVersion: —   •   Steam buildid: —";
        SideStatusText.Text = "Поиск в Steam";
        RetryInstallButton.Visibility = Visibility.Collapsed;
        if (await FindInstalledGameAsync(CancellationToken.None)) return;

        SetPathText("");
        _check = null;
        StatusText.Text = "CS:S не найдена";
        SideStatusText.Text = "Не установлена";
        SideVersionText.Text = "—";
        HintText.Text = "Установите игру через Steam или выберите папку";
        UpdateExecutableOptions("");
        UpdatePlayState();
    }

    private async Task<bool> FindInstalledGameAsync(CancellationToken ct)
    {
        var candidates = await new GameLocator(_gamePath).FindCandidatesAsync(ct);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await CheckPathAsync(candidate.RootPath, ct))
            {
                RetryInstallButton.Visibility = Visibility.Collapsed;
                return true;
            }
        }
        return false;
    }

    private async Task<bool> CheckPathAsync(string path, CancellationToken ct = default)
    {
        BuildText.Text = "ClientVersion: —   •   Steam buildid: —";
        _check = null;
        UpdatePlayState();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            SetPathText("");
            StatusText.Text = "Папка игры не найдена";
            SideStatusText.Text = "Не подключено";
            SideVersionText.Text = "—";
            UpdateExecutableOptions("");
            return false;
        }

        SetPathText(path);
        StatusText.Text = "Проверяем файлы CS:S...";
        SideStatusText.Text = "Проверка";
        try
        {
            var checker = new VersionChecker(Path.Combine(_root, "config", "version-manifest.v93.json"));
            _check = await checker.CheckAsync(path, ct);
            if (_check.Status == VersionStatus.GameNotFound)
            {
                BuildText.Text = "ClientVersion: —   •   Steam buildid: —";
                StatusText.Text = "Папка не содержит полной установки CS:S";
                SideStatusText.Text = "Неполная установка";
                SideVersionText.Text = "—";
                UpdateExecutableOptions("");
                HintText.Text = _check.Message ?? "Нужны cstrike/gameinfo.txt и cstrike.exe или hl2.exe";
                UpdatePlayState();
                return false;
            }

            UpdateExecutableOptions(path);
            BuildText.Text = $"ClientVersion: {_check.ClientVersion ?? "—"}   •   Steam buildid: {_check.BuildId ?? "—"}";
            StatusText.Text = _check.Status switch
            {
                VersionStatus.Confirmed => HashMismatchPrompt.IsRequired(_check)
                    ? "CS:S найдена; SHA-256 требует подтверждения"
                    : "CS:S готова к запуску",
                VersionStatus.ManifestEmpty => "Проверка версии или SHA-256 не настроена",
                _ => "Версия требует подтверждения"
            };
            SideStatusText.Text = _check.Status == VersionStatus.Confirmed ? "Готова" : "Проверить сборку";
            SideVersionText.Text = _check.ClientVersion is null ? "—" : $"v{_check.ClientVersion}";
            if (!string.IsNullOrWhiteSpace(_check.Message))
            {
                HintText.Text = _check.Message;
                _logger.Warning(_check.Message);
            }
            UpdatePlayState();
            return true;
        }
        catch (Exception ex)
        {
            _check = null;
            BuildText.Text = "ClientVersion: —   •   Steam buildid: —";
            StatusText.Text = "Не удалось проверить игру";
            SideStatusText.Text = "Ошибка проверки";
            SideVersionText.Text = "—";
            HintText.Text = ex.Message;
            UpdateExecutableOptions("");
            UpdatePlayState();
            _logger.Error("Ошибка проверки игры.", ex);
            return false;
        }
    }

    private void UpdateExecutableOptions(string gameRoot)
    {
        _syncingExecutable = true;
        try
        {
            var detection = string.IsNullOrWhiteSpace(gameRoot)
                ? new ExecutableDetection(null, Array.Empty<string>())
                : GameExecutableDetector.Detect(gameRoot);
            var preferred = GameExecutableDetector.Select(detection,
                string.IsNullOrWhiteSpace(_selectedExecutable) ? _savedExecutable : _selectedExecutable);
            var choices = detection.FoundPaths
                .Select(path => new ExecutableChoice(Path.GetFileName(path), path))
                .ToList();
            ExecutableBox.DisplayMemberPath = nameof(ExecutableChoice.Name);
            ExecutableBox.ItemsSource = choices;
            var selected = choices.FirstOrDefault(choice => string.Equals(choice.Path, preferred, StringComparison.OrdinalIgnoreCase))
                ?? choices.FirstOrDefault();
            ExecutableBox.SelectedItem = selected;
            _selectedExecutable = selected?.Path ?? "";
            ExecutableBox.IsEnabled = choices.Count > 1;
            ExecutablePanel.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _syncingExecutable = false;
        }
    }

    private void ExecutableChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingExecutable || ExecutableBox.SelectedItem is not ExecutableChoice choice) return;
        _selectedExecutable = choice.Path;
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Выберите папку Counter-Strike Source" };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            RetryInstallButton.Visibility = Visibility.Collapsed;
            await CheckPathAsync(dialog.SelectedPath);
        }
    }

    private async void InstallGame_Click(object sender, RoutedEventArgs e)
    {
        if (_installCts is not null) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "steam://install/240", UseShellExecute = true });
            _logger.Info("Открыт официальный Steam URI steam://install/240.");
            InstallButton.IsEnabled = false;
            RetryInstallButton.Visibility = Visibility.Visible;
            RetryInstallButton.IsEnabled = false;
            StatusText.Text = "Ожидание установки в Steam; проверяем папки библиотек...";
            HintText.Text = "Можно вручную выбрать папку игры; прогресс установки показывает Steam.";
            _installCts = new CancellationTokenSource();
            var deadline = DateTime.UtcNow.AddMinutes(30);
            var found = false;
            while (DateTime.UtcNow < deadline && !_installCts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), _installCts.Token);
                if (await FindInstalledGameAsync(_installCts.Token))
                {
                    found = true;
                    break;
                }
                StatusText.Text = "Установка ещё не завершена — ожидаем Steam...";
            }

            if (!found && !_installCts.IsCancellationRequested)
            {
                StatusText.Text = "Игра пока не найдена. Выберите папку или нажмите «Проверить снова».";
                RetryInstallButton.IsEnabled = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error("Не удалось открыть Steam или дождаться установки.", ex);
            StatusText.Text = "Не удалось открыть установку Steam";
            HintText.Text = ex.Message;
            RetryInstallButton.Visibility = Visibility.Visible;
            RetryInstallButton.IsEnabled = true;
        }
        finally
        {
            _installCts?.Dispose();
            _installCts = null;
            InstallButton.IsEnabled = true;
        }
    }

    private async void RetryInstall_Click(object sender, RoutedEventArgs e)
    {
        RetryInstallButton.IsEnabled = false;
        StatusText.Text = "Повторно проверяем библиотеки Steam...";
        try
        {
            if (!await FindInstalledGameAsync(CancellationToken.None))
            {
                StatusText.Text = "Полная установка CS:S пока не найдена. Можно выбрать папку вручную.";
                RetryInstallButton.Visibility = Visibility.Visible;
                RetryInstallButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Повторный поиск CS:S завершился ошибкой.", ex);
            StatusText.Text = "Не удалось выполнить повторную проверку";
            HintText.Text = ex.Message;
            RetryInstallButton.IsEnabled = true;
        }
    }

    private void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingProfile && ProfileBox.SelectedItem is LauncherProfile profile)
            SetActiveProfile(profile, applyProfileDefaults: true);
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingProfile && ProfilesList.SelectedItem is LauncherProfile profile)
            SetActiveProfile(profile, applyProfileDefaults: true);
    }

    private void SetActiveProfile(LauncherProfile profile, bool applyProfileDefaults)
    {
        _profileSelection.Select(profile.Id);
        _syncingProfile = true;
        try
        {
            ProfileBox.SelectedItem = _activeProfile;
            ProfilesList.SelectedItem = _activeProfile;
        }
        finally
        {
            _syncingProfile = false;
        }
        ProfileDescriptionText.Text = _activeProfile.Description;
        if (applyProfileDefaults)
        {
            NovidBox.IsChecked = _activeProfile.Novid;
            ConsoleBox.IsChecked = _activeProfile.Console;
        }
        UpdatePlayState();
    }

    private void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is LauncherProfile profile)
        {
            SetActiveProfile(profile, applyProfileDefaults: true);
            HintText.Text = $"Профиль {_activeProfile.Name} выбран";
        }
    }

    private void UpdatePlayState()
    {
        var versionAllowsLaunch = _check?.Status == VersionStatus.Confirmed ||
            (_check?.Status == VersionStatus.UnknownBuild && DiagnosticBox.IsChecked == true);
        PlayButton.IsEnabled = versionAllowsLaunch && !_launchGate.IsLaunching;
    }

    private void DiagnosticChanged(object sender, RoutedEventArgs e)
    {
        UpdatePlayState();
        if (DiagnosticBox.IsChecked == true) _logger.Warning("Диагностический режим включён.");
    }

    private LaunchOptions BuildOptions() => ProfileLaunchOptionsBuilder.Build(
        _activeProfile,
        SteamBox.IsChecked == true,
        NovidBox.IsChecked,
        ConsoleBox.IsChecked,
        string.IsNullOrWhiteSpace(_selectedExecutable) ? "" : Path.GetFileName(_selectedExecutable));

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!_launchGate.TryBegin()) return;
        UpdatePlayState();
        try
        {
            if (_check is null || !(_check.Status == VersionStatus.Confirmed ||
                (_check.Status == VersionStatus.UnknownBuild && DiagnosticBox.IsChecked == true)))
                return;

            if (HashMismatchPrompt.IsRequired(_check))
            {
                var message = "Проверка SHA-256 обнаружила расхождения. Запуск может быть небезопасным или несовместимым:\n\n" +
                    HashMismatchPrompt.Format(_check) + "\n\nПродолжить запуск?";
                var answer = WpfMessageBox.Show(message, "CS:L — SHA-256", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }

            var profilePath = await _profiles.PrepareAsync(
                _gamePath, _activeProfile, Path.Combine(_root, "templates", "gameinfo.template.txt"));
            await new GameLauncher().LaunchAsync(_gamePath, profilePath, BuildOptions(), _logger.Info);
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка запуска.", ex);
            WpfMessageBox.Show(ex.Message, "CS:L", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _launchGate.Complete();
            UpdatePlayState();
        }
    }

    private async void LoadServers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var source = ServerSourceBox.Text.Trim();
            var uri = Uri.TryCreate(source, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"
                ? source
                : Path.Combine(_root, source);
            var list = await _servers.LoadAsync(uri);
            ServersList.ItemsSource = list.Servers;
            ServerDetailText.Text = $"Обновлено: {list.UpdatedAt}; серверов: {list.Servers.Count}";
        }
        catch (Exception ex)
        {
            ServerDetailText.Text = $"Не удалось загрузить список: {ex.Message}";
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var result = await _updates.CheckAsync(Path.Combine(_root, "config", "launcher-update.json"), "0.3.2");
        UpdateStatusText.Text = result.Message;
        if (result.Available && result.Update is not null)
        {
            try
            {
                var file = await _updates.DownloadAsync(result.Update);
                UpdateStatusText.Text = $"Обновление скачано: {file}";
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = ex.Message;
            }
        }
    }

    private async void CreateDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        CreateDiagnosticButton.IsEnabled = false;
        DiagnosticStatusText.Text = "Создаём архив в фоновом потоке...";
        try
        {
            var archive = await _diagnostics.CreateAsync(_gamePath, _activeProfile.Id);
            DiagnosticStatusText.Text = $"Создан архив: {archive.ArchivePath} ({archive.FilesIncluded} файлов)";
        }
        catch (Exception ex)
        {
            DiagnosticStatusText.Text = ex.Message;
        }
        finally
        {
            CreateDiagnosticButton.IsEnabled = true;
        }
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 0;
    private void ProfilesNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;
    private void ServersNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 2;
    private void DiagnosticsNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 3;

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _installCts?.Cancel();

        while (true)
        {
            try
            {
                await SaveAsync();
                _closeApproved = true;
                _logger.Dispose();
                Close();
                return;
            }
            catch (Exception ex)
            {
                _logger.Error("Не удалось сохранить настройки при закрытии.", ex);
                var answer = WpfMessageBox.Show(
                    $"Не удалось сохранить настройки:\n{ex.Message}\n\nДа — повторить сохранение; Нет — закрыть без сохранения; Отмена — остаться в лаунчере.",
                    "CS:L — ошибка сохранения", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes) continue;
                if (answer == MessageBoxResult.No)
                {
                    _closeApproved = true;
                    _logger.Dispose();
                    Close();
                    return;
                }

                _closing = false;
                return;
            }
        }
    }

    private sealed record ExecutableChoice(string Name, string Path);
}
