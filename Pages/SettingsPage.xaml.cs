using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Zyrox.Services;

namespace Zyrox.Pages;

public record SettingsChange(bool GameDir, bool OfflineName);

public partial class SettingsPage : UserControl, IPage
{
    private LauncherState _state = null!;
    private bool _isUpdatingTheme;

    public SettingsPage()
    {
        InitializeComponent();
#if DEBUG
        OfflinePanel.Visibility = Visibility.Visible;
#endif
    }

    public event Action<SettingsChange>? Applied;

    public void Initialize(LauncherState state) => _state = state;

    public void OnShown()
    {
        var settings = _state.Settings;
        MinimizeBox.IsChecked = settings.MinimizeOnLaunch;
        SnapshotsBox.IsChecked = settings.ShowSnapshots;
        BlurBox.IsChecked = settings.BlurBackground;
        _isUpdatingTheme = true;
        ThemeBox.ItemsSource = ThemeService.Choices;
        ThemeBox.SelectedItem = ThemeService.Choices.FirstOrDefault(c => c.Id == settings.Theme) ?? ThemeService.Green;
        _isUpdatingTheme = false;
        VersionText.Text = "Installed version:" + UpdateService.CurrentVersion.ToString(3);
        UpdateRepoBox.Text = settings.UpdateRepository;
        UpdateRepoStatus.Visibility = Visibility.Collapsed;
        GameDirBox.Text = _state.RootDir;
        GameDirStatus.Visibility = Visibility.Collapsed;
        DiscordBox.IsChecked = settings.DiscordEnabled;
        OfflineNameBox.Text = settings.OfflineName;
    }

    public void Apply()
    {
        var settings = _state.Settings;
        var gameDir = GameDirBox.Text.Trim();
        var gameDirChanged = gameDir.Length > 0 && !string.Equals(gameDir, _state.RootDir, StringComparison.OrdinalIgnoreCase);
        if (gameDirChanged && !Path.IsPathFullyQualified(gameDir))
        {
            GameDirStatus.Text = "The game folder must be a full path and was not changed.";
            GameDirStatus.Visibility = Visibility.Visible;
            gameDirChanged = false;
        }

        var offlineName = OfflineNameBox.Text.Trim();
        var change = new SettingsChange(gameDirChanged, offlineName != settings.OfflineName);

        settings.MinimizeOnLaunch = MinimizeBox.IsChecked == true;
        settings.ShowSnapshots = SnapshotsBox.IsChecked == true;
        settings.BlurBackground = BlurBox.IsChecked == true;
        ApplyRepository();
        settings.DiscordEnabled = DiscordBox.IsChecked == true;
        settings.OfflineName = offlineName;
        if (gameDirChanged)
            settings.GameDir = gameDir;

        _state.Save();
        Applied?.Invoke(change);
    }

    private bool ApplyRepository()
    {
        var repository = UpdateRepoBox.Text.Trim();
        if (repository.Length > 0 && !UpdateService.IsValidRepository(repository))
        {
            UpdateRepoStatus.Text = "The repository must look like owner/project and was not saved.";
            UpdateRepoStatus.Visibility = Visibility.Visible;
            return false;
        }
        UpdateRepoStatus.Visibility = Visibility.Collapsed;
        _state.Settings.UpdateRepository = repository;
        return true;
    }

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (!ApplyRepository())
            return;
        _state.Save();
        _state.RequestUpdateCheck();
    }

    public void ShowUpdateStatus(string text)
    {
        UpdateRepoStatus.Text = text;
        UpdateRepoStatus.Visibility = Visibility.Visible;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTheme || ThemeBox.SelectedItem is not ThemeChoice theme)
            return;
        _state.Settings.Theme = theme.Id;
        _state.Save();
        _state.NotifyThemeChanged();
    }

    private void OnBlurClick(object sender, RoutedEventArgs e) => _state.Settings.BlurBackground = BlurBox.IsChecked == true;

    private void OnPickGameDir(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose game folder" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            GameDirBox.Text = dialog.FolderName;
    }

    private void OnPickBackground(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Images and GIFs|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        _state.Settings.BackgroundPath = dialog.FileName;
        _state.Save();
        _state.NotifyBackgroundChanged();
    }

    private void OnResetBackground(object sender, RoutedEventArgs e)
    {
        _state.Settings.BackgroundPath = "";
        _state.Save();
        _state.NotifyBackgroundChanged();
    }
}
