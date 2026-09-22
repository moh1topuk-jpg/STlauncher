using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using STlauncher.Core.Launch;

namespace STlauncher.Core.Packs;

/// <summary>
/// The game's list of enabled resource packs, as options.txt keeps it. The line reads
/// <c>resourcePacks:["vanilla","file/Faithful.zip","fabric"]</c>: built-in entries by
/// name, files as <c>file/&lt;name&gt;</c>, and LATER entries win over earlier ones - the
/// opposite of how the in-game menu draws the same list, where the top pack wins.
/// This class speaks the menu's language: a list of file names, highest priority first.
/// </summary>
public static class ResourcePackOrder
{
    public const string Key = "resourcePacks";
    private const string FilePrefix = "file/";

    /// <summary>Enabled pack file names, highest priority first. Empty when none or no options.txt.</summary>
    public static IReadOnlyList<string> ReadEnabled(string gameDirectory)
    {
        var line = ReadLine(gameDirectory);

        if (line is null)
        {
            return Array.Empty<string>();
        }

        return ParseEntries(line)
            .Where(e => e.StartsWith(FilePrefix, StringComparison.Ordinal))
            .Select(e => e[FilePrefix.Length..])
            .Reverse()
            .ToList();
    }

    /// <summary>
    /// Writes the enabled file packs in the given priority order, keeping every built-in
    /// or mod-provided entry ("vanilla", "fabric", "mod/…") exactly where it was.
    /// </summary>
    public static void WriteEnabled(string gameDirectory, IReadOnlyList<string> fileNamesByPriority)
    {
        Directory.CreateDirectory(gameDirectory);

        var path = Path.Combine(gameDirectory, GameOptions.FileName);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var index = lines.FindIndex(l => l.StartsWith(Key + ":", StringComparison.Ordinal));

        var existing = index >= 0 ? ParseEntries(lines[index]) : new List<string> { "vanilla" };
        var kept = existing.Where(e => !e.StartsWith(FilePrefix, StringComparison.Ordinal)).ToList();

        if (kept.Count == 0)
        {
            kept.Add("vanilla");
        }

        // Files go after the built-ins so they override them; reversed, because the
        // last entry in the file is the one the game applies last, on top.
        var entries = kept.Concat(fileNamesByPriority.Reverse().Select(n => FilePrefix + n)).ToList();
        var line = Key + ":" + JsonSerializer.Serialize(entries);

        if (index >= 0)
        {
            lines[index] = line;
        }
        else
        {
            lines.Add(line);
        }

        AtomicFile.WriteAllLines(path, lines);
    }

    private static string? ReadLine(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, GameOptions.FileName);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadLines(path).FirstOrDefault(l => l.StartsWith(Key + ":", StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The JSON array after the colon, or an empty list for anything unreadable.</summary>
    public static List<string> ParseEntries(string line)
    {
        var colon = line.IndexOf(':');
        var json = colon < 0 ? line : line[(colon + 1)..];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }
}
