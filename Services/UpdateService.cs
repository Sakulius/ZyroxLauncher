using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zyrox.Services;

public record UpdateInfo(Version Version, string ExeUrl, string HashUrl);

public static partial class UpdateService
{
    public const string DefaultRepository = "Sakulius/Zyrox";

    private const string ExeAsset = "Zyrox.exe";
    private const string HashAsset = "Zyrox.exe.sha256";
    private const string OldSuffix = ".old";
    private const string NewSuffix = ".new";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Zyrox-Launcher");
        return client;
    }

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public static bool IsValidRepository(string repository) => RepositoryPattern().IsMatch(repository);

    public static async Task<UpdateInfo?> CheckAsync(string repository)
    {
        if (!IsValidRepository(repository))
            return null;

        using var response = await SendAsync($"https://api.github.com/repos/{repository}/releases/latest", "application/vnd.github+json");
        if (!response.IsSuccessStatusCode)
            return null;
        var update = ParseRelease(await response.Content.ReadAsStringAsync(), repository);
        return update != null && update.Version > CurrentVersion ? update : null;
    }

    public static UpdateInfo? ParseRelease(string json, string repository)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var version))
            return null;

        string? exe = null;
        string? hash = null;
        var prefix = $"https://api.github.com/repos/{repository}/releases/assets/";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var url = asset.GetProperty("url").GetString();
            if (url == null || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var name = asset.GetProperty("name").GetString();
            if (name == ExeAsset)
                exe = url;
            else if (name == HashAsset)
                hash = url;
        }
        return exe != null && hash != null ? new UpdateInfo(version, exe, hash) : null;
    }

    public static async Task InstallAndRestartAsync(UpdateInfo update)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The path of Zyrox is unknown.");
        var data = await DownloadAssetAsync(update.ExeUrl);
        var hash = System.Text.Encoding.UTF8.GetString(await DownloadAssetAsync(update.HashUrl));
        ReplaceExecutable(path, data, hash);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static async Task<byte[]> DownloadAssetAsync(string url)
    {
        using var response = await SendAsync(url, "application/octet-stream");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static Task<HttpResponseMessage> SendAsync(string url, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(accept);
        return Http.SendAsync(request);
    }

    public static void ReplaceExecutable(string path, byte[] data, string hashText)
    {
        var expected = hashText.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (expected == null || !Convert.ToHexString(SHA256.HashData(data)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The checksum of the update does not match.");

        var newPath = path + NewSuffix;
        var oldPath = path + OldSuffix;
        File.WriteAllBytes(newPath, data);
        File.Move(path, oldPath, overwrite: true);
        try
        {
            File.Move(newPath, path);
        }
        catch
        {
            File.Move(oldPath, path);
            throw;
        }
    }

    public static void CleanupOldVersion()
    {
        try
        {
            if (Environment.ProcessPath is { } path && File.Exists(path + OldSuffix))
                File.Delete(path + OldSuffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-][A-Za-z0-9_.-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*$")]
    private static partial Regex RepositoryPattern();
}
