using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>
/// One answer to "what does adding this mod do". The panel used to work out the list of
/// what comes along, and the installer worked it out again when the button was pressed;
/// the two could disagree, and then a file arrived that nobody had named. Now there is
/// one plan (<see cref="ModInstallPlan"/>): the panel shows it and the installer carries
/// out that very plan, for the card button, "Other versions", mod updates and the
/// optimisation set alike.
/// </summary>
public partial class MainWindowViewModel
{
    private ModInstallResolver? _planResolver;

    private ModInstallResolver PlanResolver => _planResolver ??= new ModInstallResolver(
        new IModSource[] { _modrinth, _curseForge },
        (version, _) => IsBlockedAsync(version));

    /// <summary>The plan the panel is showing, and the build it was drawn for.</summary>
    private ModInstallPlan? _openedProjectPlan;

    private InstallTarget? _openedProjectPlanTarget;

    /// <summary>Mods already in the build that the opened mod, or something it brings, is marked incompatible with.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenedProjectConflict))]
    private string _openedProjectConflictLabel = string.Empty;

    public bool HasOpenedProjectConflict => OpenedProjectConflictLabel.Length > 0;

    /// <summary>Another version on the panel, or none: the plan drawn for the last one is not this one's.</summary>
    partial void OnOpenedProjectPreferredChanged(ModVersion? value)
    {
        _openedProjectPlan = null;
        _openedProjectPlanTarget = null;
        OpenedProjectConflictLabel = string.Empty;
    }

    /// <summary>
    /// What the jars of a build said about themselves, kept in the launcher's own cache
    /// folder between starts so a check opens only the files that changed.
    /// </summary>
    private ModMetadataCache? MetadataCacheFor(Instance? instance)
        => instance is null || string.IsNullOrWhiteSpace(instance.Id)
            ? null
            : ModMetadataCache.For(Path.Combine(_paths.Root, "cache", "mod-metadata", instance.Id + ".json"));

    /// <summary>
    /// What the build has: the launcher's records and what the jars in mods/ declare.
    /// Reading the jars is disk work and happens off the UI thread.
    /// </summary>
    private Task<InstalledBuild> ReadInstalledBuildAsync(InstallTarget target)
    {
        // Copied here: the list belongs to the UI thread.
        var records = target.Instance?.InstalledMods.ToList() ?? new List<InstalledModRecord>();
        var cache = MetadataCacheFor(target.Instance);

        return Task.Run(() =>
        {
            IReadOnlyList<BuildJar> jars;

            try
            {
                jars = BuildChecker.ReadJars(target.Directory, cache);
            }
            catch (Exception ex)
            {
                AppendConsole($"[mods] could not read the mods folder: {ex.Message}");
                jars = Array.Empty<BuildJar>();
            }

            return new InstalledBuild(records, jars, target.Loader, target.GameVersion);
        });
    }

    private async Task<ModInstallPlan> ResolveInstallPlanAsync(
        ModVersion version,
        string slug,
        string title,
        string? iconUrl,
        string projectType,
        InstallTarget target)
    {
        var build = await ReadInstalledBuildAsync(target);

        var plan = await PlanResolver.ResolveAsync(
            new ModInstallRequest(version, slug, title, iconUrl, projectType, target.GameVersion, target.Loader),
            build);

        if (plan.Failure is { } failure)
        {
            AppendConsole($"[{SourceName(version.Source).ToLowerInvariant()}] dependency of {failure}");
        }

        return plan;
    }

    /// <summary>
    /// What a version needs beyond itself, with whether the build has it and how big the
    /// missing ones are. This is the list the player reads before agreeing to an install:
    /// a mod asked for by name must not bring files nobody mentioned. Complete is false
    /// when a required mod could not be looked up - the install might still bring it.
    /// </summary>
    private async Task<(IReadOnlyList<ModDependencyItem> Items, bool Complete)> ResolveDependenciesAsync(ModVersion version, string title)
    {
        var target = CurrentInstallTarget();
        var project = OpenedProject;

        var plan = await ResolveInstallPlanAsync(version, project?.Slug ?? string.Empty, project?.Title ?? title, project?.IconUrl, BrowserKind, target);

        // The panel may have moved on to another mod while the answers were coming.
        if (ReferenceEquals(version, OpenedProjectPreferred))
        {
            _openedProjectPlan = plan;
            _openedProjectPlanTarget = target;

            OpenedProjectConflictLabel = plan.Conflicts.Count == 0
                ? string.Empty
                : Localize(
                    "Mods_PlanConflict",
                    "Marked as incompatible: {0}. Both will be in the build; one of them may have to be switched off.",
                    string.Join(", ", plan.Conflicts.Select(c => $"{c.Title} + {c.With}").Distinct()));
        }

        var items = new List<ModDependencyItem>();

        foreach (var item in plan.Required)
        {
            var blocked = item.State == ModPlanState.Blocked;

            items.Add(new ModDependencyItem(item.Title, item.State == ModPlanState.Satisfied, Required: true)
            {
                Size = item.DownloadSize,
                SizeLabel = item.DownloadSize > 0 ? FormatSize(item.DownloadSize) : string.Empty,
                Blocked = blocked,
                Unavailable = item.State == ModPlanState.Unavailable,
                PageUrl = blocked ? item.PageUrl : null
            });
        }

        // Only the optional ones are cut short. A required mod is always named, however
        // long the list already is: the install has no such limit.
        foreach (var item in plan.Optional)
        {
            if (items.Count >= MaxListedDependencies)
            {
                break;
            }

            items.Add(new ModDependencyItem(item.Title, item.State == ModPlanState.Satisfied, Required: false));
        }

        return (items, plan.IsComplete);
    }

    /// <summary>
    /// "Add" on the panel: the plan on screen is the plan installed. It is drawn again
    /// only when the panel has none for this version and this build - which the button
    /// does not allow, so that path is for whatever was missed.
    /// </summary>
    private async Task InstallOpenedPlanAsync(ModVersion version, ModProject project, InstallTarget target)
    {
        if (_openedProjectPlan is { IsComplete: true } plan &&
            ReferenceEquals(plan.Root.Version, version) &&
            _openedProjectPlanTarget == target)
        {
            await InstallPlanAsync(plan, target);
            return;
        }

        await InstallProjectWithDependenciesAsync(version, project.Slug, project.Title, project.IconUrl, target: target);
    }

    /// <summary>
    /// Installs a version and everything it requires. A mod that needs Fabric API and is
    /// installed without it crashes the game on the next start with a message the player
    /// cannot act on - so the required dependencies come along, all the way down.
    /// The build is settled once, at the start: the player may open another build while
    /// the files are coming, and the rest of them still go where the first one went.
    /// </summary>
    /// <param name="depth">Kept for the callers that name it; the plan knows its own depth.</param>
    private async Task InstallProjectWithDependenciesAsync(
        ModVersion version,
        string slug,
        string title,
        string? iconUrl,
        int depth = 0,
        string? projectType = null,
        InstallTarget? target = null)
    {
        projectType ??= BrowserKind;
        target ??= CurrentInstallTarget();

        var plan = await ResolveInstallPlanAsync(version, slug, title, iconUrl, projectType, target);

        await InstallPlanAsync(plan, target);
    }

    /// <summary>Carries out a plan as it is: these files, in this order, and no others.</summary>
    /// <param name="backup">False when the caller installs several plans in a row and has made the one backup itself.</param>
    private async Task InstallPlanAsync(ModInstallPlan plan, InstallTarget target, bool backup = true)
    {
        var root = plan.Root;

        if (plan.Failure is { } failure)
        {
            // Half a list is not installed: the missing half is what breaks the game.
            throw new InvalidOperationException(failure);
        }

        if (root.State == ModPlanState.Unavailable)
        {
            Status = Localize("Status_NoCompatibleFile", "No compatible file for this version and loader");
            return;
        }

        // Settled before anything is written: a mod whose own file cannot be fetched must
        // not leave its dependencies behind in the build.
        if (root.State == ModPlanState.Blocked)
        {
            throw new InvalidOperationException(
                Localize("Mods_BlockedFile", "the author of {0} allows downloads only from the mod's page on CurseForge", root.Title));
        }

        if (plan.Missing.FirstOrDefault() is { } missing)
        {
            throw new InvalidOperationException(
                Localize("Error_DependencyMissing", "{0} needs {1}, which has no version for this build", missing.RequiredBy ?? root.Title, missing.Title));
        }

        _modsLeftToThePlayer.Clear();

        if (backup)
        {
            await MaybeBackupAsync(BackupTrigger.BeforeModChange, target.Instance);
        }

        // Dependencies first, so a failure there leaves the build without the mod rather
        // than with a mod that cannot start.
        foreach (var item in plan.Required)
        {
            if (item.State == ModPlanState.Blocked)
            {
                // A needed mod that only its page gives out. What it needs has come; the
                // mod that was asked for still comes; this one file is named for the
                // player to fetch.
                LeaveToThePlayer(item);
                continue;
            }

            if (item.State != ModPlanState.Install)
            {
                continue;
            }

            // The build may have gained it since the plan was drawn; a second copy helps nobody.
            if (item.Slug.Length > 0 && IsProjectInstalled(target.Instance, item.Slug))
            {
                continue;
            }

            Status = Localize("Status_ResolvingDependency", "Adding {0}, which {1} needs…", item.Title, item.RequiredBy ?? root.Title);
            await InstallPlanItemAsync(item, target, isRoot: false);
        }

        await InstallPlanItemAsync(root, target, isRoot: true);

        ReportModsLeftToThePlayer(root.Title);
    }

    private void LeaveToThePlayer(ModPlanItem item)
    {
        _modsLeftToThePlayer.Add(item.Title);
        AppendConsole($"[mods] {item.Slug}: only from its page, left for the player ({item.PageUrl})");
    }

    /// <summary>Downloads the one file a plan item names, and records where it came from.</summary>
    private async Task InstallPlanItemAsync(ModPlanItem item, InstallTarget target, bool isRoot)
    {
        var version = item.Version!;
        var file = item.File!;
        var fromCurseForge = version.Source == ModSource.CurseForge;
        var folder = ProjectTypes.FolderFor(item.ProjectType);

        Status = Localize("Status_InstallingFile", "Installing {0}…", file.FileName);

        if (fromCurseForge)
        {
            // The client fetches from CurseForge's own CDN only and checks the SHA-1 the API gave.
            var outcome = await _curseForge.InstallAsync(version, target.Directory, folder);

            if (outcome.State != CurseForgeFileState.Ready || outcome.File is null)
            {
                if (!isRoot)
                {
                    // Open when the plan was drawn, closed now: the same as blocked.
                    LeaveToThePlayer(item);
                    return;
                }

                throw new InvalidOperationException(
                    Localize("Mods_BlockedFile", "the author of {0} allows downloads only from the mod's page on CurseForge", item.Title));
            }

            file = outcome.File;
        }
        else
        {
            await _mods.InstallAsync(target.Directory, folder, file.FileName, file.Url, file.Sha1, file.Size);
        }

        AppendConsole($"[mods] installed {folder}/{file.FileName} ({item.Slug} {version.VersionNumber}, {SourceName(version.Source)})");

        if (string.Equals(folder, ModManager.ModsFolderName, StringComparison.OrdinalIgnoreCase) && target.Instance is not null)
        {
            ReplaceOtherVersions(target.Instance, file.FileName);
            _installBatch.Add(file.FileName);
        }

        var record = fromCurseForge
            ? CurseForgeClient.RecordFor(version, file, item.Slug, item.Title, item.IconUrl, folder)
            : new InstalledModRecord
            {
                FileName = file.FileName,
                Source = ModSource.Modrinth,
                Id = item.Slug,
                Name = item.Title,
                IconUrl = item.IconUrl,
                Version = version.VersionNumber,
                Folder = folder
            };

        if (target.Instance is not null)
        {
            RecordInstalledMod(target.Instance, record);
        }

        // The list on screen is the open build's: with another one opened meanwhile, the
        // file that came is not its to show. It is found there when this build is reopened.
        if (IsSelectedBuild(target))
        {
            RefreshMods();
        }

        Status = Localize("Status_InstalledFile", "Installed {0}", file.FileName);
    }
}
