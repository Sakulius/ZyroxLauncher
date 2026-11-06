using System.IO;
using System.Text.Json;

namespace Zyrox.Services;

public class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public string Loader { get; set; } = "";

    public bool IsFabric { get; set; }
    public string LoaderVersion { get; set; } = "";
    public bool IsDefault { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public long PlayedSeconds { get; set; }
    public int RamMb { get; set; }
    public string JvmArgs { get; set; } = "";
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public bool ZyroxClient { get; set; }
    public bool? Fullscreen { get; set; }

    public override string ToString()
    {
        var version = GameVersion.Length == 0 ? "Latest version" : GameVersion;
        return $"{Name}  ·  {(HasLoader ? LoaderLabel + " " : "")}{version}";
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasLoader => Loader is "fabric" or "forge" or "neoforge";

    [System.Text.Json.Serialization.JsonIgnore]
    public string LoaderLabel => Loader switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        _ => "Vanilla"
    };
}

public class SavedSkin
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Slim { get; set; }
}

public class ServerEntry
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string ProfileId { get; set; } = "";
}

public class AppSettings
{
    public int RamMb { get; set; } = 4096;
    public bool Fullscreen { get; set; }
    public string GameDir { get; set; } = "";
    public string BackgroundPath { get; set; } = "";
    public bool ShowSnapshots { get; set; }
    public string LastVersion { get; set; } = "";
    public string OfflineName { get; set; } = "Player";
    public bool BlurBackground { get; set; } = true;
    public bool MinimizeOnLaunch { get; set; } = true;
    public string SelectedProfileId { get; set; } = "";
    public List<GameProfile> Profiles { get; set; } = [];
    public List<ServerEntry> Servers { get; set; } = [];
    public List<SavedSkin> SavedSkins { get; set; } = [];
    public string ActiveAccountId { get; set; } = "";
    public bool DiscordEnabled { get; set; } = true;
    public string Theme { get; set; } = "green";
    public string UpdateRepository { get; set; } = "";
}

public static class AppPaths
{
    public static readonly string Root = Environment.GetEnvironmentVariable("ZYROX_HOME") is { Length: > 0 } home
        ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zyrox");

    public static readonly string Settings = Path.Combine(Root, "settings.json");
    public static readonly string Accounts = Path.Combine(Root, "accounts.dat");
    public static readonly string Skins = Path.Combine(Root, "skins");
    public static readonly string SkinLibrary = Path.Combine(Root, "skin-library");
}

public static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}

public class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(AppPaths.Settings), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings) =>
        AtomicFile.WriteAllBytes(AppPaths.Settings, JsonSerializer.SerializeToUtf8Bytes(settings, Options));
}
