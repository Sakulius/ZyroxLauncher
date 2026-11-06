using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using DnsClient;
using DnsClient.Protocol;

namespace Zyrox.Services;

public record ServerStatus(int Online, int Max, long LatencyMs, string Motd, BitmapSource? Icon);

public static partial class ServerService
{
    private const int DefaultPort = 25565;
    private const int MaxPacketBytes = 1024 * 1024;

    private static readonly LookupClient Dns = new();

    public static (string Host, int Port) ParseAddress(string address)
    {
        address = address.Trim();
        var colon = address.LastIndexOf(':');
        return colon > 0 && int.TryParse(address[(colon + 1)..], out var port) && port is > 0 and <= 65535
            ? (address[..colon], port)
            : (address, DefaultPort);
    }

    public static async Task<ServerStatus> PingAsync(string address, CancellationToken cancellationToken)
    {
        var (host, port) = ParseAddress(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var token = timeout.Token;

        var (connectHost, connectPort) = await ResolveSrvAsync(address, host, port, token);
        using var client = new TcpClient();
        await client.ConnectAsync(connectHost, connectPort, token);
        var stream = client.GetStream();

        var handshake = new MemoryStream();
        WriteVarInt(handshake, 0);
        WriteVarInt(handshake, -1);
        var hostBytes = Encoding.UTF8.GetBytes(host);
        WriteVarInt(handshake, hostBytes.Length);
        handshake.Write(hostBytes);
        handshake.WriteByte((byte)(port >> 8));
        handshake.WriteByte((byte)port);
        WriteVarInt(handshake, 1);
        await SendPacketAsync(stream, handshake.ToArray(), token);
        await SendPacketAsync(stream, [0], token);

        var clock = Stopwatch.StartNew();
        await ReadVarIntAsync(stream, token);
        var latency = clock.ElapsedMilliseconds;
        await ReadVarIntAsync(stream, token);
        var length = await ReadVarIntAsync(stream, token);
        if (length is <= 0 or > MaxPacketBytes)
            throw new InvalidDataException("Invalid server response.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        var players = root.GetProperty("players");
        return new ServerStatus(
            players.GetProperty("online").GetInt32(),
            players.GetProperty("max").GetInt32(),
            latency,
            root.TryGetProperty("description", out var description) ? FlattenMotd(description) : "",
            root.TryGetProperty("favicon", out var favicon) ? DecodeIcon(favicon.GetString()) : null);
    }

    private static async Task<(string Host, int Port)> ResolveSrvAsync(string address, string host, int port, CancellationToken cancellationToken)
    {
        if (address.Contains(':'))
            return (host, port);
        try
        {
            var result = await Dns.QueryAsync($"_minecraft._tcp.{host}", QueryType.SRV, cancellationToken: cancellationToken);
            var record = result.Answers.SrvRecords().OrderBy(r => r.Priority).FirstOrDefault();
            if (record != null)
                return (record.Target.Value.TrimEnd('.'), record.Port);
        }
        catch (DnsResponseException)
        {
        }
        return (host, port);
    }

    private static string FlattenMotd(JsonElement element)
    {
        var builder = new StringBuilder();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String)
            {
                builder.Append(e.GetString());
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray())
                    Walk(item);
            }
            else if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("text", out var text))
                    Walk(text);
                if (e.TryGetProperty("extra", out var extra))
                    Walk(extra);
            }
        }
        Walk(element);
        return Whitespace().Replace(ColorCode().Replace(builder.ToString(), ""), " ").Trim();
    }

    private static BitmapSource? DecodeIcon(string? favicon)
    {
        const string prefix = "data:image/png;base64,";
        if (favicon == null || !favicon.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(favicon[prefix.Length..]));
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task SendPacketAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.ToArray(), token);
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var remaining = (uint)value;
        while (remaining >= 0x80)
        {
            stream.WriteByte((byte)(remaining | 0x80));
            remaining >>= 7;
        }
        stream.WriteByte((byte)remaining);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken token)
    {
        var result = 0;
        var buffer = new byte[1];
        for (var shift = 0; shift < 35; shift += 7)
        {
            await stream.ReadExactlyAsync(buffer, token);
            result |= (buffer[0] & 0x7F) << shift;
            if ((buffer[0] & 0x80) == 0)
                return result;
        }
        throw new InvalidDataException("Invalid server response.");
    }

    [GeneratedRegex("§.")]
    private static partial Regex ColorCode();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

