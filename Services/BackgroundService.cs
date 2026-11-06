using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Zyrox.Services;

public sealed record GifAnimation(IReadOnlyList<BitmapSource> Frames, IReadOnlyList<TimeSpan> Delays);

public static class BackgroundService
{
    private const int MaxGifWidth = 1280;
    private const int MaxGifFrames = 400;
    private const long MaxGifBytes = 400L * 1024 * 1024;
    private const int MinGifDelayMs = 20;
    private const int DefaultGifDelayMs = 100;
    private const int BlockSize = 6;
    private const int Columns = 80;
    private const int Rows = 45;

    public static (BitmapSource Image, bool IsPixelArt) Load(string customPath, string gameDir)
    {
        var image = TryLoad(customPath) ?? TryLoad(FindNewestScreenshot(gameDir));
        return image != null ? (image, false) : (GenerateLandscape(), true);
    }

    private static string? FindNewestScreenshot(string gameDir)
    {
        try
        {
            var folder = new DirectoryInfo(Path.Combine(gameDir, "screenshots"));
            return folder.Exists
                ? folder.EnumerateFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static BitmapSource? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 2560;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UriFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static GifAnimation? TryLoadGif(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var decoder = new GifBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count < 2 ? null : ComposeGif(decoder);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UriFormatException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static GifAnimation ComposeGif(GifBitmapDecoder decoder)
    {
        var width = ReadNumber(decoder.Metadata, "/logscrdesc/Width", decoder.Frames[0].PixelWidth);
        var height = ReadNumber(decoder.Metadata, "/logscrdesc/Height", decoder.Frames[0].PixelHeight);
        var stride = width * 4;
        var canvas = new byte[stride * height];

        var step = Math.Max(1, (int)Math.Ceiling(decoder.Frames.Count / (double)MaxGifFrames));
        var kept = decoder.Frames.Count / step;
        var scale = Math.Min(1.0, MaxGifWidth / (double)width);
        scale = Math.Min(scale, Math.Sqrt(MaxGifBytes / Math.Max(1.0, (double)kept * stride * height)));

        var frames = new List<BitmapSource>();
        var delays = new List<TimeSpan>();
        var delayMs = 0;
        for (var i = 0; i < decoder.Frames.Count; i++)
        {
            var frame = decoder.Frames[i];
            var left = ReadNumber(frame.Metadata, "/imgdesc/Left", 0);
            var top = ReadNumber(frame.Metadata, "/imgdesc/Top", 0);
            var disposal = ReadNumber(frame.Metadata, "/grctlext/Disposal", 0);
            var delay = ReadNumber(frame.Metadata, "/grctlext/Delay", 0) * 10;
            var previous = disposal == 3 ? (byte[])canvas.Clone() : null;

            DrawGifFrame(frame, canvas, stride, width, height, left, top);
            delayMs += delay < MinGifDelayMs ? DefaultGifDelayMs : delay;
            if ((i + 1) % step == 0)
            {
                frames.Add(Snapshot(canvas, width, height, stride, scale));
                delays.Add(TimeSpan.FromMilliseconds(delayMs));
                delayMs = 0;
            }

            if (previous != null)
                previous.CopyTo(canvas, 0);
            else if (disposal == 2)
                ClearRectangle(canvas, stride, width, height, left, top, frame.PixelWidth, frame.PixelHeight);
        }
        return new GifAnimation(frames, delays);
    }

    private static void DrawGifFrame(BitmapSource frame, byte[] canvas, int stride, int width, int height, int left, int top)
    {
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var frameStride = frame.PixelWidth * 4;
        var pixels = new byte[frameStride * frame.PixelHeight];
        converted.CopyPixels(pixels, frameStride, 0);
        for (var y = 0; y < frame.PixelHeight && y + top < height; y++)
        {
            for (var x = 0; x < frame.PixelWidth && x + left < width; x++)
            {
                var source = y * frameStride + x * 4;
                if (pixels[source + 3] == 0)
                    continue;
                Array.Copy(pixels, source, canvas, (y + top) * stride + (x + left) * 4, 4);
            }
        }
    }

    private static void ClearRectangle(byte[] canvas, int stride, int width, int height, int left, int top, int rectWidth, int rectHeight)
    {
        var length = Math.Min(rectWidth, width - left) * 4;
        for (var y = top; y < Math.Min(top + rectHeight, height) && length > 0; y++)
            Array.Clear(canvas, y * stride + left * 4, length);
    }

    private static BitmapSource Snapshot(byte[] canvas, int width, int height, int stride, double scale)
    {
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
        if (scale < 1)
            image = new CachedBitmap(new TransformedBitmap(image, new ScaleTransform(scale, scale)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        image.Freeze();
        return image;
    }

    private static int ReadNumber(ImageMetadata? metadata, string query, int fallback)
    {
        try
        {
            return metadata is BitmapMetadata bitmap && bitmap.ContainsQuery(query) ? Convert.ToInt32(bitmap.GetQuery(query)) : fallback;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FormatException or InvalidCastException or ArgumentException)
        {
            return fallback;
        }
    }

    private static BitmapSource GenerateLandscape()
    {
        var width = Columns * BlockSize;
        var height = Rows * BlockSize;
        var pixels = new byte[width * height * 4];
        var random = new Random(41);

        void Block(int column, int row, (int R, int G, int B) color, int noise)
        {
            if (column < 0 || column >= Columns || row < 0 || row >= Rows)
                return;
            for (var y = 0; y < BlockSize; y++)
            {
                for (var x = 0; x < BlockSize; x++)
                {
                    var shift = noise == 0 ? 0 : random.Next(-noise, noise + 1);
                    var i = ((row * BlockSize + y) * width + column * BlockSize + x) * 4;
                    pixels[i] = (byte)Math.Clamp(color.B + shift, 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp(color.G + shift, 0, 255);
                    pixels[i + 2] = (byte)Math.Clamp(color.R + shift, 0, 255);
                    pixels[i + 3] = 255;
                }
            }
        }

        for (var row = 0; row < Rows; row++)
        {
            var t = row / (double)(Rows - 1);
            var color = ((int)(38 + 214 * t), (int)(52 + 110 * t), (int)(112 + 10 * t));
            for (var column = 0; column < Columns; column++)
                Block(column, row, color, 0);
        }

        for (var row = 14; row < 20; row++)
            for (var column = 40; column < 46; column++)
                Block(column, row, (255, 214, 130), 3);

        foreach (var (cx, cy, length) in new[] { (12, 6, 10), (42, 10, 8), (64, 5, 12), (28, 15, 6) })
        {
            for (var column = cx; column < cx + length; column++)
                Block(column, cy, (232, 190, 196), 2);
            for (var column = cx + 2; column < cx + length - 2; column++)
                Block(column, cy - 1, (244, 214, 214), 2);
        }

        for (var column = 0; column < Columns; column++)
        {
            var top = (int)(25 + 4 * Math.Sin(column * 0.21) + 2 * Math.Sin(column * 0.07 + 1.3));
            for (var row = top; row < Rows; row++)
                Block(column, row, (66, 62, 112), 2);
        }

        for (var column = 0; column < Columns; column++)
        {
            var top = (int)(31 + 3 * Math.Sin(column * 0.13 + 2) + 1.5 * Math.Sin(column * 0.31));
            for (var row = top; row < Rows; row++)
                Block(column, row, (38, 66, 74), 2);
        }

        var ground = new int[Columns];
        for (var column = 0; column < Columns; column++)
        {
            var top = (int)(37 + 2.5 * Math.Sin(column * 0.17 + 0.4) + 1.2 * Math.Sin(column * 0.41));
            ground[column] = top;
            for (var row = top; row < Rows; row++)
            {
                var depth = row - top;
                var color = depth == 0 ? (82, 150, 62) : depth < 3 ? (112, 76, 50) : (88, 88, 92);
                Block(column, row, color, depth == 0 ? 10 : 7);
            }
        }

        foreach (var column in new[] { 34, 47, 52, 66, 73 })
        {
            var baseRow = ground[column];
            var trunk = 4 + column % 2;
            for (var row = baseRow - trunk; row < baseRow; row++)
                Block(column, row, (84, 58, 40), 6);
            for (var dy = -trunk - 2; dy <= -trunk + 1; dy++)
            {
                var radius = dy <= -trunk - 1 ? 1 : 2;
                for (var dx = -radius; dx <= radius; dx++)
                    Block(column + dx, baseRow + dy, (42, 104, 56), 9);
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
