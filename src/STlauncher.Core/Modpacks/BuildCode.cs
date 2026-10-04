using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Modpacks;

/// <summary>One file of a shared build: where it lives in the build and where to get it.</summary>
public sealed record BuildCodeFile(string Path, string Url, string Sha1, long Size);

/// <summary>
/// A build as a line of text: the game version, the loader, and every file Modrinth can
/// hand out, by address and hash. Files nobody can download are listed by name so the
/// other side knows what did not travel.
/// </summary>
public sealed record BuildCodePayload(
    string Name,
    string GameVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    IReadOnlyList<BuildCodeFile> Files,
    IReadOnlyList<string> Missing);

/// <summary>
/// "STB1." followed by a deflated, base64url JSON. A build of two dozen mods is about
/// two kilobytes: it fits in a chat message, which is the whole point - no file, no
/// server, one paste. Modrinth's own download addresses are shortened, since they are
/// most of the text.
/// </summary>
public static class BuildCode
{
    public const string Prefix = "STB1.";

    private const string ModrinthCdn = "https://cdn.modrinth.com/data/";

    private const string CurseForgeCdn = "https://edge.forgecdn.net/files/";

    /// <summary>
    /// Where a code may send the launcher for a file: the hosts a .mrpack may name, and
    /// CurseForge's CDN. The CDN is safe here for the same reason Modrinth's is - it
    /// serves only what was uploaded to the site, and every file is checked against the
    /// hash in the code. A .mrpack must not name it (see <see cref="ModpackWriter"/>),
    /// but a code is this launcher's own format.
    /// </summary>
    public static bool IsAllowedDownload(string? url)
        => ModpackWriter.IsAllowedDownload(url) || Mods.CurseForgeClient.IsCdnUrl(url);

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static string Encode(BuildCodePayload payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var dto = new Dto
        {
            N = payload.Name,
            V = payload.GameVersion,
            L = payload.Loader == LoaderKind.Vanilla ? null : payload.Loader.ToString().ToLowerInvariant(),
            Lv = payload.LoaderVersion,
            F = payload.Files.Select(f => new[] { f.Path, Shorten(f.Url), f.Sha1, f.Size.ToString() }).ToList(),
            X = payload.Missing.Count == 0 ? null : payload.Missing.ToList()
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(dto, Json);

        using var buffer = new MemoryStream();

        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(json);
        }

        return Prefix + Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads a code out of whatever was pasted: surrounding text and line breaks are ignored.</summary>
    public static bool TryDecode(string? text, out BuildCodePayload? payload)
    {
        payload = null;

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
        foreach (var c in text[(start + Prefix.Length)..])
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '=')
            {
                body.Append(c);
            }
            else
            {
                break;
            }
        }

        try
        {
            var base64 = body.ToString().Replace('-', '+').Replace('_', '/').TrimEnd('=');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var packed = Convert.FromBase64String(base64);

            using var source = new MemoryStream(packed);
            using var deflate = new DeflateStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);

            var dto = JsonSerializer.Deserialize<Dto>(output.ToArray(), Json);

            if (dto is null || string.IsNullOrWhiteSpace(dto.V))
            {
                return false;
            }

            var files = new List<BuildCodeFile>();

            foreach (var entry in dto.F ?? new List<string[]>())
            {
                if (entry.Length < 4 || !RelativePath.IsSafe(entry[0]) || !long.TryParse(entry[3], out var size))
                {
                    continue;
                }

                var url = Expand(entry[1]);

                if (!IsAllowedDownload(url))
                {
                    continue;
                }

                files.Add(new BuildCodeFile(entry[0], url, entry[2], size));
            }

            var loader = LoaderKind.Vanilla;

            if (!string.IsNullOrWhiteSpace(dto.L) && Enum.TryParse<LoaderKind>(dto.L, ignoreCase: true, out var parsed))
            {
                loader = parsed;
            }

            payload = new BuildCodePayload(
                string.IsNullOrWhiteSpace(dto.N) ? "Shared build" : dto.N!,
                dto.V!,
                loader,
                string.IsNullOrWhiteSpace(dto.Lv) ? null : dto.Lv,
                files,
                dto.X ?? new List<string>());

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Shorten(string url)
        => url.StartsWith(ModrinthCdn, StringComparison.OrdinalIgnoreCase) ? "m:" + url[ModrinthCdn.Length..]
            : url.StartsWith(CurseForgeCdn, StringComparison.OrdinalIgnoreCase) ? "c:" + url[CurseForgeCdn.Length..]
            : url;

    private static string Expand(string url)
        => url.StartsWith("m:", StringComparison.Ordinal) ? ModrinthCdn + url[2..]
            : url.StartsWith("c:", StringComparison.Ordinal) ? CurseForgeCdn + url[2..]
            : url;

    private sealed class Dto
    {
        [JsonPropertyName("n")] public string? N { get; set; }
        [JsonPropertyName("v")] public string? V { get; set; }
        [JsonPropertyName("l")] public string? L { get; set; }
        [JsonPropertyName("lv")] public string? Lv { get; set; }
        [JsonPropertyName("f")] public List<string[]>? F { get; set; }
        [JsonPropertyName("x")] public List<string>? X { get; set; }
    }
}
