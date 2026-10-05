using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using STlauncher.Core.Loaders;
using STlauncher.Core.Modpacks;

namespace STlauncher.Core.Friends;

/// <summary>
/// The ways to reach a friend's server; each is null when the host does not offer it.
/// </summary>
/// <param name="Direct">"address:port" of the host's own connection, when its port is open to the internet.</param>
/// <param name="Relay">"host:port" of the relay the host is connected to. Only useful together with <paramref name="RoomKey"/>.</param>
/// <param name="RoomKey">The room on that relay.</param>
/// <param name="Public">A public address anyone can join, launcher or not. May have no port: such names carry it in DNS.</param>
public sealed record ServerInviteEndpoints(string? Direct, string? Relay, string? RoomKey, string? Public)
{
    public static ServerInviteEndpoints None { get; } = new(null, null, null, null);

    /// <summary>
    /// The UDP port of the server's voice chat (Simple Voice Chat), or null when it has
    /// none. On the relay way the guest's launcher listens on this port at 127.0.0.1,
    /// because that is where the mod in the game sends its voice.
    /// </summary>
    public int? VoicePort { get; init; }

    public bool HasRelay => Relay is not null && RoomKey is not null;

    public bool HasAny => Direct is not null || HasRelay || Public is not null;
}

/// <summary>
/// Everything a friend needs to join: what the server is, which build to play it with,
/// and how to reach it.
/// </summary>
/// <param name="BuildCode">The host's build as an "STB1." code, so the friend gets the same mods. Null for none.</param>
public sealed record ServerInvite(
    string Name,
    string GameVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    string HostNickname,
    string? BuildCode,
    ServerInviteEndpoints Endpoints);

/// <summary>
/// "STS1." followed by a deflated, base64url JSON, the same shape as a build code: one
/// unbroken ASCII token that survives a chat message. It is read as strictly as it is
/// written, because it arrives from somebody else: addresses must be plain host names or
/// IP literals, the room key must be a key, names lose control characters, and a code
/// that would unpack into megabytes is refused.
/// </summary>
public static class ServerInviteCode
{
    public const string Prefix = "STS1.";

    // A build of two hundred mods is about 25 KB of code; these leave room for more and
    // still stop a pasted "invite" from unpacking into something huge.
    private const int MaxCodeLength = 128 * 1024;
    private const int MaxJsonLength = 512 * 1024;

    private const int MaxNameLength = 64;
    private const int MaxNicknameLength = 32;
    private const int MaxVersionLength = 48;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,

        // Cyrillic as itself rather than six characters of escape per letter: the JSON
        // never meets HTML, and a server called "Наш мир" should not double the code.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Encode(ServerInvite invite)
    {
        if (invite is null)
        {
            throw new ArgumentNullException(nameof(invite));
        }

        var endpoints = Sanitize(invite.Endpoints);

        var dto = new Dto
        {
            N = NullIfEmpty(CleanText(invite.Name, MaxNameLength)),
            V = invite.GameVersion,
            L = invite.Loader == LoaderKind.Vanilla ? null : invite.Loader.ToString().ToLowerInvariant(),
            Lv = invite.LoaderVersion,
            H = NullIfEmpty(CleanText(invite.HostNickname, MaxNicknameLength)),
            B = BuildCodeBody(invite.BuildCode),
            D = endpoints.Direct,
            R = endpoints.Relay,
            K = endpoints.RoomKey,
            P = endpoints.Public,
            Vp = endpoints.VoicePort
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(dto, Json);

        using var buffer = new MemoryStream();

        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(json);
        }

        return Prefix + Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads an invite out of whatever was pasted: surrounding text and line breaks are ignored.</summary>
    public static bool TryDecode(string? text, out ServerInvite? invite)
    {
        invite = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var start = text.IndexOf(Prefix, StringComparison.Ordinal);

        if (start < 0)
        {
            return false;
        }

        var body = new StringBuilder();

        // The code is one unbroken token of base64url characters; the first thing that
        // is not one of them - a space, a line break, a word - ends it.
        foreach (var c in text.AsSpan(start + Prefix.Length))
        {
            if (!IsBase64Url(c))
            {
                break;
            }

            if (body.Length == MaxCodeLength)
            {
                return false;
            }

            body.Append(c);
        }

        try
        {
            var base64 = body.ToString().Replace('-', '+').Replace('_', '/').TrimEnd('=');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var packed = Convert.FromBase64String(base64);

            var json = Inflate(packed);

            if (json is null)
            {
                return false;
            }

            var dto = JsonSerializer.Deserialize<Dto>(json, Json);

            if (dto is null || !IsVersion(dto.V))
            {
                return false;
            }

            var endpoints = Sanitize(new ServerInviteEndpoints(dto.D, dto.R, dto.K, dto.P) { VoicePort = dto.Vp });

            // An invite that leads nowhere is not an invite.
            if (!endpoints.HasAny)
            {
                return false;
            }

            var loader = LoaderKind.Vanilla;

            if (!string.IsNullOrWhiteSpace(dto.L) && Enum.TryParse<LoaderKind>(dto.L, ignoreCase: true, out var parsed))
            {
                loader = parsed;
            }

            string? buildCode = null;

            if (BuildCodeBody(dto.B is null ? null : BuildCode.Prefix + dto.B) is { } inner &&
                BuildCode.TryDecode(BuildCode.Prefix + inner, out _))
            {
                buildCode = BuildCode.Prefix + inner;
            }

            invite = new ServerInvite(
                CleanText(dto.N, MaxNameLength),
                dto.V!.Trim(),
                loader,
                IsVersion(dto.Lv) ? dto.Lv!.Trim() : null,
                CleanText(dto.H, MaxNicknameLength),
                buildCode,
                endpoints);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Keeps the endpoints that are well formed and drops the rest.</summary>
    private static ServerInviteEndpoints Sanitize(ServerInviteEndpoints? endpoints)
    {
        if (endpoints is null)
        {
            return ServerInviteEndpoints.None;
        }

        // The direct address is dialled as it stands, so it must name its port.
        var direct = HostPort.TryParse(endpoints.Direct, out var directHost, out var directPort) && directPort is not null
            ? HostPort.Format(directHost, directPort)
            : null;

        var relay = RelayEndpoint.TryParse(endpoints.Relay, out var relayEndpoint) ? relayEndpoint!.ToString() : null;
        var roomKey = RelayKeys.IsKey(endpoints.RoomKey) ? endpoints.RoomKey!.ToLowerInvariant() : null;

        // A relay without a room, or a room without a relay, leads nowhere.
        if (relay is null || roomKey is null)
        {
            relay = null;
            roomKey = null;
        }

        var open = HostPort.TryNormalize(endpoints.Public, out var normalized) ? normalized : null;

        // The guest opens a port with this number on its own machine, so it has to be one.
        var voice = endpoints.VoicePort is >= 1 and <= 65535 ? endpoints.VoicePort : null;

        return new ServerInviteEndpoints(direct, relay, roomKey, open) { VoicePort = voice };
    }

    /// <summary>The part of a build code after its prefix, or null when the text holds no such code.</summary>
    private static string? BuildCodeBody(string? buildCode)
    {
        if (string.IsNullOrWhiteSpace(buildCode))
        {
            return null;
        }

        var start = buildCode.IndexOf(BuildCode.Prefix, StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        var end = start + BuildCode.Prefix.Length;

        while (end < buildCode.Length && IsBase64Url(buildCode[end]))
        {
            end++;
        }

        var body = buildCode[(start + BuildCode.Prefix.Length)..end];
        return body.Length == 0 ? null : body;
    }

    private static byte[]? Inflate(byte[] packed)
    {
        using var source = new MemoryStream(packed);
        using var deflate = new DeflateStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();

        var chunk = new byte[8192];

        while (true)
        {
            var read = deflate.Read(chunk, 0, chunk.Length);

            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > MaxJsonLength)
            {
                return null;
            }

            output.Write(chunk, 0, read);
        }
    }

    private static bool IsBase64Url(char c)
        => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '=';

    /// <summary>A version is a short run of letters, digits and the few marks versions use: "1.21.11", "24w14a", "0.16.14+build.1".</summary>
    private static bool IsVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        if (text.Length > MaxVersionLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_' or '+' or ' '))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A name as plain text: no control characters, no more than <paramref name="max"/> characters.</summary>
    private static string CleanText(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        foreach (var c in value.Trim())
        {
            if (char.IsControl(c))
            {
                continue;
            }

            if (text.Length == max)
            {
                break;
            }

            text.Append(c);
        }

        // Half of a surrogate pair left by the cut would not survive the JSON.
        if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
        {
            text.Length--;
        }

        return text.ToString().Trim();
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private sealed class Dto
    {
        [JsonPropertyName("n")] public string? N { get; set; }
        [JsonPropertyName("v")] public string? V { get; set; }
        [JsonPropertyName("l")] public string? L { get; set; }
        [JsonPropertyName("lv")] public string? Lv { get; set; }
        [JsonPropertyName("h")] public string? H { get; set; }
        [JsonPropertyName("b")] public string? B { get; set; }
        [JsonPropertyName("d")] public string? D { get; set; }
        [JsonPropertyName("r")] public string? R { get; set; }
        [JsonPropertyName("k")] public string? K { get; set; }
        [JsonPropertyName("p")] public string? P { get; set; }

        // Added in 0.5.7. Older launchers ignore it; older invites simply lack it.
        [JsonPropertyName("voicePort")] public int? Vp { get; set; }
    }
}

public enum SharedCodeKind
{
    /// <summary>The text holds no code this launcher reads.</summary>
    None,

    /// <summary>A build ("STB1.").</summary>
    Build,

    /// <summary>An invite to a friend's server ("STS1.").</summary>
    Server
}

/// <summary>
/// One place to hand a pasted text to: it says whether that is a build or an invite to a
/// server, and gives back whichever it was. An invite carries its build inside, so a
/// text is only ever one of the two.
/// </summary>
public static class SharedCode
{
    public static SharedCodeKind Read(string? text, out BuildCodePayload? build, out ServerInvite? server)
    {
        build = null;
        server = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return SharedCodeKind.None;
        }

        var serverAt = text.IndexOf(ServerInviteCode.Prefix, StringComparison.Ordinal);
        var buildAt = text.IndexOf(BuildCode.Prefix, StringComparison.Ordinal);

        // Whichever code comes first in the text is the one that was meant. If it turns
        // out to be broken, the other still gets its chance.
        if (serverAt >= 0 && (buildAt < 0 || serverAt < buildAt))
        {
            if (ServerInviteCode.TryDecode(text, out server))
            {
                return SharedCodeKind.Server;
            }

            return BuildCode.TryDecode(text, out build) ? SharedCodeKind.Build : SharedCodeKind.None;
        }

        if (BuildCode.TryDecode(text, out build))
        {
            return SharedCodeKind.Build;
        }

        return ServerInviteCode.TryDecode(text, out server) ? SharedCodeKind.Server : SharedCodeKind.None;
    }

    public static SharedCodeKind Detect(string? text) => Read(text, out _, out _);
}
