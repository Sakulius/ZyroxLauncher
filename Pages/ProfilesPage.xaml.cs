using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Zyrox.Services;

namespace Zyrox.Pages;

public partial class ProfilesPage : UserControl, IPage
{
    private const string LatestVersion = "Latest version";
    private const int MinRamMb = 1024;
    private const int RamStepMb = 512;
    private const string VanillaType = "Vanilla";
    private const string FabricType = "Fabric";
    private const string ForgeType = "Forge";
    private const string NeoForgeType = "NeoForge";

    private LauncherState _state = null!;
    private List<string> _versions = [];
    private string? _openDetails;

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
            BuildList();
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
        foreach (var profile in _state.Settings.Profiles)
            ProfileList.Children.Add(BuildRow(profile, profile == selected));
    }

    private Border BuildRow(GameProfile profile, bool isSelected)
    {
        var isOpen = profile.Id == _openDetails;
        var row = new DockPanel { Background = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        row.MouseLeftButtonDown += (_, _) =>
        {
            _openDetails = isOpen ? null : profile.Id;
            BuildList();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        if (isSelected)
            actions.Children.Add(new TextBlock
            {
                Text = "ACTIVE",
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("Accent"),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 110,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
        else
            actions.Children.Add(Ui.Button("Select", (_, _) => _state.SelectedProfile = profile, primary: true));
        actions.Children.Add(new TextBlock
        {
            Text = isOpen ? "\uE70E" : "\uE70D",
            FontFamily = (System.Windows.Media.FontFamily)FindResource("Icons"),
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSoft"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 2, 0)
        });
        row.Children.Add(actions);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Title(profile.Name));
        var version = profile.GameVersion.Length == 0 ? LatestVersion : profile.GameVersion;
        text.Children.Add(Ui.Muted($"{profile.LoaderLabel} {version}  ·  {FormatPlayed(profile)}"));
        row.Children.Add(text);

        var content = new StackPanel();
        content.Children.Add(row);
        if (isOpen)
        {
            content.Children.Add(Ui.ProfileLinks(_state, profile));
            content.Children.Add(BuildDetails(profile));
        }
        return Ui.Card(content);
    }

    private static string FormatPlayed(GameProfile profile)
    {
        if (profile.LastPlayedUtc is not { } lastPlayed)
            return "Not played yet";
        var played = TimeSpan.FromSeconds(profile.PlayedSeconds);
        var time = played.TotalHours >= 1 ? $"{(int)played.TotalHours}h {played.Minutes}m" : $"{played.Minutes}m";
        return $"{time} played, last on {lastPlayed.ToLocalTime():MMM d}";
    }

    private StackPanel BuildDetails(GameProfile profile)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

        var picker = BuildVersionPicker(profile);
        picker.Width = double.NaN;
        picker.Height = 40;
        picker.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(Label("Minecraft version"));
        panel.Children.Add(picker);

        var totalMb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
        var ram = new Slider
        {
            Minimum = MinRamMb,
            Maximum = Math.Max(MinRamMb, totalMb / RamStepMb * RamStepMb),
            TickFrequency = RamStepMb,
            SmallChange = RamStepMb,
            LargeChange = RamStepMb,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 0, 0, 10)
        };
        ram.Value = Math.Clamp(profile.RamMb, ram.Minimum, ram.Maximum);
        var ramLabel = Label(FormatRam(ram.Value));
        ram.ValueChanged += (_, e) => ramLabel.Text = FormatRam(e.NewValue);
        panel.Children.Add(ramLabel);
        panel.Children.Add(ram);

        var fullscreen = new CheckBox { Content = "Start in fullscreen", IsChecked = profile.Fullscreen == true, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(fullscreen);

        var size = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        size.ColumnDefinitions.Add(new ColumnDefinition());
        size.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        size.ColumnDefinitions.Add(new ColumnDefinition());
        var width = new TextBox { Text = profile.ScreenWidth > 0 ? profile.ScreenWidth.ToString() : "" };
        var height = new TextBox { Text = profile.ScreenHeight > 0 ? profile.ScreenHeight.ToString() : "" };
        Grid.SetColumn(height, 2);
        size.Children.Add(width);
        size.Children.Add(height);
        panel.Children.Add(Label("Window size: width and height (empty = default)"));
        panel.Children.Add(size);

        var args = new TextBox { Text = profile.JvmArgs, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(Label("Additional Java arguments"));
        panel.Children.Add(args);

        CheckBox? client = null;
        if (ZyroxClientService.Supports(profile))
        {
            client = new CheckBox
            {
                Content = "Zyrox Client",
                IsChecked = profile.ZyroxClient,
                Margin = new Thickness(0, 0, 0, 12),
                ToolTip = "Installs and updates the in-game client when you press Play. Press Right Shift in a world to open it."
            };
            panel.Children.Add(client);
        }
        else
        {
            var unavailable = Label($"Zyrox Client is available for Fabric on Minecraft {string.Join(" and ", ZyroxClientService.SupportedVersions)}.");
            unavailable.Margin = new Thickness(0, 0, 0, 12);
            panel.Children.Add(unavailable);
        }

        var status = Ui.Muted("");
        status.Margin = new Thickness(0, 8, 0, 0);

        var actions = new WrapPanel();
        var save = Ui.Button("Save", (_, _) => SaveDetails(profile, ram, fullscreen, width, height, args, client, status));
        save.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(save);
        var folder = Ui.Button("Folder", (_, _) =>
        {
            var path = _state.ProfileDir(profile);
            Directory.CreateDirectory(path);
            LauncherState.OpenInShell(path);
        });
        folder.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(folder);
        var export = Ui.Button("Export", (_, _) => ExportProfile(profile, status));
        export.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(export);
        if (!profile.IsDefault)
        {
            var remove = Ui.Button("Remove", (_, _) => _state.RemoveProfile(profile));
            remove.Margin = new Thickness(0);
            remove.ToolTip = "Removes the profile from the list. The folder with your worlds is kept.";
            actions.Children.Add(remove);
        }
        panel.Children.Add(actions);
        panel.Children.Add(status);
        return panel;
    }

    private static TextBlock Label(string text)
    {
        var label = Ui.Muted(text);
        label.Margin = new Thickness(0, 0, 0, 4);
        return label;
    }

    private static string FormatRam(double megabytes) => $"Memory: {megabytes / 1024:0.#} GB";

    private void SaveDetails(GameProfile profile, Slider ram, CheckBox fullscreen, TextBox width, TextBox height, TextBox args, CheckBox? client, TextBlock status)
    {
        if (!TryReadNumber(width.Text, 320, out var screenWidth) || !TryReadNumber(height.Text, 240, out var screenHeight))
        {
            status.Text = "Please enter a valid window size, at least 320 × 240.";
            return;
        }
        if ((screenWidth == 0) != (screenHeight == 0))
        {
            status.Text = "Enter both width and height for the window size.";
            return;
        }
        var useClient = client == null ? profile.ZyroxClient : client.IsChecked == true;

        profile.RamMb = (int)ram.Value;
        profile.Fullscreen = fullscreen.IsChecked == true;
        profile.ScreenWidth = screenWidth;
        profile.ScreenHeight = screenHeight;
        profile.JvmArgs = args.Text.Trim();
        if (profile.ZyroxClient && !useClient)
            ZyroxClientService.Remove(_state.ProfileDir(profile));
        profile.ZyroxClient = useClient;
        _state.Save();
        status.Text = "Saved.";
    }

    private static bool TryReadNumber(string text, int minimum, out int value)
    {
        value = 0;
        text = text.Trim();
        return text.Length == 0 || (int.TryParse(text, out value) && value >= minimum);
    }

    private async void ExportProfile(GameProfile profile, TextBlock status)
    {
        var dialog = new SaveFileDialog
        {
            FileName = string.Concat(profile.Name.Split(Path.GetInvalidFileNameChars())) + ProfileShareService.Extension,
            Filter = $"Zyrox profile|*{ProfileShareService.Extension}"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        status.Text = "Exporting ...";
        try
        {
            var folder = _state.ProfileDir(profile);
            await Task.Run(() => ProfileShareService.Export(profile, folder, dialog.FileName));
            status.Text = "Exported. Worlds are not included.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Text = "Export failed:" + ex.Message;
        }
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

    private ComboBox BuildVersionPicker(GameProfile profile)
    {
        var choices = new List<string>();
        if (!profile.HasLoader)
            choices.Add(LatestVersion);
        choices.AddRange(_versions);
        if (profile.GameVersion.Length > 0 && !choices.Contains(profile.GameVersion))
            choices.Insert(0, profile.GameVersion);

        var picker = new ComboBox
        {
            Width = 150,
            Height = 36,
            Tag = "Bottom",
            ItemsSource = choices,
            SelectedItem = profile.GameVersion.Length == 0 ? LatestVersion : profile.GameVersion,
            VerticalAlignment = VerticalAlignment.Center
        };
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not string choice)
                return;
            profile.GameVersion = choice == LatestVersion ? "" : choice;
            profile.LoaderVersion = "";
            _state.NotifyProfilesChanged();
        };
        return picker;
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
