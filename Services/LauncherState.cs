using System.Diagnostics;
using System.IO;
using CmlLib.Core;
using CmlLib.Core.Auth;

namespace Zyrox.Services;

public class LauncherState
{
    private readonly SettingsService _settingsService = new();

    public LauncherState()
    {
        var isFirstStart = !File.Exists(AppPaths.Settings);
        Settings = _settingsService.Load();
        if (!Settings.Profiles.Any(p => p.IsDefault))
        {
            Settings.Profiles.Insert(0, new GameProfile
            {
                Name = "Vanilla",
                IsDefault = true,
                GameVersion = Settings.LastVersion
            });
        }
        if (isFirstStart)
        {
            var client = new GameProfile { Name = "Zyrox Client", GameVersion = ZyroxClientService.SupportedVersion, Loader = "fabric", ZyroxClient = true };
            Settings.Profiles.Add(client);
            Settings.SelectedProfileId = client.Id;
        }
        if (Settings.UpdateRepository.Length == 0)
            Settings.UpdateRepository = UpdateService.DefaultRepository;
        foreach (var profile in Settings.Profiles.Where(p => p.Loader.Length == 0))
        {
            profile.Loader = profile.IsFabric ? "fabric" : "vanilla";
            profile.IsFabric = false;
        }
        foreach (var profile in Settings.Profiles)
            ApplyDefaults(profile);

        var shared = Settings.Servers.Where(s => s.ProfileId.Length == 0).ToList();
        Settings.Servers.RemoveAll(s => s.ProfileId.Length == 0);
        foreach (var profile in Settings.Profiles)
            Settings.Servers.AddRange(shared.Select(s => new ServerEntry { Name = s.Name, Address = s.Address, ProfileId = profile.Id }));
    }

    private void ApplyDefaults(GameProfile profile)
    {
        if (profile.RamMb <= 0)
            profile.RamMb = Settings.RamMb;
        profile.Fullscreen ??= Settings.Fullscreen;
    }

    public List<ServerEntry> ServersOf(GameProfile profile) =>
        Settings.Servers.Where(s => s.ProfileId == profile.Id).ToList();

    public AppSettings Settings { get; }

    public event Action? ProfilesChanged;
    public event Action? BackgroundChanged;
    public event Action<ServerEntry>? JoinRequested;
    public event Action<string>? NavigateRequested;
    public event Action<GameProfile, string>? WorldRequested;
    public event Action? SkinChanged;
    public event Action? ThemeChanged;
    public event Action? UpdateCheckRequested;

    public MSession? Session { get; set; }
    public bool IsGameRunning { get; set; }
    public Func<Task<MSession?>>? RefreshSession { get; set; }

    public string RootDir => string.IsNullOrWhiteSpace(Settings.GameDir) ? MinecraftPath.GetOSDefaultPath() : Settings.GameDir;

    public GameProfile SelectedProfile
    {
        get => Settings.Profiles.FirstOrDefault(p => p.Id == Settings.SelectedProfileId) ?? Settings.Profiles[0];
        set
        {
            Settings.SelectedProfileId = value.Id;
            Save();
            ProfilesChanged?.Invoke();
        }
    }

    public string ProfileDir(GameProfile profile) =>
        profile.IsDefault ? RootDir : Path.Combine(RootDir, "zyrox-profiles", profile.Id);

    public GameService CreateGame(GameProfile profile) => new(RootDir, ProfileDir(profile));

    public void AddProfile(GameProfile profile)
    {
        ApplyDefaults(profile);
        Settings.Profiles.Add(profile);
        Directory.CreateDirectory(ProfileDir(profile));
        SelectedProfile = profile;
    }

    public void RemoveProfile(GameProfile profile)
    {
        if (profile.IsDefault)
            return;
        Settings.Profiles.Remove(profile);
        Settings.Servers.RemoveAll(s => s.ProfileId == profile.Id);
        Save();
        ProfilesChanged?.Invoke();
    }

    public void NotifyProfilesChanged()
    {
        Save();
        ProfilesChanged?.Invoke();
    }

    public void NotifyBackgroundChanged() => BackgroundChanged?.Invoke();
    public void RequestJoin(ServerEntry server) => JoinRequested?.Invoke(server);
    public void Navigate(string page) => NavigateRequested?.Invoke(page);
    public void RequestWorld(GameProfile profile, string folder) => WorldRequested?.Invoke(profile, folder);
    public void NotifySkinChanged() => SkinChanged?.Invoke();
    public void NotifyThemeChanged() => ThemeChanged?.Invoke();
    public void RequestUpdateCheck() => UpdateCheckRequested?.Invoke();

    public void RecordPlay(GameProfile profile, DateTime startedUtc)
    {
        profile.LastPlayedUtc = DateTime.UtcNow;
        profile.PlayedSeconds += (long)(DateTime.UtcNow - startedUtc).TotalSeconds;
        Save();
    }

    public void Save()
    {
        try
        {
            _settingsService.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine("Settings not saved:" + ex.Message);
        }
    }

    public static void OpenInShell(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
