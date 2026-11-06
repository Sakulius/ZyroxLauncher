using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Zyrox.Services;

namespace Zyrox.Pages;

public partial class ModsPage : UserControl, IPage
{
    private const int PageSize = 20;

    private readonly ModService _mods = new();

    private LauncherState _state = null!;
    private CancellationTokenSource? _search;
    private List<PendingModUpdate> _updates = [];
    private int _offset;

    public ModsPage()
    {
        InitializeComponent();
    }

    private bool IsPackMode => PacksMode.IsChecked == true;
    private ContentType? ContentMode => ResourcePacksMode.IsChecked == true ? ContentType.ResourcePack
        : ShadersMode.IsChecked == true ? ContentType.Shader : null;
    private bool IsModMode => !IsPackMode && ContentMode == null;

    public void Initialize(LauncherState state) => _state = state;

    public void OnShown()
    {
        var profile = _state.SelectedProfile;
        var showContent = !IsModMode || profile.HasLoader;
        VanillaHint.Visibility = showContent ? Visibility.Collapsed : Visibility.Visible;
        ModsContent.Visibility = showContent ? Visibility.Visible : Visibility.Collapsed;
        InstalledCard.Visibility = IsPackMode ? Visibility.Collapsed : Visibility.Visible;
        SideGap.Width = new GridLength(IsPackMode ? 0 : 24);
        SideColumn.Width = new GridLength(IsPackMode ? 0 : 320);
        UpdateButton.Visibility = IsModMode ? Visibility.Visible : Visibility.Collapsed;
        PageTitleText.Text = IsPackMode ? "Modpacks" : ContentMode == ContentType.ResourcePack ? "Resource Packs" : ContentMode == ContentType.Shader ? "Shaders" : "Mods";
        ProfileHint.Text = IsPackMode
            ? "Each modpack installs as its own profile. Everything comes from Modrinth."
            : ContentMode == ContentType.Shader
                ? $"For {profile.Name}. Shaders need the Iris mod, which you can find under Mods."
                : !showContent ? "" : $"For {profile.Name}. Everything comes from Modrinth.";
        if (!showContent)
            return;

        _updates = [];
        UpdateButton.Content = "Check for updates";
        UpdateStatus.Visibility = Visibility.Collapsed;
        if (!IsPackMode)
            RefreshInstalled();
        _ = SearchAsync(reset: true);
    }

    private async Task<string> ResolveGameVersionAsync(GameProfile profile)
    {
        if (profile.GameVersion.Length > 0)
            return profile.GameVersion;
        var versions = await _state.CreateGame(profile).GetGameVersionsAsync(includeSnapshots: false);
        return versions.FirstOrDefault() ?? throw new InvalidOperationException("No Minecraft version found.");
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && _state != null)
            OnShown();
    }

    private void OnGoToProfiles(object sender, RoutedEventArgs e) => _state.Navigate("profiles");

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsVisible)
            _ = SearchAsync(reset: true, delayMs: 300);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e) => _ = SearchAsync(reset: false);

    private async Task SearchAsync(bool reset, int delayMs = 0)
    {
        _search?.Cancel();
        var cts = new CancellationTokenSource();
        _search = cts;
        if (reset)
        {
            _offset = 0;
            ResultList.Children.Clear();
        }

        var profile = _state.SelectedProfile;
        var isPackMode = IsPackMode;
        var contentType = ContentMode;
        SearchStatus.Text = "Searching ...";
        MoreButton.Visibility = Visibility.Collapsed;
        try
        {
            await Task.Delay(delayMs, cts.Token);
            var query = SearchBox.Text.Trim();
            var gameVersion = isPackMode ? "" : await ResolveGameVersionAsync(profile);
            var hits = isPackMode
                ? await _mods.SearchPacksAsync(query, _offset, cts.Token)
                : contentType != null
                    ? await _mods.SearchContentAsync(contentType, query, gameVersion, _offset, cts.Token)
                    : await _mods.SearchAsync(query, profile.Loader, gameVersion, _offset, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            var folder = _state.ProfileDir(profile);
            HashSet<string> installed = isPackMode ? [] : contentType != null ? ModService.GetInstalledContent(folder) : ModService.GetInstalledProjects(folder);
            foreach (var hit in hits)
            {
                var isInstalled = installed.Contains(hit.ProjectId);
                ResultList.Children.Add(isPackMode ? BuildPackResult(hit)
                    : contentType != null ? BuildContentResult(hit, contentType, gameVersion, folder, isInstalled)
                    : BuildModResult(hit, profile, folder, isInstalled));
            }

            _offset += hits.Count;
            MoreButton.Visibility = hits.Count == PageSize ? Visibility.Visible : Visibility.Collapsed;
            SearchStatus.Text = ResultList.Children.Count == 0 ? "Nothing found." : "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SearchStatus.Text = "Search failed:" + ex.Message;
        }
    }

    private Border BuildModResult(ModHit hit, GameProfile profile, string folder, bool isInstalled)
    {
        var install = Ui.Button(isInstalled ? "Installed" : "Install", null, primary: true);
        install.IsEnabled = !isInstalled;
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            install.Content = "Loading ...";
            try
            {
                await _mods.InstallAsync(hit.ProjectId, profile.Loader, profile.GameVersion, folder, CancellationToken.None);
                install.Content = "Installed";
                SearchStatus.Text = "";
                RefreshInstalled();
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                install.Content = "Install";
                install.IsEnabled = true;
                SearchStatus.Text = $"Could not install {hit.Title}: {ex.Message}";
            }
        };
        return BuildRow(hit, install);
    }

    private Border BuildContentResult(ModHit hit, ContentType type, string gameVersion, string folder, bool isInstalled)
    {
        var install = Ui.Button(isInstalled ? "Installed" : "Install", null, primary: true);
        install.IsEnabled = !isInstalled;
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            install.Content = "Loading ...";
            try
            {
                await _mods.InstallContentAsync(hit.ProjectId, type, gameVersion, folder, CancellationToken.None);
                install.Content = "Installed";
                SearchStatus.Text = "";
                RefreshInstalled();
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                install.Content = "Install";
                install.IsEnabled = true;
                SearchStatus.Text = $"Could not install {hit.Title}: {ex.Message}";
            }
        };
        return BuildRow(hit, install);
    }

    private Border BuildPackResult(ModHit hit)
    {
        var install = Ui.Button("Install", null, primary: true);
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            var profile = new GameProfile { Name = hit.Title };
            var folder = _state.ProfileDir(profile);
            try
            {
                var progress = new Progress<string>(text => install.Content = text);
                var pack = await _mods.InstallPackAsync(hit.ProjectId, hit.Title, folder, progress, CancellationToken.None);
                profile.GameVersion = pack.GameVersion;
                profile.Loader = pack.Loader;
                profile.LoaderVersion = pack.LoaderVersion;
                _state.AddProfile(profile);
                install.Content = "Installed";
                SearchStatus.Text = $"{hit.Title} is now your active profile.";
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                TryDelete(folder);
                install.Content = "Install";
                install.IsEnabled = true;
                SearchStatus.Text = $"Could not install {hit.Title}: {ex.Message}";
            }
        };
        return BuildRow(hit, install);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is HttpRequestException or JsonException or IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException or InvalidDataException or ArgumentException;

    private static Border BuildRow(ModHit hit, Button action)
    {
        var row = new DockPanel();
        DockPanel.SetDock(action, Dock.Right);
        row.Children.Add(action);

        var icon = new Image { Stretch = System.Windows.Media.Stretch.UniformToFill };
        if (hit.IconUrl != null && Uri.TryCreate(hit.IconUrl, UriKind.Absolute, out var iconUri) && iconUri.Scheme == Uri.UriSchemeHttps)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = iconUri;
            bitmap.DecodePixelWidth = 96;
            bitmap.EndInit();
            icon.Source = bitmap;
        }
        var frame = Ui.IconFrame(icon);
        DockPanel.SetDock(frame, Dock.Left);
        row.Children.Add(frame);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Title(hit.Title));
        var description = Ui.Muted(hit.Description);
        description.MaxHeight = 36;
        description.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(description);
        text.Children.Add(Ui.Muted($"{FormatCount(hit.Downloads)} Downloads"));
        row.Children.Add(text);
        return Ui.Card(row);
    }

    private static string FormatCount(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000d:0.0}M",
        >= 1_000 => $"{count / 1_000d:0.0}K",
        _ => count.ToString()
    };

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        var profile = _state.SelectedProfile;
        var folder = _state.ProfileDir(profile);
        UpdateButton.IsEnabled = false;
        UpdateStatus.Visibility = Visibility.Visible;
        try
        {
            if (_updates.Count == 0)
            {
                UpdateStatus.Text = "Checking for updates ...";
                _updates = await _mods.FindUpdatesAsync(profile.Loader, profile.GameVersion, folder, CancellationToken.None);
                UpdateStatus.Text = _updates.Count == 0
                    ? "All Modrinth mods are up to date."
                    : string.Join(", ", _updates.Select(u => u.Mod.Name));
                UpdateButton.Content = _updates.Count == 0 ? "Check for updates" : $"Update all ({_updates.Count})";
            }
            else
            {
                UpdateStatus.Text = "Updating ...";
                var count = _updates.Count;
                await _mods.ApplyUpdatesAsync(_updates, profile.Loader, profile.GameVersion, folder, CancellationToken.None);
                _updates = [];
                UpdateButton.Content = "Check for updates";
                UpdateStatus.Text = $"{count} mods updated.";
                RefreshInstalled();
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            UpdateStatus.Text = "Update failed:" + ex.Message;
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private void RefreshInstalled()
    {
        var folder = _state.ProfileDir(_state.SelectedProfile);
        if (ContentMode is { } contentType)
        {
            RefreshContent(contentType, folder);
            return;
        }
        var mods = ModService.GetInstalled(folder);
        InstalledTitle.Text = $"Installed ({mods.Count})";
        InstalledList.Children.Clear();
        if (mods.Count == 0)
            InstalledList.Children.Add(Ui.Muted("No mods yet."));

        foreach (var mod in mods)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var remove = new Button
            {
                Content = "",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("Icons"),
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(8, 6, 8, 6),
                ToolTip = "Delete mod"
            };
            remove.Click += (_, _) => Change(() => ModService.Remove(mod, folder));
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var toggle = new CheckBox
            {
                IsChecked = mod.Enabled,
                ToolTip = "Enable or disable mod",
                VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = mod.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 }
            };
            toggle.Click += (_, _) => Change(() => ModService.SetEnabled(mod, toggle.IsChecked == true));
            row.Children.Add(toggle);
            InstalledList.Children.Add(row);
        }
    }

    private void RefreshContent(ContentType type, string folder)
    {
        var files = ModService.GetContentFiles(type, folder);
        InstalledTitle.Text = $"Installed ({files.Count})";
        InstalledList.Children.Clear();
        if (files.Count == 0)
            InstalledList.Children.Add(Ui.Muted("Nothing installed yet. You can turn it on in game afterwards."));

        foreach (var file in files)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var remove = new Button
            {
                Content = "\uE74D",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("Icons"),
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(8, 6, 8, 6),
                ToolTip = "Delete file"
            };
            remove.Click += (_, _) => Change(() => File.Delete(file));
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = Path.GetFileName(file), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            InstalledList.Children.Add(row);
        }
    }

    private void Change(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SearchStatus.Text = "That did not work:" + ex.Message;
        }
        _updates = [];
        UpdateButton.Content = "Check for updates";
        RefreshInstalled();
    }
}
