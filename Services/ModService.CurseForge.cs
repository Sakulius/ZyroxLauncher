using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Zyrox.Services;

// CurseForge as a second source. Its API needs a personal key from console.curseforge.com.
// Project ids are stored as "cf:<id>" so they never clash with Modrinth ids in the manifests.
public partial class ModService
{
    public const string CurseForgePrefix = "cf:";
    private const string CurseApi = "https://api.curseforge.com/v1/";
    private const int MinecraftGameId = 432;
    private const int ModsClass = 6;
    private const int ResourcePackClass = 12;
    private const int ShaderClass = 6552;
    private const int RequiredDependency = 3;

    private static readonly string[] CurseHosts = ["edge.forgecdn.net", "mediafilez.forgecdn.net"];

    // Key shipped inside the exe (Assets/curseforge.key at build time); empty when the file was missing.
    public static readonly string BuiltInCurseForgeKey = ReadBuiltInKey();

    public string CurseForgeKey { get; set; } = BuiltInCurseForgeKey;

    private static string ReadBuiltInKey()
    {
        using var stream = typeof(ModService).Assembly.GetManifestResourceStream("curseforge.key");
        return stream == null ? "" : new StreamReader(stream).ReadToEnd().Trim();
    }

    public static bool IsCurseForge(string projectId) => projectId.StartsWith(CurseForgePrefix, StringComparison.Ordinal);

    public async Task<List<ModHit>> SearchCurseForgeAsync(ContentType? type, string query, string loader, string gameVersion, int offset, CancellationToken cancellationToken)
    {
        var classId = type == ContentType.ResourcePack ? ResourcePackClass : type == ContentType.Shader ? ShaderClass : ModsClass;
        var url = $"{CurseApi}mods/search?gameId={MinecraftGameId}&classId={classId}&pageSize=20&index={offset}"
            + $"&sortField=2&sortOrder=desc&gameVersion={Uri.EscapeDataString(gameVersion)}&searchFilter={Uri.EscapeDataString(query)}";
        if (type == null && LoaderType(loader) is { } loaderType)
            url += $"&modLoaderType={loaderType}";

        using var json = JsonDocument.Parse(await CurseGetAsync(url, cancellationToken));
        return json.RootElement.GetProperty("data").EnumerateArray().Select(mod => new ModHit(
            CurseForgePrefix + mod.GetProperty("id").GetInt32(),
            mod.GetProperty("name").GetString() ?? "",
            mod.GetProperty("summary").GetString() ?? "",
            (long)mod.GetProperty("downloadCount").GetDouble(),
            mod.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object
                && logo.TryGetProperty("thumbnailUrl", out var thumb) ? thumb.GetString() : null)).ToList();
    }

    public async Task InstallCurseForgeAsync(string projectId, string loader, string gameVersion, string profileDir, CancellationToken cancellationToken)
    {
        var manifest = LoadManifest(profileDir);
        await InstallCurseRecursiveAsync(projectId, loader, gameVersion, profileDir, manifest, [], cancellationToken);
        SaveManifest(profileDir, manifest);
    }

    public async Task InstallCurseForgeContentAsync(string projectId, ContentType type, string gameVersion, string profileDir, CancellationToken cancellationToken)
    {
        var file = await FindCurseFileAsync(projectId, null, gameVersion, cancellationToken);
        var name = await DownloadCurseFileAsync(file, Path.Combine(profileDir, type.Folder), ".zip", cancellationToken);
        var manifest = LoadContentManifest(profileDir);
        manifest[projectId] = type.Folder + "/" + name;
        AtomicFile.WriteAllBytes(Path.Combine(profileDir, ContentManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    private async Task InstallCurseRecursiveAsync(string projectId, string loader, string gameVersion, string profileDir,
        Dictionary<string, string> manifest, HashSet<string> visited, CancellationToken cancellationToken)
    {
        if (!visited.Add(projectId) || (manifest.TryGetValue(projectId, out var existing) && FindMod(profileDir, existing) != null))
            return;

        var file = await FindCurseFileAsync(projectId, loader, gameVersion, cancellationToken);
        foreach (var dependency in file.GetProperty("dependencies").EnumerateArray())
        {
            if (dependency.GetProperty("relationType").GetInt32() == RequiredDependency)
                await InstallCurseRecursiveAsync(CurseForgePrefix + dependency.GetProperty("modId").GetInt32(), loader, gameVersion, profileDir, manifest, visited, cancellationToken);
        }
        manifest[projectId] = await DownloadCurseFileAsync(file, Path.Combine(profileDir, "mods"), ".jar", cancellationToken);
    }

    private async Task<JsonElement> FindCurseFileAsync(string projectId, string? loader, string gameVersion, CancellationToken cancellationToken)
    {
        var id = projectId[CurseForgePrefix.Length..];
        var url = $"{CurseApi}mods/{Uri.EscapeDataString(id)}/files?pageSize=50&gameVersion={Uri.EscapeDataString(gameVersion)}";
        if (loader != null && LoaderType(loader) is { } loaderType)
            url += $"&modLoaderType={loaderType}";

        using var json = JsonDocument.Parse(await CurseGetAsync(url, cancellationToken));
        var files = json.RootElement.GetProperty("data").EnumerateArray()
            .OrderByDescending(f => f.GetProperty("fileDate").GetDateTime())
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException($"There is no matching version for Minecraft {gameVersion}.");
        var file = files.FirstOrDefault(f => f.TryGetProperty("downloadUrl", out var u) && u.ValueKind == JsonValueKind.String);
        if (file.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("The author only allows downloads on the CurseForge website.");
        return file.Clone();
    }

    private static async Task<string> DownloadCurseFileAsync(JsonElement file, string folder, string extension, CancellationToken cancellationToken)
    {
        var uri = new Uri(file.GetProperty("downloadUrl").GetString()!);
        var name = file.GetProperty("fileName").GetString() ?? "";
        if (uri.Scheme != Uri.UriSchemeHttps || !CurseHosts.Contains(uri.Host)
            || name.Length == 0 || name != Path.GetFileName(name) || !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid file.");

        var sha1 = file.GetProperty("hashes").EnumerateArray()
            .Where(h => h.GetProperty("algo").GetInt32() == 1)
            .Select(h => h.GetProperty("value").GetString())
            .FirstOrDefault();
        var data = await Http.GetByteArrayAsync(uri, cancellationToken);
        if (sha1 == null || !Convert.ToHexString(SHA1.HashData(data)).Equals(sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The checksum of {name} does not match.");
        AtomicFile.WriteAllBytes(Path.Combine(folder, name), data);
        return name;
    }

    private async Task<string> CurseGetAsync(string url, CancellationToken cancellationToken)
    {
        if (CurseForgeKey.Length == 0)
            throw new InvalidOperationException("Add your CurseForge API key first.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-api-key", CurseForgeKey);
        using var response = await Http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException("CurseForge rejected the API key.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static int? LoaderType(string loader) => loader switch
    {
        "forge" => 1,
        "fabric" => 4,
        "neoforge" => 6,
        _ => null
    };
}
