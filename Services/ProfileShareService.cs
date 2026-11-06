using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Zyrox.Services;

public static class ProfileShareService
{
    public const string Extension = ".zyrox";

    private const string ManifestName = "zyrox-profile.json";
    private const long MaxExtractedBytes = 8L * 1024 * 1024 * 1024;

    private static readonly string[] SharedFolders = ["mods", "config", "resourcepacks", "shaderpacks"];
    private const string SharedOptionsFile = "options.txt";

    private record SharedProfile(string Name, string GameVersion, string Loader, string LoaderVersion, int RamMb);

    public static void Export(GameProfile profile, string profileDir, string targetFile)
    {
        var temp = targetFile + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var manifest = new SharedProfile(profile.Name, profile.GameVersion, profile.Loader, profile.LoaderVersion, profile.RamMb);
                using (var stream = archive.CreateEntry(ManifestName).Open())
                    JsonSerializer.Serialize(stream, manifest);

                foreach (var folder in SharedFolders)
                {
                    var source = Path.Combine(profileDir, folder);
                    if (!Directory.Exists(source))
                        continue;
                    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                        archive.CreateEntryFromFile(file, folder + "/" + Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.Fastest);
                }

                var options = Path.Combine(profileDir, SharedOptionsFile);
                if (File.Exists(options))
                    archive.CreateEntryFromFile(options, SharedOptionsFile, CompressionLevel.Fastest);
            }
            File.Move(temp, targetFile, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public static GameProfile Import(string file, Func<GameProfile, string> profileDirectory)
    {
        using var archive = ZipFile.OpenRead(file);
        var entry = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("This is not a Zyrox profile file.");
        SharedProfile manifest;
        using (var stream = entry.Open())
            manifest = JsonSerializer.Deserialize<SharedProfile>(stream) ?? throw new InvalidDataException("The profile file is damaged.");

        if (manifest.Loader is not ("vanilla" or "fabric" or "forge" or "neoforge"))
            throw new InvalidDataException("The profile file uses an unknown loader.");
        if (archive.Entries.Sum(e => e.Length) > MaxExtractedBytes)
            throw new InvalidDataException("The profile file is too large.");

        var profile = new GameProfile
        {
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? "Imported profile" : manifest.Name.Trim(),
            GameVersion = manifest.GameVersion,
            Loader = manifest.Loader,
            LoaderVersion = manifest.LoaderVersion,
            RamMb = Math.Max(0, manifest.RamMb)
        };

        var root = Path.GetFullPath(profileDirectory(profile)) + Path.DirectorySeparatorChar;
        try
        {
            foreach (var item in archive.Entries)
            {
                if (item.FullName == ManifestName || item.FullName.EndsWith('/') || !IsAllowed(item.FullName))
                    continue;
                var target = Path.GetFullPath(Path.Combine(root, item.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The profile file contains an invalid path.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                item.ExtractToFile(target, overwrite: true);
            }
        }
        catch
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            throw;
        }
        return profile;
    }

    private static bool IsAllowed(string entry) =>
        entry == SharedOptionsFile || SharedFolders.Any(folder => entry.StartsWith(folder + "/", StringComparison.Ordinal));
}
