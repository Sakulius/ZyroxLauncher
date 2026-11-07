using System.IO;
using System.Reflection;

namespace Zyrox.Services;

public static class ZyroxClientService
{
    public const string NewestVersion = "26.3";
    private const string FileName = "zyrox-client.jar";
    private static readonly Dictionary<string, string> Builds = new()
    {
        ["26.3"] = "zyrox-client.jar",
        ["1.21.11"] = "zyrox-client-1.21.11.jar"
    };
    private const string FilePattern = "zyrox-client*";
    private const string FabricApiPattern = "fabric-api*.jar*";
    private const string FabricApiProject = "P7dR8mSH";

    public static IEnumerable<string> SupportedVersions =>
        Builds.Where(build => HasJar(build.Value)).Select(build => build.Key);

    public static bool Supports(GameProfile profile) =>
        profile.Loader == "fabric" && Builds.TryGetValue(profile.GameVersion, out var jar) && HasJar(jar);

    private static bool HasJar(string resource) =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(resource) != null;

    public static async Task EnsureAsync(GameProfile profile, string profileDir, CancellationToken cancellationToken)
    {
        if (!profile.ZyroxClient)
            return;
        if (!Supports(profile))
        {
            Remove(profileDir);
            return;
        }

        var mods = Path.Combine(profileDir, "mods");
        Directory.CreateDirectory(mods);
        var target = Path.Combine(mods, FileName);
        foreach (var old in Directory.EnumerateFiles(mods, FilePattern).Where(f => !string.Equals(f, target, StringComparison.OrdinalIgnoreCase)))
            File.Delete(old);

        var data = ReadJar(Builds[profile.GameVersion]);
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

    private static byte[] ReadJar(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Zyrox Client is missing from this launcher build.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
