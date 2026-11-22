using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Zyrox.Services;

namespace Zyrox.Pages;

// One profile opened full page: header with stats and Play, sections listed on the right.
public partial class ProfileView : UserControl, IPage
{
    private const string LatestVersion = "Latest version";
    private const int MinRamMb = 1024;
    private const int RamStepMb = 512;

    private LauncherState _state = null!;
    private List<string> _versions = [];
    private string _tab = "mods";
    private int _diskRequest;

    public ProfileView()
    {
        InitializeComponent();
    }

    public void Initialize(LauncherState state)
    {
        _state = state;
        ModsView.Initialize(state);
        WorldsView.Initialize(state);
        ServersView.Initialize(state);
        GalleryView.Initialize(state);

        // The sections live inside this page, so their own titles and mode tabs are redundant here.
        ((UIElement)ModsView.Header.Children[0]).Visibility = Visibility.Collapsed;
        ModsView.PageTitleText.Visibility = Visibility.Collapsed;
        foreach (var header in new[] { WorldsView.Header, ServersView.Header, GalleryView.Header })
            header.Children[0].Visibility = Visibility.Collapsed;

        _state.ProfilesChanged += () =>
        {
            if (IsVisible)
                BuildHeader();
        };
    }

    // Called before the page is shown when something links straight to a section.
    public void SelectTab(string tab) => _tab = tab;

    public void OnShown()
    {
        BuildHeader();
        var icon = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
        IconPop.BeginAnimation(ScaleTransform.ScaleXProperty, icon);
        IconPop.BeginAnimation(ScaleTransform.ScaleYProperty, icon);
        TabFor(_tab).IsChecked = true;
        ShowTab(_tab);
    }

    private GameProfile Profile => _state.SelectedProfile;

    private void BuildHeader()
    {
        var profile = Profile;
        NameText.Text = profile.Name;
        IconGlyph.Text = profile.ZyroxClient ? "" : profile.HasLoader ? "" : "";

        Chips.Children.Clear();
        Chips.Children.Add(Chip("", profile.GameVersion.Length == 0 ? "Latest" : profile.GameVersion));
        Chips.Children.Add(Chip("", profile.HasLoader ? profile.LoaderLabel : "Vanilla"));
        if (profile.ZyroxClient)
            Chips.Children.Add(Chip("", "Zyrox Client", accent: true));

        Stats.Children.Clear();
        Stats.Children.Add(Stat("", "LAST PLAYED", profile.LastPlayedUtc is { } last ? last.ToLocalTime().ToString("MMM d, yyyy") : "Never", out _));
        var played = TimeSpan.FromSeconds(profile.PlayedSeconds);
        Stats.Children.Add(Stat("", "PLAYTIME", profile.PlayedSeconds == 0 ? "—" : played.TotalHours >= 1 ? $"{(int)played.TotalHours}h {played.Minutes}m" : $"{played.Minutes}m", out _));
        Stats.Children.Add(Stat("", "DISK", "…", out var diskValue));
        _ = MeasureDiskAsync(diskValue);

        PlayButton.IsEnabled = !_state.IsGameRunning;
        ClientTab.Visibility = ZyroxClientService.Supports(profile) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task MeasureDiskAsync(TextBlock target)
    {
        var request = ++_diskRequest;
        var folder = _state.ProfileDir(Profile);
        var bytes = await Task.Run(() =>
        {
            try
            {
                return Directory.Exists(folder)
                    ? new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length)
                    : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return -1;
            }
        });
        if (request == _diskRequest)
            target.Text = bytes < 0 ? "—" : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";
    }

    private static Border Chip(string glyph, string text, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = text, FontFamily = (FontFamily)Application.Current.FindResource("Heading"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        var chip = new Border
        {
            Background = (Brush)Application.Current.FindResource("Field"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(9, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Child = content
        };
        if (accent)
        {
            chip.SetResourceReference(Border.BorderBrushProperty, "Accent");
            chip.BorderThickness = new Thickness(1);
            content.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        }
        else
            content.SetValue(TextBlock.ForegroundProperty, Application.Current.FindResource("TextSoft"));
        return chip;
    }

    private static Border Stat(string glyph, string label, string value, out TextBlock valueText)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = label, FontFamily = (FontFamily)Application.Current.FindResource("Heading"), FontSize = 10, Foreground = (Brush)Application.Current.FindResource("TextSoft") });
        valueText = new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 2, 0, 0) };
        text.Children.Add(valueText);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Foreground = (Brush)Application.Current.FindResource("TextSoft") });
        row.Children.Add(text);
        return new Border
        {
            Background = (Brush)Application.Current.FindResource("Field"),
            BorderBrush = (Brush)Application.Current.FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 18, 8),
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 120,
            Child = row
        };
    }

    private RadioButton TabFor(string tab) => tab switch
    {
        "packs" => PacksTab,
        "shaders" => ShadersTab,
        "modpacks" => ModpacksTab,
        "client" => ClientTab,
        "worlds" => WorldsTab,
        "servers" => ServersTab,
        "gallery" => GalleryTab,
        "settings" => SettingsTab,
        _ => ModsTab
    };

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (_state == null || !IsVisible)
            return;
        ShowTab(sender == PacksTab ? "packs" : sender == ShadersTab ? "shaders" : sender == ModpacksTab ? "modpacks"
            : sender == ClientTab ? "client" : sender == WorldsTab ? "worlds" : sender == ServersTab ? "servers"
            : sender == GalleryTab ? "gallery" : sender == SettingsTab ? "settings" : "mods");
    }

    private void ShowTab(string tab)
    {
        if (tab == "client" && ClientTab.Visibility != Visibility.Visible)
            tab = "mods";
        _tab = tab;
        foreach (UIElement child in Body.Children)
            child.Visibility = Visibility.Collapsed;

        FrameworkElement view;
        switch (tab)
        {
            case "worlds": view = WorldsView; break;
            case "servers": view = ServersView; break;
            case "gallery": view = GalleryView; break;
            case "client": BuildClientPanel(); view = ClientView; break;
            case "settings": SettingsHost.Content = BuildDetails(Profile); view = SettingsView; break;
            default:
                ModsView.ModsMode.IsChecked = tab == "mods";
                ModsView.ResourcePacksMode.IsChecked = tab == "packs";
                ModsView.ShadersMode.IsChecked = tab == "shaders";
                ModsView.PacksMode.IsChecked = tab == "modpacks";
                view = ModsView;
                break;
        }
        view.Visibility = Visibility.Visible;
        MainWindow.Rise(view, 0, 14, 320);
        (view as IPage)?.OnShown();
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => _state.RequestPlay(Profile);

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsTab.IsChecked = true;
        ShowTab("settings");
    }

    private void OnFolderClick(object sender, RoutedEventArgs e) => OpenFolder(_state.ProfileDir(Profile));

    private void OnLogsClick(object sender, RoutedEventArgs e)
    {
        LogsTab.IsChecked = false;
        OpenFolder(Path.Combine(_state.ProfileDir(Profile), "logs"));
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        LauncherState.OpenInShell(path);
    }

    private void BuildClientPanel()
    {
        var profile = Profile;
        ClientPanel.Children.Clear();
        var card = new StackPanel();
        card.Children.Add(new TextBlock { Text = "Zyrox Client", Style = (Style)FindResource("PageTitle"), FontSize = 24 });
        var hint = Ui.Muted("Installs and updates the in-game client every time you press Play. Press Right Shift in a world to open the menu.");
        hint.Margin = new Thickness(0, 6, 0, 16);
        card.Children.Add(hint);
        var toggle = new CheckBox { Content = "Enabled for this profile", IsChecked = profile.ZyroxClient, Style = (Style)FindResource("ToggleSwitch") };
        var status = Ui.Muted("");
        status.Margin = new Thickness(0, 10, 0, 0);
        toggle.Click += (_, _) =>
        {
            var on = toggle.IsChecked == true;
            if (profile.ZyroxClient && !on)
                ZyroxClientService.Remove(_state.ProfileDir(profile));
            profile.ZyroxClient = on;
            _state.Save();
            BuildHeader();
            status.Text = on ? "The client is installed the next time you press Play." : "The client was removed from this profile.";
        };
        card.Children.Add(toggle);
        card.Children.Add(status);
        ClientPanel.Children.Add(Ui.Card(card));
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var profile = Profile;
        var dialog = new SaveFileDialog
        {
            FileName = string.Concat(profile.Name.Split(Path.GetInvalidFileNameChars())) + ProfileShareService.Extension,
            Filter = $"Zyrox profile|*{ProfileShareService.Extension}"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        ExportButton.IsEnabled = false;
        try
        {
            var folder = _state.ProfileDir(profile);
            await Task.Run(() => ProfileShareService.Export(profile, folder, dialog.FileName));
            ExportButton.ToolTip = "Exported. Worlds are not included.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), "Export failed: " + ex.Message, "Zyrox");
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }

    private StackPanel BuildDetails(GameProfile profile)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Settings", Style = (Style)FindResource("PageTitle"), FontSize = 24, Margin = new Thickness(0, 0, 0, 16) });

        var picker = BuildVersionPicker(profile);
        panel.Children.Add(Label("Minecraft version"));
        panel.Children.Add(picker);
        if (_versions.Count == 0)
            _ = LoadVersionsAsync(profile, picker);

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

        var args = new TextBox { Text = profile.JvmArgs, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(Label("Additional Java arguments"));
        panel.Children.Add(args);

        var status = Ui.Muted("");
        status.Margin = new Thickness(0, 8, 0, 0);

        var actions = new WrapPanel();
        var save = Ui.Button("Save", (_, _) => SaveDetails(profile, ram, fullscreen, width, height, args, status), primary: true);
        save.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(save);
        if (!profile.IsDefault)
        {
            var remove = Ui.Button("Remove profile", (_, _) =>
            {
                _state.RemoveProfile(profile);
                _state.Navigate("profiles");
            });
            remove.Margin = new Thickness(0);
            remove.ToolTip = "Removes the profile from the list. The folder with your worlds is kept.";
            actions.Children.Add(remove);
        }
        panel.Children.Add(actions);
        panel.Children.Add(status);
        return panel;
    }

    private async Task LoadVersionsAsync(GameProfile profile, ComboBox picker)
    {
        try
        {
            _versions = await _state.CreateGame(profile).GetGameVersionsAsync(_state.Settings.ShowSnapshots);
            picker.ItemsSource = VersionChoices(profile);
            picker.SelectedItem = profile.GameVersion.Length == 0 ? LatestVersion : profile.GameVersion;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            picker.ToolTip = "Could not load versions: " + ex.Message;
        }
    }

    private static TextBlock Label(string text)
    {
        var label = Ui.Muted(text);
        label.Margin = new Thickness(0, 0, 0, 4);
        return label;
    }

    private static string FormatRam(double megabytes) => $"Memory: {megabytes / 1024:0.#} GB";

    private void SaveDetails(GameProfile profile, Slider ram, CheckBox fullscreen, TextBox width, TextBox height, TextBox args, TextBlock status)
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

        profile.RamMb = (int)ram.Value;
        profile.Fullscreen = fullscreen.IsChecked == true;
        profile.ScreenWidth = screenWidth;
        profile.ScreenHeight = screenHeight;
        profile.JvmArgs = args.Text.Trim();
        _state.Save();
        status.Text = "Saved.";
    }

    private static bool TryReadNumber(string text, int minimum, out int value)
    {
        value = 0;
        text = text.Trim();
        return text.Length == 0 || (int.TryParse(text, out value) && value >= minimum);
    }

    private List<string> VersionChoices(GameProfile profile)
    {
        var choices = new List<string>();
        if (!profile.HasLoader)
            choices.Add(LatestVersion);
        choices.AddRange(_versions);
        if (profile.GameVersion.Length > 0 && !choices.Contains(profile.GameVersion))
            choices.Insert(0, profile.GameVersion);
        return choices;
    }

    private ComboBox BuildVersionPicker(GameProfile profile)
    {
        var picker = new ComboBox
        {
            Height = 40,
            Tag = "Bottom",
            Margin = new Thickness(0, 0, 0, 12),
            ItemsSource = VersionChoices(profile),
            SelectedItem = profile.GameVersion.Length == 0 ? LatestVersion : profile.GameVersion
        };
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not string choice || choice == (profile.GameVersion.Length == 0 ? LatestVersion : profile.GameVersion))
                return;
            profile.GameVersion = choice == LatestVersion ? "" : choice;
            profile.LoaderVersion = "";
            _state.NotifyProfilesChanged();
        };
        return picker;
    }
}
