extern alias NeoForgePackage;

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installer.Forge;
using NeoForgePackage::CmlLib.Core.Installer.NeoForge;
using NeoForgePackage::CmlLib.Core.Installer.NeoForge.Installers;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;

namespace Zyrox.Services;

public class GameService
{
    private const string FabricMeta = "https://meta.fabricmc.net/v2/versions/loader/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly MinecraftLauncher _launcher;
    private readonly string _rootDir;

    public GameService(string rootDir, string profileDir)
    {
        _rootDir = rootDir;
        var path = new MinecraftPath(rootDir);
        if (!string.Equals(rootDir, profileDir, StringComparison.OrdinalIgnoreCase))
        {
            path = new MinecraftPath(profileDir)
            {
                Library = path.Library,
                Versions = path.Versions,
                Resource = path.Resource,
                Assets = path.Assets,
                Runtime = path.Runtime
            };
        }
        _launcher = new MinecraftLauncher(path);
    }

    public async Task<List<string>> GetGameVersionsAsync(bool includeSnapshots)
    {
        var versions = await _launcher.GetAllVersionsAsync();
        return versions
            .Where(v => v.Type == "release" || (includeSnapshots && v.Type == "snapshot"))
            .Where(v => !v.Name.Contains("fabric-loader-", StringComparison.Ordinal) && !v.Name.Contains("forge", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.ReleaseTime)
            .Select(v => v.Name)
            .ToList();
    }

    public async Task<Process> PrepareAsync(
        GameProfile profile,
        MSession session,
        AppSettings settings,
        ServerEntry? server,
        string? world,
        IProgress<InstallerProgressChangedEventArgs> fileProgress,
        IProgress<ByteProgress> byteProgress,
        CancellationToken cancellationToken)
    {
        var version = await ResolveVersionAsync(profile, fileProgress, byteProgress, cancellationToken);
        var option = new MLaunchOption
        {
            Session = session,
            MaximumRamMb = profile.RamMb > 0 ? profile.RamMb : settings.RamMb,
            FullScreen = profile.Fullscreen == true,
            GameLauncherName = "Zyrox",
            GameLauncherVersion = "2.0"
        };
        if (server != null)
        {
            var (host, port) = ServerService.ParseAddress(server.Address);
            option.ServerIp = host;
            option.ServerPort = port;
        }
        if (profile.ScreenWidth > 0 && profile.ScreenHeight > 0)
        {
            option.ScreenWidth = profile.ScreenWidth;
            option.ScreenHeight = profile.ScreenHeight;
        }
        if (!string.IsNullOrWhiteSpace(profile.JvmArgs))
            option.ExtraJvmArguments = [MArgument.FromCommandLine(profile.JvmArgs.Trim())];
        if (world != null)
            option.QuickPlaySingleplayer = world;
        return await _launcher.InstallAndBuildProcessAsync(version, option, fileProgress, byteProgress, cancellationToken);
    }

    private async Task<string> ResolveVersionAsync(
        GameProfile profile,
        IProgress<InstallerProgressChangedEventArgs> fileProgress,
        IProgress<ByteProgress> byteProgress,
        CancellationToken cancellationToken)
    {
        var gameVersion = profile.GameVersion;
        if (gameVersion.Length == 0)
        {
            var versions = await GetGameVersionsAsync(includeSnapshots: false);
            gameVersion = versions.FirstOrDefault() ?? throw new InvalidOperationException("No Minecraft version found.");
        }

        var id = profile.Loader switch
        {
            "fabric" => await EnsureFabricAsync(profile, gameVersion, cancellationToken),
            "forge" => await InstallForgeAsync(profile, gameVersion, fileProgress, byteProgress, cancellationToken),
            "neoforge" => await InstallNeoForgeAsync(profile, gameVersion, fileProgress, byteProgress, cancellationToken),
            _ => gameVersion
        };        await _launcher.GetAllVersionsAsync(cancellationToken);
        return id;
    }

    private async Task<string> InstallForgeAsync(GameProfile profile, string gameVersion,
        IProgress<InstallerProgressChangedEventArgs> fileProgress, IProgress<ByteProgress> byteProgress, CancellationToken cancellationToken)
    {
        var options = new ForgeInstallOptions { FileProgress = fileProgress, ByteProgress = byteProgress, CancellationToken = cancellationToken };
        var installer = new ForgeInstaller(_launcher);
        return profile.LoaderVersion.Length > 0
            ? await installer.Install(gameVersion, profile.LoaderVersion, options)
            : await installer.Install(gameVersion, options);
    }

    private async Task<string> InstallNeoForgeAsync(GameProfile profile, string gameVersion,
        IProgress<InstallerProgressChangedEventArgs> fileProgress, IProgress<ByteProgress> byteProgress, CancellationToken cancellationToken)
    {
        var options = new NeoForgeInstallOptions { FileProgress = fileProgress, ByteProgress = byteProgress, CancellationToken = cancellationToken };
        var installer = new NeoForgeInstaller(_launcher);
        return profile.LoaderVersion.Length > 0
            ? await installer.Install(gameVersion, profile.LoaderVersion, options)
            : await installer.Install(gameVersion, options);
    }

    private async Task<string> EnsureFabricAsync(GameProfile profile, string gameVersion, CancellationToken cancellationToken)
    {
        if (profile.LoaderVersion.Length > 0)
        {
            var known = $"fabric-loader-{profile.LoaderVersion}-{gameVersion}";
            if (File.Exists(Path.Combine(_rootDir, "versions", known, known + ".json")))
                return known;
        }

        var escaped = Uri.EscapeDataString(gameVersion);
        using var loaders = JsonDocument.Parse(await Http.GetStringAsync(FabricMeta + escaped, cancellationToken));
        var loader = profile.LoaderVersion.Length > 0 ? profile.LoaderVersion : loaders.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("loader"))
            .Where(l => l.GetProperty("stable").GetBoolean())
            .Select(l => l.GetProperty("version").GetString())
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Fabric does not support Minecraft {gameVersion} yet.");

        var json = await Http.GetByteArrayAsync($"{FabricMeta}{escaped}/{Uri.EscapeDataString(loader)}/profile/json", cancellationToken);
        using var document = JsonDocument.Parse(json);
        var id = document.RootElement.GetProperty("id").GetString() ?? "";
        if (id.Length == 0 || id != Path.GetFileName(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Invalid response from Fabric.");

        AtomicFile.WriteAllBytes(Path.Combine(_rootDir, "versions", id, id + ".json"), json);
        profile.LoaderVersion = loader;
        return id;
    }
}
