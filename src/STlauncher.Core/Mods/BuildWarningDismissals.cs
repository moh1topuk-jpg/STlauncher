using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Mods;

/// <summary>
/// "Do not show for this mod": a soft warning about one jar, put away by the player. It
/// holds while the file is the same one - same size, same time written - and lapses by
/// itself when the mod is updated or replaced. Switching a mod off and on renames the
/// file, which is not a change: the name is kept without ".disabled".
/// </summary>
public sealed class DismissedBuildWarning
{
    [JsonPropertyName("file")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("written")]
    public long WrittenTicks { get; set; }
}

/// <summary>The size and write time of a mod file: what "the file changed" is measured by.</summary>
public readonly record struct ModFileStamp(long Size, long WrittenTicks)
{
    public static ModFileStamp? Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new ModFileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public static class BuildWarningDismissals
{
    /// <summary>"sodium.jar.disabled" and "sodium.jar" are the same mod.</summary>
    public static string Normalize(string fileName)
        => fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? fileName[..^".disabled".Length] : fileName;

    /// <summary>
    /// True when the player put this warning away and the file is still the one they saw.
    /// A warning that stops the game is never hidden this way.
    /// </summary>
    public static bool IsDismissed(IReadOnlyList<DismissedBuildWarning>? dismissed, BuildIssue issue, Func<string, ModFileStamp?> stampOf)
    {
        if (dismissed is null || dismissed.Count == 0 || issue.IsBlocking)
        {
            return false;
        }

        var name = Normalize(issue.FileName);
        var entry = dismissed.FirstOrDefault(d => string.Equals(d.FileName, name, StringComparison.OrdinalIgnoreCase));

        if (entry is null || stampOf(issue.FileName) is not { } stamp)
        {
            return false;
        }

        return stamp.Size == entry.Size && stamp.WrittenTicks == entry.WrittenTicks;
    }

    /// <summary>Records the dismissal for the file as it is now; replaces an older one for the same name.</summary>
    public static bool Dismiss(List<DismissedBuildWarning> dismissed, string fileName, ModFileStamp stamp)
    {
        var name = Normalize(fileName);
        dismissed.RemoveAll(d => string.Equals(d.FileName, name, StringComparison.OrdinalIgnoreCase));
        dismissed.Add(new DismissedBuildWarning { FileName = name, Size = stamp.Size, WrittenTicks = stamp.WrittenTicks });
        return true;
    }

    /// <summary>
    /// Drops the dismissals whose file is gone or changed, so an updated mod is looked at
    /// again. Returns how many went.
    /// </summary>
    public static int Prune(List<DismissedBuildWarning> dismissed, Func<string, ModFileStamp?> stampOfName)
    {
        return dismissed.RemoveAll(d =>
        {
            var stamp = stampOfName(d.FileName) ?? stampOfName(d.FileName + ".disabled");
            return stamp is null || stamp.Value.Size != d.Size || stamp.Value.WrittenTicks != d.WrittenTicks;
        });
    }
}
