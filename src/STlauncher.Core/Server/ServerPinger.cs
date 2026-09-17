using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Server;

public sealed record ServerStatus(
    string Motd,
    int Online,
    int Max,
    string? VersionName,
    int ProtocolVersion,
    byte[]? Favicon);

/// <summary>
/// Minecraft Server List Ping. Connects over TCP and reads the status the server already
/// answers to the game client, which is where the icon (favicon) and MOTD come from.
/// </summary>
public static class ServerPinger
{
    public const int DefaultPort = 25565;

    public static async Task<ServerStatus?> PingAsync(
        string host,
        int port = DefaultPort,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var limit = timeout ?? TimeSpan.FromSeconds(5);

        // ReadTimeout/WriteTimeout have no effect on asynchronous socket operations, so the
        // old code only ever guarded the connect: a server that completed the handshake and
        // then went silent hung the caller forever. One linked token covers every step.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(limit);
        var token = linked.Token;

        try
        {
            using var client = new TcpClient();

            await client.ConnectAsync(host, port, token).ConfigureAwait(false);

            if (!client.Connected)
            {
                return null;
            }

            using var stream = client.GetStream();

            await WriteHandshakeAsync(stream, host, port, token).ConfigureAwait(false);

            // Status request: a single packet with no payload.
            await stream.WriteAsync(new byte[] { 0x01, 0x00 }, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);

            var json = await ReadStatusJsonAsync(stream, token).ConfigureAwait(false);

            return json is null ? null : Parse(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task WriteHandshakeAsync(
        Stream stream,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();

        WriteVarInt(payload, 0x00);
        WriteVarInt(payload, -1); // "unknown" protocol: the server replies with its own version
        WriteString(payload, host);

        var portBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
        payload.Write(portBytes);

        WriteVarInt(payload, 1); // next state: status

        var body = payload.ToArray();

        using var packet = new MemoryStream();
        WriteVarInt(packet, body.Length);
        packet.Write(body);

        await stream.WriteAsync(packet.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadStatusJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = await ReadVarIntAsync(stream, cancellationToken).ConfigureAwait(false);

        if (length <= 0 || length > 8 * 1024 * 1024)
        {
            return null;
        }

        var buffer = new byte[length];
        var read = 0;

        while (read < length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken)
                .ConfigureAwait(false);

            if (chunk == 0)
            {
                return null;
            }

            read += chunk;
        }

        using var memory = new MemoryStream(buffer);
        _ = await ReadVarIntAsync(memory, cancellationToken).ConfigureAwait(false); // packet id
        _ = await ReadVarIntAsync(memory, cancellationToken).ConfigureAwait(false); // string length

        using var reader = new StreamReader(memory, Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    public static ServerStatus Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var motd = root.TryGetProperty("description", out var description)
            ? Flatten(description).Trim()
            : string.Empty;

        var online = 0;
        var max = 0;

        if (root.TryGetProperty("players", out var players))
        {
            if (players.TryGetProperty("online", out var onlineValue) && onlineValue.TryGetInt32(out var o))
            {
                online = o;
            }

            if (players.TryGetProperty("max", out var maxValue) && maxValue.TryGetInt32(out var m))
            {
                max = m;
            }
        }

        string? versionName = null;
        var protocol = 0;

        if (root.TryGetProperty("version", out var version))
        {
            if (version.TryGetProperty("name", out var name))
            {
                versionName = name.GetString();
            }

            if (version.TryGetProperty("protocol", out var protocolValue) && protocolValue.TryGetInt32(out var p))
            {
                protocol = p;
            }
        }

        byte[]? favicon = null;

        if (root.TryGetProperty("favicon", out var faviconValue) &&
            faviconValue.ValueKind == JsonValueKind.String)
        {
            favicon = DecodeFavicon(faviconValue.GetString());
        }

        return new ServerStatus(motd, online, max, versionName, protocol, favicon);
    }

    public static byte[]? DecodeFavicon(string? value)
    {
        const string prefix = "data:image/png;base64,";

        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(value[prefix.Length..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>MOTD can be a plain string or a chat component tree.</summary>
    public static string Flatten(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return StripFormatting(element.GetString() ?? string.Empty);

            case JsonValueKind.Array:
                var list = new StringBuilder();
                foreach (var child in element.EnumerateArray())
                {
                    list.Append(Flatten(child));
                }

                return list.ToString();

            case JsonValueKind.Object:
                var text = new StringBuilder();

                if (element.TryGetProperty("text", out var inner))
                {
                    text.Append(Flatten(inner));
                }

                if (element.TryGetProperty("extra", out var extra))
                {
                    text.Append(Flatten(extra));
                }

                return text.ToString();

            default:
                return string.Empty;
        }
    }

    /// <summary>Removes legacy colour codes such as "§a".</summary>
    public static string StripFormatting(string value)
    {
        var result = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\u00a7' && i + 1 < value.Length)
            {
                i++;
                continue;
            }

            result.Append(value[i]);
        }

        return result.ToString();
    }

    public static void WriteVarInt(Stream stream, int value)
    {
        var unsigned = (uint)value;

        do
        {
            var current = (byte)(unsigned & 0x7F);
            unsigned >>= 7;

            if (unsigned != 0)
            {
                current |= 0x80;
            }

            stream.WriteByte(current);
        }
        while (unsigned != 0);
    }

    public static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var result = 0;
        var shift = 0;
        var buffer = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return -1;
            }

            result |= (buffer[0] & 0x7F) << shift;

            if ((buffer[0] & 0x80) == 0)
            {
                return result;
            }

            shift += 7;

            if (shift >= 35)
            {
                return -1;
            }
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }
}