using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;
using CssLauncher.Core;
using CssLauncher.Game;
using CssLauncher.Infrastructure;

namespace CssLauncher.App;

public partial class MainWindow : Window
{
    private const string CurrentVersion = "0.3.6-rc2";
    private readonly FileLogger _logger = new();
    private readonly JsonSettingsService _settings = new();
    private readonly string _root = AppContext.BaseDirectory;
    private readonly bool _safeMode;
    private readonly bool _previousRunWasUnclean;
    private readonly CrashRecoveryMarker _recoveryMarker;
    private readonly ServerListService _servers = new();
    private readonly LauncherUpdateService _updates = new();
    private readonly DiagnosticArchiveService _diagnostics = new();
    private readonly LaunchGate _launchGate = new();
    private VersionCheckResult? _check;
    private CancellationTokenSource? _installCts;
    private string _gamePath = "";
    private string _selectedExecutable = "";
    private string _savedExecutable = "";
    private bool _syncingExecutable;
    private bool _closing;
    private bool _closeApproved;
    private bool _settingsLoadFailed;

    public MainWindow(bool safeMode, bool previousRunWasUnclean, CrashRecoveryMarker recoveryMarker)
    {
        _safeMode = safeMode;
        _previousRunWasUnclean = previousRunWasUnclean;
        _recoveryMarker = recoveryMarker;
        InitializeComponent();
        Title = $"CS:L {CurrentVersion}";
        LauncherVersionText.Text = $"CS:L v{CurrentVersion}";
        FitWindowToWorkArea();
        _logger.Message += message => Dispatcher.BeginInvoke(new Action(() =>
            HintText.Text = message.Length > 100 ? message[..100] : message));
        Loaded += async (_, _) =>
        {
            try
            {
                if (_safeMode)
                {
                    SafeModeNotice.Visibility = Visibility.Visible;
                    PlayButton.IsEnabled = false;
                    InstallButton.IsEnabled = false;
                    ChooseFolderButton.IsEnabled = false;
                    CheckUpdateButton.IsEnabled = false;
                    StatusText.Text = "Безопасный режим восстановления";
                    SideStatusText.Text = "Безопасный режим";
                }
                if (_previousRunWasUnclean)
                {
                    RecoveryNoticeText.Text = "Предыдущий запуск CS:L завершился некорректно. Проверьте копии настроек и создайте диагностический архив.";
                    RecoveryNotice.Visibility = Visibility.Visible;
                    var answer = WpfMessageBox.Show(
                        "Предыдущий запуск CS:L завершился некорректно. Открыть вкладку восстановления? Там доступны копии настроек и диагностика.",
                        "CS:L — восстановление после сбоя", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer == MessageBoxResult.Yes) MainTabs.SelectedItem = RecoveryTab;
                }
                await LoadAsync();
            }
            catch (Exception ex)
            {
                _logger.Error("Не удалось завершить инициализацию лаунчера.", ex);
                StatusText.Text = "Ошибка инициализации";
                SideStatusText.Text = "Ошибка";
                HintText.Text = ex.Message;
                PlayButton.IsEnabled = false;
            }
        };
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
    }

    private void FitWindowToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0) return;

        MinWidth = Math.Min(760, workArea.Width);
        MinHeight = Math.Min(460, workArea.Height);
        Width = Math.Min(1200, workArea.Width);
        Height = Math.Min(760, workArea.Height);

        var compact = workArea.Width < 700;
        SidebarPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = compact
            ? new GridLength(0)
            : new GridLength(Math.Clamp(workArea.Width * 0.23, 145, 176));
        SidebarGapColumn.Width = compact ? new GridLength(0) : new GridLength(13);
        GameActionsColumn.Width = new GridLength(compact ? 132 : 158);
    }

    private void WindowHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        try { DragMove(); }
        catch (InvalidOperationException) { }
        e.Handled = true;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "□";
        MaximizeButton.ToolTip = maximized ? "Восстановить" : "Развернуть";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton,
            maximized ? "Восстановить размер окна" : "Развернуть окно");
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private async Task LoadAsync()
    {
        LauncherSettings settings;
        try
        {
            settings = await _settings.LoadAsync();
        }
        catch (SettingsCorruptedException ex)
        {
            _logger.Warning($"Повреждённый settings.json перемещён в {ex.BackupPath}.");
            var latestBackup = ex.RecoverableBackups.FirstOrDefault();
            if (latestBackup is not null && WpfMessageBox.Show(
                    $"Файл настроек повреждён; его исходник сохранён здесь:\n{ex.BackupPath}\n\nВосстановить последнюю рабочую копию от {latestBackup.CreatedUtc:u}?",
                    "CS:L — восстановление настроек", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try { settings = await _settings.RestoreBackupAsync(latestBackup.Path); }
                catch (Exception restoreError)
                {
                    _logger.Error("Не удалось восстановить последнюю копию настроек.", restoreError);
                    WpfMessageBox.Show($"Не удалось восстановить копию. Используются значения по умолчанию.\n\n{restoreError.Message}",
                        "CS:L — восстановление настроек", MessageBoxButton.OK, MessageBoxImage.Error);
                    settings = new LauncherSettings();
                }
            }
            else
            {
                WpfMessageBox.Show($"Файл настроек был повреждён и сохранён как резервная копия:\n{ex.BackupPath}\n\nИспользуются значения по умолчанию.",
                    "CS:L — восстановление настроек", MessageBoxButton.OK, MessageBoxImage.Warning);
                settings = new LauncherSettings();
            }
        }
        catch (UnsupportedSettingsSchemaException ex)
        {
            _settingsLoadFailed = true;
            _logger.Warning(ex.Message);
            WpfMessageBox.Show($"{ex.Message}\n\nФайл сохранён без изменений; новые настройки не будут записаны поверх неизвестной схемы.",
                "CS:L — версия настроек", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        await RefreshRecoveryListsAsync();
        if (settings.LaunchOptions is not null)
        {
            NovidBox.IsChecked = settings.EffectiveLaunchOptions.Novid;
            ConsoleBox.IsChecked = settings.EffectiveLaunchOptions.Console;
        }

        SetPathText(settings.GamePath);
        if (_safeMode)
        {
            _check = null;
            StatusText.Text = "Безопасный режим восстановления";
            SideStatusText.Text = "Безопасный режим";
            UpdateExecutableOptions("");
            UpdatePlayState();
            return;
        }
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
            Executable = string.IsNullOrWhiteSpace(_selectedExecutable) ? _savedExecutable : Path.GetFileName(_selectedExecutable)
        };
        return _settings.SaveAsync(settings);
    }

    private async Task RefreshRecoveryListsAsync()
    {
        try
        {
            SettingsBackupsList.ItemsSource = await _settings.GetRecoverableBackupsAsync();
            RestoreSettingsButton.IsEnabled = SettingsBackupsList.Items.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось обновить список резервных копий.", ex);
            HintText.Text = $"Не удалось прочитать список резервных копий: {ex.Message}";
        }
    }

    private async void RestoreSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SettingsBackupsList.SelectedItem is not SettingsBackupInfo backup)
            {
                WpfMessageBox.Show("Сначала выберите копию настроек.", "CS:L — восстановление", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (WpfMessageBox.Show($"Восстановить настройки из копии от {backup.CreatedUtc:u}? Текущий файл будет сохранён в резервную копию.",
                    "CS:L — подтверждение восстановления", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await _settings.RestoreBackupAsync(backup.Path);
            _settingsLoadFailed = false;
            _logger.Info($"Настройки восстановлены из {backup.Path}.");
            WpfMessageBox.Show("Настройки восстановлены. Перезапустите CS:L, чтобы применить их полностью.",
                "CS:L — восстановление", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshRecoveryListsAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка восстановления настроек.", ex);
            WpfMessageBox.Show($"Не удалось восстановить настройки.\n\n{ex.Message}",
                "CS:L — восстановление", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ResetEverything_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (WpfMessageBox.Show("Сбросить локальные настройки CS:L? Файлы игры и Steam не изменяются.",
                    "CS:L — сброс настроек", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _settingsLoadFailed = false;
            SetPathText("");
            _savedExecutable = "";
            _selectedExecutable = "";
            _check = null;
            DiagnosticBox.IsChecked = false;
            SteamBox.IsChecked = true;
            NovidBox.IsChecked = true;
            ConsoleBox.IsChecked = true;
            UpdateExecutableOptions("");
            UpdatePlayState();
            await _settings.SaveAsync(new LauncherSettings());
            _logger.Warning("Выполнен подтверждённый сброс настроек CS:L.");
            WpfMessageBox.Show("Настройки CS:L сброшены. Игровые файлы не изменялись.",
                "CS:L — сброс настроек", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshRecoveryListsAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка сброса настроек.", ex);
            WpfMessageBox.Show($"Не удалось сбросить настройки.\n\n{ex.Message}", "CS:L — сброс настроек", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task AutoDetectAsync()
    {
        if (_safeMode) return;
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
        var candidates = await new GameLocator(_gamePath, logError: ex => _logger.Error("Ошибка чтения Steam-библиотеки или реестра.", ex))
            .FindCandidatesAsync(ct);
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
        try
        {
            using var dialog = new Forms.FolderBrowserDialog { Description = "Выберите папку Counter-Strike Source" };
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                RetryInstallButton.Visibility = Visibility.Collapsed;
                await CheckPathAsync(dialog.SelectedPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось выбрать папку игры.", ex);
            WpfMessageBox.Show($"Не удалось проверить выбранную папку.\n\n{ex.Message}", "CS:L — выбор игры", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private LaunchOptions BuildOptions() => new(
        Novid: NovidBox.IsChecked == true,
        Console: ConsoleBox.IsChecked == true,
        Steam: SteamBox.IsChecked == true,
        Executable: string.IsNullOrWhiteSpace(_selectedExecutable) ? "" : Path.GetFileName(_selectedExecutable));

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!_launchGate.TryBegin()) return;
        UpdatePlayState();
        try
        {
            var previousCheck = _check;
            if (string.IsNullOrWhiteSpace(_gamePath) || !await CheckPathAsync(_gamePath) || _check is null)
                return;

            var currentCheck = _check;
            if (currentCheck.Status != VersionStatus.Confirmed &&
                !(currentCheck.Status == VersionStatus.UnknownBuild && DiagnosticBox.IsChecked == true)) return;

            var changed = previousCheck is null || previousCheck.Status != currentCheck.Status ||
                !string.Equals(previousCheck.ClientVersion, currentCheck.ClientVersion, StringComparison.Ordinal) ||
                !string.Equals(previousCheck.BuildId, currentCheck.BuildId, StringComparison.Ordinal) ||
                !previousCheck.Mismatches.SequenceEqual(currentCheck.Mismatches);
            if (changed && WpfMessageBox.Show(
                    $"Состояние игры изменилось после предыдущей проверки.\n\nБыло: {previousCheck?.ClientVersion ?? "не проверено"} / build {previousCheck?.BuildId ?? "—"}\nСейчас: {currentCheck.ClientVersion ?? "—"} / build {currentCheck.BuildId ?? "—"}\n\nПроверка выполнена заново. Продолжить?",
                    "CS:L — изменилось состояние игры", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            if (HashMismatchPrompt.IsRequired(currentCheck))
            {
                var message = "Проверка SHA-256 обнаружила расхождения. Запуск может быть небезопасным или несовместимым:\n\n" +
                    HashMismatchPrompt.Format(currentCheck) + "\n\nПродолжить запуск?";
                var answer = WpfMessageBox.Show(message, "CS:L — SHA-256", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }

            _logger.Info("Запуск выполняется с официальным cstrike\\gameinfo.txt; пользовательские игровые файлы не изменяются.");
            await new GameLauncher().LaunchAsync(_gamePath, BuildOptions(), _logger.Info);
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
            _logger.Error("Не удалось загрузить список серверов.", ex);
            ServerDetailText.Text = $"Не удалось загрузить список: {ex.Message}";
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _updates.CheckAsync(Path.Combine(_root, "config", "launcher-update.json"), CurrentVersion);
            UpdateStatusText.Text = result.Message;
            if (result.Available && result.Update is not null)
            {
                var file = await _updates.DownloadAsync(result.Update);
                UpdateStatusText.Text = $"Обновление скачано: {file}";
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Ошибка проверки/загрузки обновления.", ex);
            UpdateStatusText.Text = $"Ошибка обновления: {ex.Message}";
            WpfMessageBox.Show(UpdateStatusText.Text, "CS:L — обновление", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CreateDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        CreateDiagnosticButton.IsEnabled = false;
        DiagnosticStatusText.Text = "Создаём архив в фоновом потоке...";
        try
        {
            var archive = await _diagnostics.CreateAsync(_gamePath);
            DiagnosticStatusText.Text = $"Создан архив: {archive.ArchivePath} ({archive.FilesIncluded} файлов)";
        }
        catch (Exception ex)
        {
            _logger.Error("Не удалось создать диагностический архив.", ex);
            DiagnosticStatusText.Text = ex.Message;
            WpfMessageBox.Show($"Не удалось создать диагностический архив.\n\n{ex.Message}", "CS:L — диагностика", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CreateDiagnosticButton.IsEnabled = true;
        }
    }

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
                CompleteRecoveryMarkerForCleanExit();
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
                    CompleteRecoveryMarkerForCleanExit();
                    _logger.Dispose();
                    Close();
                    return;
                }

                _closing = false;
                return;
            }
        }
    }

    private void CompleteRecoveryMarkerForCleanExit()
    {
        try { _recoveryMarker.CompleteRun(); }
        catch (Exception ex)
        {
            _logger.Error("Не удалось удалить маркер завершённого запуска.", ex);
            WpfMessageBox.Show($"Не удалось очистить маркер аварийного запуска. При следующем старте может снова появиться предложение восстановления.\n\n{ex.Message}",
                "CS:L — ошибка завершения", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed record ExecutableChoice(string Name, string Path);
}
