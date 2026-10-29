using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Content;
using STlauncher.Core.Instances;
using STlauncher.Core.Launch;
using STlauncher.Core.Loaders;

namespace STlauncher.App.ViewModels;

/// <summary>One remembered state of a build in the "Put back a working state" panel.</summary>
public sealed partial class BuildStateItem : ObservableObject
{
    public BuildStateItem(BuildState state, string dateLabel, string summary, string sizeLabel, IReadOnlyList<string> lines, bool isUndo)
    {
        State = state;
        DateLabel = dateLabel;
        Summary = summary;
        SizeLabel = sizeLabel;
        Lines = lines;
        IsUndo = isUndo;
    }

    public BuildState State { get; }

    public string DateLabel { get; }

    public string Summary { get; }

    public string SizeLabel { get; }

    /// <summary>What a restore to it would change, line by line.</summary>
    public IReadOnlyList<string> Lines { get; }

    public bool HasChanges => Lines.Count > 0;

    /// <summary>The state from just before the last restore: going back to it undoes that restore.</summary>
    public bool IsUndo { get; }

    /// <summary>Its list is open and the confirming button is shown.</summary>
    [ObservableProperty]
    private bool _isOpen;
}

/// <summary>
/// "Put the build back as it was": the crash card offers the last state the game worked
/// on when the mods changed since, with the exact list of what would change; the build's
/// "…" menu lists every kept state. Nothing moves without the click under that list.
/// See <see cref="BuildStates"/> for what is kept and how.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The build's settings and records at the time of a state, kept with it.</summary>
    private sealed record BuildStateDefinition(
        string? VersionId,
        LoaderKind Loader,
        string? LoaderVersion,
        List<string> CatalogItems,
        List<InstalledModRecord> Records);

    private const int RollbackLinesShown = 10;

    // ===================== Crash card =====================

    [ObservableProperty]
    private bool _hasRollbackOffer;

    [ObservableProperty]
    private string _rollbackTitle = string.Empty;

    [ObservableProperty]
    private string _rollbackButtonLabel = string.Empty;

    public ObservableCollection<string> RollbackLines { get; } = new();

    /// <summary>Said after a restore, with the way to undo it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRollbackDone))]
    private string _rollbackDoneText = string.Empty;

    public bool HasRollbackDone => RollbackDoneText.Length > 0;

    [ObservableProperty]
    private bool _isRollbackBusy;

    private Instance? _rollbackInstance;
    private BuildState? _rollbackTarget;

    // ===================== Builds page panel =====================

    [ObservableProperty]
    private bool _isBuildStatesShown;

    public ObservableCollection<BuildStateItem> BuildStateItems { get; } = new();

    public bool HasBuildStates => BuildStateItems.Count > 0;

    [ObservableProperty]
    private bool _isBuildStatesLoading;

    /// <summary>The kept states of a build, or null where none are kept: a build in another launcher's folder.</summary>
    private BuildStates? StatesFor(Instance? instance)
        => instance is null || !string.IsNullOrWhiteSpace(instance.ExternalGameDirectory)
            ? null
            : new BuildStates(_instances.GameDirectory(instance));

    private static string DefinitionOf(Instance instance)
        => JsonSerializer.Serialize(new BuildStateDefinition(
            instance.VersionId,
            instance.Loader,
            instance.LoaderVersion,
            instance.EnabledCatalogItems.ToList(),
            instance.InstalledMods.ToList()));

    private static BuildStateDefinition? ReadDefinition(BuildState state)
    {
        if (string.IsNullOrWhiteSpace(state.Definition))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BuildStateDefinition>(state.Definition);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ===================== Around a launch =====================

    /// <summary>Before the game starts: the mods it starts on, when they are not the last ones that worked.</summary>
    private async Task<BuildStates?> BeforeLaunchStateAsync(Instance? instance)
    {
        var states = StatesFor(instance);

        if (states is null)
        {
            return null;
        }

        var definition = DefinitionOf(instance!);

        try
        {
            var pending = await Task.Run(() => states.BeforeLaunch(definition));

            if (pending is not null)
            {
                AppendConsole($"[states] kept the mods this launch starts on ({pending.ModCount} mods)");
            }

            return states;
        }
        catch (Exception ex)
        {
            // Remembering is a nicety; the game starts regardless.
            AppendConsole($"[states] could not keep the build's state: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// After the game closed: a clean session makes the state one to come back to; a crash
    /// on mods that changed since the last working state puts the offer on the crash card.
    /// </summary>
    private async Task AfterLaunchStateAsync(BuildStates? states, Instance? instance, LaunchResult result)
    {
        if (states is null || instance is null)
        {
            return;
        }

        var worked = result.Started && result.ExitCode == 0 && !result.IsFailedLaunch;
        var definition = DefinitionOf(instance);

        try
        {
            await Task.Run(() => states.AfterLaunch(worked, definition));
        }
        catch (Exception ex)
        {
            AppendConsole($"[states] could not record the outcome: {ex.Message}");
        }

        // Java that would not run says nothing about the mods.
        if (worked || result.StartError != JavaStartError.None)
        {
            return;
        }

        await OfferRollbackAsync(states, instance);
    }

    private async Task OfferRollbackAsync(BuildStates states, Instance instance)
    {
        try
        {
            var found = await Task.Run(() =>
            {
                var target = states.Offer();
                return target is null ? null : new { Target = target, Plan = states.Plan(target) };
            });

            if (found is null || !found.Plan.Any)
            {
                return;
            }

            var then = ReadDefinition(found.Target);
            var lines = DescribeRestore(instance, found.Plan, then);

            _rollbackInstance = instance;
            _rollbackTarget = found.Target;
            RollbackTitle = Localize(
                "Rollback_OfferTitle",
                "Before this, the build worked on {0} ({1}). Since then:",
                StateDate(found.Target),
                ModCountLabel(found.Target.ModCount));
            RollbackButtonLabel = Localize("Rollback_OfferButton", "Put the build back as it was on {0}", StateDay(found.Target));
            ReplaceLines(RollbackLines, lines);
            RollbackDoneText = string.Empty;
            HasRollbackOffer = true;
            AppendConsole($"[states] offered the state of {found.Target.Id}: {found.Plan.Mods.Count} mod change(s), {found.Plan.Config.Count} setting file(s)");
        }
        catch (Exception ex)
        {
            AppendConsole($"[states] could not compare with the last working state: {ex.Message}");
        }
    }

    private void ClearRollbackOffer()
    {
        HasRollbackOffer = false;
        RollbackDoneText = string.Empty;
        RollbackLines.Clear();
        _rollbackInstance = null;
        _rollbackTarget = null;
    }

    [RelayCommand]
    private void DismissRollbackOffer() => ClearRollbackOffer();

    /// <summary>The click under the list on the crash card.</summary>
    [RelayCommand]
    private async Task ApplyRollbackAsync()
    {
        if (_rollbackInstance is not { } instance || _rollbackTarget is not { } target)
        {
            return;
        }

        if (await RestoreStateAsync(instance, target) is { } done)
        {
            HasRollbackOffer = false;
            RollbackDoneText = done;

            // The one-click fix was about a file of the build that broke; it may be gone now.
            CrashFixLabel = string.Empty;
        }
    }

    /// <summary>Puts the build back as it was before the restore just made.</summary>
    [RelayCommand]
    private async Task UndoRollbackAsync()
    {
        var instance = _rollbackInstance ?? SelectedInstance;
        var states = StatesFor(instance);

        if (states is null || await Task.Run(() => states.LastUndo()) is not { } undo)
        {
            return;
        }

        if (await RestoreStateAsync(instance!, undo) is not null)
        {
            RollbackDoneText = string.Empty;
            Status = Localize("Rollback_Undone", "The restore is undone: the build is as it was before it.");
            RefreshBuildStatesIfShown();
        }
    }

    // ===================== Builds page =====================

    [RelayCommand]
    private async Task OpenBuildStatesAsync()
    {
        IsBuildStatesShown = true;
        await LoadBuildStatesAsync();
    }

    [RelayCommand]
    private void CloseBuildStates()
    {
        IsBuildStatesShown = false;
        BuildStateItems.Clear();
        OnPropertyChanged(nameof(HasBuildStates));
    }

    /// <summary>Opens one state's list of changes; the others close.</summary>
    [RelayCommand]
    private void ChooseBuildState(BuildStateItem? item)
    {
        foreach (var other in BuildStateItems)
        {
            other.IsOpen = ReferenceEquals(other, item) && !other.IsOpen;
        }
    }

    [RelayCommand]
    private async Task RestoreBuildStateAsync(BuildStateItem? item)
    {
        if (item is null || SelectedInstance is not { } instance)
        {
            return;
        }

        if (await RestoreStateAsync(instance, item.State) is { } done)
        {
            Status = done;
            await LoadBuildStatesAsync();
        }
    }

    private void RefreshBuildStatesIfShown()
    {
        if (IsBuildStatesShown)
        {
            _ = LoadBuildStatesAsync();
        }
    }

    private async Task LoadBuildStatesAsync()
    {
        var instance = SelectedInstance;
        var states = StatesFor(instance);
        BuildStateItems.Clear();

        if (states is null)
        {
            OnPropertyChanged(nameof(HasBuildStates));
            return;
        }

        try
        {
            IsBuildStatesLoading = true;

            var loaded = await Task.Run(() =>
            {
                var list = states.Working().Select(s => (State: s, Undo: false)).ToList();

                if (states.LastUndo() is { } undo)
                {
                    list.Insert(0, (undo, true));
                }

                return list.Select(x => (x.State, x.Undo, Plan: states.Plan(x.State), Cost: states.CostOf(x.State))).ToList();
            });

            if (!ReferenceEquals(instance, SelectedInstance))
            {
                return;
            }

            foreach (var (state, isUndo, plan, cost) in loaded)
            {
                var then = ReadDefinition(state);
                var lines = DescribeRestore(instance!, plan, then);

                var date = isUndo
                    ? Localize("Rollback_UndoDate", "Before the restore of {0}", StateDate(state))
                    : StateDate(state);

                BuildStateItems.Add(new BuildStateItem(
                    state,
                    date,
                    StateSummary(instance!, state, plan),
                    Localize("Rollback_Cost", "takes {0}", FormatSize(cost)),
                    lines,
                    isUndo));
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[states] could not list the build's states: {ex.Message}");
        }
        finally
        {
            IsBuildStatesLoading = false;
            OnPropertyChanged(nameof(HasBuildStates));
        }
    }

    // ===================== The restore itself =====================

    /// <summary>
    /// Moves what came after aside, brings back what was there, and puts the build's
    /// records and version back as they were. Returns what to tell the player, or null
    /// when nothing happened (and the status says why).
    /// </summary>
    private async Task<string?> RestoreStateAsync(Instance instance, BuildState target)
    {
        var states = StatesFor(instance);

        if (states is null || IsRollbackBusy)
        {
            return null;
        }

        if (IsGameRunning && IsSelectedBuild(instance))
        {
            Status = Localize("Rollback_GameRunning", "Close the game of this build first: its files cannot be changed while it runs.");
            return null;
        }

        try
        {
            IsRollbackBusy = true;
            var now = DefinitionOf(instance);
            var result = await Task.Run(() => states.Restore(target, now));

            if (ReadDefinition(target) is { } then)
            {
                await ApplyStateDefinitionAsync(instance, then);
            }

            // A catalog build: the catalog's own state is offered again, as a question.
            // Until the player takes it, Play leaves the restored files alone.
            if (!string.IsNullOrWhiteSpace(instance.CatalogBuildId))
            {
                instance.CatalogHeld = true;
                instance.DeclinedCatalogOffer = null;
            }

            _instances.Save(instance);
            RefreshMods();
            OfferCatalogAfterRestore(instance);

            AppendConsole($"[states] '{instance.Name}' put back to {target.Id}: {result.MovedAside} moved to {result.AsideFolder}, {result.Restored} brought back, {result.Failed.Count} failed");

            var text = Localize(
                "Rollback_Done",
                "The build is as it was on {0}: {1} file(s) moved aside to {2}, {3} brought back.",
                StateDate(target),
                result.MovedAside,
                result.AsideFolder,
                result.Restored);

            if (result.Failed.Count > 0)
            {
                text += " " + Localize("Rollback_Failed", "Could not bring back: {0}.", string.Join(", ", result.Failed.Select(Path.GetFileName)));
            }

            return text;
        }
        catch (BuildStateException ex) when (ex.Error == BuildStateError.GameRunning)
        {
            Status = Localize("Rollback_GameRunning", "Close the game of this build first: its files cannot be changed while it runs.");
            return null;
        }
        catch (Exception ex)
        {
            Status = Localize("Rollback_Error", "The build was left as it is: {0}", ex.Message);
            AppendConsole($"[states] restore failed: {ex}");
            return null;
        }
        finally
        {
            IsRollbackBusy = false;
        }
    }

    /// <summary>The records, the catalog items and the game and loader versions of the state.</summary>
    private async Task ApplyStateDefinitionAsync(Instance instance, BuildStateDefinition then)
    {
        instance.InstalledMods = then.Records?.ToList() ?? new List<InstalledModRecord>();
        instance.EnabledCatalogItems = then.CatalogItems?.ToList() ?? new List<string>();

        var versionChanged = !string.Equals(instance.VersionId, then.VersionId, StringComparison.Ordinal) ||
                             instance.Loader != then.Loader ||
                             !string.Equals(instance.LoaderVersion, then.LoaderVersion, StringComparison.Ordinal);

        if (!versionChanged || !string.IsNullOrWhiteSpace(instance.ProfileVersionId))
        {
            return;
        }

        instance.VersionId = then.VersionId;
        instance.Loader = then.Loader;
        instance.LoaderVersion = then.LoaderVersion;
        _instances.Save(instance);

        if (!IsSelectedBuild(instance))
        {
            return;
        }

        // The editor shows the selected build: it follows, the way a catalog change does.
        _applyingInstance = true;

        try
        {
            SelectedLoader = then.Loader;
            SelectedVersion = _allVersions.FirstOrDefault(v => v.Id == then.VersionId) ?? SelectedVersion;
        }
        finally
        {
            _applyingInstance = false;
        }

        await LoadLoaderVersionsAsync();
        SelectedLoaderVersion = LoaderVersions.FirstOrDefault(v => v.Version == then.LoaderVersion) ?? SelectedLoaderVersion;
        SyncInstance();
    }

    /// <summary>After a restore of a catalog build: what the catalog has is put up as a question again.</summary>
    private void OfferCatalogAfterRestore(Instance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.CatalogBuildId) || _loadedCatalog is null)
        {
            return;
        }

        var build = _loadedCatalog.Builds.FirstOrDefault(b => string.Equals(b.Id, instance.CatalogBuildId, StringComparison.OrdinalIgnoreCase));

        if (build is null)
        {
            return;
        }

        ClearPendingBuildSync();

        var preview = PreviewCatalogBuild(instance, build);
        var notice = BuildChangeNotice.Between(BuildSnapshot.Of(instance), BuildSnapshot.Of(preview), CatalogItemName);
        var serverChanged = !string.Equals(instance.ServerAddress, preview.ServerAddress, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(instance.ServerName, preview.ServerName, StringComparison.Ordinal);

        if (notice.Any || serverChanged)
        {
            _pendingCatalogBuild = build;
            _pendingCatalogNotice = notice;
            _pendingServerLine = serverChanged
                ? Localize("BuildChange_Server", "Server: {0} → {1}", instance.ServerAddress ?? "-", preview.ServerAddress ?? "-")
                : null;
            _pendingDefinition = $"{preview.VersionId}|{preview.Loader}|{preview.LoaderVersion}|{preview.ServerAddress}|{preview.ServerName}";
        }

        _ = StartBuildSyncAsync(instance);
    }

    // ===================== Words =====================

    private CultureInfo StateCulture => CultureInfo.GetCultureInfo(Language == "en" ? "en-GB" : "ru-RU");

    private string StateDate(BuildState state) => state.WhenUtc.ToLocalTime().ToString("d MMM, HH:mm", StateCulture);

    private string StateDay(BuildState state) => state.WhenUtc.ToLocalTime().ToString("d MMM", StateCulture);

    private static string ModCountLabel(int mods)
    {
        var key = (mods % 10, mods % 100) switch
        {
            (1, not 11) => "Rollback_ModsOne",
            (>= 2 and <= 4, < 12 or > 14) => "Rollback_ModsFew",
            _ => "Rollback_ModsMany"
        };

        return Localize(key, "{0} mods", mods);
    }

    /// <summary>"27 mods · before Physics Mod, Voxy were added".</summary>
    private string StateSummary(Instance instance, BuildState state, BuildStatePlan plan)
    {
        var names = NamesFor(instance, ReadDefinition(state));

        // A new version or a switch flipped is not a mod added: those are only the ones
        // whose name and file stem were not in the build at all.
        var thenKeys = plan.Mods.Where(c => c.Kind == BuildChangeKind.Removed)
            .SelectMany(c => new[] { ModName(names, c.Path), FileStem(FileOf(c.Path)) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nowKeys = plan.Mods.Where(c => c.Kind == BuildChangeKind.Added)
            .SelectMany(c => new[] { ModName(names, c.Path), FileStem(FileOf(c.Path)) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = plan.Mods.Where(c => c.Kind == BuildChangeKind.Added && IsEnabledJar(c.Path) &&
                                         !thenKeys.Contains(ModName(names, c.Path)) && !thenKeys.Contains(FileStem(FileOf(c.Path))))
            .Select(c => ModName(names, c.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var removed = plan.Mods.Where(c => c.Kind == BuildChangeKind.Removed && IsEnabledJar(c.Path) &&
                                           !nowKeys.Contains(ModName(names, c.Path)) && !nowKeys.Contains(FileStem(FileOf(c.Path))))
            .Select(c => ModName(names, c.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string tail;

        if (!plan.Any)
        {
            tail = Localize("Rollback_SummarySame", "the build is like this now");
        }
        else if (state.Kind == BuildStateKind.BeforeRestore && removed.Count > 0)
        {
            // The undo is about what it brings back, not about what came "after" it.
            tail = Localize("Rollback_SummaryUndo", "brings back {0}", ShortList(removed));
        }
        else if (added.Count > 0)
        {
            tail = Localize("Rollback_SummaryBeforeAdded", "before {0} were added", ShortList(added));
        }
        else if (removed.Count > 0)
        {
            tail = Localize("Rollback_SummaryBeforeRemoved", "before {0} were removed", ShortList(removed));
        }
        else if (plan.Mods.Count > 0)
        {
            tail = Localize("Rollback_SummaryOtherVersions", "other versions of the same mods");
        }
        else
        {
            tail = Localize("Rollback_SummarySettings", "the same mods, other settings");
        }

        return ModCountLabel(state.ModCount) + " · " + tail;
    }

    private static string ShortList(IReadOnlyList<string> names)
        => names.Count <= 3
            ? string.Join(", ", names)
            : Localize("Rollback_AndMore", "{0} and {1} more", string.Join(", ", names.Take(3)), names.Count - 3);

    /// <summary>The list under the button: every mod that would change, the settings in one line, the version.</summary>
    private List<string> DescribeRestore(Instance instance, BuildStatePlan plan, BuildStateDefinition? then)
    {
        var names = NamesFor(instance, then);
        var lines = new List<string>();

        if (then is not null && string.IsNullOrWhiteSpace(instance.ProfileVersionId))
        {
            if (!string.Equals(instance.VersionId, then.VersionId, StringComparison.Ordinal) && then.VersionId is not null)
            {
                lines.Add(Localize("Rollback_LineVersion", "Minecraft {0} → back to {1}", instance.VersionId ?? "-", then.VersionId));
            }

            if (instance.Loader != then.Loader || !string.Equals(instance.LoaderVersion, then.LoaderVersion, StringComparison.Ordinal))
            {
                lines.Add(Localize("Rollback_LineLoader", "{0} {1} → back to {2} {3}", instance.Loader, instance.LoaderVersion ?? string.Empty, then.Loader, then.LoaderVersion ?? string.Empty));
            }
        }

        var added = plan.Mods.Where(c => c.Kind == BuildChangeKind.Added).ToList();
        var removed = plan.Mods.Where(c => c.Kind == BuildChangeKind.Removed).ToList();
        var modLines = new List<string>();

        // The same file switched on or off since: said as that, not as one gone and one new.
        foreach (var now in added.ToList())
        {
            var file = FileOf(now.Path);
            var old = removed.FirstOrDefault(r => string.Equals(BuildStates.BaseName(FileOf(r.Path)), BuildStates.BaseName(file), StringComparison.OrdinalIgnoreCase));

            if (old is null)
            {
                continue;
            }

            added.Remove(now);
            removed.Remove(old);
            modLines.Add(IsEnabledJar(old.Path)
                ? Localize("Rollback_LineOnAgain", "\"{0}\" is switched on again", ModName(names, old.Path))
                : Localize("Rollback_LineOffAgain", "\"{0}\" is switched off again", ModName(names, old.Path)));
        }

        // Another version of the same mod since: the old one comes back. Told apart by the
        // mod's name, or by the file name up to its version for a jar nobody named.
        foreach (var now in added.ToList())
        {
            var name = ModName(names, now.Path);
            var stem = FileStem(FileOf(now.Path));
            var old = removed.FirstOrDefault(r =>
                string.Equals(ModName(names, r.Path), name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(FileStem(FileOf(r.Path)), stem, StringComparison.OrdinalIgnoreCase));

            if (old is null)
            {
                continue;
            }

            added.Remove(now);
            removed.Remove(old);
            modLines.Add(Localize("Rollback_LineVersionBack", "\"{0}\": {1} is moved aside, {2} comes back", ModName(names, old.Path), FileOf(now.Path), FileOf(old.Path)));
        }

        modLines.AddRange(added.Select(c => Localize("Rollback_LineAdded", "\"{0}\" (added since) is moved aside", ModName(names, c.Path))));
        modLines.AddRange(removed.Select(c => Localize("Rollback_LineRemoved", "\"{0}\" (removed since) comes back", ModName(names, c.Path))));
        modLines.AddRange(plan.Mods.Where(c => c.Kind == BuildChangeKind.Changed)
            .Select(c => Localize("Rollback_LineChanged", "\"{0}\": the earlier file comes back", ModName(names, c.Path))));

        if (modLines.Count > RollbackLinesShown)
        {
            var rest = modLines.Count - RollbackLinesShown;
            modLines = modLines.Take(RollbackLinesShown).ToList();
            modLines.Add(Localize("Rollback_LineMore", "…and {0} more mod(s)", rest));
        }

        lines.AddRange(modLines);

        var configBack = plan.Config.Count(c => c.Kind is BuildChangeKind.Changed or BuildChangeKind.Removed);
        var configNew = plan.Config.Count(c => c.Kind == BuildChangeKind.Added);

        if (configBack > 0 || configNew > 0)
        {
            lines.Add(Localize("Rollback_LineConfig", "Mod settings (config) go back to that day: {0} changed file(s), {1} new one(s) moved aside", configBack, configNew));
        }

        return lines;
    }

    /// <summary>File name → the mod's name, from what the build knows now and knew then.</summary>
    private Dictionary<string, string> NamesFor(Instance instance, BuildStateDefinition? then)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? fileName, string? name)
        {
            if (!string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(name))
            {
                names.TryAdd(BuildStates.BaseName(Path.GetFileName(fileName)), name!);
            }
        }

        foreach (var record in instance.InstalledMods.Concat(then?.Records ?? new List<InstalledModRecord>()))
        {
            Add(record.FileName, record.Name);
        }

        if (IsSelectedBuild(instance))
        {
            // A jar without metadata is shown under its file name; that says nothing new.
            foreach (var mod in InstalledMods.Where(m => m.IsMod && !mod_IsFileName(m)))
            {
                Add(mod.FileName, mod.DisplayName);
            }

            static bool mod_IsFileName(InstalledModItem m)
                => string.Equals(m.DisplayName, Path.GetFileNameWithoutExtension(BuildStates.BaseName(m.FileName)), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(m.DisplayName, m.FileName, StringComparison.OrdinalIgnoreCase);
        }

        return names;
    }

    private static string ModName(IReadOnlyDictionary<string, string> names, string path)
    {
        var file = BuildStates.BaseName(FileOf(path));

        return names.TryGetValue(file, out var name) ? name : FileStem(file);
    }

    /// <summary>"physics-mod-3.0.14-fabric-1.21.1.jar" → "physics-mod": the file name up to the version.</summary>
    private static string FileStem(string fileName)
    {
        var bare = Path.GetFileNameWithoutExtension(BuildStates.BaseName(fileName));
        var parts = bare.Split('-', '_', '+');
        var kept = parts.TakeWhile(p => p.Length == 0 || !(char.IsDigit(p[0]) || (p.Length > 1 && (p[0] == 'v' || p[0] == 'V') && char.IsDigit(p[1])))).ToList();

        return kept.Count > 0 ? string.Join("-", kept) : bare;
    }

    private static string FileOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static bool IsEnabledJar(string path) => path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

    private static void ReplaceLines(ObservableCollection<string> target, IEnumerable<string> lines)
    {
        target.Clear();

        foreach (var line in lines)
        {
            target.Add(line);
        }
    }
}
