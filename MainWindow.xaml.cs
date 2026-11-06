using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using Zyrox.Pages;
using Zyrox.Services;

namespace Zyrox;

public partial class MainWindow : Window
{
    private const double HomeBlur = 7;
    private const int SoftBackgroundWidth = 72;
    private const double ParallaxStrength = 0.018;
    private const int CheerDelayMs = 1100;

    private readonly LauncherState _state = new();
    private readonly SkinService _skins = new();
    private readonly DiscordService _discord = new();
    private readonly DispatcherTimer _themeTimer = new() { Interval = TimeSpan.FromMinutes(10) };
    private readonly DispatcherTimer _gifTimer = new(DispatcherPriority.Render);
    private readonly Stopwatch _speedClock = new();

    private AuthService _auth = null!;
    private MSession? _session;
    private CancellationTokenSource? _launchCts;
    private Process? _gameProcess;
    private GameProfile? _runningProfile;
    private UpdateInfo? _availableUpdate;
    private GifAnimation? _gif;
    private DateTime _gameStartedUtc;
    private string? _logsFolder;
    private bool _isOffline;
    private bool _isLoggingIn;
    private bool _minimizedForGame;
    private bool _isUpdatingProfiles;
    private bool _isParallaxHooked;
    private string _currentPage = "home";
    private string _lastMainPage = "home";
    private int _gifFrame;
    private int _avatarRequest;
    private int _backgroundRequest;
    private long _speedBytes;

    public MainWindow()
    {
        InitializeComponent();
        CreateAuth();

        ProfilesView.Initialize(_state);
        ModsView.Initialize(_state);
        ServersView.Initialize(_state);
        SkinsView.Initialize(_state);
        WorldsView.Initialize(_state);
        GalleryView.Initialize(_state);
        SettingsView.Initialize(_state);
        SettingsView.Applied += OnSettingsApplied;

        _state.ProfilesChanged += RefreshProfiles;
        _state.BackgroundChanged += () => _ = ApplyBackgroundAsync();
        _state.JoinRequested += OnJoinRequested;
        _state.WorldRequested += OnWorldRequested;
        _state.SkinChanged += () => _ = LoadAvatarAsync();
        _state.RefreshSession = RefreshForPagesAsync;
        _state.ThemeChanged += () => ThemeService.Apply(_state.Settings.Theme);
        _state.UpdateCheckRequested += () => _ = CheckForUpdateAsync(manual: true);
        _gifTimer.Tick += OnGifTick;
        _themeTimer.Tick += (_, _) => ThemeService.Apply(_state.Settings.Theme);
        _state.NavigateRequested += ShowPage;
    }

    private bool IsGameRunning => _gameProcess != null;
    private bool IsLaunching => _launchCts != null;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateService.CleanupOldVersion();
        ThemeService.Apply(_state.Settings.Theme);
        _themeTimer.Start();
        Avatar.SetSkin(SkinTexture.Placeholder());
        _ = CheckForUpdateAsync(manual: false);
        RefreshProfiles();
        UpdateBlur();
        UpdateUi();
        _ = ApplyBackgroundAsync();
        ConfigureDiscord();
        await RestoreSessionAsync();
    }

    private void CreateAuth() => _auth = new AuthService();

    private async Task CheckForUpdateAsync(bool manual)
    {
        try
        {
            _availableUpdate = await UpdateService.CheckAsync(_state.Settings.UpdateRepository);
            UpdateButton.Visibility = _availableUpdate != null ? Visibility.Visible : Visibility.Collapsed;
            if (_availableUpdate != null)
                UpdateButton.Content = $"Install update {_availableUpdate.Version.ToString(3)}";
            if (manual)
                SettingsView.ShowUpdateStatus(_availableUpdate != null
                    ? $"Version {_availableUpdate.Version.ToString(3)} is available. You can install it from the home page."
                    : "Zyrox is up to date.");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (manual)
                SettingsView.ShowUpdateStatus("Update check failed:" + ex.Message);
        }
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate == null)
            return;
        if (IsLaunching || IsGameRunning)
        {
            SetStatus("Close Minecraft first so Zyrox can update.");
            return;
        }

        UpdateButton.IsEnabled = false;
        SetStatus("Downloading update ...");
        try
        {
            await UpdateService.InstallAndRestartAsync(_availableUpdate);
            Close();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus("Update failed:" + ex.Message);
            UpdateButton.IsEnabled = true;
        }
    }

    private void ConfigureDiscord()
    {
        try
        {
            _discord.Configure(_state.Settings.DiscordEnabled);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            SetStatus("Could not start the Discord status:" + ex.Message);
        }
    }

    private async Task<MSession?> RefreshForPagesAsync()
    {
        if (_session == null || _isOffline)
            return null;
        _session = await _auth.RefreshAsync(_state.Settings.ActiveAccountId);
        _state.Session = _session;
        return _session;
    }

    private async Task RestoreSessionAsync()
    {
        if (AuthService.HasSavedAccount)
        {
            try
            {
                SetStatus("Checking sign-in ...");
                SetSession(await _auth.RefreshAsync(_state.Settings.ActiveAccountId), offline: false);
                SetStatus("");
                return;
            }
            catch (Exception ex)
            {
                SetStatus("Automatic sign-in failed:" + AuthService.Describe(ex));
            }
        }
        UseFallbackProfile();
    }

    private void UseFallbackProfile()
    {
#if DEBUG
        var name = string.IsNullOrWhiteSpace(_state.Settings.OfflineName) ? "Player" : _state.Settings.OfflineName.Trim();
        SetSession(MSession.CreateOfflineSession(name), offline: true);
#else
        SetSession(null, offline: false);
#endif
    }

    private void SetSession(MSession? session, bool offline)
    {
        _session = session;
        _isOffline = offline;
        _state.Session = session;
        if (session?.UUID != null && !offline && _state.Settings.ActiveAccountId != session.UUID)
        {
            _state.Settings.ActiveAccountId = session.UUID;
            _state.Save();
        }
        UpdateUi();
        _ = LoadAvatarAsync();
    }

    private async Task LoadAvatarAsync()
    {
        var request = ++_avatarRequest;
        var session = _session;
        SkinTexture? skin = null;
        if (session != null)
        {
            try
            {
                var uuid = _isOffline ? await _skins.FindUuidAsync(session.Username ?? "") : session.UUID;
                if (uuid != null)
                    skin = await _skins.GetSkinAsync(uuid);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
            {
            }
        }
        if (request == _avatarRequest)
        {
            Avatar.SetSkin(skin ?? SkinTexture.Placeholder());
            if (skin != null)
                Avatar.PlayWave();
        }
    }

    private void RefreshProfiles()
    {
        _isUpdatingProfiles = true;
        ProfileBox.ItemsSource = null;
        ProfileBox.ItemsSource = _state.Settings.Profiles;
        ProfileBox.SelectedItem = _state.SelectedProfile;
        _isUpdatingProfiles = false;
        UpdateUi();
    }

    private void UpdateUi()
    {
        PlayerName.Text = _session?.Username ?? "Not signed in";
        AccountsButton.IsEnabled = !_isLoggingIn && !IsLaunching;
        AccountsButton.ToolTip = _session == null ? "Sign in" : _session.Username + " (accounts)";

        var animate = !IsGameRunning && WindowState != WindowState.Minimized && IsActive;
        Avatar.AnimationEnabled = animate;
        Avatar.IsWalking = IsLaunching;
        SetParallax(animate);
        UpdateGifPlayback();
        SkinsView.SetAnimation(animate);

        PlayButton.Content = IsLaunching ? "CANCEL" : IsGameRunning ? "RUNNING" : _isLoggingIn ? "SIGNING IN ..." : _session == null ? "SIGN IN" : "PLAY";
        PlayButton.IsEnabled = IsLaunching || (!IsGameRunning && !_isLoggingIn);
        ProfileBox.IsEnabled = !IsLaunching && !IsGameRunning;
        SettingsTab.IsEnabled = !IsLaunching && !IsGameRunning;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private async void OnAccountClick(object sender, RoutedEventArgs e)
    {
        if (_session != null && !_isOffline)
        {
            ShowAccountMenu();
            return;
        }
        await LoginAsync();
    }

    private void ShowAccountMenu()
    {
        var menu = new ContextMenu { PlacementTarget = AccountsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var account in _auth.GetAccounts())
        {
            var isActive = account.Id == _state.Settings.ActiveAccountId;
            var item = new MenuItem { Header = isActive ? account.Name + "  ✓" : account.Name };
            if (!isActive)
                item.Click += async (_, _) => await SwitchAccountAsync(account);
            menu.Items.Add(item);
        }

        var add = new MenuItem { Header = "+ Add account" };
        add.Click += async (_, _) => await LoginAsync();
        menu.Items.Add(add);

        var signOut = new MenuItem { Header = "Sign out of this account" };
        signOut.Click += async (_, _) => await LogoutAsync();
        menu.Items.Add(signOut);
        menu.IsOpen = true;
    }

    private async Task SwitchAccountAsync(AccountInfo account)
    {
        _isLoggingIn = true;
        UpdateUi();
        SetStatus($"Switching to {account.Name} ...");
        try
        {
            var session = await _auth.RefreshAsync(account.Id);
            _isLoggingIn = false;
            SetSession(session, offline: false);
            SetStatus("");
        }
        catch (Exception ex)
        {
            _isLoggingIn = false;
            SetStatus($"Switching to {account.Name} failed:" + AuthService.Describe(ex));
            UpdateUi();
        }
    }

    private async Task LoginAsync()
    {
        _isLoggingIn = true;
        UpdateUi();
        SetStatus("Sign in using the Microsoft window ...");
        try
        {
            var session = await _auth.LoginAsync();
            _isLoggingIn = false;
            SetSession(session, offline: false);
            Avatar.PlayCheer();
            SetStatus("");
        }
        catch (Exception ex)
        {
            _isLoggingIn = false;
            SetStatus("Sign-in failed:" + AuthService.Describe(ex));
            UpdateUi();
        }
    }

    private async Task LogoutAsync()
    {
        try
        {
            await _auth.RemoveAccountAsync(_state.Settings.ActiveAccountId);
            _state.Settings.ActiveAccountId = "";
            _state.Save();
            SetStatus("");
        }
        catch (Exception ex)
        {
            SetStatus("Sign-out failed:" + ex.Message);
            return;
        }

        var next = _auth.GetAccounts().FirstOrDefault();
        if (next != null)
            await SwitchAccountAsync(next);
        else
            UseFallbackProfile();
    }

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_launchCts != null)
        {
            _launchCts.Cancel();
            SetStatus("Canceling ...");
            return;
        }
        if (_session == null)
            await LoginAsync();
        else
            await LaunchAsync(null, null);
    }

    private async void OnJoinRequested(ServerEntry server)
    {
        ShowPage("home");
        if (IsLaunching || IsGameRunning)
        {
            SetStatus("Minecraft is already running.");
            return;
        }
        if (_session == null)
        {
            SetStatus("Sign in first to join a server.");
            return;
        }
        await LaunchAsync(server, null);
    }

    private async void OnWorldRequested(GameProfile profile, string world)
    {
        ShowPage("home");
        if (IsLaunching || IsGameRunning)
        {
            SetStatus("Minecraft is already running.");
            return;
        }
        if (_session == null)
        {
            SetStatus("Sign in first to play this world.");
            return;
        }
        _state.SelectedProfile = profile;
        await LaunchAsync(null, world);
    }

    private async Task LaunchAsync(ServerEntry? server, string? world)
    {
        if (_session == null)
            return;

        var profile = _state.SelectedProfile;
        using var cts = new CancellationTokenSource();
        _launchCts = cts;
        LogsButton.Visibility = Visibility.Collapsed;
        UpdateUi();
        ShowProgress(true);
        try
        {
            if (!_isOffline)
            {
                SetStatus("Checking sign-in ...");
                _session = await _auth.RefreshAsync(_state.Settings.ActiveAccountId, cts.Token);
                _state.Session = _session;
            }

            SetStatus("Checking game files ...");
            var fileProgress = new Progress<InstallerProgressChangedEventArgs>(p =>
            {
                if (_launchCts == cts && !cts.IsCancellationRequested && p.TotalTasks > 0)
                    SetStatus($"Files: {p.ProgressedTasks} of {p.TotalTasks}");
            });
            var byteProgress = new Progress<ByteProgress>(p =>
            {
                if (_launchCts == cts && p.TotalBytes > 0)
                    ReportBytes(p);
            });

            await ZyroxClientService.EnsureAsync(profile, _state.ProfileDir(profile), cts.Token);
            var game = _state.CreateGame(profile);
            var process = await game.PrepareAsync(profile, _session, _state.Settings, server, world, fileProgress, byteProgress, cts.Token);
            Avatar.IsWalking = false;
            Avatar.PlayCheer();
            await Task.Delay(CheerDelayMs, cts.Token);
            StartGame(process, profile, server);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Abgebrochen.");
        }
        catch (Exception ex)
        {
            SetStatus("Launch failed:" + AuthService.Describe(ex));
        }
        finally
        {
            _launchCts = null;
            ShowProgress(false);
            UpdateUi();
        }
    }

    private void ReportBytes(ByteProgress progress)
    {
        Progress.Value = Math.Clamp(progress.ToRatio(), 0, 1);

        var elapsed = _speedClock.Elapsed.TotalSeconds;
        if (elapsed < 0.5)
            return;
        var speed = Math.Max(0, progress.ProgressedBytes - _speedBytes) / elapsed;
        _speedBytes = progress.ProgressedBytes;
        _speedClock.Restart();
        ProgressDetail.Text = $"{progress.ProgressedBytes / 1048576} of {progress.TotalBytes / 1048576} MB  ·  {speed / 1048576:0.0} MB/s";
    }

    private void StartGame(Process process, GameProfile profile, ServerEntry? server)
    {
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Dispatcher.BeginInvoke(() => OnGameExited(process));
        process.Start();
        _gameProcess = process;
        _state.IsGameRunning = true;
        _discord.ShowPlaying(profile.Name, server != null ? "On" + server.Name : profile.HasLoader ? profile.LoaderLabel + " " + profile.GameVersion : "Minecraft " + profile.GameVersion);
        _runningProfile = profile;
        _gameStartedUtc = DateTime.UtcNow;
        _logsFolder = Path.Combine(_state.ProfileDir(profile), "logs");
        _state.Save();
        SetStatus($"{profile.Name} is running.");

        if (_state.Settings.MinimizeOnLaunch)
        {
            _minimizedForGame = true;
            WindowState = WindowState.Minimized;
        }
    }

    private void OnGameExited(Process process)
    {
        var code = process.ExitCode;
        process.Dispose();
        _gameProcess = null;
        _state.IsGameRunning = false;
        _discord.Clear();

        if (_runningProfile != null)
        {
            _state.RecordPlay(_runningProfile, _gameStartedUtc);
            _runningProfile = null;
        }

        if (_minimizedForGame)
        {
            _minimizedForGame = false;
            WindowState = WindowState.Normal;
        }
        var crashed = code != 0;
        SetStatus(crashed ? $"Minecraft exited with error code {code}." : "");
        LogsButton.Visibility = crashed ? Visibility.Visible : Visibility.Collapsed;
        UpdateUi();
    }

    private void OnLogsClick(object sender, RoutedEventArgs e)
    {
        if (_logsFolder != null)
            LauncherState.OpenInShell(_logsFolder);
    }

    private void ShowProgress(bool visible)
    {
        Progress.Value = 0;
        ProgressDetail.Text = "";
        _speedBytes = 0;
        _speedClock.Restart();
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ProgressTrack.Visibility = visibility;
        ProgressDetail.Visibility = visibility;
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUpdatingProfiles && ProfileBox.SelectedItem is GameProfile profile)
            _state.SelectedProfile = profile;
    }

    private async void OnSettingsApplied(SettingsChange change)
    {
        if (change.GameDir)
            _ = ApplyBackgroundAsync();
        UpdateBlur();
        ConfigureDiscord();
        if (change.OfflineName && _isOffline)
            await RestoreSessionAsync();
    }

    private async Task ApplyBackgroundAsync()
    {
        var request = ++_backgroundRequest;
        var custom = _state.Settings.BackgroundPath;
        var root = _state.RootDir;
        var (image, isPixelArt, soft, gif) = await Task.Run(() =>
        {
            var animation = BackgroundService.TryLoadGif(custom);
            var (loaded, pixelArt) = animation != null ? (animation.Frames[0], false) : BackgroundService.Load(custom, root);
            var scale = (double)SoftBackgroundWidth / loaded.PixelWidth;
            var small = new TransformedBitmap(loaded, new ScaleTransform(scale, scale));
            small.Freeze();
            return (loaded, pixelArt, (BitmapSource)small, animation);
        });
        if (request != _backgroundRequest)
            return;

        _gif = null;
        UpdateGifPlayback();
        var scaling = isPixelArt ? BitmapScalingMode.NearestNeighbor : gif != null ? BitmapScalingMode.Linear : BitmapScalingMode.HighQuality;
        RenderOptions.SetBitmapScalingMode(BackgroundB, scaling);
        BackgroundB.Source = image;
        BackgroundSoft.Source = soft;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450));
        fade.Completed += (_, _) =>
        {
            if (request != _backgroundRequest)
                return;
            RenderOptions.SetBitmapScalingMode(BackgroundA, scaling);
            BackgroundA.Source = image;
            BackgroundB.BeginAnimation(OpacityProperty, null);
            BackgroundB.Opacity = 0;
            _gif = gif;
            _gifFrame = 0;
            UpdateGifPlayback();
        };
        BackgroundB.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateGifPlayback()
    {
        var play = _gif != null && _currentPage == "home" && !IsGameRunning && WindowState != WindowState.Minimized && IsActive;
        if (play == _gifTimer.IsEnabled)
            return;
        if (play)
            _gifTimer.Interval = _gif!.Delays[_gifFrame];
        _gifTimer.IsEnabled = play;
    }

    private void OnGifTick(object? sender, EventArgs e)
    {
        if (_gif == null)
            return;
        _gifFrame = (_gifFrame + 1) % _gif.Frames.Count;
        BackgroundA.Source = _gif.Frames[_gifFrame];
        _gifTimer.Interval = _gif.Delays[_gifFrame];
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && sender is RadioButton { Tag: string page })
            ShowPage(page);
    }

    private void OnSkinsClick(object sender, RoutedEventArgs e) => ShowPage("skins");

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowPage(_lastMainPage);

    private void ShowPage(string page)
    {
        if (page == _currentPage)
            return;

        if (_currentPage == "settings")
            SettingsView.Apply();
        _currentPage = page;

        var isMain = page is "home" or "profiles" or "settings";
        if (isMain)
            _lastMainPage = page;
        BackButton.Visibility = isMain ? Visibility.Collapsed : Visibility.Visible;
        HomeTab.IsChecked = page == "home";
        ProfilesTab.IsChecked = page == "profiles";
        SettingsTab.IsChecked = page == "settings";

        FrameworkElement[] views = [HomePage, ProfilesView, ModsView, SkinsView, WorldsView, ServersView, GalleryView, SettingsView];
        foreach (var view in views)
            view.Visibility = Visibility.Collapsed;

        FrameworkElement current = page switch
        {
            "profiles" => ProfilesView,
            "mods" => ModsView,
            "skins" => SkinsView,
            "worlds" => WorldsView,
            "servers" => ServersView,
            "gallery" => GalleryView,
            "settings" => SettingsView,
            _ => HomePage
        };
        current.Visibility = Visibility.Visible;
        AnimateIn(current);
        UpdateBlur();
        UpdateGifPlayback();

        (current as IPage)?.OnShown();
    }

    private static void AnimateIn(FrameworkElement element)
    {
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = ease });
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    private void UpdateBlur()
    {
        var isHome = _currentPage == "home";
        var duration = TimeSpan.FromMilliseconds(300);
        var radius = _state.Settings.BlurBackground ? HomeBlur : 0;
        BackgroundBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(radius, duration));
        PageScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(isHome ? 0 : 1, duration));
        BackgroundSoft.BeginAnimation(OpacityProperty, new DoubleAnimation(isHome ? 0 : 1, duration));
    }

    private void SetParallax(bool enabled)
    {
        if (enabled == _isParallaxHooked)
            return;
        _isParallaxHooked = enabled;
        if (enabled)
            CompositionTarget.Rendering += OnParallax;
        else
            CompositionTarget.Rendering -= OnParallax;
    }

    private void OnParallax(object? sender, EventArgs e)
    {
        var mouse = Mouse.GetPosition(Root);
        var targetX = Math.Clamp(ActualWidth / 2 - mouse.X, -ActualWidth, ActualWidth) * ParallaxStrength;
        var targetY = Math.Clamp(ActualHeight / 2 - mouse.Y, -ActualHeight, ActualHeight) * ParallaxStrength;
        BackgroundShift.X += (targetX - BackgroundShift.X) * 0.06;
        BackgroundShift.Y += (targetY - BackgroundShift.Y) * 0.06;
    }

    private void OnActivationChanged(object? sender, EventArgs e) => UpdateUi();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        Root.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        UpdateUi();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _launchCts?.Cancel();
        if (_currentPage == "settings")
            SettingsView.Apply();
        _state.Save();
        _discord.Dispose();
    }
}