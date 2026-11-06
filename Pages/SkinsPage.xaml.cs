using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Zyrox.Services;

namespace Zyrox.Pages;

public partial class SkinsPage : UserControl, IPage
{
    private const int HeadPixels = 72;

    private readonly SkinService _skins = new();

    private LauncherState _state = null!;
    private SavedSkin? _selected;
    private bool _isUpdatingModel;

    public SkinsPage()
    {
        InitializeComponent();
    }

    public void Initialize(LauncherState state) => _state = state;

    public void SetAnimation(bool enabled) => Preview.AnimationEnabled = enabled;

    public void OnShown()
    {
        BuildLibrary();
        if (_selected != null && _state.Settings.SavedSkins.Contains(_selected))
            Select(_selected);
        else if (_state.Settings.SavedSkins.Count > 0)
            Select(_state.Settings.SavedSkins[0]);
        else
            ShowEmpty();
    }

    private void ShowEmpty()
    {
        _selected = null;
        Preview.SetSkin(SkinTexture.Placeholder());
        PreviewName.Text = "No skin selected";
        ApplyButton.IsEnabled = false;
    }

    private void BuildLibrary()
    {
        Library.Children.Clear();
        if (_state.Settings.SavedSkins.Count == 0)
        {
            Library.Children.Add(Ui.Card(Ui.Muted("Nothing here yet. Add a skin file or save your current skin.")));
            return;
        }
        foreach (var skin in _state.Settings.SavedSkins)
            Library.Children.Add(BuildTile(skin));
    }

    private Border BuildTile(SavedSkin skin)
    {
        var content = new StackPanel { Width = 120 };
        var head = new Image { Width = HeadPixels, Height = HeadPixels, Source = LoadHead(skin) };
        RenderOptions.SetBitmapScalingMode(head, BitmapScalingMode.NearestNeighbor);
        content.Children.Add(head);
        content.Children.Add(new TextBlock
        {
            Text = skin.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 8)
        });

        var delete = Ui.Button("Delete", (_, _) => Delete(skin));
        delete.Margin = new Thickness(0);
        delete.Padding = new Thickness(8, 4, 8, 4);
        delete.HorizontalAlignment = HorizontalAlignment.Center;
        content.Children.Add(delete);

        var card = Ui.Card(content, new Thickness(0, 0, 12, 12));
        card.Padding = new Thickness(12);
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, _) => Select(skin);
        return card;
    }

    private static BitmapSource? LoadHead(SavedSkin skin)
    {
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(SkinLibrary.PathOf(skin)));
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var head = new CroppedBitmap(frame, new Int32Rect(8, 8, 8, 8));
            head.Freeze();
            return head;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    private void Select(SavedSkin skin)
    {
        try
        {
            Preview.SetSkin(SkinTexture.FromPng(File.ReadAllBytes(SkinLibrary.PathOf(skin)), skin.Slim));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException)
        {
            ApplyStatus.Text = "This skin file is damaged.";
            return;
        }

        _selected = skin;
        PreviewName.Text = skin.Name;
        ApplyButton.IsEnabled = true;
        ApplyStatus.Text = "";
        _isUpdatingModel = true;
        (skin.Slim ? SlimBox : ClassicBox).IsChecked = true;
        _isUpdatingModel = false;
    }

    private void OnModelChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingModel || _selected == null)
            return;
        _selected.Slim = SlimBox.IsChecked == true;
        _state.Save();
        Select(_selected);
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Minecraft skin (PNG)|*.png" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            var data = File.ReadAllBytes(dialog.FileName);
            var skin = SkinLibrary.Add(data, Path.GetFileNameWithoutExtension(dialog.FileName), slim: false);
            _state.Settings.SavedSkins.Add(skin);
            _state.Save();
            BuildLibrary();
            Select(skin);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException or UnauthorizedAccessException)
        {
            ApplyStatus.Text = "That is not a valid skin. It must be a PNG with 64×64 or 64×32 pixels.";
        }
    }

    private void OnSaveCurrentClick(object sender, RoutedEventArgs e)
    {
        var session = _state.Session;
        var (png, slim) = session?.UUID != null ? SkinLibrary.ReadCached(session.UUID) : (null, false);
        if (png == null)
        {
            ApplyStatus.Text = "Your current skin is not loaded yet. Sign in first.";
            return;
        }

        var skin = SkinLibrary.Add(png, session!.Username ?? "My skin", slim);
        _state.Settings.SavedSkins.Add(skin);
        _state.Save();
        BuildLibrary();
        Select(skin);
        ApplyStatus.Text = "Current skin saved.";
    }

    private void Delete(SavedSkin skin)
    {
        _state.Settings.SavedSkins.Remove(skin);
        _state.Save();
        SkinLibrary.Delete(skin);
        if (_selected == skin)
            _selected = null;
        OnShown();
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;
        ApplyButton.IsEnabled = false;
        ApplyStatus.Text = "Uploading ...";
        try
        {
            var session = _state.RefreshSession != null ? await _state.RefreshSession() : null;
            if (session?.AccessToken == null)
            {
                ApplyStatus.Text = "Sign in with your Microsoft account first.";
                return;
            }
            await _skins.UploadAsync(session.AccessToken, File.ReadAllBytes(SkinLibrary.PathOf(_selected)), _selected.Slim);
            ApplyStatus.Text = "Done. Your new skin is active.";
            _state.NotifySkinChanged();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            ApplyStatus.Text = "Upload failed:" + ex.Message;
        }
        finally
        {
            ApplyButton.IsEnabled = _selected != null;
        }
    }
}
