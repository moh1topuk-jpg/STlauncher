using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace STlauncher.Core.Packs;

/// <summary>
/// The resource pack format a game version expects, read from the client jar's own
/// version.json (<c>"pack_version": {"resource": 46}</c>). A table of versions to
/// formats would be wrong the week a new snapshot ships; the jar is never wrong.
/// </summary>
public static class GamePackFormat
{
    /// <summary>The resource pack format of the given client jar, or null when it cannot be read.</summary>
    public static int? ReadResourceFormat(string clientJarPath)
    {
        try
        {
            if (!File.Exists(clientJarPath))
            {
                return null;
            }

            using var archive = ZipFile.OpenRead(clientJarPath);
            var entry = archive.GetEntry("version.json");

            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("pack_version", out var pack))
            {
                return null;
            }

            // Old versions carry one number; newer ones split resource and data.
            return pack.ValueKind switch
            {
                JsonValueKind.Number => pack.GetInt32(),
                JsonValueKind.Object when pack.TryGetProperty("resource", out var r) && r.ValueKind == JsonValueKind.Number => r.GetInt32(),
                JsonValueKind.Object when pack.TryGetProperty("resource", out var r2) && r2.ValueKind == JsonValueKind.Object && r2.TryGetProperty("major", out var major) => major.GetInt32(),
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
