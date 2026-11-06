using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Zyrox.Services;

public class SkinTexture
{
    public const int Width = 64;
    public const int CapeHeight = 32;
    private const int Upscale = 8;

    private SkinTexture(BitmapSource baseLayer, BitmapSource? overlay, BitmapSource? cape, bool slim, int height)
    {
        Base = baseLayer;
        Overlay = overlay;
        Cape = cape;
        Slim = slim;
        Height = height;
    }

    public BitmapSource Base { get; }
    public BitmapSource? Overlay { get; }
    public BitmapSource? Cape { get; }
    public bool Slim { get; }
    public int Height { get; }
    public bool IsLegacy => Height == 32;

    public static SkinTexture FromPng(byte[] png, bool slim, byte[]? capePng = null)
    {
        using var stream = new MemoryStream(png);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var skin = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var height = skin.PixelHeight;
        if (skin.PixelWidth != Width || (height != 64 && height != 32))
            throw new ArgumentException("Unknown skin format.");

        var overlay = new byte[Width * height * 4];
        skin.CopyPixels(overlay, Width * 4, 0);

        var opaque = (byte[])overlay.Clone();
        for (var i = 3; i < opaque.Length; i += 4)
            opaque[i] = 255;

        var hasOverlay = height == 64 || HasTransparentPixel(overlay, 32, 0, 32, 16);

        return new SkinTexture(Scale(opaque, height), hasOverlay ? Scale(overlay, height) : null, DecodeCape(capePng), slim, height);
    }

    public static SkinTexture Placeholder()
    {
        const int height = 64;
        var pixels = new byte[Width * height * 4];

        void Fill(int x, int y, int w, int h, byte shade)
        {
            for (var py = y; py < y + h; py++)
            {
                for (var px = x; px < x + w; px++)
                {
                    var i = (py * Width + px) * 4;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = shade;
                    pixels[i + 3] = 255;
                }
            }
        }

        Fill(0, 0, 32, 16, 128);
        Fill(16, 16, 24, 16, 104);
        Fill(40, 16, 16, 16, 90);
        Fill(32, 48, 16, 16, 90);
        Fill(0, 16, 16, 16, 76);
        Fill(16, 48, 16, 16, 70);
        return new SkinTexture(Scale(pixels, height), null, null, false, height);
    }

    private static BitmapSource? DecodeCape(byte[]? png)
    {
        if (png == null)
            return null;
        using var stream = new MemoryStream(png);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (frame.PixelWidth != Width || frame.PixelHeight != CapeHeight)
            return null;
        var cape = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[Width * CapeHeight * 4];
        cape.CopyPixels(pixels, Width * 4, 0);
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        return Scale(pixels, CapeHeight);
    }

    private static bool HasTransparentPixel(byte[] pixels, int x, int y, int w, int h)
    {
        for (var py = y; py < y + h; py++)
            for (var px = x; px < x + w; px++)
                if (pixels[(py * Width + px) * 4 + 3] < 128)
                    return true;
        return false;
    }

    private static BitmapSource Scale(byte[] pixels, int height)
    {
        var scaledWidth = Width * Upscale;
        var scaledHeight = height * Upscale;
        var scaled = new byte[scaledWidth * scaledHeight * 4];
        for (var y = 0; y < scaledHeight; y++)
        {
            for (var x = 0; x < scaledWidth; x++)
            {
                var from = (y / Upscale * Width + x / Upscale) * 4;
                var to = (y * scaledWidth + x) * 4;
                Buffer.BlockCopy(pixels, from, scaled, to, 4);
            }
        }
        var bitmap = BitmapSource.Create(scaledWidth, scaledHeight, 96, 96, PixelFormats.Bgra32, null, scaled, scaledWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

public static class SkinLibrary
{
    public static string PathOf(SavedSkin skin) => Path.Combine(AppPaths.SkinLibrary, skin.Id + ".png");

    public static SavedSkin Add(byte[] png, string name, bool slim)
    {
        SkinTexture.FromPng(png, slim);
        var skin = new SavedSkin { Name = name, Slim = slim };
        AtomicFile.WriteAllBytes(PathOf(skin), png);
        return skin;
    }

    public static void Delete(SavedSkin skin)
    {
        try
        {
            File.Delete(PathOf(skin));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static (byte[]? Png, bool Slim) ReadCached(string uuid)
    {
        uuid = uuid.Replace("-", "");
        var path = Path.Combine(AppPaths.Skins, uuid + ".png");
        return File.Exists(path) ? (File.ReadAllBytes(path), File.Exists(Path.Combine(AppPaths.Skins, uuid + ".slim"))) : (null, false);
    }
}

public class SkinService
{
    private const string UploadUrl = "https://api.minecraftservices.com/minecraft/profile/skins";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task UploadAsync(string accessToken, byte[] png, bool slim)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(slim ? "slim" : "classic"), "variant");
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "skin.png");

        using var request = new HttpRequestMessage(HttpMethod.Post, UploadUrl) { Content = form };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Http.SendAsync(request);
        if (response.IsSuccessStatusCode)
            return;

        throw new InvalidOperationException((int)response.StatusCode switch
        {
            400 => "Minecraft rejected the file. Check that it is a valid skin.",
            401 or 403 => "Your sign-in has expired. Please sign in again.",
            429 => "Too many skin changes. Wait a moment and try again.",
            var code => $"Error code {code}."
        });
    }

    public async Task<string?> FindUuidAsync(string username)
    {
        using var response = await Http.GetAsync(
            $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(username)}");
        if (!response.IsSuccessStatusCode)
            return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString();
    }

    public async Task<SkinTexture?> GetSkinAsync(string uuid)
    {
        uuid = uuid.Replace("-", "");
        if (uuid.Length != 32 || !uuid.All(Uri.IsHexDigit))
            return null;

        var skinPath = Path.Combine(AppPaths.Skins, uuid + ".png");
        var modelPath = Path.Combine(AppPaths.Skins, uuid + ".slim");
        var capePath = Path.Combine(AppPaths.Skins, uuid + ".cape.png");
        try
        {
            var (url, slim, capeUrl) = await GetTextureAsync(uuid);
            if (url != null)
            {
                AtomicFile.WriteAllBytes(skinPath, await Http.GetByteArrayAsync(url));
                if (slim)
                    File.WriteAllBytes(modelPath, []);
                else
                    File.Delete(modelPath);
                if (capeUrl != null)
                    AtomicFile.WriteAllBytes(capePath, await Http.GetByteArrayAsync(capeUrl));
                else
                    File.Delete(capePath);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or KeyNotFoundException or FormatException)
        {
        }

        if (!File.Exists(skinPath))
            return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(skinPath);
            var cape = File.Exists(capePath) ? await File.ReadAllBytesAsync(capePath) : null;
            return SkinTexture.FromPng(bytes, File.Exists(modelPath), cape);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<(Uri? Url, bool Slim, Uri? Cape)> GetTextureAsync(string uuid)
    {
        using var response = await Http.GetAsync($"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}");
        if (!response.IsSuccessStatusCode)
            return (null, false, null);

        using var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var property in profile.RootElement.GetProperty("properties").EnumerateArray())
        {
            if (property.GetProperty("name").GetString() != "textures")
                continue;

            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(property.GetProperty("value").GetString()!));
            using var textures = JsonDocument.Parse(decoded);
            if (!textures.RootElement.GetProperty("textures").TryGetProperty("SKIN", out var skin))
                return (null, false, null);

            var url = TextureUri(skin);
            if (url == null)
                return (null, false, null);

            var slim = skin.TryGetProperty("metadata", out var metadata)
                && metadata.TryGetProperty("model", out var model)
                && model.GetString() == "slim";
            var cape = textures.RootElement.GetProperty("textures").TryGetProperty("CAPE", out var capeNode) ? TextureUri(capeNode) : null;
            return (url, slim, cape);
        }
        return (null, false, null);
    }

    private static Uri? TextureUri(JsonElement texture)
    {
        var url = new Uri(texture.GetProperty("url").GetString()!.Replace("http://", "https://"));
        return url.Host == "textures.minecraft.net" ? url : null;
    }
}