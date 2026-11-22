using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Zyrox.Services;

namespace Zyrox.Pages;

public partial class ProfilesPage : UserControl, IPage
{
    private const string VanillaType = "Vanilla";
    private const string FabricType = "Fabric";
    private const string ForgeType = "Forge";
    private const string NeoForgeType = "NeoForge";

    private LauncherState _state = null!;
    private List<string> _versions = [];

    public ProfilesPage()
    {
        InitializeComponent();
        LoaderBox.ItemsSource = new[] { FabricType, NeoForgeType, ForgeType, VanillaType };
        LoaderBox.SelectedIndex = 0;
    }

    public void Initialize(LauncherState state)
    {
        _state = state;
        _state.ProfilesChanged += () =>
        {
            if (IsVisible)
                BuildList();
        };
    }

    public async void OnShown()
    {
        BuildList();
        if (_versions.Count > 0)
            return;
        try
        {
            _versions = await _state.CreateGame(_state.Settings.Profiles[0]).GetGameVersionsAsync(_state.Settings.ShowSnapshots);
            VersionBox.ItemsSource = _versions;
            VersionBox.SelectedIndex = _versions.Count > 0 ? 0 : -1;
        }
        catch (Exception ex)
        {
            CreateStatus.Text = "Could not load versions:" + ex.Message;
        }
    }

    private void BuildList()
    {
        ProfileList.Children.Clear();
        var selected = _state.SelectedProfile;
        var delay = 0;
        foreach (var profile in _state.Settings.Profiles)
        {
            var row = BuildRow(profile, profile == selected);
            ProfileList.Children.Add(row);
            MainWindow.Rise(row, delay += 40, 10, 360);
        }
    }

    // Clicking a row opens the full profile page, like the NoRisk launcher.
    private Border BuildRow(GameProfile profile, bool isSelected)
    {
        var row = new DockPanel { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        row.MouseLeftButtonUp += (_, _) =>
        {
            _state.SelectedProfile = profile;
            _state.Navigate("profile");
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        if (isSelected)
            actions.Children.Add(new TextBlock
            {
                Text = "ACTIVE",
                FontFamily = (FontFamily)FindResource("Heading"),
                FontSize = 12,
                Foreground = (Brush)FindResource("Accent"),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 110,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
        else
            // Button marks the mouse-up as handled, so this click does not also open the page.
            actions.Children.Add(Ui.Button("Select", (_, _) => _state.SelectedProfile = profile, primary: true));
        actions.Children.Add(new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)FindResource("Icons"),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSoft"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 2, 0)
        });
        row.Children.Add(actions);

        var icon = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1, 1, 1, 2),
            Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock
            {
                Text = profile.ZyroxClient ? "" : profile.HasLoader ? "" : "",
                FontFamily = (FontFamily)FindResource("Icons"),
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        icon.SetResourceReference(Border.BorderBrushProperty, isSelected ? "Accent" : "Line");
        icon.Background = (Brush)FindResource("Field");
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Title(profile.Name));
        var version = profile.GameVersion.Length == 0 ? "Latest version" : profile.GameVersion;
        text.Children.Add(Ui.Muted($"{profile.LoaderLabel} {version}  ·  {FormatPlayed(profile)}"));
        row.Children.Add(text);

        return Ui.Card(row);
    }

    private static string FormatPlayed(GameProfile profile)
    {
        if (profile.LastPlayedUtc is not { } lastPlayed)
            return "Not played yet";
        var played = TimeSpan.FromSeconds(profile.PlayedSeconds);
        var time = played.TotalHours >= 1 ? $"{(int)played.TotalHours}h {played.Minutes}m" : $"{played.Minutes}m";
        return $"{time} played, last on {lastPlayed.ToLocalTime():MMM d}";
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = $"Zyrox profile|*{ProfileShareService.Extension}" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        var answer = MessageBox.Show(Window.GetWindow(this),
            "Mods in this file can run code when the game starts. Only import profiles from people you trust.\n\nImport now?",
            "Import profile", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        CreateStatus.Text = "Importing ...";
        try
        {
            var file = dialog.FileName;
            var profile = await Task.Run(() => ProfileShareService.Import(file, _state.ProfileDir));
            _state.AddProfile(profile);
            CreateStatus.Text = $"{profile.Name} was imported.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        {
            CreateStatus.Text = "Import failed:" + ex.Message;
        }
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            CreateStatus.Text = "Give the profile a name.";
            return;
        }
        if (VersionBox.SelectedItem is not string version)
        {
            CreateStatus.Text = "Choose a Minecraft version.";
            return;
        }

        _state.AddProfile(new GameProfile
        {
            Name = name,
            GameVersion = version,
            Loader = (string?)LoaderBox.SelectedItem switch
            {
                FabricType => "fabric",
                ForgeType => "forge",
                NeoForgeType => "neoforge",
                _ => "vanilla"
            }
        });
        NameBox.Clear();
        CreateStatus.Text = "";
    }
}
