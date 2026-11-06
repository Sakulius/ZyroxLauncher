using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Zyrox.Services;

namespace Zyrox.Pages;

public partial class GalleryPage : UserControl, IPage
{
    private const int MaxScreenshots = 60;
    private const int ThumbnailWidth = 320;

    private LauncherState _state = null!;
    private CancellationTokenSource? _load;

    public GalleryPage()
    {
        InitializeComponent();
    }

    public void Initialize(LauncherState state) => _state = state;

    public async void OnShown()
    {
        _load?.Cancel();
        var cts = new CancellationTokenSource();
        _load = cts;
        Grid.Children.Clear();

        var profile = _state.SelectedProfile;
        var folders = new List<string> { Path.Combine(_state.ProfileDir(profile), "screenshots") };
        var files = await Task.Run(() => FindScreenshots(folders));
        if (cts.IsCancellationRequested)
            return;

        Hint.Text = files.Count == 0
            ? "No screenshots yet. Press F2 in game and they will show up here."
            : $"Screenshots of {profile.Name}. Pick one as your background.";

        foreach (var file in files)
        {
            var thumbnail = await Task.Run(() => LoadThumbnail(file));
            if (cts.IsCancellationRequested)
                return;
            if (thumbnail != null)
                Grid.Children.Add(BuildTile(file, thumbnail));
        }
    }

    private static List<string> FindScreenshots(List<string> folders) => folders
        .Where(Directory.Exists)
        .SelectMany(folder => new DirectoryInfo(folder).EnumerateFiles("*.png"))
        .OrderByDescending(f => f.LastWriteTimeUtc)
        .Take(MaxScreenshots)
        .Select(f => f.FullName)
        .ToList();

    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = ThumbnailWidth;
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

    private Border BuildTile(string file, BitmapSource thumbnail)
    {
        var content = new StackPanel { Width = 256 };
        content.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Height = 144,
            Child = new Image { Source = thumbnail, Stretch = System.Windows.Media.Stretch.UniformToFill }
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-8, 10, 0, 0) };
        actions.Children.Add(Ui.Button("Set as background", (_, _) =>
        {
            _state.Settings.BackgroundPath = file;
            _state.Save();
            _state.NotifyBackgroundChanged();
        }));
        actions.Children.Add(Ui.Button("Open", (_, _) => LauncherState.OpenInShell(file)));
        content.Children.Add(actions);

        var card = Ui.Card(content, new Thickness(0, 0, 14, 14));
        card.Padding = new Thickness(12);
        return card;
    }
}
