using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Packs;

/// <summary>What a resource pack says about itself, read from the archive or folder.</summary>
/// <param name="Format">pack_format from pack.mcmeta, or null when the file has none.</param>
/// <param name="MinFormat">Lower end of supported_formats, when the pack declares a range.</param>
/// <param name="MaxFormat">Upper end of supported_formats, when the pack declares a range.</param>
/// <param name="Icon">pack.png as bytes, or null.</param>
public sealed record ResourcePackInfo(
    string Name,
    string FileName,
    string Path,
    bool IsFolder,
    long Size,
    string Description,
    int? Format,
    int? MinFormat,
    int? MaxFormat,
    byte[]? Icon)
{
    /// <summary>True when the pack declares support for the given format, by exact match or range.</summary>
    public bool Supports(int format)
    {
        if (MinFormat is { } min && MaxFormat is { } max)
        {
            return format >= min && format <= max;
        }

        return Format == format;
    }
}

/// <summary>
/// Reads pack.mcmeta and pack.png out of a resource pack. The game does the same thing
/// in its own menu; doing it here means the launcher can show a pack before the game
/// is started, with the description its author wrote.
/// </summary>
public static class ResourcePackInspector
{
    private static readonly Regex FormattingCodes = new("§.", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Everything in the folder that looks like a pack: zips and unpacked folders with pack.mcmeta.</summary>
    public static IReadOnlyList<ResourcePackInfo> ListPacks(string packsDirectory)
    {
        if (!Directory.Exists(packsDirectory))
        {
            return Array.Empty<ResourcePackInfo>();
        }

        var result = new List<ResourcePackInfo>();

        foreach (var entry in Directory.EnumerateFileSystemEntries(packsDirectory))
        {
            var name = Path.GetFileName(entry);

            if (name.StartsWith('.'))
            {
                continue;
            }

            if (Directory.Exists(entry) || entry.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(Inspect(entry));
            }
        }

        return result
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ResourcePackInfo Inspect(string path)
    {
        var fileName = Path.GetFileName(path);
        var isFolder = Directory.Exists(path);
        var displayName = isFolder ? fileName : Path.GetFileNameWithoutExtension(fileName);

        string? mcmeta = null;
        byte[]? icon = null;
        long size = 0;

        try
        {
            if (isFolder)
            {
                var metaPath = Path.Combine(path, "pack.mcmeta");
                var iconPath = Path.Combine(path, "pack.png");
                mcmeta = File.Exists(metaPath) ? File.ReadAllText(metaPath) : null;
                icon = File.Exists(iconPath) ? File.ReadAllBytes(iconPath) : null;
                size = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            else
            {
                size = new FileInfo(path).Length;

                using var archive = ZipFile.OpenRead(path);

                // Most packs sit at the root; some are zipped with one folder around them.
                var meta = archive.GetEntry("pack.mcmeta")
                           ?? archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/pack.mcmeta", StringComparison.OrdinalIgnoreCase) && e.FullName.Count(c => c == '/') == 1);
                var png = archive.GetEntry("pack.png")
                          ?? archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/pack.png", StringComparison.OrdinalIgnoreCase) && e.FullName.Count(c => c == '/') == 1);

                if (meta is not null)
                {
                    using var reader = new StreamReader(meta.Open(), Encoding.UTF8);
                    mcmeta = reader.ReadToEnd();
                }

                if (png is not null && png.Length < 8 * 1024 * 1024)
                {
                    using var stream = png.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    icon = buffer.ToArray();
                }
            }
        }
        catch (Exception)
        {
            // A damaged archive still gets a row, with what could be read.
        }

        var (description, format, min, max) = ParseMeta(mcmeta);

        return new ResourcePackInfo(displayName, fileName, path, isFolder, size, description, format, min, max, icon);
    }

    /// <summary>
    /// Pulls the description and formats out of pack.mcmeta. The description is a text
    /// component: a plain string, an object with "text" and "extra", or an array of
    /// those - and often carries § colour codes, which mean nothing outside the game.
    /// </summary>
    public static (string Description, int? Format, int? Min, int? Max) ParseMeta(string? mcmeta)
    {
        if (string.IsNullOrWhiteSpace(mcmeta))
        {
            return (string.Empty, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(mcmeta, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (!doc.RootElement.TryGetProperty("pack", out var pack))
            {
                return (string.Empty, null, null, null);
            }

            int? format = pack.TryGetProperty("pack_format", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : null;
            int? min = null, max = null;

            if (pack.TryGetProperty("supported_formats", out var supported))
            {
                (min, max) = ReadRange(supported);
            }

            var description = pack.TryGetProperty("description", out var d) ? ComponentText(d) : string.Empty;

            return (Clean(description), format, min, max);
        }
        catch (Exception)
        {
            return (string.Empty, null, null, null);
        }
    }

    private static (int? Min, int? Max) ReadRange(JsonElement supported)
    {
        switch (supported.ValueKind)
        {
            case JsonValueKind.Number:
                var single = supported.GetInt32();
                return (single, single);

            case JsonValueKind.Array when supported.GetArrayLength() == 2:
                return (supported[0].GetInt32(), supported[1].GetInt32());

            case JsonValueKind.Object:
                var min = supported.TryGetProperty("min_inclusive", out var lo) ? lo.GetInt32() : (int?)null;
                var max = supported.TryGetProperty("max_inclusive", out var hi) ? hi.GetInt32() : (int?)null;
                return (min, max);

            default:
                return (null, null);
        }
    }

    private static string ComponentText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;

            case JsonValueKind.Array:
                return string.Concat(element.EnumerateArray().Select(ComponentText));

            case JsonValueKind.Object:
                var text = element.TryGetProperty("text", out var t) ? ComponentText(t) : string.Empty;
                var extra = element.TryGetProperty("extra", out var e) ? ComponentText(e) : string.Empty;
                return text + extra;

            default:
                return string.Empty;
        }
    }

    private static string Clean(string text)
    {
        var plain = FormattingCodes.Replace(text, string.Empty).Replace("\r", string.Empty).Replace('\n', ' ').Trim();
        return Regex.Replace(plain, @"\s{2,}", " ");
    }
}
