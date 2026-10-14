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
/// clipboard; the other player picks "From a build code", is shown what the build is
/// and what it downloads, and on "Add the build" it appears with its mods. No file to
/// attach, no server between them; what Modrinth does not host is named so nobody
/// wonders where it went.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// The code of a build: every file Modrinth hosts by address and hash, the rest by
    /// name. Shared by "Share" (see MainWindowViewModel.BuildShare.cs), by "Copy the long
    /// code" and by the invite to a server made from a build.
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
    /// Reads a code from the clipboard. A build code and an invite to a friend's server
    /// are both shown first and wait for the player's answer.
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

        await AddFromCodeTextAsync(text, CodeOrigin.Anywhere);
    }

    /// <summary>
    /// What a code leads to, wherever the text came from: the clipboard ("Code from the
    /// clipboard" on the home screen, "From a build code" on the builds page) or the box
    /// on the "Join a friend" tab. True when the code was understood - an invite or a
    /// build is on screen, waiting for the player's answer; otherwise the status line
    /// says why not. Nothing is created or downloaded from here.
    /// </summary>
    private async Task<bool> AddFromCodeTextAsync(string? text, CodeOrigin origin)
    {
        var kind = PastedCode.Classify(text, out var shortCode);

        // A code of the other kind is not acted on from here: the player asked this
        // place for a build (or for a server) and gets told where the other one goes,
        // with a button that takes it there.
        if (origin == CodeOrigin.Builds && kind is PastedCodeKind.Invite or PastedCodeKind.ShortInvite)
        {
            _misplacedCode = kind == PastedCodeKind.ShortInvite ? RelayKeys.FormatInviteCode(shortCode) : (text ?? string.Empty).Trim();
            BuildCodeNoteIsInvite = true;
            Status = Localize("Share_IsInvite", "This is an invite to a server, not a build code. Paste it into “Friends → Join a friend”.");
            return false;
        }

        if (origin == CodeOrigin.FriendsBox && kind is PastedCodeKind.Build or PastedCodeKind.ShortBuild)
        {
            _misplacedCode = kind == PastedCodeKind.ShortBuild ? BuildShare.Format(shortCode) : (text ?? string.Empty).Trim();
            FriendInviteNoteIsBuild = true;
            Status = Localize("Share_IsBuild", "This is a build code, not an invite to a server. Its place is “Builds → From a build code”.");
            return false;
        }

        // A short build code is only a name too, but one the relay keeps on disk: the
        // friend who shared it need not be online.
        var shownBuildCode = string.Empty;

        if (kind is PastedCodeKind.Build or PastedCodeKind.ShortBuild && IsBuildPreviewOpen)
        {
            // One build at a time is on screen waiting for its answer.
            return false;
        }

        if (kind == PastedCodeKind.ShortBuild)
        {
            var shown = BuildShare.Format(shortCode);
            var failedTitle = Localize("Code_PreviewFailedTitle", "The build could not be fetched by the code");

            shownBuildCode = shown;
            BuildPreviewCode = shown;

            if (RelayLocation.Resolve(_loadedCatalog) is not { } shareRelay)
            {
                Status = Localize("Share_FetchNoRelay", "This is a short build code, and the launcher does not know the server that keeps them yet. Ask the friend for the long code, or try again later.");
                ShowBuildPreviewProblem(failedTitle, Status);
                return false;
            }

            Status = Localize("Share_Fetching", "Asking for the build {0}…", shown);

            // The wait is on screen, with a way out of it: nothing exists yet either way.
            var fetchToken = BeginBuildPreviewFetch(shown);

            string? longCode;
            BuildShareFailure shareFailure;

            try
            {
                (longCode, shareFailure) = await Task.Run(() => BuildShare.FetchAsync(shareRelay, shortCode, fetchToken), fetchToken);
            }
            catch (OperationCanceledException)
            {
                longCode = null;
                shareFailure = BuildShareFailure.None;
            }

            if (fetchToken.IsCancellationRequested)
            {
                // "Cancel" has closed the dialog already; an answer that came anyway is dropped.
                Status = Localize("Code_PreviewFetchCancelled", "The request was cancelled. Nothing was added.");
                return false;
            }

            if (longCode is null)
            {
                Status = ShareFetchFailureText(shareFailure, shown);
                ShowBuildPreviewProblem(failedTitle, Status);
                return false;
            }

            if (!BuildCode.TryDecode(longCode, out _))
            {
                Status = Localize("Share_FetchUnreadable", "The code {0} was found, but this launcher cannot read the build under it. The friend's launcher may be newer: update yours and try again.", shown);
                ShowBuildPreviewProblem(failedTitle, Status);
                return false;
            }

            text = longCode;
        }

        // A short invite is only a name: the relay holds what it stands for.
        if (kind == PastedCodeKind.ShortInvite)
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

            // Nothing is added yet here either: the build goes on screen (see
            // MainWindowViewModel.BuildPreview.cs) and "Add the build" there makes it. A
            // code the safety checks stop is refused in the same dialog, with the reason.
            case SharedCodeKind.Build when payload is not null && OpenBuildPreview(text, shownBuildCode) is var verdict && verdict != BuildCodeVerdict.NotACode:
                return verdict == BuildCodeVerdict.Ready;

            default:
                Status = origin switch
                {
                    CodeOrigin.Builds => Localize("Share_ClipboardNoBuild", "The clipboard holds no build code. Copy a code like SB-XXXXX-XXXXX, or the long line that starts with STB1., and press the button again."),
                    CodeOrigin.FriendsBox => Localize("Friends_BoxUnknown", "This does not look like an invite. A code looks like ST-XXXXX-XXXXX; the long line starts with STS1."),
                    _ => Localize("Friends_ClipboardEmpty", "The clipboard holds neither a build code nor an invite. Copy a build code (SB-XXXXX-XXXXX or the line that starts with STB1.) or an invite (ST-XXXXX-XXXXX) and try again.")
                };
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
