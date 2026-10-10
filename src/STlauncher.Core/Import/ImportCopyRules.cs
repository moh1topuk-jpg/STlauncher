using System;
using System.Collections.Generic;
using System.Linq;
using STlauncher.Core.Instances;

namespace STlauncher.Core.Import;

/// <summary>
/// What of a found build's folder is copied into the launcher, and what is left behind.
/// </summary>
/// <remarks>
/// Two kinds of source, two opposite rules.
///
/// A launcher that keeps each build in a folder of its own (Prism, CurseForge, a TLauncher
/// version folder) has nothing in there but the build, so everything is taken except what
/// is rebuilt anyway - caches, logs - and the launcher's own description files.
///
/// A shared .minecraft root is the other launcher's home: its sign-in tokens
/// (launcher_accounts.json, launcher_msa_credentials.bin), its settings, its updater. No
/// list of what to skip there can be complete, because the next launcher version adds a
/// file nobody has heard of. So from a shared root only a named set is taken, and a file
/// outside it is never copied whatever it is.
/// </remarks>
public static class ImportCopyRules
{
    /// <summary>The folders of a shared .minecraft that are the game's content.</summary>
    public static readonly IReadOnlyList<string> SharedRootFolders = new[]
    {
        "mods", "config", "defaultconfigs", "saves", "resourcepacks", "shaderpacks",
        "datapacks", "kubejs", "scripts"
    };

    /// <summary>The loose files of a shared .minecraft that are the game's settings.</summary>
    public static readonly IReadOnlyList<string> SharedRootFiles = new[]
    {
        "options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat"
    };

    /// <summary>
    /// Folders of a build's own directory that are caches or logs: large, rebuilt on the
    /// next start or fetched again, and of no use in a new place.
    /// </summary>
    private static readonly string[] OwnFolderSkipped =
    {
        "logs", "crash-reports", ".cache", "cache", ".fabric", ".mixin.out", "natives",
        "versions", "assets", "libraries", "runtime", "bin",
        "tlloader", "backup", "downloads", "staging"
    };

    /// <summary>Description files the source launchers keep beside the game files.</summary>
    private static readonly string[] LauncherMetadataFiles =
    {
        "instance.cfg", "mmc-pack.json", "minecraftinstance.json", "instance.json",
        "profile.json", ".curseclient", ".packignore"
    };

    /// <summary>True when a top-level entry of the build's folder comes along.</summary>
    public static bool IsCopiedFromRoot(ExternalInstance source, string name, bool isDirectory)
    {
        if (source is null || string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (source.SharesGameDirectory)
        {
            return (isDirectory ? SharedRootFolders : SharedRootFiles).Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        if (isDirectory)
        {
            return !OwnFolderSkipped.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                   !name.StartsWith("webcache", StringComparison.OrdinalIgnoreCase);
        }

        // The launcher's own definition must never be overwritten by a file of that name.
        if (string.Equals(name, InstanceManager.DefinitionFileName, StringComparison.OrdinalIgnoreCase) ||
            IsLauncherFile(name) ||
            IsAccountFile(name) ||
            LauncherMetadataFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // GDLauncher keeps its description in a file with a name any program might use.
        return !(source.Source == ExternalLauncherKind.GdLauncher &&
                 string.Equals(name, "config.json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Loose files in a game folder that belong to the other launcher, not to the game:
    /// its executables, its own settings and - above all - its account files. Copying
    /// those would duplicate someone's sign-in into a folder nobody expects to hold it.
    /// </summary>
    public static bool IsLauncherFile(string fileName)
    {
        var name = fileName.ToLowerInvariant();

        return name.StartsWith("launcher_", StringComparison.Ordinal) ||
               name.StartsWith("tlauncher", StringComparison.Ordinal) ||
               name.StartsWith("clientid", StringComparison.Ordinal) ||
               name.EndsWith(".exe", StringComparison.Ordinal) ||
               name.Contains(".exe.", StringComparison.Ordinal) ||
               name.EndsWith(".bin", StringComparison.Ordinal) ||
               name.EndsWith(".log", StringComparison.Ordinal) ||
               name is "treatment_tags.json" or "usercache.json" or "usernamecache.json";
    }

    /// <summary>
    /// Files that hold a sign-in, wherever in the build they sit: the launchers' account
    /// stores, and the one a popular mod keeps inside the game folder. Checked at every
    /// depth, in both kinds of source.
    /// </summary>
    public static bool IsAccountFile(string fileName)
    {
        var name = fileName.ToLowerInvariant();

        return name.StartsWith("launcher_accounts", StringComparison.Ordinal) ||
               name.StartsWith("launcher_msa_credentials", StringComparison.Ordinal) ||
               name.StartsWith("launcher_profiles", StringComparison.Ordinal) ||
               name.StartsWith("tlauncherprofiles", StringComparison.Ordinal) ||
               name is "accounts.json" or "microsoft_accounts.json";
    }
}
