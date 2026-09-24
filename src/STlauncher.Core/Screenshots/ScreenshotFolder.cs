using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace STlauncher.Core.Screenshots;

/// <summary>A picture the game saved, newest first when listed.</summary>
public sealed record ScreenshotInfo(string Path, string FileName, DateTime TakenAt, long Size);

/// <summary>
/// The game's screenshots/ folder. File names carry the moment they were taken
/// ("2026-09-24_21.15.03.png"), which is more reliable than the file time after a
/// copy between machines.
/// </summary>
public static class ScreenshotFolder
{
    public const string FolderName = "screenshots";

    public static string Directory(string gameDirectory) => Path.Combine(gameDirectory, FolderName);

    public static IReadOnlyList<ScreenshotInfo> List(string gameDirectory)
    {
        var directory = Directory(gameDirectory);

        if (!System.IO.Directory.Exists(directory))
        {
            return Array.Empty<ScreenshotInfo>();
        }

        var result = new List<ScreenshotInfo>();

        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(path);
                result.Add(new ScreenshotInfo(path, info.Name, TakenAt(info), info.Length));
            }
            catch (Exception)
            {
                // A file that vanished between the listing and the stat is not worth an error.
            }
        }

        return result.OrderByDescending(s => s.TakenAt).ToList();
    }

    public static DateTime TakenAt(FileInfo file)
    {
        var stem = Path.GetFileNameWithoutExtension(file.Name);

        // "2026-09-24_21.15.03" and the "_2" suffix of a second shot in the same second.
        var core = stem.Length >= 19 ? stem[..19] : stem;

        if (DateTime.TryParseExact(core, "yyyy-MM-dd_HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        return file.LastWriteTime;
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
