using System.IO;
using System.IO.Compression;
using System.Text;

namespace Zyrox.Services;

public record WorldInfo(string Folder, string Path, string Name, string? GameVersion, DateTime LastPlayedUtc, string? IconPath);

public static class WorldService
{
    private const byte TagEnd = 0;
    private const byte TagByte = 1;
    private const byte TagShort = 2;
    private const byte TagInt = 3;
    private const byte TagLong = 4;
    private const byte TagFloat = 5;
    private const byte TagDouble = 6;
    private const byte TagByteArray = 7;
    private const byte TagString = 8;
    private const byte TagList = 9;
    private const byte TagCompound = 10;
    private const byte TagIntArray = 11;
    private const byte TagLongArray = 12;

    public static List<WorldInfo> GetWorlds(string profileDir)
    {
        var saves = Path.Combine(profileDir, "saves");
        if (!Directory.Exists(saves))
            return [];
        return Directory.EnumerateDirectories(saves)
            .Where(folder => File.Exists(Path.Combine(folder, "level.dat")))
            .Select(ReadWorld)
            .OrderByDescending(world => world.LastPlayedUtc)
            .ToList();
    }

    public static string Backup(WorldInfo world, string profileDir)
    {
        var folder = Path.Combine(profileDir, "zyrox-backups");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"{world.Folder}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var temp = target + ".tmp";
        try
        {
            ZipFile.CreateFromDirectory(world.Path, temp, CompressionLevel.Fastest, includeBaseDirectory: true);
            File.Move(temp, target);
        }
        finally
        {
            File.Delete(temp);
        }
        return target;
    }

    private static WorldInfo ReadWorld(string folder)
    {
        var name = Path.GetFileName(folder);
        string? version = null;
        var lastPlayed = Directory.GetLastWriteTimeUtc(folder);
        try
        {
            var values = new Dictionary<string, object>();
            using var file = File.OpenRead(Path.Combine(folder, "level.dat"));
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new BinaryReader(new BufferedStream(gzip));
            if (reader.ReadByte() == TagCompound)
            {
                ReadString(reader);
                ReadCompound(reader, "", values);
            }

            if (values.TryGetValue("Data.LevelName", out var levelName) && levelName is string { Length: > 0 } text)
                name = text;
            if (values.TryGetValue("Data.Version.Name", out var versionName))
                version = versionName as string;
            if (values.TryGetValue("Data.LastPlayed", out var played) && played is long milliseconds && milliseconds > 0)
                lastPlayed = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
        }

        var icon = Path.Combine(folder, "icon.png");
        return new WorldInfo(Path.GetFileName(folder), folder, name, version, lastPlayed, File.Exists(icon) ? icon : null);
    }

    private static void ReadCompound(BinaryReader reader, string prefix, Dictionary<string, object> values)
    {
        while (true)
        {
            var type = reader.ReadByte();
            if (type == TagEnd)
                return;
            var key = prefix + ReadString(reader);
            if (type == TagCompound)
                ReadCompound(reader, key + ".", values);
            else if (type == TagString)
                values[key] = ReadString(reader);
            else if (type == TagLong)
                values[key] = ReadBigEndianLong(reader);
            else
                SkipPayload(reader, type);
        }
    }

    private static void SkipPayload(BinaryReader reader, byte type)
    {
        switch (type)
        {
            case TagByte: Skip(reader, 1); break;
            case TagShort: Skip(reader, 2); break;
            case TagInt or TagFloat: Skip(reader, 4); break;
            case TagLong or TagDouble: Skip(reader, 8); break;
            case TagByteArray: Skip(reader, ReadBigEndianInt(reader)); break;
            case TagIntArray: Skip(reader, ReadBigEndianInt(reader) * 4L); break;
            case TagLongArray: Skip(reader, ReadBigEndianInt(reader) * 8L); break;
            case TagString: ReadString(reader); break;
            case TagList:
                var itemType = reader.ReadByte();
                var count = ReadBigEndianInt(reader);
                for (var i = 0; i < count; i++)
                    SkipPayload(reader, itemType);
                break;
            case TagCompound:
                while (true)
                {
                    var inner = reader.ReadByte();
                    if (inner == TagEnd)
                        break;
                    ReadString(reader);
                    SkipPayload(reader, inner);
                }
                break;
            default:
                throw new InvalidDataException("Unknown NBT type.");
        }
    }

    private static void Skip(BinaryReader reader, long bytes)
    {
        if (bytes < 0)
            throw new InvalidDataException("Invalid NBT length.");
        for (long i = 0; i < bytes; i++)
            reader.ReadByte();
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = (reader.ReadByte() << 8) | reader.ReadByte();
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static int ReadBigEndianInt(BinaryReader reader) =>
        (reader.ReadByte() << 24) | (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();

    private static long ReadBigEndianLong(BinaryReader reader) =>
        ((long)ReadBigEndianInt(reader) << 32) | (uint)ReadBigEndianInt(reader);
}
