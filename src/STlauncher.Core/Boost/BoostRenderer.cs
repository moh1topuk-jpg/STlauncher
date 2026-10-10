using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Boost;

/// <summary>
/// The renderer's place seen from the other side: not "is it empty, may the switch fill
/// it", but "somebody else has just filled it". Sodium, Embeddium and Rubidium do one job
/// and any two of them together crash the game, so when a server's build brings its own
/// the one the switch installed steps aside.
/// </summary>
public static class BoostRenderer
{
    private const string DisabledSuffix = ".disabled";

    /// <summary>True for a project slug or a mod id of the renderer family.</summary>
    public static bool IsFamily(string? id)
        => !string.IsNullOrEmpty(id) && BoostMods.RendererFamily.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the jar is a renderer: it declares one of the family's ids, or - for a
    /// jar that says nothing readable about itself - it is named after one of them. The
    /// same two signs the switch goes by when it looks whether its slot is taken.
    /// </summary>
    public static bool IsRenderer(BuildJar jar, LoaderKind loader, string? gameVersion)
        => BoostMods.Ids(jar, loader, gameVersion).Any(IsFamily) ||
           BoostMods.RendererFamily.Any(slug => BoostMods.IsNamedAfter(jar.FileName, slug));

    /// <summary>
    /// Called when something other than the switch has put <paramref name="incomingFileName"/>
    /// into mods/. If that file is a renderer, the switch's own renderer jar is renamed to
    /// .disabled and moved in the record from "installed" to "parked": switching off then
    /// has nothing of it to take back, and switching on again finds the slot filled and
    /// leaves the parked jar where it is - until the day the slot is empty again.
    /// </summary>
    /// <remarks>
    /// Only a jar that is still byte for byte what the switch installed is touched. One
    /// the player has updated or replaced since is the player's, and the build check
    /// names the two renderers instead.
    /// </remarks>
    /// <param name="incomingProject">The project the file came from, when the caller knows it.</param>
    /// <returns>The jars switched off; empty when there was nothing to do.</returns>
    public static IReadOnlyList<BoostJarRecord> StepAside(
        string gameDirectory,
        BoostRecord record,
        string incomingFileName,
        string? incomingProject,
        LoaderKind loader,
        string? gameVersion)
    {
        var none = Array.Empty<BoostJarRecord>();

        if (record.Jars.Count == 0)
        {
            return none;
        }

        var directory = ModManager.ModsDirectory(gameDirectory);
        var incoming = BoostMods.Bare(incomingFileName);

        if (!IsFamily(incomingProject) && !IsRendererFile(directory, incoming, loader, gameVersion))
        {
            return none;
        }

        var parked = new List<BoostJarRecord>();

        foreach (var jar in record.Jars.ToList())
        {
            // The same name is the same file: the newcomer took the switch's jar over,
            // and switching off already leaves a jar the catalog owns alone.
            if (string.Equals(jar.FileName, incoming, StringComparison.OrdinalIgnoreCase) ||
                (!IsFamily(jar.Slug) && !IsRendererFile(directory, jar.FileName, loader, gameVersion)))
            {
                continue;
            }

            var path = Path.Combine(directory, jar.FileName);

            if (!BoostJars.IsUnchanged(path, jar) || File.Exists(path + DisabledSuffix))
            {
                continue;
            }

            try
            {
                File.Move(path, path + DisabledSuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Stays on; the build check says "two renderers" before the game is started.
                continue;
            }

            record.Jars.Remove(jar);
            record.Parked.RemoveAll(p => string.Equals(p.FileName, jar.FileName, StringComparison.OrdinalIgnoreCase));
            record.Parked.Add(jar);
            parked.Add(jar);
        }

        return parked;
    }

    private static bool IsRendererFile(string modsDirectory, string fileName, LoaderKind loader, string? gameVersion)
    {
        var path = Path.Combine(modsDirectory, fileName);

        if (!File.Exists(path))
        {
            return BoostMods.RendererFamily.Any(slug => BoostMods.IsNamedAfter(fileName, slug));
        }

        return IsRenderer(new BuildJar(fileName, true, ModMetadataReader.Read(path)), loader, gameVersion);
    }
}
