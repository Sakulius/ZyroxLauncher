using System.IO;
using System.Reflection;

namespace Zyrox.Services;

public static class ZyroxClientService
{
    public const string SupportedVersion = "26.3";
    private const string ResourceName = "zyrox-client.jar";
    private const string FilePattern = "zyrox-client*";
    private const string FabricApiPattern = "fabric-api*.jar*";
    private const string FabricApiProject = "P7dR8mSH";

    public static bool Supports(GameProfile profile) =>
        profile.Loader == "fabric" && profile.GameVersion == SupportedVersion;

    public static async Task EnsureAsync(GameProfile profile, string profileDir, CancellationToken cancellationToken)
    {
        if (!profile.ZyroxClient)
            return;
        if (!Supports(profile))
        {
            Remove(profileDir);
            throw new InvalidOperationException($"Zyrox Client needs a Fabric profile with Minecraft {SupportedVersion}. Change the version or turn it off in the profile details.");
        }

        var mods = Path.Combine(profileDir, "mods");
        Directory.CreateDirectory(mods);
        var target = Path.Combine(mods, ResourceName);
        foreach (var old in Directory.EnumerateFiles(mods, FilePattern).Where(f => !string.Equals(f, target, StringComparison.OrdinalIgnoreCase)))
            File.Delete(old);

        var data = ReadJar();
        var current = File.Exists(target) ? await File.ReadAllBytesAsync(target, cancellationToken) : null;
        if (current == null || !data.AsSpan().SequenceEqual(current))
            AtomicFile.WriteAllBytes(target, data);

        if (!Directory.EnumerateFiles(mods, FabricApiPattern).Any())
            await new ModService().InstallAsync(FabricApiProject, profile.Loader, profile.GameVersion, profileDir, cancellationToken);
    }

    public static void Remove(string profileDir)
    {
        var mods = Path.Combine(profileDir, "mods");
        if (!Directory.Exists(mods))
            return;
        foreach (var file in Directory.EnumerateFiles(mods, FilePattern))
            File.Delete(file);
    }

    private static byte[] ReadJar()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Zyrox Client is missing from this launcher build.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
