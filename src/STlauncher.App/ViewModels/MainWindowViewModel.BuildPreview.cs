using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Loaders;
using STlauncher.Core.Modpacks;

namespace STlauncher.App.ViewModels;

/// <summary>What the "what will be installed" dialog is showing.</summary>
public enum BuildPreviewState
{
    None,

    /// <summary>The launcher's server is being asked what a short code stands for.</summary>
    Fetching,

    /// <summary>The build is read and shown; nothing exists yet.</summary>
    Ready,

    /// <summary>The code led nowhere, or is one the launcher will not act on: the reason, and a way out.</summary>
    Problem
}

/// <summary>A line of the list of files: the heading of a kind, or one file under it.</summary>
public sealed record BuildPreviewRow(bool IsHeader, string Title, string Detail, string Source)
{
    public bool IsFile => !IsHeader;

    public bool HasSource => Source.Length > 0;
}

/// <summary>One thing the build needs besides its files, and whether the computer has it.</summary>
public sealed record BuildPreviewNeed(string Text, Presence State)
{
    public bool IsPresent => State == Presence.Present;

    public bool IsAbsent => State == Presence.Absent;

    public bool IsUnknown => State == Presence.Unknown;
}

/// <summary>
/// The step between "the code is read" and "the build exists". A build code used to make
/// the build and start downloading at once; an invite to a server has always been shown
/// first. Now a build is shown too: its name (the player may change it), the game and the
/// loader, every file with its size and where it comes from, the files the code could not
/// carry - and only "Add the build" creates anything. Whatever way the code arrived, it
/// ends up here.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Names of missing files and refused entries shown before "and N more".</summary>
    private const int MaxPreviewNames = 40;

    private const int MaxPreviewProblems = 6;

    /// <summary>The build on screen. Null whenever the dialog is not in <see cref="BuildPreviewState.Ready"/>.</summary>
    private BuildCodePreview? _buildPreview;

    /// <summary>The text the preview was read from, as it stood where the player put it.</summary>
    private string _buildPreviewSource = string.Empty;

    /// <summary>
    /// The code stands in the invite box on "Join a friend" and came here by the button
    /// under it. It stays in the box until the build is added: "Cancel" must leave it there.
    /// </summary>
    private bool _buildPreviewFromFriendBox;

    private CancellationTokenSource? _buildPreviewFetch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuildPreviewOpen))]
    [NotifyPropertyChangedFor(nameof(IsBuildPreviewFetching))]
    [NotifyPropertyChangedFor(nameof(IsBuildPreviewReady))]
    [NotifyPropertyChangedFor(nameof(IsBuildPreviewProblem))]
    private BuildPreviewState _buildPreviewState;

    public bool IsBuildPreviewOpen => BuildPreviewState != BuildPreviewState.None;

    public bool IsBuildPreviewFetching => BuildPreviewState == BuildPreviewState.Fetching;

    public bool IsBuildPreviewReady => BuildPreviewState == BuildPreviewState.Ready;

    public bool IsBuildPreviewProblem => BuildPreviewState == BuildPreviewState.Problem;

    /// <summary>The short code the build was asked for by (SB-XXXXX-XXXXX); empty for a long code.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildPreviewCode))]
    private string _buildPreviewCode = string.Empty;

    public bool HasBuildPreviewCode => BuildPreviewCode.Length > 0;

    /// <summary>The name the build will get. The player may type another before adding.</summary>
    [ObservableProperty]
    private string _buildPreviewName = string.Empty;

    /// <summary>What happens to the name when a build is already called that.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildPreviewNameNote))]
    private string _buildPreviewNameNote = string.Empty;

    public bool HasBuildPreviewNameNote => BuildPreviewNameNote.Length > 0;

    /// <summary>A build with the very same mods is already here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildPreviewSameNote))]
    private string _buildPreviewSameNote = string.Empty;

    public bool HasBuildPreviewSameNote => BuildPreviewSameNote.Length > 0;

    [ObservableProperty]
    private string _buildPreviewVersion = string.Empty;

    [ObservableProperty]
    private string _buildPreviewLoader = string.Empty;

    /// <summary>"27 files · 184 MB".</summary>
    [ObservableProperty]
    private string _buildPreviewTotals = string.Empty;

    /// <summary>Headings and files in one flat list, so the list can keep only the rows on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildPreviewRows))]
    private IReadOnlyList<BuildPreviewRow> _buildPreviewRows = Array.Empty<BuildPreviewRow>();

    public bool HasBuildPreviewRows => BuildPreviewRows.Count > 0;

    /// <summary>Files the code names but could not carry: a friend hands them over.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildPreviewMissing))]
    private IReadOnlyList<string> _buildPreviewMissing = Array.Empty<string>();

    public bool HasBuildPreviewMissing => BuildPreviewMissing.Count > 0;

    /// <summary>The game, the loader and Java: here already, or still to come.</summary>
    [ObservableProperty]
    private IReadOnlyList<BuildPreviewNeed> _buildPreviewNeeds = Array.Empty<BuildPreviewNeed>();

    /// <summary>What the dialog says while it waits for the server.</summary>
    [ObservableProperty]
    private string _buildPreviewBusyText = string.Empty;

    [ObservableProperty]
    private string _buildPreviewProblemTitle = string.Empty;

    [ObservableProperty]
    private string _buildPreviewProblemText = string.Empty;

    /// <summary>The files a refused code was refused for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildPreviewProblemLines))]
    private IReadOnlyList<string> _buildPreviewProblemLines = Array.Empty<string>();

    public bool HasBuildPreviewProblemLines => BuildPreviewProblemLines.Count > 0;

    /// <summary>Why "Add the build" did nothing just now; empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildPreviewNote))]
    private string _buildPreviewNote = string.Empty;

    public bool HasBuildPreviewNote => BuildPreviewNote.Length > 0;

    partial void OnBuildPreviewNameChanged(string value) => RefreshBuildPreviewNameNote();

    /// <summary>The name the build would really get: the typed one, or the code's when the field is empty.</summary>
    private string BuildPreviewWantedName()
        => BuildCodePreview.CleanName(string.IsNullOrWhiteSpace(BuildPreviewName) ? _buildPreview?.Payload.Name : BuildPreviewName);

    /// <summary>A name that is taken gets "(2)", as everywhere a build is added; here it is said before it happens.</summary>
    private void RefreshBuildPreviewNameNote()
    {
        if (_buildPreview is null)
        {
            BuildPreviewNameNote = string.Empty;
            return;
        }

        var wanted = BuildPreviewWantedName();
        var given = UniqueInstanceName(wanted);

        BuildPreviewNameNote = string.Equals(wanted, given, StringComparison.Ordinal)
            ? string.Empty
            : Localize("Code_PreviewClash", "You already have a build with this name: this one will be added as “{0}”.", given);
    }

    // ===================== Opening =====================

    /// <summary>The dialog in its waiting state: a short code is on its way to the server.</summary>
    /// <returns>The token "Cancel" and Esc stop the question with.</returns>
    private CancellationToken BeginBuildPreviewFetch(string shownCode)
    {
        _buildPreviewFetch?.Cancel();
        _buildPreviewFetch = new CancellationTokenSource();

        ClearBuildPreview();
        BuildPreviewCode = shownCode;
        BuildPreviewBusyText = Localize("Share_Fetching", "Asking for the build {0}…", shownCode);
        BuildPreviewState = BuildPreviewState.Fetching;

        return _buildPreviewFetch.Token;
    }

    /// <summary>The dialog saying why the code led to no build. Nothing was created.</summary>
    private void ShowBuildPreviewProblem(string title, string text, IReadOnlyList<string>? lines = null)
    {
        ClearBuildPreview(keepCode: true);
        BuildPreviewProblemTitle = title;
        BuildPreviewProblemText = text;
        BuildPreviewProblemLines = lines ?? Array.Empty<string>();
        BuildPreviewState = BuildPreviewState.Problem;
    }

    /// <summary>
    /// Reads a long build code and puts the build on screen. The safety checks run here,
    /// before anything is shown: a code they stop is refused with the reason.
    /// </summary>
    /// <returns>
    /// <see cref="BuildCodeVerdict.Ready"/> when the build is on screen and waits for the
    /// player's answer; <see cref="BuildCodeVerdict.Refused"/> when the dialog says why not.
    /// </returns>
    private BuildCodeVerdict OpenBuildPreview(string? text, string shownCode)
    {
        var reading = BuildCodeInspector.Read(text);

        if (reading.Verdict == BuildCodeVerdict.NotACode || (reading.Verdict == BuildCodeVerdict.Ready && reading.Preview is null))
        {
            return BuildCodeVerdict.NotACode;
        }

        if (reading.Verdict == BuildCodeVerdict.Refused)
        {
            var lines = reading.Problems.Take(MaxPreviewProblems).Select(BuildPreviewProblemLine).ToList();

            if (reading.Problems.Count > MaxPreviewProblems)
            {
                lines.Add(Localize("Code_PreviewMore", "and {0} more", reading.Problems.Count - MaxPreviewProblems));
            }

            BuildPreviewCode = shownCode;
            ShowBuildPreviewProblem(
                Localize("Code_PreviewRefusedTitle", "The launcher will not take this code"),
                Localize("Code_PreviewRefusedText", "The code names files the launcher will not download. Nothing was created or downloaded."),
                lines);

            Status = Localize("Code_PreviewRefusedStatus", "The build code was refused: {0}", lines[0]);
            AppendConsole($"[code] refused before the preview: {reading.Problems.Count} problem(s), first: {reading.Problems[0].Kind} {reading.Problems[0].Path} {reading.Problems[0].Host}");
            return BuildCodeVerdict.Refused;
        }

        var preview = reading.Preview!;
        var payload = preview.Payload;

        ClearBuildPreview();

        _buildPreview = preview;
        _buildPreviewSource = (text ?? string.Empty).Trim();

        BuildPreviewCode = shownCode;
        BuildPreviewVersion = payload.GameVersion;
        BuildPreviewLoader = payload.Loader == LoaderKind.Vanilla
            ? Localize("Code_PreviewVanilla", "No loader")
            : string.IsNullOrWhiteSpace(payload.LoaderVersion) ? payload.Loader.ToString() : $"{payload.Loader} {payload.LoaderVersion}";

        BuildPreviewTotals = BuildPreviewCountAndSize(preview.FileCount, preview.TotalSize);
        BuildPreviewRows = BuildPreviewRowsOf(preview);

        var missing = preview.Missing.Take(MaxPreviewNames).ToList();

        if (preview.Missing.Count > MaxPreviewNames)
        {
            missing.Add(Localize("Code_PreviewMore", "and {0} more", preview.Missing.Count - MaxPreviewNames));
        }

        BuildPreviewMissing = missing;
        BuildPreviewNeeds = BuildPreviewNeedsOf(payload, BuildPrerequisiteProbe.Probe(_paths, payload.GameVersion, payload.Loader, payload.LoaderVersion));

        // Last: the note about a taken name is worked out from the name.
        BuildPreviewName = BuildCodePreview.CleanName(payload.Name);
        RefreshBuildPreviewNameNote();

        BuildPreviewState = BuildPreviewState.Ready;
        Status = Localize("Code_PreviewReady", "The build “{0}” is read from the code. Nothing has been downloaded yet.", BuildPreviewName);
        AppendConsole($"[code] preview: '{payload.Name}', {preview.FileCount} file(s), {preview.TotalSize} bytes, {preview.Missing.Count} not in the code");

        _ = LookForSameBuildAsync(preview);

        return BuildCodeVerdict.Ready;
    }

    private static string BuildPreviewCountAndSize(int count, long size)
        => size > 0 ? Plural(count, "Code_PreviewFiles") + " · " + FormatSize(size) : Plural(count, "Code_PreviewFiles");

    /// <summary>A single mod is a megabyte or three: rounded to whole megabytes, half the list would read "1 MB".</summary>
    private static string BuildPreviewFileSize(long bytes)
        => bytes is >= 1024L * 1024 and < 10L * 1024 * 1024
            ? Localize("Size_MbPrecise", "{0:F1} MB", bytes / 1024d / 1024)
            : FormatSize(bytes);

    private static IReadOnlyList<BuildPreviewRow> BuildPreviewRowsOf(BuildCodePreview preview)
    {
        var rows = new List<BuildPreviewRow>(preview.FileCount + preview.Groups.Count);

        foreach (var group in preview.Groups)
        {
            var title = group.Kind switch
            {
                BuildFileKind.Mod => Localize("Builds_TabMods", "Mods"),
                BuildFileKind.ResourcePack => Localize("Builds_TabPacks", "Resource packs"),
                BuildFileKind.Shader => Localize("Builds_TabShaders", "Shaders"),
                _ => Localize("Code_PreviewKindOther", "Settings and the rest")
            };

            rows.Add(new BuildPreviewRow(true, title, BuildPreviewCountAndSize(group.Files.Count, group.Size), string.Empty));

            foreach (var file in group.Files)
            {
                rows.Add(new BuildPreviewRow(false, file.Name, file.Size > 0 ? BuildPreviewFileSize(file.Size) : string.Empty, file.Source));
            }
        }

        return rows;
    }

    /// <summary>
    /// The game, the loader and Java, each said only as far as the folders tell. The game
    /// and the loader are fetched when the build is first started, not when it is added.
    /// </summary>
    private static IReadOnlyList<BuildPreviewNeed> BuildPreviewNeedsOf(BuildCodePayload payload, BuildPrerequisites found)
    {
        var needs = new List<BuildPreviewNeed>();

        if (found.Game != Presence.Unknown)
        {
            needs.Add(new BuildPreviewNeed(
                found.Game == Presence.Present
                    ? Localize("Code_PreviewGameHere", "Minecraft {0} is already on this computer", payload.GameVersion)
                    : Localize("Code_PreviewGameLater", "Minecraft {0} is downloaded when the build is first started", payload.GameVersion),
                found.Game));
        }

        if (payload.Loader != LoaderKind.Vanilla)
        {
            var loader = string.IsNullOrWhiteSpace(payload.LoaderVersion) ? payload.Loader.ToString() : $"{payload.Loader} {payload.LoaderVersion}";

            needs.Add(new BuildPreviewNeed(
                found.Loader switch
                {
                    Presence.Present => Localize("Code_PreviewLoaderHere", "{0} is already on this computer", loader),
                    Presence.Absent => Localize("Code_PreviewLoaderLater", "{0} is downloaded when the build is first started", loader),
                    _ => Localize("Code_PreviewLoaderUnknown", "{0}: the launcher checks for it when the build is first started", loader)
                },
                found.Loader));
        }

        needs.Add(new BuildPreviewNeed(
            found.Java == Presence.Present && found.JavaMajor is { } here
                ? Localize("Code_PreviewJavaHere", "Java {0} is already on this computer", here)
                : found.JavaMajor is { } major
                    ? Localize("Code_PreviewJavaMaybe", "Java {0}: the launcher checks for it on the first start and downloads it if there is none", major)
                    : Localize("Code_PreviewJavaUnknown", "Java: the launcher checks on the first start and downloads one if none fits"),
            found.Java == Presence.Present ? Presence.Present : Presence.Unknown));

        return needs;
    }

    private static string BuildPreviewProblemLine(BuildCodeProblem problem)
    {
        var name = string.IsNullOrWhiteSpace(problem.Path) ? "?" : problem.Path;

        return problem.Kind switch
        {
            BuildCodeProblemKind.HostNotAllowed => Localize("Code_PreviewProblemHost", "{0}: the address {1} is not one the launcher downloads build files from", name, problem.Host.Length == 0 ? "?" : problem.Host),
            BuildCodeProblemKind.UnsafePath => Localize("Code_PreviewProblemPath", "{0}: the path leaves the build's folder", name),
            BuildCodeProblemKind.DuplicatePath => Localize("Code_PreviewProblemDuplicate", "{0}: listed twice", name),
            BuildCodeProblemKind.BadHash => Localize("Code_PreviewProblemHash", "{0}: no checksum to verify the file by", name),
            _ => Localize("Code_PreviewProblemBroken", "{0}: the entry is damaged", name)
        };
    }

    /// <summary>
    /// Looks through the builds of the same game and loader for one that holds exactly
    /// the mods of the code. File names in the mods folders are read, nothing else.
    /// </summary>
    private async Task LookForSameBuildAsync(BuildCodePreview preview)
    {
        try
        {
            var payload = preview.Payload;

            var candidates = _allInstances
                .Where(i => i.Loader == payload.Loader && string.Equals(i.VersionId, payload.GameVersion, StringComparison.OrdinalIgnoreCase))
                .Select(i => (i.Name, Directory: Path.Combine(_instances.GameDirectory(i), "mods")))
                .ToList();

            if (candidates.Count == 0)
            {
                return;
            }

            var same = await Task.Run(() =>
            {
                foreach (var (name, directory) in candidates)
                {
                    try
                    {
                        if (Directory.Exists(directory) &&
                            preview.HasSameMods(Directory.EnumerateFiles(directory, "*.jar", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!)))
                        {
                            return name;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A folder that cannot be listed is simply not compared.
                    }
                }

                return null;
            });

            if (same is not null && ReferenceEquals(_buildPreview, preview))
            {
                BuildPreviewSameNote = Localize("Code_PreviewSame", "You already have a build with the same mods: “{0}”. This one is added as a separate build; that one is not changed.", same);
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[code] could not compare with the builds here: {ex.Message}");
        }
    }

    private void ClearBuildPreview(bool keepCode = false)
    {
        _buildPreview = null;
        _buildPreviewSource = string.Empty;
        _buildPreviewFromFriendBox = false;

        if (!keepCode)
        {
            BuildPreviewCode = string.Empty;
        }

        BuildPreviewName = string.Empty;
        BuildPreviewNameNote = string.Empty;
        BuildPreviewSameNote = string.Empty;
        BuildPreviewVersion = string.Empty;
        BuildPreviewLoader = string.Empty;
        BuildPreviewTotals = string.Empty;
        BuildPreviewRows = Array.Empty<BuildPreviewRow>();
        BuildPreviewMissing = Array.Empty<string>();
        BuildPreviewNeeds = Array.Empty<BuildPreviewNeed>();
        BuildPreviewBusyText = string.Empty;
        BuildPreviewProblemTitle = string.Empty;
        BuildPreviewProblemText = string.Empty;
        BuildPreviewProblemLines = Array.Empty<string>();
        BuildPreviewNote = string.Empty;
    }

    // ===================== The player's answer =====================

    /// <summary>
    /// "Cancel", the cross and Esc. Nothing was created, so there is nothing to undo: the
    /// dialog goes, and the code stays wherever the player had it.
    /// </summary>
    [RelayCommand]
    private void CancelBuildPreview()
    {
        switch (BuildPreviewState)
        {
            case BuildPreviewState.None:
                return;

            case BuildPreviewState.Fetching:
                // The one waiting for the answer says so in the status line when it wakes.
                _buildPreviewFetch?.Cancel();
                break;

            case BuildPreviewState.Ready:
                Status = Localize("Code_PreviewCancelled", "The build was not added: nothing was created or downloaded.");
                break;

            case BuildPreviewState.Problem:
                // The dialog has said it; the same sentence beside the button would say it twice.
                CloseBuildCodeNote();
                break;
        }

        BuildPreviewState = BuildPreviewState.None;
        ClearBuildPreview();
    }

    /// <summary>"Add the build": from here on everything goes as it did before there was a preview.</summary>
    [RelayCommand]
    private async Task ConfirmBuildPreviewAsync()
    {
        if (BuildPreviewState != BuildPreviewState.Ready || _buildPreview is not { } preview)
        {
            return;
        }

        if (IsBusy || IsModsBusy)
        {
            BuildPreviewNote = Localize("Friends_BoxBusy", "The launcher is busy with a build right now. Wait until it is done and press the button again.");
            return;
        }

        var payload = preview.Payload with { Name = BuildPreviewWantedName() };
        var source = _buildPreviewSource;
        var fromFriendBox = _buildPreviewFromFriendBox;

        BuildPreviewState = BuildPreviewState.None;
        ClearBuildPreview();

        // The code has been used: the box it was typed into need not keep it.
        if (fromFriendBox || (source.Length > 0 && string.Equals((FriendInviteEntry ?? string.Empty).Trim(), source, StringComparison.Ordinal)))
        {
            FriendInviteEntry = string.Empty;
        }

        CloseBuildCodeNote();

        var instance = await AddBuildFromPayloadAsync(payload, showBuild: true);

        if (instance is null)
        {
            BuildCodeNote = Status;
            return;
        }

        // The importer's own last word: anything but "installed" names files that did not come.
        var imported = Localize("Status_ModpackInstalled", "Modpack \"{0}\" installed", payload.Name);
        var whole = string.Equals(Status, imported, StringComparison.Ordinal);

        if (preview.Missing.Count > 0)
        {
            var names = string.Join(", ", preview.Missing);

            // Said once more, and where it stays on screen: the status line moves on.
            BuildCodeNote = whole
                ? Localize("Code_PreviewAddedMissing", "The build “{0}” is added. These files were not in the code - get them from your friend and put them into the build: {1}", instance.Name, names)
                : Status + " " + Localize("Code_PreviewAlsoMissing", "Not in the code, get them from your friend: {0}", names);

            if (whole)
            {
                Status = Localize("Code_AddedPartial", "Build “{0}” added. Not in the code, add by hand: {1}", instance.Name, names);
            }

            return;
        }

        if (whole)
        {
            Status = Localize("Code_Added", "Build “{0}” added from the code", instance.Name);
        }
        else
        {
            BuildCodeNote = Status;
        }
    }
}
