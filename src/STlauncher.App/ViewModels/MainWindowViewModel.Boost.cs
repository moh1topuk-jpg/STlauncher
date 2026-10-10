using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Services;
using STlauncher.Core.Boost;
using STlauncher.Core.Instances;
using STlauncher.Core.Launch;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>One line of the list the player reads before agreeing: a mod or a setting, and what happens to it.</summary>
/// <param name="Muted">True for a line that only explains why nothing happens.</param>
/// <param name="Indented">True for a mod that comes because the one above requires it.</param>
public sealed record BoostLine(string Title, string Detail, bool Muted = false, bool Indented = false)
{
    public double Opacity => Muted ? 0.6 : 1;

    public Avalonia.Thickness Margin => new(Indented ? 16 : 0, 0, 0, 0);
}

/// <summary>
/// "Ускорение": one switch per build that adds the accelerator mods the build lacks and
/// lowers the settings that cost frames - and, unlike a preset, can be switched off
/// again. Everything it does is written into the build's record
/// (<see cref="BoostRecord"/>), so off means exactly "as it was": the jars it brought
/// are switched off, the settings it wrote go back to their previous values, and
/// whatever the player has changed since stays the player's.
/// </summary>
/// <remarks>
/// A build made for a server from the catalog can have it too. The catalog sync only
/// ever touches the files it recorded as its own (see PlanBuildSync) and switches off an
/// older copy when it brings a mod itself, so the two do not fight: a slot the catalog
/// fills is simply found filled here, and a jar the catalog has since taken over is not
/// switched off with the rest.
/// </remarks>
public partial class MainWindowViewModel
{
    /// <summary>The plan on screen and the build it was drawn for; "Switch on" carries out this one.</summary>
    private BoostModPlan? _boostModPlan;

    private InstallTarget? _boostTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleBoost), nameof(BoostNote), nameof(HasBoostNote))]
    private bool _isBoostBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBoostStatus))]
    private string _boostStatus = string.Empty;

    /// <summary>The list of what will happen is open, waiting for a yes or a no.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleBoost))]
    private bool _isBoostReviewOpen;

    [ObservableProperty]
    private string _boostModsHeader = string.Empty;

    [ObservableProperty]
    private string _boostOptionsHeader = string.Empty;

    public ObservableCollection<BoostLine> BoostModLines { get; } = new();

    public ObservableCollection<BoostLine> BoostOptionLines { get; } = new();

    public bool HasBoostStatus => BoostStatus.Length > 0;

    public bool IsBoostOn => SelectedInstance?.Boost?.Active == true;

    /// <summary>
    /// What the switch is bound to. Moving it is a request: switching on first opens the
    /// list of what will happen, and the switch goes back to "off" until that is agreed.
    /// </summary>
    public bool BoostSwitch
    {
        get => IsBoostOn;
        set
        {
            if (value == IsBoostOn || !CanToggleBoost)
            {
                Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(BoostSwitch)));
                return;
            }

            _ = value ? PrepareBoostAsync() : TurnBoostOffAsync();
        }
    }

    /// <summary>Not while the game runs: it holds its jars open and writes options.txt over ours when it exits.</summary>
    public bool CanToggleBoost
        => SelectedInstance is not null && !IsBoostBusy && !IsBoostReviewOpen && !IsGameRunning && !IsApplyingPreset;

    public string BoostNote
    {
        get
        {
            if (SelectedInstance is null)
            {
                return string.Empty;
            }

            if (IsGameRunning)
            {
                return Localize("Boost_GameRunning", "The game is running: the switch works after you leave it.");
            }

            return SelectedInstance.Boost is { Active: true } record
                ? Localize(
                    "Boost_OnNote",
                    "On in “{0}”: mods added - {1}, settings changed - {2}. Switch it off and the mods are switched off and the settings go back to what they were.",
                    SelectedInstance.Name,
                    record.Jars.Count,
                    record.Options.Count)
                : string.Empty;
        }
    }

    public bool HasBoostNote => BoostNote.Length > 0;

    /// <summary>What the switch brought into this build, read from its record: the list behind "What changed".</summary>
    public IReadOnlyList<BoostLine> BoostAppliedModLines
        => SelectedInstance?.Boost is { Active: true } record
            ? record.Jars
                .Select(jar => new BoostLine(jar.Title is { Length: > 0 } title ? title : jar.FileName, jar.FileName, Indented: jar.Dependency))
                .ToList()
            : Array.Empty<BoostLine>();

    /// <summary>The settings the switch wrote, each with what it was before.</summary>
    public IReadOnlyList<BoostLine> BoostAppliedOptionLines
        => SelectedInstance?.Boost is { Active: true } record
            ? record.Options
                .Select(option => new BoostLine(
                    BoostOptionLabel(option.Key),
                    $"{BoostValue(option.Key, option.Previous)} → {BoostValue(option.Key, option.Written)}"))
                .ToList()
            : Array.Empty<BoostLine>();

    /// <summary>Another build is open, or the game started or stopped: the switch reads its state again.</summary>
    private void RaiseBoostState(bool buildChanged = false)
    {
        if (buildChanged)
        {
            // The list was drawn for the build that was open before.
            CloseBoostReview();
            BoostStatus = string.Empty;
        }

        OnPropertyChanged(nameof(IsBoostOn));
        OnPropertyChanged(nameof(BoostSwitch));
        OnPropertyChanged(nameof(CanToggleBoost));
        OnPropertyChanged(nameof(BoostNote));
        OnPropertyChanged(nameof(HasBoostNote));
        OnPropertyChanged(nameof(BoostAppliedModLines));
        OnPropertyChanged(nameof(BoostAppliedOptionLines));
    }

    partial void OnIsApplyingPresetChanged(bool value) => OnPropertyChanged(nameof(CanToggleBoost));

    private void CloseBoostReview()
    {
        _boostModPlan = null;
        _boostTarget = null;
        IsBoostReviewOpen = false;
    }

    // ===================== On: first the list =====================

    private async Task PrepareBoostAsync()
    {
        var instance = SelectedInstance;

        if (instance is null)
        {
            return;
        }

        var target = CurrentInstallTarget() with { GameVersion = SelectedVersion?.Id ?? instance.VersionId };

        try
        {
            IsBoostBusy = true;
            BoostStatus = Localize("Boost_Checking", "Looking at what this build has and what Modrinth offers for it…");

            var (modPlan, options, optionsReadable) = await DrawBoostPlanAsync(target);

            // The player may have opened another build while Modrinth was answering.
            if (!ReferenceEquals(SelectedInstance, instance))
            {
                return;
            }

            ShowBoostPlan(target, modPlan, options, optionsReadable);

            if (!modPlan.ChangesAnything && options.Count == 0)
            {
                BoostStatus = Localize(
                    "Boost_NothingToDo",
                    "There is nothing to speed up here: the build already has these mods or cannot take them, and the settings are no higher than the switch would set.");
                return;
            }

            _boostModPlan = modPlan;
            _boostTarget = target;
            BoostStatus = string.Empty;
            IsBoostReviewOpen = true;
        }
        catch (Exception ex)
        {
            BoostStatus = Localize("Boost_Failed", "Could not do it: {0}", ex.Message);
            AppendConsole($"[boost] {ex}");
        }
        finally
        {
            IsBoostBusy = false;
            OnPropertyChanged(nameof(BoostSwitch));
        }
    }

    private async Task<(BoostModPlan Mods, IReadOnlyList<BoostOptionChange> Options, bool OptionsReadable)> DrawBoostPlanAsync(InstallTarget target)
    {
        // Copied here: the lists belong to the UI thread.
        var records = target.Instance?.InstalledMods.ToList() ?? new List<InstalledModRecord>();
        var parkedRecords = target.Instance?.Boost?.Parked.ToList() ?? new List<BoostJarRecord>();
        var cache = MetadataCacheFor(target.Instance);

        var (jars, parked, optionsFile) = await Task.Run(() => (
            BuildChecker.ReadJars(target.Directory, cache),
            BoostJars.StillParked(target.Directory, parkedRecords),
            OptionsFile.TryLoad(Path.Combine(target.Directory, GameOptions.FileName))));

        var mods = await BoostMods.PlanAsync(_modrinth, PlanResolver, records, jars, parked, target.Loader, target.GameVersion);

        IReadOnlyList<BoostOptionChange> options = optionsFile is null
            ? Array.Empty<BoostOptionChange>()
            : BoostOptions.Plan(optionsFile, target.GameVersion, DisplayInfo.RefreshRate());

        return (mods, options, optionsFile is not null);
    }

    private void ShowBoostPlan(InstallTarget target, BoostModPlan plan, IReadOnlyList<BoostOptionChange> options, bool optionsReadable)
    {
        BoostModLines.Clear();
        BoostOptionLines.Clear();

        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in plan.Items)
        {
            switch (item.State)
            {
                case BoostModState.Install when item.Plan is { } install:
                    // What a mod requires stands under it, once: the second mod that
                    // needs Fabric API does not bring a second one.
                    listed.Add(install.Root.ProjectId);
                    BoostModLines.Add(new BoostLine(item.Title, FormatSize(install.Root.DownloadSize)));

                    foreach (var dependency in install.Downloads.Where(d => !ReferenceEquals(d, install.Root) && listed.Add(d.ProjectId)))
                    {
                        BoostModLines.Add(new BoostLine(
                            dependency.Title,
                            Localize("Boost_ModNeededBy", "needed by {0} · {1}", dependency.RequiredBy ?? item.Title, FormatSize(dependency.DownloadSize)),
                            Indented: true));
                    }

                    break;

                case BoostModState.Reenable:
                    BoostModLines.Add(new BoostLine(item.Title, Localize("Boost_ModReenable", "switched back on, already downloaded")));
                    break;

                case BoostModState.Present:
                    BoostModLines.Add(new BoostLine(item.Title, Localize("Boost_ModPresent", "already in the build: {0}", item.FileName), Muted: true));
                    break;

                case BoostModState.SkippedForOptiFine:
                    BoostModLines.Add(new BoostLine(item.Title, Localize("Boost_ModOptiFine", "not added: the build has OptiFine, and the two do not work together"), Muted: true));
                    break;

                case BoostModState.Conflict:
                    BoostModLines.Add(new BoostLine(item.Title, Localize("Boost_ModConflict", "not added: marked incompatible with {0}, which is in the build", item.Detail), Muted: true));
                    break;

                default:
                    BoostModLines.Add(new BoostLine(
                        item.Title,
                        item.Detail is { Length: > 0 } detail
                            ? Localize("Boost_ModUnavailableWhy", "not added: {0}", detail)
                            : Localize("Boost_ModUnavailable", "not added: no version for {0} {1}", target.GameVersion, target.Loader),
                        Muted: true));
                    break;
            }
        }

        var downloads = plan.Downloads;

        BoostModsHeader = plan.Items.Count == 0
            ? Localize("Boost_ModsNone", "Mods: there are none for this build. They exist for Fabric, Quilt, NeoForge and Forge from 1.16.")
            : downloads.Count > 0
                ? Localize("Boost_ModsDownload", "Mods: files to download - {0}, {1} in all", downloads.Count, FormatSize(plan.TotalBytes))
                : Localize("Boost_ModsNoDownload", "Mods: nothing is downloaded");

        foreach (var change in options)
        {
            BoostOptionLines.Add(new BoostLine(
                BoostOptionLabel(change.Key),
                $"{BoostValue(change.Key, change.Previous)} → {BoostValue(change.Key, change.Value)}"));
        }

        BoostOptionsHeader = !optionsReadable
            ? Localize("Boost_OptionsUnreadable", "Game settings: options.txt cannot be read as text, so it is left untouched.")
            : options.Count > 0
                ? Localize("Boost_OptionsChange", "Game settings: to change - {0}", options.Count)
                : Localize("Boost_OptionsNone", "Game settings: nothing to change, they are no higher than the switch would set.");
    }

    /// <summary>The setting's name as the game's menu has it; the bare key for one nobody named.</summary>
    private static string BoostOptionLabel(string key)
    {
        var resource = "Boost_Opt_" + key;
        return Localize(resource, key);
    }

    /// <summary>A value of options.txt in the words the game's own menu uses.</summary>
    private static string BoostValue(string key, string? value)
    {
        if (value is null)
        {
            return Localize("Boost_ValDefault", "not set");
        }

        var bare = value.Trim().Trim('"');

        string On() => Localize("Boost_ValOn", "on");
        string Off() => Localize("Boost_ValOff", "off");

        return key switch
        {
            "particles" => bare switch
            {
                "0" => Localize("Boost_ValParticlesAll", "all"),
                "1" => Localize("Boost_ValParticlesLess", "decreased"),
                "2" => Localize("Boost_ValParticlesMin", "minimal"),
                _ => bare
            },
            "cloudStatus" or "renderClouds" => bare switch
            {
                "fancy" or "true" => Localize("Boost_ValFancy", "fancy"),
                "fast" => Localize("Boost_ValFast", "fast"),
                "off" or "false" => Off(),
                _ => bare
            },
            "entityShadows" or "enableVsync" => bare switch { "true" => On(), "false" => Off(), _ => bare },
            "ao" => bare switch
            {
                "2" => Localize("Boost_ValMax", "maximum"),
                "1" => Localize("Boost_ValMin", "minimum"),
                "0" => Off(),
                _ => bare
            },
            "entityDistanceScaling" when double.TryParse(bare, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
                => $"{scale * 100:0}%",

            // The slider's last stop is "unlimited", written as 260.
            "maxFps" when bare == "260" => Localize("Boost_ValUnlimited", "unlimited"),
            _ => bare
        };
    }

    [RelayCommand]
    private void CancelBoost()
    {
        CloseBoostReview();
        OnPropertyChanged(nameof(BoostSwitch));
    }

    // ===================== On: the player agreed =====================

    [RelayCommand]
    private async Task ConfirmBoostAsync()
    {
        if (_boostModPlan is not { } plan || _boostTarget is not { Instance: { } instance } target || IsBoostBusy || IsGameRunning)
        {
            return;
        }

        CloseBoostReview();

        var record = instance.Boost ??= new BoostRecord();
        var problems = new List<string>();

        try
        {
            IsBoostBusy = true;

            // The record says "on" from the first file: a launcher closed half-way must
            // still know which jars are its own to take back.
            record.Active = true;
            record.AppliedAt = DateTimeOffset.Now;
            _instances.Save(instance);

            if (plan.ChangesAnything)
            {
                await MaybeBackupAsync(BackupTrigger.BeforeModChange, instance);
            }

            foreach (var jar in await Task.Run(() => BoostJars.SwitchOn(target.Directory, plan.Reenable)))
            {
                RenameSkinModRecord(instance, jar.FileName + ".disabled", jar.FileName, disabled: false);
                record.Parked.RemoveAll(p => string.Equals(p.FileName, jar.FileName, StringComparison.OrdinalIgnoreCase));
                record.Jars.Add(jar);
                AppendConsole($"[boost] switched {jar.FileName} back on");
            }

            _instances.Save(instance);

            foreach (var item in plan.Items.Where(i => i.State == BoostModState.Install && i.Plan is not null))
            {
                var install = item.Plan!;

                try
                {
                    BoostStatus = Localize("Boost_Installing", "Adding {0}…", item.Title);
                    _installBatch.Clear();

                    await InstallPlanAsync(install, target, backup: false);
                }
                catch (Exception ex)
                {
                    problems.Add(item.Title);
                    AppendConsole($"[boost] {item.Title}: {ex.Message}");
                }

                // Whatever did land is written down, a failed plan's dependencies too.
                foreach (var fileName in _installBatch)
                {
                    var planned = install.Downloads.FirstOrDefault(d => string.Equals(d.File?.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                    var isRoot = planned is not null && ReferenceEquals(planned, install.Root);

                    record.Jars.RemoveAll(j => string.Equals(j.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                    record.Jars.Add(await Task.Run(() => BoostJars.Describe(target.Directory, fileName, planned?.Slug, planned?.Title, dependency: !isRoot)));
                }

                _installBatch.Clear();
                _instances.Save(instance);
            }

            await Task.Run(() => ApplyBoostOptions(target, record));
            _instances.Save(instance);

            AppendConsole($"[boost] on for {instance.Name}: {record.Jars.Count} jar(s), {record.Options.Count} option(s)");

            // The counts are in the note above the status; this line is about when.
            BoostStatus = Localize("Boost_Done", "Done. The game reads all of this on its next start.")
                          + (problems.Count > 0
                              ? " " + Localize("Boost_DoneProblems", "Could not be added: {0}. The reason is in the console.", string.Join(", ", problems))
                              : string.Empty);
        }
        catch (Exception ex)
        {
            BoostStatus = Localize("Boost_Failed", "Could not do it: {0}", ex.Message);
            AppendConsole($"[boost] {ex}");
        }
        finally
        {
            IsBoostBusy = false;

            if (IsSelectedBuild(target))
            {
                RefreshMods();
                RefreshBrowserInstallState();
            }

            RaiseBoostState();
        }
    }

    /// <summary>
    /// Lowers the settings and writes down what each was. Drawn again from the file as it
    /// is now, so the record is of what was really there when the values were written.
    /// </summary>
    private static void ApplyBoostOptions(InstallTarget target, BoostRecord record)
    {
        var path = Path.Combine(target.Directory, GameOptions.FileName);

        if (OptionsFile.TryLoad(path) is not { } file)
        {
            return;
        }

        var changes = BoostOptions.Plan(file, target.GameVersion, DisplayInfo.RefreshRate());

        if (changes.Count == 0)
        {
            return;
        }

        var written = BoostOptions.Apply(file, changes);
        file.Save(path);

        // Keys written by an earlier, unfinished run keep the older "previous".
        foreach (var option in written.Where(w => record.Options.All(o => o.Key != w.Key)))
        {
            record.Options.Add(option);
        }
    }

    // ===================== Off =====================

    private async Task TurnBoostOffAsync()
    {
        var instance = SelectedInstance;

        if (instance?.Boost is not { Active: true } record)
        {
            return;
        }

        var directory = _instances.GameDirectory(instance);
        var loader = instance.Loader;
        var gameVersion = instance.VersionId;
        var cache = MetadataCacheFor(instance);

        try
        {
            IsBoostBusy = true;
            BoostStatus = Localize("Boost_TurningOff", "Putting the build back as it was…");

            // A jar the catalog has since made part of the server's build is the
            // catalog's: switched off here, the build would no longer be the server's.
            var catalogOwned = record.Jars
                .Where(jar => instance.InstalledMods.Any(m =>
                    m.Source == ModSource.Catalog &&
                    string.Equals(m.FileName, jar.FileName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var mine = record.Jars.Except(catalogOwned).ToList();
            var options = record.Options.ToList();

            await MaybeBackupAsync(BackupTrigger.BeforeModChange, instance);

            var (jars, settings) = await Task.Run(() =>
            {
                var revertedJars = BoostJars.SwitchOff(directory, mine, loader, gameVersion, cache);
                BoostOptionsRevert? revertedOptions = null;
                var path = Path.Combine(directory, GameOptions.FileName);

                if (options.Count > 0 && OptionsFile.TryLoad(path) is { } file)
                {
                    revertedOptions = BoostOptions.Revert(file, options);

                    if (file.Changed)
                    {
                        file.Save(path);
                    }
                }

                return (revertedJars, revertedOptions);
            });

            foreach (var jar in jars.SwitchedOff)
            {
                RenameSkinModRecord(instance, jar.FileName, jar.FileName + ".disabled", disabled: true);
                _knownModUpdates.Remove(jar.FileName);
                AppendConsole($"[boost] switched off {jar.FileName}, kept in mods/ as {jar.FileName}.disabled");
            }

            record.Parked.RemoveAll(p => jars.SwitchedOff.Any(j => string.Equals(j.FileName, p.FileName, StringComparison.OrdinalIgnoreCase)));
            record.Parked.AddRange(jars.SwitchedOff);
            record.Jars.Clear();
            record.Options.Clear();
            record.Active = false;
            _instances.Save(instance);

            var parts = new List<string>
            {
                Localize("Boost_Off", "Switched off.")
            };

            if (jars.SwitchedOff.Count > 0)
            {
                parts.Add(Localize("Boost_OffJars", "Mods switched off: {0}. Their files stay in the mods folder with “.disabled” at the end, and the mod list can switch any of them back on.", jars.SwitchedOff.Count));
            }

            if (jars.LeftNeeded.Count > 0)
            {
                parts.Add(Localize("Boost_OffNeeded", "Left on because other mods need them: {0}.",
                    string.Join(", ", jars.LeftNeeded.Select(n => $"{n.Jar.Title ?? n.Jar.FileName} ({n.NeededBy})"))));
            }

            var untouched = jars.LeftToPlayer.Concat(catalogOwned).Select(j => j.Title ?? j.FileName).ToList();

            if (untouched.Count > 0)
            {
                parts.Add(Localize("Boost_OffLeftJars", "Not touched, because they were updated, replaced or removed since: {0}.", string.Join(", ", untouched)));
            }

            if (settings is null && options.Count > 0)
            {
                parts.Add(Localize("Boost_OffOptionsUnreadable", "options.txt cannot be read as text, so the settings stay as they are."));
            }
            else if (settings is not null)
            {
                if (settings.Restored.Count > 0)
                {
                    parts.Add(Localize("Boost_OffOptions", "Settings put back: {0}.", settings.Restored.Count));
                }

                if (settings.LeftToPlayer.Count > 0)
                {
                    parts.Add(Localize("Boost_OffLeftOptions", "Left as you changed them: {0}.",
                        string.Join(", ", settings.LeftToPlayer.Select(BoostOptionLabel))));
                }
            }

            BoostStatus = string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            BoostStatus = Localize("Boost_Failed", "Could not do it: {0}", ex.Message);
            AppendConsole($"[boost] {ex}");
        }
        finally
        {
            IsBoostBusy = false;

            if (ReferenceEquals(SelectedInstance, instance))
            {
                RefreshMods();
                RefreshBrowserInstallState();
            }

            RaiseBoostState();
        }
    }
}
