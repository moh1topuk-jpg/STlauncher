using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using STlauncher.Core.Metadata;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>
/// Build list management (search, quick play), the new-build wizard and the Modrinth
/// project card.
/// </summary>
public partial class MainWindowViewModel
{
    private readonly List<Instance> _allInstances = new();

    // ===================== Build list =====================

    [ObservableProperty]
    private string _buildFilter = string.Empty;

    partial void OnBuildFilterChanged(string value) => ApplyBuildFilter();

    private void ApplyBuildFilter()
    {
        var selectedId = SelectedInstance?.Id;
        var filter = (BuildFilter ?? string.Empty).Trim();

        var filtered = filter.Length == 0
            ? _allInstances
            : _allInstances
                .Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || (i.VersionId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();

        Instances.Clear();
        foreach (var instance in filtered)
        {
            instance.DetectedModCount = CountModFiles(instance);
            Instances.Add(instance);
        }

        if (selectedId is not null && Instances.All(i => i.Id != selectedId))
        {
            SelectedInstance = Instances.FirstOrDefault();
        }
    }

    /// <summary>
    /// Mod files in the build's folder: the count an imported build shows, since it has no
    /// catalog list. A glance at one directory, cheap enough to do for every row.
    /// </summary>
    private int CountModFiles(Instance instance)
    {
        try
        {
            var mods = System.IO.Path.Combine(_instances.GameDirectory(instance), "mods");

            // Switched-off mods count too: the mods tab lists them, and two numbers for
            // one folder read as a bug.
            return System.IO.Directory.Exists(mods)
                ? System.IO.Directory.EnumerateFiles(mods).Count(STlauncher.Core.Mods.ModManager.IsModFile)
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Replaces the list item so bound text (rename, last played) refreshes.</summary>
    /// <summary>
    /// True while a list row is being swapped for itself. The ListBox drops its selection
    /// on the swap and the binding writes null into SelectedInstance; the selection is put
    /// back straight after, and that write must not restart everything a real selection
    /// change does - which mid-launch reset the loader version underneath the launch.
    /// </summary>
    private bool _refreshingListItem;

    private void RefreshBuildListItem(Instance instance)
    {
        var index = Instances.IndexOf(instance);

        if (index < 0)
        {
            return;
        }

        // Decided before the swap: after it the selection may already be gone. That was
        // the bug - the check came second, found null, and the main screen went blank
        // the moment the game started.
        var wasSelected = ReferenceEquals(SelectedInstance, instance);

        _refreshingListItem = true;

        try
        {
            Instances[index] = instance;

            if (wasSelected && !ReferenceEquals(SelectedInstance, instance))
            {
                SelectedInstance = instance;
            }
        }
        finally
        {
            _refreshingListItem = false;
        }

        // The header binds to SelectedInstance.Name and friends; the instance is not
        // observable, so tell the bindings to read it again.
        if (wasSelected)
        {
            OnPropertyChanged(nameof(SelectedInstance));
            OnPropertyChanged(nameof(LastPlayedLabel));
            RaisePlaytimeLabels();
        }
    }

    // ===================== New build wizard =====================

    [ObservableProperty]
    private bool _isNewBuildOpen;

    [ObservableProperty]
    private string _newBuildName = string.Empty;

    [ObservableProperty]
    private LoaderKind _newBuildLoader = LoaderKind.Fabric;

    private VersionSummary? _newBuildVersion;

    public VersionSummary? NewBuildVersion
    {
        get => _newBuildVersion;
        set
        {
            if (SetProperty(ref _newBuildVersion, value))
            {
                OnPropertyChanged(nameof(CanCreateBuild));
            }
        }
    }

    public bool CanCreateBuild => NewBuildVersion is not null && !string.IsNullOrWhiteSpace(NewBuildName);

    partial void OnNewBuildNameChanged(string value) => OnPropertyChanged(nameof(CanCreateBuild));

    [RelayCommand]
    private void OpenNewBuild()
    {
        NewBuildName = string.Empty;
        NewBuildLoader = LoaderKind.Fabric;
        NewBuildVersion = SelectedVersion ?? Versions.FirstOrDefault();
        NewBuildCopySettings = SelectedInstance is not null;
        OnPropertyChanged(nameof(NewBuildCopySettingsLabel));
        OnPropertyChanged(nameof(CanCopyNewBuildSettings));
        IsNewBuildOpen = true;
    }

    [RelayCommand]
    private void CloseNewBuild() => IsNewBuildOpen = false;

    [RelayCommand]
    private void CreateBuildFromWizard()
    {
        if (NewBuildVersion is null)
        {
            return;
        }

        try
        {
            var name = string.IsNullOrWhiteSpace(NewBuildName) ? NewBuildVersion.Id : NewBuildName.Trim();
            var instance = _instances.Create(name);

            instance.VersionId = NewBuildVersion.Id;
            instance.Loader = NewBuildLoader;
            instance.MaxMemoryMb = MemoryForNewBuild(mods: 0);
            _instances.Save(instance);
            CopySettingsIntoNewBuild(instance);

            _allInstances.Add(instance);
            ApplyBuildFilter();
            SelectedInstance = instance;

            IsNewBuildOpen = false;
            IsBuildSettingsOpen = true;
            Status = Localize("Status_BuildCreated", "Build \"{0}\" created", instance.Name);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_CreateBuild", "Failed to create the build: {0}", ex.Message);
        }
    }

    // ===================== Mod project card =====================

    [ObservableProperty]
    private bool _isProjectOpen;

    [ObservableProperty]
    private ModProject? _openedProject;

    [ObservableProperty]
    private Bitmap? _openedProjectIcon;

    [ObservableProperty]
    private bool _isProjectBusy;

    [ObservableProperty]
    private string _openedProjectDescription = string.Empty;

    [ObservableProperty]
    private bool _isProjectTranslated;

    private string _projectOriginalDescription = string.Empty;

    /// <summary>
    /// Counts openings and closings of the panel. A card clicked while another is still
    /// loading starts a second load beside the first (the command does not wait for
    /// itself), and whatever an earlier one still brings is for a panel that has moved on.
    /// </summary>
    private int _projectRun;

    /// <summary>True while the newest opening is still asking: nothing is installed from a half-filled panel.</summary>
    private bool _projectLoading;

    /// <summary>The build, game version and loader the panel's version and its list were worked out for.</summary>
    private (string? Build, string? Version, LoaderKind Loader) _projectTarget;

    private (string? Build, string? Version, LoaderKind Loader) CurrentProjectTarget()
        => (SelectedInstance?.Id, SelectedVersion?.Id, SelectedLoader);

    /// <summary>
    /// The panel's version, the list of what comes with it and the total belong to one
    /// build. With another build, game version or loader on screen they describe an
    /// install that is not the one "Add" would do, so the panel closes instead of offering it.
    /// </summary>
    private void CloseProjectIfBuildChanged()
    {
        if (IsProjectOpen && _projectTarget != CurrentProjectTarget())
        {
            CloseProject();
        }
    }

    public ObservableCollection<ModVersion> OpenedProjectVersions { get; } = new();

    public ObservableCollection<Bitmap> OpenedProjectGallery { get; } = new();

    /// <summary>What the opened mod needs, and whether the build has it.</summary>
    public ObservableCollection<ModDependencyItem> OpenedProjectDependencies { get; } = new();

    /// <summary>The version that "Add" would install: the newest release for this build.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenedProjectFileLabel))]
    [NotifyPropertyChangedFor(nameof(HasOpenedProjectVersion))]
    [NotifyPropertyChangedFor(nameof(CanAddOpenedProject))]
    private ModVersion? _openedProjectPreferred;

    partial void OnOpenedProjectChanged(ModProject? value)
    {
        OnPropertyChanged(nameof(OpenedProjectSourceLabel));
        OnPropertyChanged(nameof(OpenProjectPageLabel));
        RaiseOpenedProjectActions();
    }

    public bool HasOpenedProjectVersion => OpenedProjectPreferred is not null;

    /// <summary>"0.8.12 · 1.4 MB" for the version above.</summary>
    public string OpenedProjectFileLabel
    {
        get
        {
            if (OpenedProjectPreferred is null)
            {
                return Localize("Builds_DetailsNoVersions", "No version for this build");
            }

            var file = ModrinthClient.SelectFile(OpenedProjectPreferred, SelectedVersion?.Id, LoaderFor(BrowserKind));
            var size = file is null ? string.Empty : " · " + FormatSize(file.Size);
            return OpenedProjectPreferred.VersionNumber + size;
        }
    }

    public string VersionForLabel => Localize("Mods_VersionFor", "Version for {0}", SelectedVersion?.Id ?? SelectedInstance?.VersionId ?? "?");

    public bool OpenedProjectInstalled => OpenedProject is not null && IsProjectInstalled(OpenedProject.Slug);

    public bool OpenedProjectNotInstalled => OpenedProject is not null && !OpenedProjectInstalled;

    public string OpenedProjectByline => OpenedProject is null
        ? string.Empty
        : ModBrowserItem.CompactCount(OpenedProject.Downloads);

    /// <summary>The list of every compatible version, shown on request.</summary>
    [ObservableProperty]
    private bool _showOtherVersions;

    [RelayCommand]
    private void ToggleOtherVersions() => ShowOtherVersions = !ShowOtherVersions;

    [RelayCommand]
    private void OpenProjectPage()
    {
        OpenUrl(OpenedProjectPageUrl());
    }

    /// <summary>"Add" on the details panel: the preferred version, dependencies included.</summary>
    [RelayCommand]
    private async Task InstallOpenedProjectAsync()
    {
        if (OpenedProject is null || OpenedProjectPreferred is null || IsProjectBusy || _projectLoading ||
            IsOpenedProjectListIncomplete)
        {
            return;
        }

        // Every way the build changes closes the panel; this is for the one that was missed.
        if (_projectTarget != CurrentProjectTarget())
        {
            CloseProject();
            return;
        }

        // Kept: another card may be opened while the files are coming.
        var project = OpenedProject;
        var version = OpenedProjectPreferred;
        var target = CurrentInstallTarget();

        try
        {
            IsProjectBusy = true;
            _installBatch.Clear();
            await InstallOpenedPlanAsync(version, project, target);
            RefreshBrowserInstallState();
            RefreshHiddenItems();

            if (ReferenceEquals(OpenedProjectPreferred, version))
            {
                await RefreshOpenedProjectDependenciesAsync(version, project.Title);
            }

            // The panel stays open, so the list is not opened on the new mods; they are
            // outlined there, and the status line names every file that came. Not with
            // another build opened meanwhile: its list has none of them.
            if (IsSelectedBuild(target))
            {
                RevealFreshMods(_installBatch.ToList(), focus: null);
            }

            ReportModsLeftToThePlayer(project.Title);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_InstallMod", "Mod install failed: {0}", ex.Message);
            _ = ExplainDownloadFailureAsync(Localize("Net_WhatMod", "the mod"), ex);
            AppendConsole(ex.ToString());
        }
        finally
        {
            IsProjectBusy = _projectLoading;
        }
    }

    [RelayCommand]
    private async Task UninstallOpenedProjectAsync()
    {
        if (OpenedProject is null || IsProjectBusy)
        {
            return;
        }

        try
        {
            IsProjectBusy = true;
            await UninstallProjectAsync(OpenedProject.Slug);
        }
        finally
        {
            IsProjectBusy = _projectLoading;
        }
    }

    private async Task RefreshOpenedProjectDependenciesAsync(ModVersion? version, string title)
    {
        OpenedProjectDependencies.Clear();
        OpenedProjectTotalLabel = string.Empty;
        IsOpenedProjectListIncomplete = false;

        if (version is null)
        {
            return;
        }

        var (items, complete) = await ResolveDependenciesAsync(version, title);

        // The panel may have moved on to another mod while the answers were coming.
        if (!ReferenceEquals(version, OpenedProjectPreferred))
        {
            return;
        }

        OpenedProjectDependencies.Clear();

        foreach (var item in items)
        {
            OpenedProjectDependencies.Add(item);
        }

        IsOpenedProjectListIncomplete = !complete;
        RefreshOpenedProjectTotal();
    }

    [RelayCommand]
    private async Task OpenProjectAsync(ModBrowserItem? item)
    {
        if (item is null)
        {
            return;
        }

        // From here on only this opening may write to the panel: after every answer it
        // checks that no other card was clicked, and the panel not closed, in the meantime.
        var run = ++_projectRun;

        foreach (var other in ModBrowserItems)
        {
            other.IsSelected = ReferenceEquals(other, item);
        }

        try
        {
            _projectLoading = true;
            _projectTarget = CurrentProjectTarget();
            IsProjectBusy = true;
            IsProjectOpen = true;
            ShowOtherVersions = false;
            OpenedProject = null;
            OpenedProjectIcon = null;
            OpenedProjectPreferred = null;
            OpenedProjectVersions.Clear();
            ResetGallery();
            OpenedProjectDependencies.Clear();
            OpenedProjectTotalLabel = string.Empty;
            IsOpenedProjectBlocked = false;
            IsOpenedProjectListIncomplete = false;
            OpenedProjectDescription = item.Result.Description;

            // Nothing else announces it, and the build may have changed since the panel was last open.
            OnPropertyChanged(nameof(VersionForLabel));

            var source = SourceFor(item.Result.Source);

            // The list already has the logo cached, so show it immediately.
            var icon = await _images.GetAsync(item.Result.IconUrl).ConfigureAwait(true);

            if (run != _projectRun)
            {
                return;
            }

            OpenedProjectIcon = icon;

            var project = await source.GetProjectAsync(item.Result.ProjectId).ConfigureAwait(true);

            if (run != _projectRun)
            {
                return;
            }

            OpenedProject = project;

            if (project?.IconUrl is { Length: > 0 } iconUrl && OpenedProjectIcon is null)
            {
                icon = await _images.GetAsync(iconUrl).ConfigureAwait(true);

                if (run != _projectRun)
                {
                    return;
                }

                OpenedProjectIcon = icon;
            }

            if (project?.Body is { Length: > 0 } body)
            {
                OpenedProjectDescription = StripMarkdown(body);
            }

            _projectOriginalDescription = OpenedProjectDescription;
            IsProjectTranslated = false;

            var versions = await source
                .GetVersionsAsync(item.Result.ProjectId, SelectedVersion?.Id, LoaderFor(BrowserKind))
                .ConfigureAwait(true);

            if (run != _projectRun)
            {
                return;
            }

            foreach (var version in versions)
            {
                OpenedProjectVersions.Add(version);
            }

            var preferred = ModrinthClient.SelectPreferred(versions);
            OpenedProjectPreferred = preferred;

            // Said before the button is offered: a file its author keeps to the site is
            // opened in the browser, not installed.
            var blocked = await IsBlockedAsync(preferred);

            if (run != _projectRun)
            {
                return;
            }

            IsOpenedProjectBlocked = blocked;

            OnPropertyChanged(nameof(OpenedProjectInstalled));
            OnPropertyChanged(nameof(OpenedProjectNotInstalled));
            RaiseOpenedProjectActions();
            OnPropertyChanged(nameof(OpenedProjectByline));

            await RefreshOpenedProjectDependenciesAsync(preferred, item.Result.Title);

            if (run != _projectRun)
            {
                return;
            }

            if (project is not null)
            {
                foreach (var (url, index) in project.Gallery.Take(8).Select((u, i) => (u, i)))
                {
                    var image = await _images.GetAsync(url).ConfigureAwait(true);

                    // The project may have been closed or swapped while the image loaded.
                    if (run != _projectRun)
                    {
                        return;
                    }

                    if (image is not null)
                    {
                        OpenedProjectGallery.Add(image);
                        _galleryUrls.Add(index < project.GalleryFull.Count ? project.GalleryFull[index] : url);
                        OnPropertyChanged(nameof(LightboxCounter));
                        OnPropertyChanged(nameof(HasNextLightboxImage));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[catalog] project failed: {ex.Message}");
        }
        finally
        {
            // An earlier opening must not announce the panel ready while the newest is still filling it.
            if (run == _projectRun)
            {
                _projectLoading = false;
                IsProjectBusy = false;
            }
        }
    }

    [RelayCommand]
    private void CloseProject()
    {
        // A load still on its way is for a panel that is gone.
        _projectRun++;

        if (_projectLoading)
        {
            _projectLoading = false;
            IsProjectBusy = false;
        }

        IsProjectOpen = false;
        OpenedProject = null;
        OpenedProjectIcon = null;
        OpenedProjectPreferred = null;
        OpenedProjectVersions.Clear();
        ResetGallery();
        OpenedProjectDependencies.Clear();
        OpenedProjectTotalLabel = string.Empty;
        IsOpenedProjectBlocked = false;
        IsOpenedProjectListIncomplete = false;

        foreach (var item in ModBrowserItems)
        {
            item.IsSelected = false;
        }
    }

    /// <summary>Translates the project description in place; a second click restores it.</summary>
    [RelayCommand]
    private async Task TranslateProjectAsync()
    {
        if (IsProjectTranslated)
        {
            OpenedProjectDescription = _projectOriginalDescription;
            IsProjectTranslated = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(_projectOriginalDescription) || IsProjectBusy)
        {
            return;
        }

        var run = _projectRun;

        try
        {
            IsProjectBusy = true;
            var translated = await _translations.TranslateAsync(
                _projectOriginalDescription,
                _localization.Current);

            // Another mod's panel is not the place for this one's description.
            if (run != _projectRun)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(translated))
            {
                OpenedProjectDescription = translated;
                IsProjectTranslated = true;
            }
            else
            {
                Status = Localize("Builds_TranslateFailed", "Translation is unavailable right now");
            }
        }
        finally
        {
            IsProjectBusy = _projectLoading;
        }
    }

    [RelayCommand]
    private async Task InstallVersionAsync(ModVersion? version)
    {
        var file = version is null
            ? null
            : ModrinthClient.SelectFile(version, SelectedVersion?.Id, LoaderFor(BrowserKind));

        if (file is null || IsProjectBusy || _projectLoading ||
            (version!.Source != ModSource.CurseForge && string.IsNullOrEmpty(file.Url)))
        {
            return;
        }

        // The list of versions was asked for one build; it is not installed into another.
        if (_projectTarget != CurrentProjectTarget())
        {
            CloseProject();
            return;
        }

        // Kept: the versions are this mod's, whatever card is opened while the file comes.
        var project = OpenedProject;
        var run = _projectRun;
        var title = project?.Title ?? file.FileName;
        var target = CurrentInstallTarget();

        try
        {
            IsProjectBusy = true;

            // Checked before the installed version is taken out: a version only its page
            // gives out must not cost the player the one that works.
            if (await IsBlockedAsync(version))
            {
                Status = Localize("Mods_BlockedFile", "the author of {0} allows downloads only from the mod's page on CurseForge", title);
                return;
            }

            // Another version of an installed mod replaces it rather than sits beside it.
            // The old one is taken out through the list on screen, so only while that list
            // is still this build's; with another build opened meanwhile, the install
            // switches the old file off instead of removing it.
            if (project is not null && IsSelectedBuild(target) && IsProjectInstalled(project.Slug))
            {
                await UninstallProjectAsync(project.Slug);
            }

            await InstallProjectWithDependenciesAsync(
                version!,
                project?.Slug ?? string.Empty,
                title,
                project?.IconUrl,
                target: target);

            if (run == _projectRun)
            {
                OpenedProjectPreferred = version;
                IsOpenedProjectBlocked = false;
                ShowOtherVersions = false;
            }

            RefreshBrowserInstallState();
            RefreshHiddenItems();
        }
        catch (Exception ex)
        {
            Status = Localize("Error_InstallMod", "Mod install failed: {0}", ex.Message);
            _ = ExplainDownloadFailureAsync(Localize("Net_WhatMod", "the mod"), ex);
        }
        finally
        {
            IsProjectBusy = _projectLoading;
        }
    }

    /// <summary>Modrinth bodies are Markdown with HTML inside; the card shows readable plain text.</summary>
    public static string StripMarkdown(string markdown) => Core.Text.MarkdownText.ToPlainText(markdown);
}