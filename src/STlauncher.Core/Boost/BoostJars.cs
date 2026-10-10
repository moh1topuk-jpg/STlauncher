using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Boost;

/// <summary>What switching off did to the jars the switch had brought.</summary>
/// <param name="SwitchedOff">Renamed to .disabled: still in the folder, one click from coming back.</param>
/// <param name="LeftToPlayer">Gone, replaced by another file, or not the file that was installed: the player's now.</param>
/// <param name="LeftNeeded">Still required by a mod the player added since; taking it away would stop the game.</param>
public sealed record BoostJarsRevert(
    IReadOnlyList<BoostJarRecord> SwitchedOff,
    IReadOnlyList<BoostJarRecord> LeftToPlayer,
    IReadOnlyList<(BoostJarRecord Jar, string NeededBy)> LeftNeeded);

/// <summary>
/// The files side of the switch: which jars are still the ones it installed, switching
/// them off and back on. A file is never deleted here - a jar taken out of the game is
/// renamed to .disabled, the same thing the mod list's own switch does.
/// </summary>
public static class BoostJars
{
    private const string DisabledSuffix = ".disabled";

    /// <summary>The record of a jar just installed, with the hash that later says "this is still that file".</summary>
    public static BoostJarRecord Describe(string gameDirectory, string fileName, string? slug, string? title, bool dependency)
        => new()
        {
            FileName = fileName,
            Slug = slug,
            Title = title,
            Dependency = dependency,
            Sha1 = ModManager.TryComputeSha1(Path.Combine(ModManager.ModsDirectory(gameDirectory), fileName))
        };

    /// <summary>
    /// Switches off the jars that are still exactly what the switch installed. One the
    /// player has updated, replaced under the same name, removed or already switched off
    /// is not touched; neither is one that another mod in the build now requires.
    /// </summary>
    public static BoostJarsRevert SwitchOff(
        string gameDirectory,
        IReadOnlyList<BoostJarRecord> installed,
        LoaderKind loader,
        string? gameVersion,
        ModMetadataCache? cache = null)
    {
        var directory = ModManager.ModsDirectory(gameDirectory);
        var ours = new List<BoostJarRecord>();
        var left = new List<BoostJarRecord>();
        var needed = new List<(BoostJarRecord, string)>();

        foreach (var jar in installed)
        {
            (IsUnchanged(Path.Combine(directory, jar.FileName), jar) ? ours : left).Add(jar);
        }

        if (ours.Count > 0)
        {
            var jars = BuildChecker.ReadJars(gameDirectory, cache);

            // A jar that stays may be what keeps another of ours needed, so the question
            // is asked again until nothing changes.
            for (var moved = true; moved;)
            {
                moved = false;

                var going = ours.Select(o => o.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var staying = jars.Where(j => j.Enabled && !going.Contains(j.FileName)).ToList();

                foreach (var jar in ours.ToList())
                {
                    var file = jars.FirstOrDefault(j => string.Equals(j.FileName, jar.FileName, StringComparison.OrdinalIgnoreCase));

                    if (file is null)
                    {
                        continue;
                    }

                    var ids = BoostMods.Ids(file, loader, gameVersion).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var dependent = staying
                        .Select(s => ModMetadataReader.SectionFor(s.Sections, loader, gameVersion))
                        .FirstOrDefault(s => s is not null && s.Dependencies.Any(d => d.Required && ids.Contains(d.Id)));

                    if (dependent is not null)
                    {
                        ours.Remove(jar);
                        needed.Add((jar, dependent.Name));
                        moved = true;
                        break;
                    }
                }
            }
        }

        var switchedOff = new List<BoostJarRecord>();

        foreach (var jar in ours)
        {
            var path = Path.Combine(directory, jar.FileName);

            try
            {
                // A switched-off file of this name is already there, and it is not ours
                // to overwrite.
                if (File.Exists(path + DisabledSuffix))
                {
                    left.Add(jar);
                    continue;
                }

                File.Move(path, path + DisabledSuffix);
                switchedOff.Add(jar);
            }
            catch (Exception)
            {
                left.Add(jar);
            }
        }

        return new BoostJarsRevert(switchedOff, left, needed);
    }

    /// <summary>The parked jars that are still in the folder, switched off and unchanged, with nothing in the way of switching them on.</summary>
    public static IReadOnlyList<BoostJarRecord> StillParked(string gameDirectory, IEnumerable<BoostJarRecord> parked)
    {
        var directory = ModManager.ModsDirectory(gameDirectory);

        return parked
            .Where(jar => !File.Exists(Path.Combine(directory, jar.FileName)) &&
                          IsUnchanged(Path.Combine(directory, jar.FileName + DisabledSuffix), jar))
            .ToList();
    }

    /// <summary>Switches parked jars back on; returns the ones that came back.</summary>
    public static IReadOnlyList<BoostJarRecord> SwitchOn(string gameDirectory, IEnumerable<BoostJarRecord> parked)
    {
        var directory = ModManager.ModsDirectory(gameDirectory);
        var back = new List<BoostJarRecord>();

        foreach (var jar in StillParked(gameDirectory, parked))
        {
            try
            {
                File.Move(Path.Combine(directory, jar.FileName + DisabledSuffix), Path.Combine(directory, jar.FileName));
                back.Add(jar);
            }
            catch (Exception)
            {
                // Stays switched off; the plan drawn next time finds the slot empty.
            }
        }

        return back;
    }

    /// <summary>True when the file exists and is byte for byte what was installed. Without a recorded hash nothing is claimed.</summary>
    internal static bool IsUnchanged(string path, BoostJarRecord jar)
        => !string.IsNullOrEmpty(jar.Sha1) &&
           File.Exists(path) &&
           string.Equals(ModManager.TryComputeSha1(path), jar.Sha1, StringComparison.OrdinalIgnoreCase);
}
