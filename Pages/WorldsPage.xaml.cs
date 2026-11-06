using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Zyrox.Services;

namespace Zyrox.Pages;

public partial class WorldsPage : UserControl, IPage
{
    private LauncherState _state = null!;
    private CancellationTokenSource? _load;

    public WorldsPage()
    {
        InitializeComponent();
    }

    public void Initialize(LauncherState state) => _state = state;

    public async void OnShown()
    {
        _load?.Cancel();
        var cts = new CancellationTokenSource();
        _load = cts;
        WorldList.Children.Clear();

        var profile = _state.SelectedProfile;
        Hint.Text = $"Worlds of {profile.Name}. Backups are saved in the profile folder under zyrox-backups.";
        var folder = _state.ProfileDir(profile);
        var worlds = await Task.Run(() => WorldService.GetWorlds(folder));
        if (cts.IsCancellationRequested)
            return;

        foreach (var world in worlds)
            WorldList.Children.Add(BuildRow(world, profile, folder));
        if (worlds.Count == 0)
            WorldList.Children.Add(Ui.Card(Ui.Muted("No worlds yet. Create one in game and it will show up here.")));
    }

    private Border BuildRow(WorldInfo world, GameProfile profile, string profileDir)
    {
        var row = new DockPanel();

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        var status = Ui.Muted("");

        Button backup = null!;
        backup = Ui.Button("Back up", async (_, _) =>
        {
            if (_state.IsGameRunning)
            {
                status.Text = "Close Minecraft first, or the backup will be incomplete.";
                return;
            }
            backup.IsEnabled = false;
            status.Text = "Backing up ...";
            try
            {
                var file = await Task.Run(() => WorldService.Backup(world, profileDir));
                status.Text = "Saved as" + Path.GetFileName(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                status.Text = "Backup failed:" + ex.Message;
            }
            backup.IsEnabled = true;
        });
        actions.Children.Add(backup);
        actions.Children.Add(Ui.Button("Folder", (_, _) => LauncherState.OpenInShell(world.Path)));
        actions.Children.Add(Ui.Button("Play", (_, _) => _state.RequestWorld(profile, world.Folder), primary: true));
        row.Children.Add(actions);

        var icon = new Image { Stretch = System.Windows.Media.Stretch.UniformToFill };
        if (world.IconPath != null)
            icon.Source = LoadIcon(world.IconPath);
        var frame = Ui.IconFrame(icon);
        DockPanel.SetDock(frame, Dock.Left);
        row.Children.Add(frame);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Title(world.Name));
        var version = world.GameVersion != null ? $"Minecraft {world.GameVersion}  ·  " : "";
        text.Children.Add(Ui.Muted($"{version}Last played {world.LastPlayedUtc.ToLocalTime():MMM d, yyyy HH:mm}"));
        text.Children.Add(status);
        row.Children.Add(text);
        return Ui.Card(row);
    }

    private static BitmapSource? LoadIcon(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
