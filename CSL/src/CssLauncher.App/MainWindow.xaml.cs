using System.Diagnostics;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using CssLauncher.Game;
using CssLauncher.Infrastructure;
using CssLauncher.Core;

namespace CssLauncher.App;

public partial class MainWindow : Window
{
    private readonly FileLogger _logger;
    private readonly JsonSettingsService _settings = new();
    private readonly string _root = AppContext.BaseDirectory;
    private readonly string _dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CssLauncher");
    private VersionCheckResult? _check;
    private CancellationTokenSource? _installCts;

    public MainWindow()
    {
        InitializeComponent();
        _logger = new FileLogger();
        _logger.Message += m => Dispatcher.Invoke(() => { LogBox.AppendText(m + Environment.NewLine); LogBox.ScrollToEnd(); });
        Loaded += async (_, _) => await LoadSettingsAsync();
        Closing += async (_, _) => { _installCts?.Cancel(); await SaveSettingsAsync(); _logger.Dispose(); };
    }

    private async Task LoadSettingsAsync()
    {
        Directory.CreateDirectory(Path.Combine(_dataRoot, "profiles", "default", "custom"));
        Directory.CreateDirectory(Path.Combine(_dataRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(_dataRoot, "downloads"));
        Directory.CreateDirectory(Path.Combine(_dataRoot, "backups"));
        var s = await _settings.LoadAsync();
        GamePathBox.Text = s.GamePath;
        DiagnosticBox.IsChecked = s.DiagnosticMode;
        SteamBox.IsChecked = s.EffectiveLaunchOptions.Steam;
        NovidBox.IsChecked = s.EffectiveLaunchOptions.Novid;
        ConsoleBox.IsChecked = s.EffectiveLaunchOptions.Console;
        WindowedBox.IsChecked = s.EffectiveLaunchOptions.Windowed;
        NoborderBox.IsChecked = s.EffectiveLaunchOptions.Noborder;
        if (!string.IsNullOrWhiteSpace(s.EffectiveLaunchOptions.Executable)) ExecutableBox.Text = s.EffectiveLaunchOptions.Executable;
        if (!string.IsNullOrWhiteSpace(s.GamePath)) await CheckPathAsync(s.GamePath); else await AutoDetectAsync();
    }

    private Task SaveSettingsAsync() => _settings.SaveAsync(new LauncherSettings(GamePathBox.Text, BuildLaunchOptions(), DiagnosticBox.IsChecked == true));
    private LaunchOptions BuildLaunchOptions() => new(NovidBox.IsChecked == true, ConsoleBox.IsChecked == true, WindowedBox.IsChecked == true, NoborderBox.IsChecked == true, Steam: SteamBox.IsChecked == true, Executable: ExecutableBox.Text ?? "");

    private async Task AutoDetectAsync()
    {
        StatusText.Text = "Ищем CS:S в библиотеках Steam...";
        var candidate = (await new GameLocator(GamePathBox.Text).FindCandidatesAsync(CancellationToken.None)).FirstOrDefault();
        if (candidate is null) { StatusText.Text = "CS:S не найдена"; SideStatusText.Text = "Не установлена"; HintText.Text = "Установите игру через Steam"; _logger.Info("CS:S не найдена. Можно запустить официальную установку через Steam."); return; }
        GamePathBox.Text = candidate.RootPath;
        await CheckPathAsync(candidate.RootPath);
    }

    private async Task CheckPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _check = null; StatusText.Text = "Папка игры не найдена"; PathText.Text = "Установите CS:S через Steam или выберите папку вручную"; SideStatusText.Text = "Не подключено"; SideVersionText.Text = "—"; UpdatePlayState(); return;
        }
        var manifest = Path.Combine(_root, "config", "version-manifest.v93.json");
        _check = await new VersionChecker(manifest).CheckAsync(path);
        if (!string.IsNullOrWhiteSpace(_check.ExecutablePath)) ExecutableBox.Text = Path.GetFileName(_check.ExecutablePath);
        StatusText.Text = _check.Status == VersionStatus.Confirmed ? "CS:S готова к запуску" : "Версия требует подтверждения";
        PathText.Text = path;
        BuildText.Text = $"ClientVersion: {_check.ClientVersion ?? "—"}   •   Steam buildid: {_check.BuildId ?? "—"}";
        SideStatusText.Text = _check.Status == VersionStatus.Confirmed ? "Готова" : "Проверить сборку";
        SideVersionText.Text = _check.ClientVersion is null ? "—" : $"v{_check.ClientVersion}";
        _logger.Info($"ClientVersion: {_check.ClientVersion ?? "не определён"}; Steam buildid: {_check.BuildId ?? "не определён"}; exe: {_check.ExecutablePath ?? "не найден"}; статус: {_check.Status}");
        if (!string.IsNullOrWhiteSpace(_check.Message)) _logger.Warning(_check.Message);
        foreach (var m in _check.Mismatches) _logger.Warning($"{m.Path}: {m.Kind}, expected={m.Expected}, actual={m.Actual}");
        UpdatePlayState();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Выберите папку Counter-Strike Source" };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) { GamePathBox.Text = dialog.SelectedPath; _ = CheckPathAsync(dialog.SelectedPath); }
    }

    private async void InstallGame_Click(object sender, RoutedEventArgs e)
    {
        if (_installCts is not null) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "steam://install/240", UseShellExecute = true });
            _logger.Info("Открыт официальный Steam URI steam://install/240. Файлы игры загружает Steam.");
            InstallButton.Content = "Ожидаем Steam..."; InstallButton.IsEnabled = false; HintText.Text = "Steam устанавливает CS:S. Это может занять время...";
            _installCts = new CancellationTokenSource();
            var deadline = DateTime.UtcNow.AddMinutes(30);
            while (!_installCts.Token.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), _installCts.Token);
                var candidate = (await new GameLocator().FindCandidatesAsync(_installCts.Token)).FirstOrDefault();
                if (candidate is null) continue;
                GamePathBox.Text = candidate.RootPath; _logger.Info($"Steam завершил установку, игра найдена: {candidate.RootPath}"); await CheckPathAsync(candidate.RootPath); break;
            }
            if (DateTime.UtcNow >= deadline) { HintText.Text = "Не нашли игру за 30 минут. Можно проверить Steam и повторить."; _logger.Warning("Ожидание установки Steam завершено по таймауту 30 минут."); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Error("Не удалось открыть официальный установщик Steam.", ex); MessageBox.Show("Не удалось открыть Steam. Убедитесь, что Steam установлен и запущен.", "CS:L", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _installCts?.Dispose(); _installCts = null; InstallButton.Content = "Установить через Steam"; InstallButton.IsEnabled = true; }
    }

    private void OpenMods_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.Combine(_dataRoot, "mods"));
    private void OpenProfile_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.Combine(_dataRoot, "profiles", "default"));
    private static void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true }); }
    private void DiagnosticChanged(object sender, RoutedEventArgs e) { if (DiagnosticBox.IsChecked == true) _logger.Warning("Диагностический режим включён: версия не подтверждена."); UpdatePlayState(); }
    private void UpdatePlayState() => PlayButton.IsEnabled = _check?.Status == VersionStatus.Confirmed || (_check?.Status == VersionStatus.UnknownBuild && DiagnosticBox.IsChecked == true);

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_check?.Status == VersionStatus.UnknownBuild) _logger.Warning("Запуск неизвестной сборки в диагностическом режиме.");
            var profile = await new ProfileService(Path.Combine(_root, "templates", "gameinfo.template.txt")).CreateOrUpdateAsync(GamePathBox.Text);
            await new GameLauncher().LaunchAsync(GamePathBox.Text, profile, BuildLaunchOptions(), _logger.Info);
        }
        catch (Exception ex) { _logger.Error("Ошибка запуска.", ex); MessageBox.Show(ex.Message, "CS:L", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
