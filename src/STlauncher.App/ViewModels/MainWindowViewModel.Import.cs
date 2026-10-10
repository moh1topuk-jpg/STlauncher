using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Import;

namespace STlauncher.App.ViewModels;

/// <summary>One found build, with the tick the player puts next to it.</summary>
public partial class ImportCandidate : ObservableObject
{
    public ImportCandidate(ExternalInstance instance, string sourceLabel, string? problemLabel, bool preselect = true)
    {
        Instance = instance;
        SourceLabel = sourceLabel;
        ProblemLabel = problemLabel;
        IsSelected = preselect && instance.IsUsable;
    }

    public ExternalInstance Instance { get; }

    public string SourceLabel { get; }

    /// <summary>Why it cannot be imported, in words. Empty when it can.</summary>
    public string? ProblemLabel { get; }

    public string Name => Instance.Name;

    public string Summary
    {
        get
        {
            // A .minecraft profile is named freely; the real version sits next to it.
            var version = Instance.HasOwnProfile && !string.IsNullOrWhiteSpace(Instance.GameVersion) &&
                          !string.Equals(Instance.GameVersion, Instance.VersionId, StringComparison.Ordinal)
                ? $"{Instance.VersionId} ({Instance.GameVersion})"
                : Instance.VersionId;

            if (string.IsNullOrWhiteSpace(version))
            {
                version = MainWindowViewModel.Localize("Import_VersionUnknown", "version not known");
            }

            var text = Instance.Loader == Core.Loaders.LoaderKind.Vanilla
                ? version
                : $"{version} · {Instance.Loader}";

            return Instance.VersionInferred
                ? $"{text} · {MainWindowViewModel.Localize("Import_VersionInferred", "worked out from the mods")}"
                : text;
        }
    }

    /// <summary>
    /// What the player has to do after importing, when anything. A build whose version
    /// nobody wrote down still imports fine - it just needs one picked in its settings.
    /// </summary>
    public string Note => Instance.IsUsable && !Instance.HasKnownVersion
        ? MainWindowViewModel.Localize("Import_PickVersionLater", "pick the version and loader in the build settings after importing")
        : string.Empty;

    public bool HasNote => Note.Length > 0;

    /// <summary>
    /// What comes along from the old launcher's settings of this build: said before the
    /// import, so a memory limit or a Java option never appears in a build unannounced.
    /// </summary>
    public string CarriedLabel
    {
        get
        {
            if (!Instance.IsUsable || Instance.Settings is not { } settings)
            {
                return string.Empty;
            }

            var parts = new List<string>();

            if (settings.MaxMemoryMb is { } memory)
            {
                parts.Add(MainWindowViewModel.Localize("Import_CarriedMemory", "memory {0} MB", memory));
            }

            if (settings is { Width: { } width, Height: { } height })
            {
                parts.Add(MainWindowViewModel.Localize("Import_CarriedWindow", "window {0}x{1}", width, height));
            }

            if (settings.JvmArguments.Count > 0)
            {
                parts.Add(MainWindowViewModel.Localize("Import_CarriedJvm", "Java options: {0}", string.Join(" ", settings.JvmArguments)));
            }

            return parts.Count == 0
                ? string.Empty
                : MainWindowViewModel.Localize("Import_Carried", "Settings that come along: {0}", string.Join(" · ", parts));
        }
    }

    public bool HasCarried => CarriedLabel.Length > 0;

    /// <summary>Java options of the old build that are left behind, and why.</summary>
    public string DroppedLabel
    {
        get
        {
            if (!Instance.IsUsable || Instance.Settings is not { DroppedJvmArguments.Count: > 0 } settings)
            {
                return string.Empty;
            }

            // Enough to recognise them by; a classpath can run to a screen of text.
            var shown = settings.DroppedJvmArguments
                .Take(3)
                .Select(a => a.Length > 36 ? a[..36] + "…" : a)
                .ToList();

            var list = string.Join(", ", shown);

            if (settings.DroppedJvmArguments.Count > shown.Count)
            {
                list = MainWindowViewModel.Localize("Import_CarriedDroppedMore", "{0} and {1} more", list, settings.DroppedJvmArguments.Count - shown.Count);
            }

            return MainWindowViewModel.Localize(
                "Import_CarriedDropped",
                "Java options left behind: {0}. They can run foreign code or point at files.",
                list);
        }
    }

    public bool HasDropped => DroppedLabel.Length > 0;

    public bool IsUsable => Instance.IsUsable;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>A folder the sweep of the other drives found, waiting for the player to open it.</summary>
public partial class SweepFolder : ObservableObject
{
    public SweepFolder(string path, string kindLabel)
    {
        Path = path;
        KindLabel = kindLabel;
    }

    public string Path { get; }

    public string KindLabel { get; }

    /// <summary>What opening it gave: how many builds, or that there were none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    private string _result = string.Empty;

    public bool IsShown => Result.Length > 0;
}

/// <summary>
/// Importing builds that already exist on the machine. The point is that moving to this
/// launcher should not mean rebuilding everything by hand.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<ImportCandidate> ImportCandidates { get; } = new();

    [ObservableProperty]
    private bool _isImportOpen;

    [ObservableProperty]
    private bool _isImportBusy;

    [ObservableProperty]
    private string _importStatus = string.Empty;

    /// <summary>Folder the player pointed at by hand, for builds in unusual places.</summary>
    [ObservableProperty]
    private string _importFolder = string.Empty;

    /// <summary>
    /// False leaves the files where they are; true copies them in. Kept as a bool rather
    /// than an enum so the two radio buttons bind without a converter.
    /// </summary>
    [ObservableProperty]
    private bool _importByCopying;

    public bool ImportByLinking => !ImportByCopying;

    partial void OnImportByCopyingChanged(bool value)
    {
        OnPropertyChanged(nameof(ImportByLinking));
        RefreshImportSummary();
    }

    /// <summary>How many builds are ticked, and what importing them will cost.</summary>
    [ObservableProperty]
    private string _importSummary = string.Empty;

    /// <summary>Set by the view: the folder picker needs a window, which a view model has no business holding.</summary>
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    // ===================== The offer =====================
    // Nobody reads a menu to find out that their old builds can be brought over. So the
    // launcher looks for them itself at startup and, when it finds some, says so on the
    // main screen with one button - until the player imports them or closes the card.

    /// <summary>What the startup scan found, kept so the dialog opens with it at once.</summary>
    private IReadOnlyList<ExternalInstance> _importableBuilds = Array.Empty<ExternalInstance>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportSuggestion))]
    private string _importSuggestionText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportSuggestion))]
    private bool _importSuggestionDismissed;

    public bool HasImportSuggestion => !ImportSuggestionDismissed && ImportSuggestionText.Length > 0;

    private async Task LookForImportableBuildsAsync()
    {
        if (ImportSuggestionDismissed)
        {
            return;
        }

        try
        {
            var scanned = new List<string>();
            var found = await Task.Run(() => ExternalInstanceScanner.ScanAll(null, scanned));
            AppendConsole($"[import] looked in {scanned.Count} place(s): {string.Join("; ", scanned)}");
            AppendConsole($"[import] found {found.Count} build(s): {string.Join("; ", found.Select(f => $"{f.Name} [{f.Source}, {f.VersionId}{(f.Problem == ExternalInstanceProblem.None ? string.Empty : ", " + f.Problem)}]"))}");

            // Only builds that would actually import, and are not here already.
            var usable = found.Where(i => i.IsUsable && !IsAlreadyLinked(i)).ToList();

            if (usable.Count == 0)
            {
                return;
            }

            _importableBuilds = found;

            var launchers = usable
                .Select(i => SourceLabel(i))
                .Where(l => l.Length > 0)
                .Distinct()
                .Take(3)
                .ToList();

            ImportSuggestionText = launchers.Count == 0
                ? Localize("Import_SuggestPlain", "Found {0} build(s) from other launchers", usable.Count)
                : Localize("Import_Suggest", "Found {0} build(s) in {1}", usable.Count, string.Join(", ", launchers));
        }
        catch (Exception ex)
        {
            AppendConsole($"[import] scan failed: {ex.Message}");
        }
    }

    /// <summary>Opens the import dialog with what the startup scan found, no second scan.</summary>
    [RelayCommand]
    private void AcceptImportSuggestion()
    {
        Section = ShellSection.Builds;
        IsImportOpen = true;

        if (_importableBuilds.Count > 0)
        {
            ShowCandidates(_importableBuilds);
        }
    }

    [RelayCommand]
    private void DismissImportSuggestion()
    {
        ImportSuggestionDismissed = true;
        PersistSettings();
    }

    [RelayCommand]
    private async Task OpenImport()
    {
        IsImportOpen = true;

        if (ImportCandidates.Count == 0)
        {
            await ScanForInstancesAsync();
        }
    }

    [RelayCommand]
    private void CloseImport()
    {
        // A sweep nobody is looking at has no business walking the drives.
        _sweepCancellation?.Cancel();
        IsImportOpen = false;
    }

    // ===================== Other drives =====================
    // The usual places are looked at on their own. Walking whole drives is another
    // matter - slow, and nobody asked - so it happens only on this button, for a bounded
    // time, and ends with a list of folders. What is in a folder is read when the player
    // opens it, and those builds arrive unticked: nothing here imports by itself.

    public ObservableCollection<SweepFolder> SweepFolders { get; } = new();

    public bool HasSweepFolders => SweepFolders.Count > 0;

    private CancellationTokenSource? _sweepCancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SweepButtonLabel))]
    [NotifyPropertyChangedFor(nameof(CanSweep))]
    private bool _isSweeping;

    public string SweepButtonLabel => IsSweeping
        ? Localize("Import_SweepStop", "Stop the search")
        : Localize("Import_Sweep", "Search other drives");

    /// <summary>The same button starts the sweep and stops it, so it stays live while one runs.</summary>
    public bool CanSweep => IsSweeping || !IsImportBusy;

    partial void OnIsImportBusyChanged(bool value) => OnPropertyChanged(nameof(CanSweep));

    [RelayCommand]
    private async Task SweepDrivesAsync()
    {
        if (IsSweeping)
        {
            _sweepCancellation?.Cancel();
            return;
        }

        if (IsImportBusy)
        {
            return;
        }

        var roots = DriveSweep.OtherDriveRoots();

        if (roots.Count == 0)
        {
            ImportStatus = Localize("Import_SweepNoDrives", "This computer has no other drives.");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _sweepCancellation = cancellation;

        try
        {
            IsImportBusy = true;
            IsSweeping = true;
            SweepFolders.Clear();
            OnPropertyChanged(nameof(HasSweepFolders));

            var progress = new Progress<string>(folder =>
            {
                if (IsSweeping)
                {
                    ImportStatus = Localize("Import_Sweeping", "Looking in {0}…", folder);
                }
            });

            ImportStatus = Localize("Import_Sweeping", "Looking in {0}…", string.Join(", ", roots));

            var token = cancellation.Token;
            var result = await Task.Run(() => DriveSweep.Sweep(roots, progress: progress, cancellationToken: token), token);

            IsSweeping = false;
            ShowSweepHits(result.Hits);

            AppendConsole($"[import] sweep of {string.Join(", ", roots)}: {result.FoldersVisited} folder(s) seen, {result.Hits.Count} hit(s){(result.TimedOut ? ", time ran out" : string.Empty)}: {string.Join("; ", result.Hits.Select(h => h.Path))}");

            ImportStatus = SweepFolders.Count == 0
                ? Localize("Import_SweepNothing", "No game folders on the other drives. If the build sits deeper, point at its folder by hand.")
                : result.TimedOut || result.HitLimitReached
                    ? Localize("Import_SweepFoundPartial", "Folders found: {0}. The search ran out of time before seeing everything; if yours is missing, point at it by hand.", SweepFolders.Count)
                    : Localize("Import_SweepFound", "Folders found: {0}. Press “Show builds” next to the one you want.", SweepFolders.Count);
        }
        catch (OperationCanceledException)
        {
            ImportStatus = Localize("Import_SweepCancelled", "The search was stopped.");
        }
        catch (Exception ex)
        {
            ImportStatus = Localize("Import_Failed", "Import failed: {0}", ex.Message);
        }
        finally
        {
            _sweepCancellation = null;
            IsSweeping = false;
            IsImportBusy = false;
        }
    }

    private void ShowSweepHits(IReadOnlyList<SweepHit> hits)
    {
        SweepFolders.Clear();

        foreach (var hit in hits)
        {
            // A folder whose builds are already on the list was found by the ordinary scan.
            var prefix = System.IO.Path.TrimEndingDirectorySeparator(hit.Path) + System.IO.Path.DirectorySeparatorChar;

            if (ImportCandidates.Any(c =>
                    (c.Instance.GameDirectory + System.IO.Path.DirectorySeparatorChar).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var label = SourceLabel(hit.Kind);

            SweepFolders.Add(new SweepFolder(
                hit.Path,
                label.Length > 0 ? label : Localize("Import_SweepKindLauncher", "a launcher's folder")));
        }

        OnPropertyChanged(nameof(HasSweepFolders));
    }

    /// <summary>Reads the builds of one found folder into the list, unticked.</summary>
    [RelayCommand]
    private async Task ShowSweepFolderAsync(SweepFolder? folder)
    {
        if (folder is null || IsImportBusy)
        {
            return;
        }

        try
        {
            IsImportBusy = true;
            ImportStatus = Localize("Import_Scanning", "Looking for builds…");

            var found = await Task.Run(() => ExternalInstanceScanner.ScanUnknownFolder(folder.Path));
            var added = 0;

            foreach (var item in found)
            {
                if (ImportCandidates.Any(c =>
                        string.Equals(c.Instance.GameDirectory, item.GameDirectory, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Instance.VersionId, item.VersionId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                AddCandidate(item, preselect: false);
                added++;
            }

            folder.Result = found.Count == 0
                ? Localize("Import_SweepShownNone", "no builds in it")
                : Localize("Import_SweepShown", "builds: {0}", found.Count);

            ImportStatus = Localize(
                "Import_Found",
                "Found: {0}, of them ready to import: {1}",
                ImportCandidates.Count,
                ImportCandidates.Count(c => c.IsUsable));

            AppendConsole($"[import] {folder.Path}: {found.Count} build(s), {added} new");
            RefreshImportSummary();
        }
        catch (Exception ex)
        {
            ImportStatus = Localize("Import_Failed", "Import failed: {0}", ex.Message);
        }
        finally
        {
            IsImportBusy = false;
        }
    }

    /// <summary>Looks through every launcher the scanner knows about.</summary>
    [RelayCommand]
    private async Task ScanForInstancesAsync()
    {
        if (IsImportBusy)
        {
            return;
        }

        try
        {
            IsImportBusy = true;
            ImportStatus = Localize("Import_Scanning", "Looking for builds…");

            var scanned = new List<string>();
            var found = await Task.Run(() => ExternalInstanceScanner.ScanAll(null, scanned));
            AppendConsole($"[import] looked in {scanned.Count} place(s): {string.Join("; ", scanned)}");
            AppendConsole($"[import] found {found.Count} build(s): {string.Join("; ", found.Select(f => $"{f.Name} [{f.Source}, {f.VersionId}{(f.Problem == ExternalInstanceProblem.None ? string.Empty : ", " + f.Problem)}]"))}");

            ShowCandidates(found);
        }
        catch (Exception ex)
        {
            ImportStatus = Localize("Import_Failed", "Import failed: {0}", ex.Message);
        }
        finally
        {
            IsImportBusy = false;
        }
    }

    /// <summary>Scans a folder the player chose, for builds in places nobody can guess.</summary>
    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        if (IsImportBusy)
        {
            return;
        }

        var folder = ImportFolder;

        if (string.IsNullOrWhiteSpace(folder) && PickFolderAsync is not null)
        {
            folder = await PickFolderAsync() ?? string.Empty;
            ImportFolder = folder;
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        try
        {
            IsImportBusy = true;
            ImportStatus = Localize("Import_Scanning", "Looking for builds…");

            var found = await Task.Run(() => ExternalInstanceScanner.ScanUnknownFolder(folder));

            ShowCandidates(found);
        }
        catch (Exception ex)
        {
            ImportStatus = Localize("Import_Failed", "Import failed: {0}", ex.Message);
        }
        finally
        {
            IsImportBusy = false;
        }
    }

    private void ShowCandidates(IReadOnlyList<ExternalInstance> found)
    {
        ImportCandidates.Clear();

        foreach (var item in found)
        {
            AddCandidate(item, preselect: true);
        }

        // Counted after the duplicate check, not from the raw scan: a build imported
        // last time is found again but is not ready to import.
        var usable = ImportCandidates.Count(c => c.IsUsable);

        ImportStatus = found.Count == 0
            ? Localize("Import_NothingFound", "No builds from other launchers were found.")
            : Localize("Import_Found", "Found: {0}, of them ready to import: {1}", found.Count, usable);

        RefreshImportSummary();
    }

    private void AddCandidate(ExternalInstance item, bool preselect)
    {
        // A build linked in an earlier import is still sitting in the other launcher's
        // folder, so the scan finds it again. Offering it as new would duplicate it.
        var instance = item.IsUsable && IsAlreadyLinked(item)
            ? item with { Problem = ExternalInstanceProblem.AlreadyImported }
            : item;

        var candidate = new ImportCandidate(instance, SourceLabel(instance), ProblemLabel(instance), preselect);

        // Ticking a build changes what the copy will cost, and that number is the
        // whole basis for choosing between the two modes.
        candidate.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImportCandidate.IsSelected))
            {
                RefreshImportSummary();
            }
        };

        ImportCandidates.Add(candidate);
    }

    /// <summary>
    /// True when the copy would take from a shared .minecraft, where only the game's own
    /// folders are taken - worth saying, because screenshots and map data stay behind.
    /// </summary>
    [ObservableProperty]
    private bool _importCopiesFromShared;

    /// <summary>
    /// Copying is the expensive choice, so the size is on screen before the decision, not
    /// after it.
    /// </summary>
    private void RefreshImportSummary()
    {
        var selected = ImportCandidates.Where(c => c.IsSelected && c.IsUsable).ToList();

        ImportCopiesFromShared = ImportByCopying && selected.Any(c => c.Instance.SharesGameDirectory);

        if (selected.Count == 0)
        {
            ImportSummary = string.Empty;
            return;
        }

        if (!ImportByCopying)
        {
            ImportSummary = Localize("Import_SelectedLink", "Selected: {0}", selected.Count);
            return;
        }

        var bytes = selected.Sum(c => InstanceImporter.EstimateCopySize(c.Instance));

        ImportSummary = Localize(
            "Import_SelectedCopy",
            "Selected: {0}, about {1} will be copied",
            selected.Count,
            FormatSize(bytes));
    }

    [RelayCommand]
    private async Task ImportSelectedAsync()
    {
        if (IsImportBusy)
        {
            return;
        }

        var selected = ImportCandidates.Where(c => c.IsSelected && c.IsUsable).ToList();

        if (selected.Count == 0)
        {
            return;
        }

        try
        {
            IsImportBusy = true;

            var mode = ImportByCopying ? ImportMode.Copy : ImportMode.Link;
            var imported = 0;
            var skippedLinks = new List<string>();

            foreach (var candidate in selected)
            {
                ImportStatus = Localize("Import_Importing", "Importing {0}…", candidate.Name);

                var progress = new Progress<string>(part =>
                    ImportStatus = Localize("Import_ImportingPart", "{0}: {1}", candidate.Name, part));

                var result = await _importer.ImportAsync(
                    candidate.Instance,
                    mode,
                    UniqueInstanceName(candidate.Name),
                    progress);

                _allInstances.Add(result.Instance);
                imported++;

                AppendConsole($"[import] {candidate.Name} -> {result.Instance.Id} ({mode}, {result.CopiedFiles} file(s))");

                if (result.CarriedSettings is { } carried)
                {
                    AppendConsole($"[import]   settings carried: memory {carried.MaxMemoryMb?.ToString() ?? "-"} MB, window {carried.Width?.ToString() ?? "-"}x{carried.Height?.ToString() ?? "-"}, JVM [{string.Join(' ', carried.JvmArguments)}], dropped [{string.Join(' ', carried.DroppedJvmArguments)}]");
                }

                if (result.SkippedLinks.Count > 0)
                {
                    AppendConsole($"[import]   links not copied: {string.Join("; ", result.SkippedLinks)}");
                    skippedLinks.AddRange(result.SkippedLinks.Take(3).Select(link => $"{candidate.Name}: {link}"));
                }
            }

            ApplyBuildFilter();
            ImportStatus = Localize("Import_Done", "Imported: {0}", imported);

            // A build whose worlds were behind a link arrived without them. That is the
            // one thing about a finished import the player must not have to discover.
            if (skippedLinks.Count > 0)
            {
                ImportStatus += " " + Localize(
                    "Import_LinksSkipped",
                    "Links to other folders inside the builds were not copied: {0}",
                    string.Join("; ", skippedLinks.Take(4)));
            }

            IsImportOpen = false;

            // The offer has served its purpose.
            ImportSuggestionText = string.Empty;

            Status = ImportStatus;
        }
        catch (LinkedSourceException ex)
        {
            ImportStatus = Localize("Import_Failed", "Import failed: {0}", LinkExplanation(ex.LinkPath, ex.Target));
            AppendConsole($"[import] {ex.Message}");
        }
        catch (Exception ex)
        {
            ImportStatus = Localize("Import_Failed", "Import failed: {0}", ex.Message);
            AppendConsole($"[import] {ex}");
        }
        finally
        {
            IsImportBusy = false;
        }
    }

    /// <summary>
    /// True when an existing build already points at this folder and version. Only linked
    /// imports can be recognised: a copy has no tie back to where it came from.
    /// </summary>
    private bool IsAlreadyLinked(ExternalInstance candidate)
        => _allInstances.Any(i =>
            !string.IsNullOrWhiteSpace(i.ExternalGameDirectory) &&
            string.Equals(
                System.IO.Path.GetFullPath(i.ExternalGameDirectory!).TrimEnd(System.IO.Path.DirectorySeparatorChar),
                System.IO.Path.GetFullPath(candidate.GameDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.ProfileVersionId ?? i.VersionId, candidate.VersionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The launcher's own name for a fork found by its files, the kind's name otherwise.</summary>
    private static string SourceLabel(ExternalInstance instance)
        => instance.LauncherName is { Length: > 0 } name ? name : SourceLabel(instance.Source);

    private static string SourceLabel(ExternalLauncherKind kind) => kind switch
    {
        ExternalLauncherKind.DotMinecraft => ".minecraft",
        ExternalLauncherKind.Prism => "Prism Launcher",
        ExternalLauncherKind.PolyMc => "PolyMC",
        ExternalLauncherKind.MultiMc => "MultiMC",
        ExternalLauncherKind.CurseForge => "CurseForge",
        ExternalLauncherKind.Modrinth => "Modrinth App",
        ExternalLauncherKind.GdLauncher => "GDLauncher",
        ExternalLauncherKind.AtLauncher => "ATLauncher",
        ExternalLauncherKind.Ftb => "FTB App",
        ExternalLauncherKind.Technic => "Technic",
        ExternalLauncherKind.Xmcl => "XMCL",
        _ => string.Empty
    };

    /// <summary>
    /// A build that cannot be imported is still listed, with the reason. One that vanishes
    /// without explanation looks like a build the launcher lost.
    /// </summary>
    private static string? ProblemLabel(ExternalInstance instance)
        => instance.Problem == ExternalInstanceProblem.SourceIsLink
            ? LinkExplanation(null, instance.LinkTarget)
            : ProblemLabel(instance.Problem);

    /// <summary>
    /// Why a link is not imported, with where it leads when that can be read - so the
    /// player can point at the real folder by hand, which is their decision to make.
    /// </summary>
    private static string LinkExplanation(string? path, string? target)
    {
        var text = string.IsNullOrWhiteSpace(target)
            ? Localize("Import_ProblemLinkUnknown", "this is a link to another folder - the launcher does not go through links. Point at the real folder by hand")
            : Localize("Import_ProblemLink", "this is a link to {0} - the launcher does not go through links. Point at that folder by hand", target!);

        return string.IsNullOrWhiteSpace(path) ? text : $"{path}: {text}";
    }

    // ===================== Java options of a build =====================

    /// <summary>
    /// Extra JVM arguments of the selected build. They usually arrive with an imported
    /// build; here they can be seen, changed and removed. Whatever is typed goes through
    /// the same allowlist as what is imported, because the field is also where an
    /// argument from a guide on the internet ends up.
    /// </summary>
    public string BuildJvmArgs
    {
        get => SelectedInstance?.ExtraJvmArgs ?? string.Empty;
        set
        {
            if (SelectedInstance is null)
            {
                return;
            }

            var filtered = Core.Launch.JvmArgumentAllowlist.Filter(value);
            var kept = filtered.Kept.Count == 0 ? null : filtered.KeptText;

            if (!string.Equals(kept, SelectedInstance.ExtraJvmArgs, StringComparison.Ordinal))
            {
                SelectedInstance.ExtraJvmArgs = kept;

                try
                {
                    _instances.Save(SelectedInstance);
                }
                catch (Exception ex)
                {
                    Status = Localize("Error_SaveBuild", "Failed to save the build: {0}", ex.Message);
                }
            }

            if (filtered.Dropped.Count > 0)
            {
                Status = Localize(
                    "Settings_JvmArgsDropped",
                    "Not accepted: {0}. Only memory sizes, -XX switches and --add-opens are allowed.",
                    string.Join(" ", filtered.Dropped.Take(3).Select(a => a.Length > 36 ? a[..36] + "…" : a)));
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// What the build's stored arguments turn into on the command line. Filtered again
    /// here, at the last step: the definition file can be edited, restored from a backup
    /// or handed over, and nothing in it is taken on trust.
    /// </summary>
    private static IReadOnlyList<string> LaunchJvmArgs(Core.Instances.Instance? instance)
    {
        var kept = Core.Launch.JvmArgumentAllowlist.Filter(instance?.ExtraJvmArgs).Kept;

        if (kept.Count == 0)
        {
            return Array.Empty<string>();
        }

        // A switch from another Java version - a collector that was removed since - would
        // stop the JVM before the game starts. With this it is skipped with a warning.
        return new[] { "-XX:+IgnoreUnrecognizedVMOptions" }.Concat(kept).ToList();
    }

    private static string? ProblemLabel(ExternalInstanceProblem problem) => problem switch
    {
        ExternalInstanceProblem.None => null,
        ExternalInstanceProblem.MissingVersionJson => Localize(
            "Import_ProblemNoProfile", "no version profile - the build is incomplete"),
        ExternalInstanceProblem.BrokenVersionJson => Localize(
            "Import_ProblemBrokenProfile", "the version profile cannot be read"),
        ExternalInstanceProblem.IncompleteProfile => Localize(
            "Import_ProblemIncomplete", "the profile describes no way to start the game"),
        ExternalInstanceProblem.AlreadyImported => Localize(
            "Import_ProblemAlready", "already imported"),
        _ => null
    };

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return Localize("Size_Gb", "{0:F1} GB", bytes / 1024d / 1024 / 1024);
        }

        return bytes >= 1024L * 1024
            ? Localize("Size_Mb", "{0:F0} MB", bytes / 1024d / 1024)
            : Localize("Size_Kb", "{0} KB", Math.Max(1, bytes / 1024));
    }
}
