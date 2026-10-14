using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Friends;

namespace STlauncher.App.ViewModels;

/// <summary>What the strip under the build's header is showing.</summary>
public enum BuildSharePanel
{
    None,

    /// <summary>The one sentence about what is kept on the launcher's server, before the first share.</summary>
    Consent,

    /// <summary>The code is being made and left with the server.</summary>
    Working,

    /// <summary>The short code, copied.</summary>
    Short,

    /// <summary>The short code could not be made: the long one was copied, and why.</summary>
    Long
}

/// <summary>Where a pasted code was handed to the launcher. A code of the wrong kind for the place is pointed to its own.</summary>
public enum CodeOrigin
{
    /// <summary>The home screen's "Code from the clipboard": whatever it is, it is acted on.</summary>
    Anywhere,

    /// <summary>"From a build code" on the builds page.</summary>
    Builds,

    /// <summary>The invite box on "Join a friend".</summary>
    FriendsBox
}

/// <summary>
/// "Share" on a build. The build's long code is left with the launcher's server, which
/// gives back a short one - SB-XXXXX-XXXXX - and that is what goes to the clipboard: a
/// thing to say aloud or type into a chat, like the invite to a friends' server. When the
/// server cannot be reached, or is a version that keeps no builds, the long code is
/// copied as it always was and the player is told why.
///
/// Leaving the build's list of mods on a server is something the player is told about
/// once, in the place where the button is, before it happens for the first time.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The player has read what sharing keeps on the server and went on. Kept in the settings.</summary>
    private bool _buildShareAccepted;

    /// <summary>The build the strip is about: it does not follow the player to another build.</summary>
    private string? _buildShareInstanceId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuildShareShown))]
    [NotifyPropertyChangedFor(nameof(IsBuildShareConsent))]
    [NotifyPropertyChangedFor(nameof(IsBuildShareWorking))]
    [NotifyPropertyChangedFor(nameof(IsBuildShareShort))]
    [NotifyPropertyChangedFor(nameof(IsBuildShareLong))]
    private BuildSharePanel _buildSharePanel;

    /// <summary>The short code as it was copied: SB-XXXXX-XXXXX.</summary>
    [ObservableProperty]
    private string _buildShareCode = string.Empty;

    /// <summary>The sentence of the strip: what is happening, or what was copied and why.</summary>
    [ObservableProperty]
    private string _buildShareText = string.Empty;

    /// <summary>The second, quieter line: what did not go into the code, or what to do with the long one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildShareDetail))]
    private string _buildShareDetail = string.Empty;

    public bool HasBuildShareDetail => BuildShareDetail.Length > 0;

    /// <summary>Some of the build's files could not go into the code: a .mrpack carries them all.</summary>
    [ObservableProperty]
    private bool _buildShareHasMissing;

    private bool BuildShareIsHere
        => BuildSharePanel != BuildSharePanel.None &&
           SelectedInstance is { } instance &&
           string.Equals(instance.Id, _buildShareInstanceId, StringComparison.OrdinalIgnoreCase);

    public bool IsBuildShareShown => BuildShareIsHere;

    public bool IsBuildShareConsent => BuildShareIsHere && BuildSharePanel == BuildSharePanel.Consent;

    public bool IsBuildShareWorking => BuildShareIsHere && BuildSharePanel == BuildSharePanel.Working;

    public bool IsBuildShareShort => BuildShareIsHere && BuildSharePanel == BuildSharePanel.Short;

    public bool IsBuildShareLong => BuildShareIsHere && BuildSharePanel == BuildSharePanel.Long;

    /// <summary>Another build was opened: the strip belongs to the one it was made for.</summary>
    private void RefreshBuildSharePanel()
    {
        OnPropertyChanged(nameof(IsBuildShareShown));
        OnPropertyChanged(nameof(IsBuildShareConsent));
        OnPropertyChanged(nameof(IsBuildShareWorking));
        OnPropertyChanged(nameof(IsBuildShareShort));
        OnPropertyChanged(nameof(IsBuildShareLong));
    }

    private void ShowBuildShare(BuildSharePanel panel, string text = "", string detail = "")
    {
        BuildShareText = text;
        BuildShareDetail = detail;
        BuildSharePanel = panel;
    }

    /// <summary>The share button of the build's header.</summary>
    [RelayCommand]
    private async Task ShareBuildAsync()
    {
        if (SelectedInstance is not { } instance || IsModsBusy)
        {
            return;
        }

        if (!_buildShareAccepted)
        {
            // Nothing has left the machine yet: the sentence first, the button under it.
            _buildShareInstanceId = instance.Id;
            ShowBuildShare(BuildSharePanel.Consent);
            return;
        }

        await PublishBuildAsync();
    }

    /// <summary>"Share" under the sentence about the server: said once, remembered.</summary>
    [RelayCommand]
    private async Task ConfirmBuildShareAsync()
    {
        if (SelectedInstance is null || IsModsBusy)
        {
            return;
        }

        _buildShareAccepted = true;
        PersistSettings();

        await PublishBuildAsync();
    }

    [RelayCommand]
    private void CloseBuildShare() => BuildSharePanel = BuildSharePanel.None;

    /// <summary>The copy button beside the code: the clipboard has usually moved on by the second friend.</summary>
    [RelayCommand]
    private async Task CopyBuildShareCodeAsync()
    {
        if (BuildShareCode.Length == 0)
        {
            return;
        }

        try
        {
            await CopyToClipboardAsync(BuildShareCode);
            Status = Localize("Share_Copied", "Build code {0} copied. A friend pastes it into “From a build code”.", BuildShareCode);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_Ui", "Something went wrong: {0}", ex.Message);
        }
    }

    /// <summary>
    /// The long code on purpose: it needs no server and launchers from before short
    /// codes read it. Nothing is sent anywhere.
    /// </summary>
    [RelayCommand]
    private async Task CopyLongBuildCodeAsync()
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
            BuildSharePanel = BuildSharePanel.None;

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

    private async Task PublishBuildAsync()
    {
        if (SelectedInstance is not { } instance)
        {
            return;
        }

        var gameVersion = instance.VersionId ?? SelectedVersion?.Id;

        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            BuildSharePanel = BuildSharePanel.None;
            Status = Localize("Export_NoVersion", "Pick the game version in the build settings first: a modpack has to name it");
            return;
        }

        try
        {
            IsModsBusy = true;
            _buildShareInstanceId = instance.Id;
            ShowBuildShare(BuildSharePanel.Working, Localize("Share_Making", "Reading the build's files and making its code…"));

            BuildShareHasMissing = false;

            var (code, files, missing) = await MakeBuildCodeAsync(instance, gameVersion!);

            // The strip then offers the file that carries everything (see the page).
            BuildShareHasMissing = missing.Count > 0;

            var relay = RelayLocation.Resolve(_loadedCatalog);
            string? shortCode = null;
            string reason;

            if (relay is null)
            {
                reason = Localize("Share_FailNoRelay", "The short code could not be made: the launcher does not know its server's address yet.");
            }
            else
            {
                BuildShareText = Localize("Share_Sending", "Leaving the list of mods with the launcher's server…");
                Status = BuildShareText;

                var (published, failure) = await Task.Run(() => BuildShare.PublishAsync(relay, code));
                shortCode = published;
                reason = ShareFailureText(failure);
                AppendConsole($"[code] share: {(published is null ? failure.ToString() : "ok")}");
            }

            AppendConsole($"[code] {instance.Name}: {files} file(s) by address, {missing.Count} not on Modrinth, {code.Length} characters");

            var notInCode = missing.Count == 0
                ? string.Empty
                : Localize("Share_Missing", "Not on Modrinth, so a friend will not get these by the code: {0}", string.Join(", ", missing));

            if (shortCode is not null)
            {
                var shown = BuildShare.Format(shortCode);

                await CopyToClipboardAsync(shown);
                BuildShareCode = shown;
                ShowBuildShare(BuildSharePanel.Short, Localize("Share_CopiedPanel", "Copied. A friend pastes it into “From a build code”."), notInCode);
                Status = Localize("Share_Copied", "Build code {0} copied. A friend pastes it into “From a build code”.", shown);
                return;
            }

            // The long code is the build itself and has always been enough.
            await CopyToClipboardAsync(code);

            var longNote = Localize("Share_LongCopied", "The long code is copied instead ({0} characters): it works without the server and is pasted the same way.", code.Length);

            ShowBuildShare(BuildSharePanel.Long, reason, notInCode.Length == 0 ? longNote : longNote + " " + notInCode);
            Status = reason + " " + longNote;
        }
        catch (Exception ex)
        {
            BuildSharePanel = BuildSharePanel.None;
            Status = Localize("Code_Failed", "Could not make the build code: {0}", ex.Message);
            AppendConsole($"[code] failed: {ex}");
        }
        finally
        {
            IsModsBusy = false;
        }
    }

    private static string ShareFailureText(BuildShareFailure failure) => failure switch
    {
        BuildShareFailure.None => string.Empty,
        BuildShareFailure.Unreachable => Localize("Share_FailUnreachable", "The short code could not be made: the launcher's server does not answer. Check the connection."),
        BuildShareFailure.Unsupported => Localize("Share_FailUnsupported", "The short code could not be made: the launcher's server does not keep builds yet."),
        BuildShareFailure.RateLimited => Localize("Share_FailLimit", "The short code could not be made: too many builds were shared from your address in the last hour."),
        BuildShareFailure.TooBig => Localize("Share_FailTooBig", "The short code could not be made: this build is too large for the server to keep."),
        BuildShareFailure.Busy => Localize("Share_FailBusy", "The short code could not be made: the launcher's server has no room right now."),
        _ => Localize("Share_FailRejected", "The short code could not be made: the launcher's server did not take the build.")
    };

    /// <summary>Why a short build code led nowhere, as a sentence.</summary>
    private static string ShareFetchFailureText(BuildShareFailure failure, string shown) => failure switch
    {
        BuildShareFailure.Unreachable => Localize("Share_FetchUnreachable", "The server that keeps build codes does not answer. Check the connection and try again."),
        BuildShareFailure.Unsupported => Localize("Share_FetchUnsupported", "The launcher's server does not keep short build codes yet. Ask the friend for the long code."),
        BuildShareFailure.NotFound => Localize("Share_FetchNotFound", "There is no build under the code {0}: it has a typo, or nobody used it for half a year and it was removed. Ask the friend to share the build again.", shown),
        BuildShareFailure.RateLimited => Localize("Share_FetchLimit", "Too many requests from your address in the last hour. Wait a little and try again."),
        BuildShareFailure.Busy => Localize("Share_FetchBusy", "The launcher's server is busy right now. Try again in a minute."),
        _ => Localize("Share_FetchRejected", "The server's answer to the code {0} makes no sense. Ask the friend for the long code.", shown)
    };

    // ===================== "From a build code" on the builds page =====================

    /// <summary>What "From a build code" has to say, next to the button; empty when nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildCodeNote))]
    private string _buildCodeNote = string.Empty;

    public bool HasBuildCodeNote => BuildCodeNote.Length > 0;

    /// <summary>The note says "this is an invite to a server" and carries the button that takes it there.</summary>
    [ObservableProperty]
    private bool _buildCodeNoteIsInvite;

    /// <summary>The server is being asked what a short build code stands for.</summary>
    [ObservableProperty]
    private bool _isBuildCodeLooking;

    /// <summary>The note under the invite box says "this is a build code" and carries the button that adds it.</summary>
    [ObservableProperty]
    private bool _friendInviteNoteIsBuild;

    /// <summary>The text a "this goes over there" note is about, for the button to carry.</summary>
    private string _misplacedCode = string.Empty;

    /// <summary>"From a build code": the clipboard, read as a build.</summary>
    [RelayCommand]
    private async Task PasteBuildCodeAsync()
    {
        if (IsBusy || IsModsBusy || IsBuildCodeLooking)
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
            BuildCodeNote = Localize("Error_Ui", "Something went wrong: {0}", ex.Message);
            return;
        }

        await AddBuildFromTextAsync(text);
    }

    private async Task AddBuildFromTextAsync(string? text)
    {
        try
        {
            IsBuildCodeLooking = true;
            BuildCodeNoteIsInvite = false;
            BuildCodeNote = string.Empty;

            if (await AddFromCodeTextAsync(text, CodeOrigin.Builds))
            {
                BuildCodeNote = string.Empty;
            }
            else
            {
                // The status line at the bottom says it too; here it is next to the button it is about.
                BuildCodeNote = Status;
            }
        }
        finally
        {
            IsBuildCodeLooking = false;
        }
    }

    [RelayCommand]
    private void CloseBuildCodeNote()
    {
        BuildCodeNote = string.Empty;
        BuildCodeNoteIsInvite = false;
    }

    /// <summary>An invite was pasted where builds are added: to the box it belongs in, filled in and waiting for "Join".</summary>
    [RelayCommand]
    private void OpenInviteFromBuildNote()
    {
        var text = _misplacedCode;

        CloseBuildCodeNote();
        Section = ShellSection.Host;
        FriendsTab = FriendsTab.Join;
        FriendInviteEntry = text;
    }

    /// <summary>
    /// A build code was pasted into the invite box. The build is shown right here, over
    /// the page; the code stays in the box until "Add the build", so "Cancel" leaves
    /// everything as it was.
    /// </summary>
    [RelayCommand]
    private async Task AddBuildFromFriendNoteAsync()
    {
        if (IsBusy || IsModsBusy || IsBuildCodeLooking)
        {
            return;
        }

        await AddBuildFromTextAsync(_misplacedCode);

        if (IsBuildPreviewReady)
        {
            _buildPreviewFromFriendBox = true;
        }
    }
}
