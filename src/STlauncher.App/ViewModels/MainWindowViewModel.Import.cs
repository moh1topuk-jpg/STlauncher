using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Import;

namespace STlauncher.App.ViewModels;

/// <summary>One found build, with the tick the player puts next to it.</summary>
public partial class ImportCandidate : ObservableObject
{
    public ImportCandidate(ExternalInstance instance, string sourceLabel, string? problemLabel)
    {
        Instance = instance;
        SourceLabel = sourceLabel;
        ProblemLabel = problemLabel;
        IsSelected = instance.IsUsable;
    }

    public ExternalInstance Instance { get; }

    public string SourceLabel { get; }

    /// <summary>Why it cannot be imported, in words. Empty when it can.</summary>
    public string? ProblemLabel { get; }

    public string Name => Instance.Name;

    public string Summary => Instance.Loader == Core.Loaders.LoaderKind.Vanilla
        ? Instance.VersionId
        : $"{Instance.VersionId} · {Instance.Loader}";

    public bool IsUsable => Instance.IsUsable;

    [ObservableProperty]
    private bool _isSelected;
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
    private void CloseImport() => IsImportOpen = false;

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

            var found = await Task.Run(() => ExternalInstanceScanner.ScanAll());

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
            // A build linked in an earlier import is still sitting in the other launcher's
            // folder, so the scan finds it again. Offering it as new would duplicate it.
            var instance = IsAlreadyLinked(item)
                ? item with { Problem = ExternalInstanceProblem.AlreadyImported }
                : item;

            var candidate = new ImportCandidate(instance, SourceLabel(instance.Source), ProblemLabel(instance.Problem));

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

        // Counted after the duplicate check, not from the raw scan: a build imported
        // last time is found again but is not ready to import.
        var usable = ImportCandidates.Count(c => c.IsUsable);

        ImportStatus = found.Count == 0
            ? Localize("Import_NothingFound", "No builds from other launchers were found.")
            : Localize("Import_Found", "Found: {0}, of them ready to import: {1}", found.Count, usable);

        RefreshImportSummary();
    }

    /// <summary>
    /// Copying is the expensive choice, so the size is on screen before the decision, not
    /// after it.
    /// </summary>
    private void RefreshImportSummary()
    {
        var selected = ImportCandidates.Where(c => c.IsSelected && c.IsUsable).ToList();

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

                AppendConsole($"[import] {candidate.Name} -> {result.Instance.Id} ({mode})");
            }

            ApplyBuildFilter();
            ImportStatus = Localize("Import_Done", "Imported: {0}", imported);
            IsImportOpen = false;

            Status = ImportStatus;
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

    private static string SourceLabel(ExternalLauncherKind kind) => kind switch
    {
        ExternalLauncherKind.DotMinecraft => ".minecraft",
        ExternalLauncherKind.Prism => "Prism Launcher",
        ExternalLauncherKind.MultiMc => "MultiMC",
        ExternalLauncherKind.CurseForge => "CurseForge",
        ExternalLauncherKind.Modrinth => "Modrinth App",
        ExternalLauncherKind.GdLauncher => "GDLauncher",
        ExternalLauncherKind.AtLauncher => "ATLauncher",
        _ => string.Empty
    };

    /// <summary>
    /// A build that cannot be imported is still listed, with the reason. One that vanishes
    /// without explanation looks like a build the launcher lost.
    /// </summary>
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
