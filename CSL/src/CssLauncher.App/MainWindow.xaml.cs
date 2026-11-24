using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;
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
    private VersionCheckResult? _check;
    private LauncherProfile _activeProfile = ProfileCatalogService.BuiltInProfiles[0];
    private CancellationTokenSource? _installCts;

    public MainWindow()
    {
        InitializeComponent();
        _logger.Message += m => Dispatcher.Invoke(() => HintText.Text = m.Length > 100 ? m[..100] : m);
        Loaded += async (_, _) => await LoadAsync();
        Closing += async (_, _) => { _installCts?.Cancel(); await SaveAsync(); _logger.Dispose(); };
    }
    private async Task LoadAsync()
    {
        foreach (var profile in ProfileCatalogService.BuiltInProfiles) { ProfileBox.Items.Add(profile); ProfilesList.Items.Add(profile); }
        ProfileBox.SelectedIndex = 0; ProfilesList.SelectedIndex = 0;
        var settings = await _settings.LoadAsync(); SetPathText(settings.GamePath); DiagnosticBox.IsChecked = settings.DiagnosticMode;
        SteamBox.IsChecked = settings.EffectiveLaunchOptions.Steam; NovidBox.IsChecked = settings.EffectiveLaunchOptions.Novid; ConsoleBox.IsChecked = settings.EffectiveLaunchOptions.Console;
        if (!string.IsNullOrWhiteSpace(settings.GamePath)) await CheckPathAsync(settings.GamePath); else await AutoDetectAsync();
    }
    private void SetPathText(string path) => PathText.Text = string.IsNullOrWhiteSpace(path) ? "Укажите папку или установите игру через Steam" : path;
    private Task SaveAsync() => _settings.SaveAsync(new LauncherSettings(PathText.Text ?? "", new LaunchOptions(NovidBox.IsChecked == true, ConsoleBox.IsChecked == true, Steam: SteamBox.IsChecked == true), DiagnosticBox.IsChecked == true));
    private async Task AutoDetectAsync()
    {
        StatusText.Text = "Ищем CS:S в библиотеках Steam...";
        var candidate = (await new GameLocator().FindCandidatesAsync(CancellationToken.None)).FirstOrDefault();
        if (candidate is null) { StatusText.Text = "CS:S не найдена"; SideStatusText.Text = "Не установлена"; HintText.Text = "Установите игру через Steam"; return; }
        PathText.Text = candidate.RootPath; await CheckPathAsync(candidate.RootPath);
    }
    private async Task CheckPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { _check = null; StatusText.Text = "Папка игры не найдена"; SideStatusText.Text = "Не подключено"; UpdatePlayState(); return; }
        _check = await new VersionChecker(Path.Combine(_root, "config", "version-manifest.v93.json")).CheckAsync(path);
        PathText.Text = path; BuildText.Text = $"ClientVersion: {_check.ClientVersion ?? "—"}   •   Steam buildid: {_check.BuildId ?? "—"}";
        StatusText.Text = _check.Status == VersionStatus.Confirmed ? "CS:S готова к запуску" : "Версия требует подтверждения"; SideStatusText.Text = _check.Status == VersionStatus.Confirmed ? "Готова" : "Проверить сборку"; SideVersionText.Text = _check.ClientVersion is null ? "—" : $"v{_check.ClientVersion}"; UpdatePlayState();
        if (!string.IsNullOrWhiteSpace(_check.Message)) _logger.Warning(_check.Message);
    }
    private void ChooseFolder_Click(object sender, RoutedEventArgs e) { using var dialog = new Forms.FolderBrowserDialog { Description = "Выберите папку Counter-Strike Source" }; if (dialog.ShowDialog() == Forms.DialogResult.OK) _ = CheckPathAsync(dialog.SelectedPath); }
    private async void InstallGame_Click(object sender, RoutedEventArgs e)
    {
        if (_installCts is not null) return; try { Process.Start(new ProcessStartInfo { FileName = "steam://install/240", UseShellExecute = true }); _logger.Info("Открыт официальный Steam URI steam://install/240."); InstallButton.IsEnabled = false; _installCts = new CancellationTokenSource();
            var deadline = DateTime.UtcNow.AddMinutes(30); while (DateTime.UtcNow < deadline && !_installCts.Token.IsCancellationRequested) { await Task.Delay(4000, _installCts.Token); var c = (await new GameLocator().FindCandidatesAsync(_installCts.Token)).FirstOrDefault(); if (c is null) continue; await CheckPathAsync(c.RootPath); break; }
        } catch (OperationCanceledException) { } catch (Exception ex) { _logger.Error("Не удалось открыть Steam.", ex); } finally { _installCts?.Dispose(); _installCts = null; InstallButton.IsEnabled = true; }
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e) { if (ProfileBox.SelectedItem is LauncherProfile p) { _activeProfile = p; ProfileDescriptionText.Text = p.Description; } }
    private void ApplyProfile_Click(object sender, RoutedEventArgs e) { if (ProfilesList.SelectedItem is LauncherProfile p) { _activeProfile = p; ProfileBox.SelectedItem = p; HintText.Text = $"Профиль {p.Name} выбран"; } }
    private void UpdatePlayState() => PlayButton.IsEnabled = _check?.Status == VersionStatus.Confirmed || (_check?.Status == VersionStatus.UnknownBuild && DiagnosticBox.IsChecked == true);
    private void DiagnosticChanged(object sender, RoutedEventArgs e) { UpdatePlayState(); if (DiagnosticBox.IsChecked == true) _logger.Warning("Диагностический режим включён."); }
    private LaunchOptions BuildOptions() => new(NovidBox.IsChecked == true, ConsoleBox.IsChecked == true, Steam: SteamBox.IsChecked == true, Extra: _activeProfile.Extra);
    private async void Play_Click(object sender, RoutedEventArgs e)
    { try { var path = await _profiles.PrepareAsync(PathText.Text, _activeProfile, Path.Combine(_root, "templates", "gameinfo.template.txt")); await new GameLauncher().LaunchAsync(PathText.Text, path, BuildOptions(), _logger.Info); } catch (Exception ex) { _logger.Error("Ошибка запуска.", ex); System.Windows.MessageBox.Show(ex.Message, "CS:L", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private async void LoadServers_Click(object sender, RoutedEventArgs e) { try { var list = await _servers.LoadAsync(Path.Combine(_root, ServerSourceBox.Text)); ServersList.ItemsSource = list.Servers; ServerDetailText.Text = $"Обновлено: {list.UpdatedAt}; серверов: {list.Servers.Count}"; } catch (Exception ex) { ServerDetailText.Text = $"Не удалось загрузить список: {ex.Message}"; } }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) { var result = await _updates.CheckAsync(Path.Combine(_root, "config", "launcher-update.json"), "0.3.0"); UpdateStatusText.Text = result.Message; if (result.Available && result.Update is not null) try { var file = await _updates.DownloadAsync(result.Update); UpdateStatusText.Text = $"Обновление скачано: {file}"; } catch (Exception ex) { UpdateStatusText.Text = ex.Message; } }
    private async void CreateDiagnostic_Click(object sender, RoutedEventArgs e) { try { var archive = await _diagnostics.CreateAsync(PathText.Text, _activeProfile.Id); DiagnosticStatusText.Text = $"Создан архив: {archive.ArchivePath} ({archive.FilesIncluded} файлов)"; } catch (Exception ex) { DiagnosticStatusText.Text = ex.Message; } }
    private void HomeNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 0;
    private void ProfilesNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;
    private void ServersNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 2;
    private void DiagnosticsNav_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 3;
}
