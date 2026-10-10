using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Friends;
using STlauncher.Core.Instances;
using STlauncher.Core.Modpacks;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>
/// A build handed over as one line of text. "Copy the build code" puts it in the
/// clipboard; the other player picks "From a build code" and the build appears with
/// its mods downloaded. No file to attach, no server between them; what Modrinth does
/// not host is named so nobody wonders where it went.
/// </summary>
public partial class MainWindowViewModel
{
    [RelayCommand]
    private async Task CopyBuildCodeAsync()
    {
        if (SelectedInstance is not { } instance || IsModsBusy)
        {
            return;
        }

        var gameVersion = instance.VersionId ?? SelectedVersion?.Id;

        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            Status = Localize("Export_NoVersion", "Pick the game version in the build settings first: a modpack has to name it");
            return;
        }

        try
        {
            IsModsBusy = true;

            var (code, files, missing) = await MakeBuildCodeAsync(instance, gameVersion!);

            await CopyToClipboardAsync(code);
            AppendConsole($"[code] {instance.Name}: {files} file(s) by address, {missing.Count} not on Modrinth, {code.Length} characters");

            Status = missing.Count == 0
                ? Localize("Code_Copied", "Build code copied: {0} file(s), {1} characters. Paste it to a friend.", files, code.Length)
                : Localize("Code_CopiedPartial", "Build code copied: {0} file(s). Not on Modrinth, so not in the code: {1}", files, string.Join(", ", missing));
        }
        catch (Exception ex)
        {
            Status = Localize("Code_Failed", "Could not make the build code: {0}", ex.Message);
            AppendConsole($"[code] failed: {ex}");
        }
        finally
        {
            IsModsBusy = false;
        }
    }

    /// <summary>
    /// The code of a build: every file Modrinth hosts by address and hash, the rest by
    /// name. Shared by "Copy the build code" and by the invite to a server made from a build.
    /// </summary>
    private async Task<(string Code, int Files, List<string> Missing)> MakeBuildCodeAsync(Instance instance, string gameVersion)
    {
        Status = Localize("Export_Hashing", "Export: reading the build's files…");

        var directory = _instances.GameDirectory(instance);
        var candidates = await Task.Run(() => CollectExportCandidates(directory));

        Status = Localize("Export_Lookup", "Export: checking {0} file(s) against Modrinth…", candidates.Count);
        var known = await _modrinth.GetVersionsByHashesAsync(candidates.Select(c => c.Sha1));

        var files = new List<BuildCodeFile>();
        var missing = new List<string>();

        foreach (var candidate in candidates)
        {
            var file = known.TryGetValue(candidate.Sha1, out var version)
                ? version.Files.FirstOrDefault(f => string.Equals(f.Sha1, candidate.Sha1, StringComparison.OrdinalIgnoreCase))
                : null;

            if (file is not null && ModpackWriter.IsAllowedDownload(file.Url))
            {
                files.Add(new BuildCodeFile(candidate.RelativePath, file.Url, candidate.Sha1, candidate.Size));
            }
            else
            {
                missing.Add(Path.GetFileName(candidate.RelativePath));
            }
        }

        var loaderVersion = instance.LoaderVersion
                            ?? (ReferenceEquals(instance, SelectedInstance) ? SelectedLoaderVersion?.Version : null);

        var code = BuildCode.Encode(new BuildCodePayload(
            instance.Name,
            gameVersion,
            instance.Loader,
            loaderVersion,
            files,
            missing));

        return (code, files.Count, missing);
    }

    /// <summary>
    /// Reads a code from the clipboard: a build code makes a new build out of it, an
    /// invite to a friend's server is shown first and waits for the player's answer.
    /// </summary>
    [RelayCommand]
    private async Task AddBuildFromCodeAsync()
    {
        if (IsBusy || IsModsBusy)
        {
            return;
        }

        string? text;

        try
        {
            text = await ReadClipboardAsync();
        }
        catch (Exception ex)
        {
            Status = Localize("Code_Failed", "Could not make the build code: {0}", ex.Message);
            return;
        }

        await AddFromCodeTextAsync(text, fromClipboard: true);
    }

    /// <summary>
    /// What a code leads to, wherever the text came from: the clipboard ("A friend's
    /// build") or the box on the "Join a friend" tab. True when the code was understood -
    /// an invite is on screen, or a build was added; otherwise the status line says why not.
    /// </summary>
    private async Task<bool> AddFromCodeTextAsync(string? text, bool fromClipboard)
    {
        // A short invite is only a name: the relay holds what it stands for.
        if (SharedCode.Detect(text) == SharedCodeKind.None && RelayKeys.TryFindInviteCode(text, out var shortCode))
        {
            if (RelayLocation.Resolve(_loadedCatalog) is not { } relay)
            {
                Status = Localize("Friends_ShortNoRelay", "This is a short invite, and the launcher does not know the server that keeps such invites yet. Ask the friend for the long one, or try again later.");
                return false;
            }

            Status = Localize("Friends_ShortFetching", "Asking for the invite {0}…", RelayKeys.FormatInviteCode(shortCode));
            var (full, failure) = await Task.Run(() => RelayInvite.FetchAsync(relay, shortCode));

            if (full is null)
            {
                Status = failure == RelayFailure.RelayUnreachable
                    ? Localize("Friends_ShortUnreachable", "The server that keeps invites does not answer. Check the connection and try again.")
                    : Localize("Friends_ShortOffline", "Nobody answers to the invite {0}: the friend's server is not running right now, or the code has a typo.", RelayKeys.FormatInviteCode(shortCode));
                return false;
            }

            text = full;
        }

        switch (SharedCode.Read(text, out var payload, out var invite))
        {
            case SharedCodeKind.Server when invite is not null:
                // Nothing is added yet: the invite goes on screen and waits for a yes.
                ShowFriendInvite(invite);
                return true;

            case SharedCodeKind.Build when payload is not null:
                var instance = await AddBuildFromPayloadAsync(payload, showBuild: true);

                if (instance is null)
                {
                    return false;
                }

                Status = payload.Missing.Count > 0
                    ? Localize("Code_AddedPartial", "Build “{0}” added. Not in the code, add by hand: {1}", instance.Name, string.Join(", ", payload.Missing))
                    : Localize("Code_Added", "Build “{0}” added from the code", instance.Name);

                return true;

            default:
                Status = fromClipboard
                    ? Localize("Friends_ClipboardEmpty", "The clipboard holds neither a build code nor an invite. Copy the invite (ST-XXXXX-XXXXX) or the line that starts with STB1. or STS1. and try again.")
                    : Localize("Friends_BoxUnknown", "This does not look like an invite. A code looks like ST-XXXXX-XXXXX; the long line starts with STS1.");
                return false;
        }
    }

    /// <summary>
    /// Makes a new build out of a decoded code and downloads its files. Returns the build,
    /// or null when it could not be made; the status line then says why.
    /// </summary>
    /// <param name="showBuild">Open the build's mods tab, where the download is seen arriving.</param>
    private async Task<Instance?> AddBuildFromPayloadAsync(BuildCodePayload payload, bool showBuild)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"stlauncher-code-{Guid.NewGuid():N}.mrpack");

        try
        {
            // The code is a modpack without the files inside; the importer takes it from here.
            ModpackWriter.Write(
                temporary,
                payload.Name,
                "1.0.0",
                payload.GameVersion,
                payload.Loader,
                payload.LoaderVersion,
                payload.Files.Select(f => new ModpackExportFile(f.Path, f.Url, f.Sha1, string.Empty, f.Size)).ToList(),
                Array.Empty<ModpackOverride>());

            var instance = _instances.Create(UniqueInstanceName(payload.Name));
            instance.VersionId = payload.GameVersion;
            instance.Loader = payload.Loader;
            instance.LoaderVersion = payload.LoaderVersion;

            // The build is new and nobody has set its memory yet: start it with what its mods take.
            instance.MaxMemoryMb = MemoryForNewBuild(payload.Files.Count(f =>
                f.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
                f.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)));
            _instances.Save(instance);

            _allInstances.Add(instance);
            ApplyBuildFilter();
            SelectedInstance = instance;

            if (showBuild)
            {
                Section = ShellSection.Builds;
                BuildTab = BuildTab.Mods;
            }

            AppendConsole($"[code] new build '{instance.Name}': {payload.Files.Count} file(s) to download, {payload.Missing.Count} not in the code");
            await ImportModpackAsync(temporary);

            return instance;
        }
        catch (Exception ex)
        {
            Status = Localize("Code_Failed", "Could not make the build code: {0}", ex.Message);
            AppendConsole($"[code] failed: {ex}");
            return null;
        }
        finally
        {
            try { File.Delete(temporary); } catch (Exception) { }
        }
    }
}
