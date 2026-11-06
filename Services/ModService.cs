using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zyrox.Services;

public record ModHit(string ProjectId, string Title, string Description, long Downloads, string? IconUrl);
public record InstalledMod(string Path, string Name, bool Enabled);
public record ModUpdate(InstalledMod Mod, string NewFileName);

public class ModService
{
    private const string Api = "https://api.modrinth.com/v2/";
    private const string ManifestName = "zyrox-mods.json";
    private const string ContentManifestName = "zyrox-content.json";
    private const string DisabledSuffix = ".disabled";
    private const int MaxParallelDownloads = 6;

    private static readonly string[] AllowedPackHosts = ["cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"];
    private static readonly string[] OverrideFolders = ["overrides/", "client-overrides/"];

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Zyrox-Launcher/2.0");
        return client;
    }

    public async Task<List<ModHit>> SearchAsync(string query, string loader, string gameVersion, int offset, CancellationToken cancellationToken)
    {
        var facets = JsonSerializer.Serialize(new[]
        {
            new[] { "project_type:mod" },
            new[] { "categories:" + loader },
            new[] { "versions:" + gameVersion }
        });
        return await SearchInternalAsync(query, facets, offset, cancellationToken);
    }

    public async Task<List<ModHit>> SearchPacksAsync(string query, int offset, CancellationToken cancellationToken)
    {
        var facets = JsonSerializer.Serialize(new[]
        {
            new[] { "project_type:modpack" },
            new[] { "categories:fabric", "categories:forge", "categories:neoforge" }
        });
        return await SearchInternalAsync(query, facets, offset, cancellationToken);
    }

    private static async Task<List<ModHit>> SearchInternalAsync(string query, string facets, int offset, CancellationToken cancellationToken)
    {
        var url = $"{Api}search?limit=20&offset={offset}&index={(query.Length == 0 ? "downloads" : "relevance")}"
            + $"&query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}";
        using var json = JsonDocument.Parse(await Http.GetStringAsync(url, cancellationToken));
        return json.RootElement.GetProperty("hits").EnumerateArray().Select(hit => new ModHit(
            hit.GetProperty("project_id").GetString()!,
            hit.GetProperty("title").GetString() ?? "",
            hit.GetProperty("description").GetString() ?? "",
            hit.GetProperty("downloads").GetInt64(),
            hit.TryGetProperty("icon_url", out var icon) && icon.ValueKind == JsonValueKind.String ? icon.GetString() : null)).ToList();
    }

    public async Task<List<ModHit>> SearchContentAsync(ContentType type, string query, string gameVersion, int offset, CancellationToken cancellationToken)
    {
        var facets = JsonSerializer.Serialize(new[]
        {
            new[] { "project_type:" + type.ProjectType },
            new[] { "versions:" + gameVersion }
        });
        return await SearchInternalAsync(query, facets, offset, cancellationToken);
    }

    public async Task InstallContentAsync(string projectId, ContentType type, string gameVersion, string profileDir, CancellationToken cancellationToken)
    {
        var url = $"{Api}project/{Uri.EscapeDataString(projectId)}/version"
            + $"?game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}";
        var versions = JsonSerializer.Deserialize<List<ModVersion>>(await Http.GetStringAsync(url, cancellationToken)) ?? [];
        var file = versions.FirstOrDefault()?.PrimaryFile
            ?? throw new InvalidOperationException($"There is no matching version for Minecraft {gameVersion}.");

        var uri = new Uri(file.Url);
        var name = file.Filename;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "cdn.modrinth.com"
            || name != Path.GetFileName(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid file.");

        var data = await Http.GetByteArrayAsync(uri, cancellationToken);
        VerifyHash(data, file.Hashes.GetValueOrDefault("sha512"), name);
        AtomicFile.WriteAllBytes(Path.Combine(profileDir, type.Folder, name), data);

        var manifest = LoadContentManifest(profileDir);
        manifest[projectId] = type.Folder + "/" + name;
        AtomicFile.WriteAllBytes(Path.Combine(profileDir, ContentManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    public static HashSet<string> GetInstalledContent(string profileDir) => LoadContentManifest(profileDir)
        .Where(e => File.Exists(Path.Combine(profileDir, e.Value)))
        .Select(e => e.Key)
        .ToHashSet();

    public static List<string> GetContentFiles(ContentType type, string profileDir)
    {
        var folder = Path.Combine(profileDir, type.Folder);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.zip").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
    }

    private static Dictionary<string, string> LoadContentManifest(string profileDir)
    {
        try
        {
            var path = Path.Combine(profileDir, ContentManifestName);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        return [];
    }

    public async Task InstallAsync(string projectId, string loader, string gameVersion, string profileDir, CancellationToken cancellationToken)
    {
        var manifest = LoadManifest(profileDir);
        await InstallRecursiveAsync(projectId, loader, gameVersion, profileDir, manifest, [], cancellationToken);
        SaveManifest(profileDir, manifest);
    }

    private static async Task InstallRecursiveAsync(string projectId, string loader, string gameVersion, string profileDir,
        Dictionary<string, string> manifest, HashSet<string> visited, CancellationToken cancellationToken)
    {
        if (!visited.Add(projectId) || (manifest.TryGetValue(projectId, out var existing) && FindMod(profileDir, existing) != null))
            return;

        var version = await FindVersionAsync(projectId, loader, gameVersion, cancellationToken);
        await InstallDependenciesAsync(version, loader, gameVersion, profileDir, manifest, visited, cancellationToken);

        var file = version.PrimaryFile ?? throw new InvalidOperationException("The mod has no file.");
        manifest[projectId] = await DownloadModAsync(file, profileDir, cancellationToken);
    }

    private static async Task InstallDependenciesAsync(ModVersion version, string loader, string gameVersion, string profileDir,
        Dictionary<string, string> manifest, HashSet<string> visited, CancellationToken cancellationToken)
    {
        foreach (var dependency in version.Dependencies.Where(d => d.Type == "required"))
        {
            var project = dependency.ProjectId ?? await FindProjectOfVersionAsync(dependency.VersionId, cancellationToken);
            if (project != null)
                await InstallRecursiveAsync(project, loader, gameVersion, profileDir, manifest, visited, cancellationToken);
        }
    }

    private static async Task<ModVersion> FindVersionAsync(string projectId, string loader, string gameVersion, CancellationToken cancellationToken)
    {
        var url = $"{Api}project/{Uri.EscapeDataString(projectId)}/version"
            + $"?loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}"
            + $"&game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}";
        var versions = JsonSerializer.Deserialize<List<ModVersion>>(await Http.GetStringAsync(url, cancellationToken)) ?? [];
        return versions.FirstOrDefault()
            ?? throw new InvalidOperationException($"There is no matching mod version for Minecraft {gameVersion}.");
    }

    private static async Task<string?> FindProjectOfVersionAsync(string? versionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(versionId))
            return null;
        using var json = JsonDocument.Parse(await Http.GetStringAsync($"{Api}version/{Uri.EscapeDataString(versionId)}", cancellationToken));
        return json.RootElement.GetProperty("project_id").GetString();
    }

    private static async Task<string> DownloadModAsync(ModFile file, string profileDir, CancellationToken cancellationToken)
    {
        var uri = new Uri(file.Url);
        var name = file.Filename;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "cdn.modrinth.com"
            || name != Path.GetFileName(name) || !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid mod file.");

        var data = await Http.GetByteArrayAsync(uri, cancellationToken);
        VerifyHash(data, file.Hashes.GetValueOrDefault("sha512"), name);
        AtomicFile.WriteAllBytes(Path.Combine(profileDir, "mods", name), data);
        return name;
    }

    private static void VerifyHash(byte[] data, string? expected, string name)
    {
        if (expected == null || !Convert.ToHexString(SHA512.HashData(data)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The checksum of {name} does not match.");
    }

    public async Task<List<PendingModUpdate>> FindUpdatesAsync(string loader, string gameVersion, string profileDir, CancellationToken cancellationToken)
    {
        var mods = GetInstalled(profileDir);
        if (mods.Count == 0)
            return [];

        var byHash = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
            byHash[Convert.ToHexString(SHA512.HashData(await File.ReadAllBytesAsync(mod.Path, cancellationToken))).ToLowerInvariant()] = mod;

        var body = JsonSerializer.Serialize(new
        {
            hashes = byHash.Keys,
            algorithm = "sha512",
            loaders = new[] { loader },
            game_versions = new[] { gameVersion }
        });
        using var response = await Http.PostAsync(Api + "version_files/update",
            new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
        response.EnsureSuccessStatusCode();

        var latest = JsonSerializer.Deserialize<Dictionary<string, ModVersion>>(await response.Content.ReadAsStringAsync(cancellationToken)) ?? [];
        var updates = new List<PendingModUpdate>();
        foreach (var (hash, version) in latest)
        {
            var file = version.PrimaryFile;
            if (file != null && byHash.TryGetValue(hash, out var mod)
                && !hash.Equals(file.Hashes.GetValueOrDefault("sha512"), StringComparison.OrdinalIgnoreCase))
                updates.Add(new PendingModUpdate(mod, version));
        }
        return updates;
    }

    public async Task ApplyUpdatesAsync(IEnumerable<PendingModUpdate> updates, string loader, string gameVersion, string profileDir, CancellationToken cancellationToken)
    {
        var manifest = LoadManifest(profileDir);
        foreach (var update in updates)
        {
            var file = update.Version.PrimaryFile!;
            var visited = new HashSet<string>();
            if (update.Version.ProjectId != null)
                visited.Add(update.Version.ProjectId);
            await InstallDependenciesAsync(update.Version, loader, gameVersion, profileDir, manifest, visited, cancellationToken);

            var newName = await DownloadModAsync(file, profileDir, cancellationToken);
            var newPath = Path.Combine(profileDir, "mods", newName);
            if (!string.Equals(update.Mod.Name, newName, StringComparison.OrdinalIgnoreCase))
                File.Delete(update.Mod.Path);
            else if (!update.Mod.Enabled)
                File.Delete(update.Mod.Path);

            if (!update.Mod.Enabled)
                File.Move(newPath, newPath + DisabledSuffix, overwrite: true);

            if (update.Version.ProjectId != null)
                manifest[update.Version.ProjectId] = newName;
        }
        SaveManifest(profileDir, manifest);
    }

    public async Task<ModpackInfo> InstallPackAsync(string projectId, string title, string profileDir, IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report("Loading pack ...");
        var versions = JsonSerializer.Deserialize<List<ModVersion>>(
            await Http.GetStringAsync($"{Api}project/{Uri.EscapeDataString(projectId)}/version", cancellationToken)) ?? [];
        var version = versions.FirstOrDefault(v => v.Loaders.Any(IsSupportedLoader))
            ?? throw new InvalidOperationException("This modpack uses a loader Zyrox does not support.");
        var packFile = version.PrimaryFile ?? throw new InvalidOperationException("The modpack has no file.");

        var uri = new Uri(packFile.Url);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "cdn.modrinth.com")
            throw new InvalidOperationException("Invalid modpack file.");
        var packData = await Http.GetByteArrayAsync(uri, cancellationToken);
        VerifyHash(packData, packFile.Hashes.GetValueOrDefault("sha512"), packFile.Filename);

        using var stream = new MemoryStream(packData);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var indexEntry = archive.GetEntry("modrinth.index.json") ?? throw new InvalidOperationException("The modpack is invalid.");
        PackIndex index;
        using (var indexStream = indexEntry.Open())
            index = await JsonSerializer.DeserializeAsync<PackIndex>(indexStream, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("The modpack is invalid.");

        var gameVersion = index.Dependencies.GetValueOrDefault("minecraft")
            ?? throw new InvalidOperationException("The modpack does not name a Minecraft version.");
        var (loader, loaderVersion) = ReadLoader(index);

        var root = Path.GetFullPath(profileDir) + Path.DirectorySeparatorChar;
        var files = index.Files.Where(f => f.Env?.GetValueOrDefault("client") != "unsupported").ToList();
        var done = 0;
        using var gate = new SemaphoreSlim(MaxParallelDownloads);
        await Task.WhenAll(files.Select(async file =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await DownloadPackFileAsync(file, root, cancellationToken);
                progress.Report($"{Interlocked.Increment(ref done)} / {files.Count}");
            }
            finally
            {
                gate.Release();
            }
        }));

        progress.Report("Extracting ...");
        foreach (var entry in archive.Entries)
        {
            var folder = OverrideFolders.FirstOrDefault(f => entry.FullName.StartsWith(f, StringComparison.Ordinal));
            if (folder == null || entry.FullName.EndsWith('/'))
                continue;
            var target = SafeCombine(root, entry.FullName[folder.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }

        return new ModpackInfo(title, gameVersion, loader, loaderVersion);
    }

    private static bool IsSupportedLoader(string loader) => loader is "fabric" or "forge" or "neoforge";

    private static (string Loader, string Version) ReadLoader(PackIndex index)
    {
        if (index.Dependencies.TryGetValue("fabric-loader", out var fabric))
            return ("fabric", fabric);
        if (index.Dependencies.TryGetValue("neoforge", out var neoForge))
            return ("neoforge", neoForge);
        if (index.Dependencies.TryGetValue("forge", out var forge))
            return ("forge", forge);
        throw new InvalidOperationException("This modpack uses a loader Zyrox does not support.");
    }

    private static async Task DownloadPackFileAsync(PackFile file, string root, CancellationToken cancellationToken)
    {
        var target = SafeCombine(root, file.Path);
        var source = file.Downloads
            .Select(d => Uri.TryCreate(d, UriKind.Absolute, out var u) ? u : null)
            .FirstOrDefault(u => u != null && u.Scheme == Uri.UriSchemeHttps && AllowedPackHosts.Contains(u.Host))
            ?? throw new InvalidOperationException($"There is no allowed download source for {file.Path}.");

        var data = await Http.GetByteArrayAsync(source, cancellationToken);
        VerifyHash(data, file.Hashes.GetValueOrDefault("sha512"), file.Path);
        AtomicFile.WriteAllBytes(target, data);
    }

    private static string SafeCombine(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidOperationException("The modpack contains an invalid path.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The modpack contains an invalid path.");
        return full;
    }

    public static HashSet<string> GetInstalledProjects(string profileDir) =>
        LoadManifest(profileDir).Where(e => FindMod(profileDir, e.Value) != null).Select(e => e.Key).ToHashSet();

    public static List<InstalledMod> GetInstalled(string profileDir)
    {
        var folder = Path.Combine(profileDir, "mods");
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var enabled = !f.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
                var name = Path.GetFileName(f);
                return new InstalledMod(f, enabled ? name : name[..^DisabledSuffix.Length], enabled);
            })
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void SetEnabled(InstalledMod mod, bool enabled)
    {
        if (mod.Enabled != enabled)
            File.Move(mod.Path, enabled ? mod.Path[..^DisabledSuffix.Length] : mod.Path + DisabledSuffix);
    }

    public static void Remove(InstalledMod mod, string profileDir)
    {
        File.Delete(mod.Path);
        var manifest = LoadManifest(profileDir);
        foreach (var key in manifest.Where(e => e.Value.Equals(mod.Name, StringComparison.OrdinalIgnoreCase)).Select(e => e.Key).ToList())
            manifest.Remove(key);
        SaveManifest(profileDir, manifest);
    }

    private static string? FindMod(string profileDir, string fileName)
    {
        var path = Path.Combine(profileDir, "mods", fileName);
        return File.Exists(path) ? path : File.Exists(path + DisabledSuffix) ? path + DisabledSuffix : null;
    }

    private static Dictionary<string, string> LoadManifest(string profileDir)
    {
        try
        {
            var path = Path.Combine(profileDir, ManifestName);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        return [];
    }

    private static void SaveManifest(string profileDir, Dictionary<string, string> manifest) =>
        AtomicFile.WriteAllBytes(Path.Combine(profileDir, ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest));

    public class ModVersion
    {
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
        [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = [];
        [JsonPropertyName("dependencies")] public List<ModDependency> Dependencies { get; set; } = [];
        [JsonPropertyName("files")] public List<ModFile> Files { get; set; } = [];

        [JsonIgnore] public ModFile? PrimaryFile => Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();
    }

    public class ModDependency
    {
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
        [JsonPropertyName("version_id")] public string? VersionId { get; set; }
        [JsonPropertyName("dependency_type")] public string Type { get; set; } = "";
    }

    public class ModFile
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("filename")] public string Filename { get; set; } = "";
        [JsonPropertyName("primary")] public bool Primary { get; set; }
        [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = [];
    }

    private class PackIndex
    {
        [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = [];
        [JsonPropertyName("files")] public List<PackFile> Files { get; set; } = [];
    }

    private class PackFile
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = [];
        [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; set; }
        [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = [];
    }
}

public record ContentType(string ProjectType, string Folder)
{
    public static readonly ContentType ResourcePack = new("resourcepack", "resourcepacks");
    public static readonly ContentType Shader = new("shader", "shaderpacks");
}

public record PendingModUpdate(InstalledMod Mod, ModService.ModVersion Version);
public record ModpackInfo(string Name, string GameVersion, string Loader, string LoaderVersion);
