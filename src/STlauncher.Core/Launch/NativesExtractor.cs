using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace STlauncher.Core.Launch;

public static class NativesExtractor
{
    public static int Extract(NativeExtraction extraction)
    {
        if (!File.Exists(extraction.ArchivePath))
        {
            return 0;
        }

        Directory.CreateDirectory(extraction.DestinationDirectory);
        var destinationRoot = Path.GetFullPath(extraction.DestinationDirectory);
        var extracted = 0;

        using var archive = ZipFile.OpenRead(extraction.ArchivePath);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsExcluded(entry.FullName, extraction.Excludes))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));

            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Blocked archive entry outside of destination: {entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            extracted++;
        }

        return extracted;
    }

    private static bool IsExcluded(string entryName, IReadOnlyList<string> excludes)
    {
        foreach (var exclude in excludes)
        {
            if (entryName.StartsWith(exclude.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}