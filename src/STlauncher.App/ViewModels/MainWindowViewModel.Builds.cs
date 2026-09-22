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

            return System.IO.Directory.Exists(mods)
                ? System.IO.Directory.EnumerateFiles(mods, "*.jar").Count()
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
            _instances.Save(instance);

            _allInstances.Add(instance);
            ApplyBuildFilter();
            SelectedInstance = instance;

            IsNewBuildOpen = false;
            BuildTab = BuildTab.Settings;
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

    public ObservableCollection<ModVersion> OpenedProjectVersions { get; } = new();

    public ObservableCollection<Bitmap> OpenedProjectGallery { get; } = new();

    /// <summary>What the opened mod needs, and whether the build has it.</summary>
    public ObservableCollection<ModDependencyItem> OpenedProjectDependencies { get; } = new();

    /// <summary>The version that "Add" would install: the newest release for this build.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenedProjectFileLabel))]
    [NotifyPropertyChangedFor(nameof(HasOpenedProjectVersion))]
    private ModVersion? _openedProjectPreferred;

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

            var file = ModrinthClient.SelectFile(OpenedProjectPreferred, SelectedVersion?.Id, SelectedLoader);
            var size = file is null ? string.Empty : " · " + FormatSize(file.Size);
            return OpenedProjectPreferred.VersionNumber + size;
        }
    }

    public string VersionForLabel => Localize("Mods_VersionFor", "Version for {0}", SelectedVersion?.Id ?? "?");

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
        if (OpenedProject is not null)
        {
            OpenUrl($"https://modrinth.com/mod/{OpenedProject.Slug}");
        }
    }

    /// <summary>"Add" on the details panel: the preferred version, dependencies included.</summary>
    [RelayCommand]
    private async Task InstallOpenedProjectAsync()
    {
        if (OpenedProject is null || OpenedProjectPreferred is null || IsProjectBusy)
        {
            return;
        }

        try
        {
            IsProjectBusy = true;
            await InstallProjectWithDependenciesAsync(
                OpenedProjectPreferred, OpenedProject.Slug, OpenedProject.Title, OpenedProject.IconUrl);
            RefreshBrowserInstallState();
            RefreshHiddenItems();
            await RefreshOpenedProjectDependenciesAsync(OpenedProjectPreferred, OpenedProject.Title);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_InstallMod", "Mod install failed: {0}", ex.Message);
            AppendConsole(ex.ToString());
        }
        finally
        {
            IsProjectBusy = false;
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
            IsProjectBusy = false;
        }
    }

    private async Task RefreshOpenedProjectDependenciesAsync(ModVersion? version, string title)
    {
        OpenedProjectDependencies.Clear();

        if (version is null)
        {
            return;
        }

        foreach (var dependency in version.Dependencies.Where(d => !string.IsNullOrEmpty(d.ProjectId)).Take(5))
        {
            try
            {
                var project = await _modrinth.GetProjectAsync(dependency.ProjectId!).ConfigureAwait(true);

                if (project is not null)
                {
                    OpenedProjectDependencies.Add(new ModDependencyItem(
                        project.Title, IsProjectInstalled(project.Slug), dependency.IsRequired));
                }
            }
            catch (Exception ex)
            {
                AppendConsole($"[modrinth] dependency of {title}: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task OpenProjectAsync(ModBrowserItem? item)
    {
        if (item is null)
        {
            return;
        }

        foreach (var other in ModBrowserItems)
        {
            other.IsSelected = ReferenceEquals(other, item);
        }

        try
        {
            IsProjectBusy = true;
            IsProjectOpen = true;
            ShowOtherVersions = false;
            OpenedProject = null;
            OpenedProjectIcon = null;
            OpenedProjectPreferred = null;
            OpenedProjectVersions.Clear();
            OpenedProjectGallery.Clear();
            OpenedProjectDependencies.Clear();
            OpenedProjectDescription = item.Result.Description;

            // The list already has the logo cached, so show it immediately.
            OpenedProjectIcon = await _images.GetAsync(item.Result.IconUrl).ConfigureAwait(true);

            var project = await _modrinth.GetProjectAsync(item.Result.ProjectId).ConfigureAwait(true);
            OpenedProject = project;

            if (project?.IconUrl is { Length: > 0 } iconUrl && OpenedProjectIcon is null)
            {
                OpenedProjectIcon = await _images.GetAsync(iconUrl).ConfigureAwait(true);
            }

            if (project?.Body is { Length: > 0 } body)
            {
                OpenedProjectDescription = StripMarkdown(body);
            }

            _projectOriginalDescription = OpenedProjectDescription;
            IsProjectTranslated = false;

            var versions = await _modrinth
                .GetVersionsAsync(item.Result.ProjectId, SelectedVersion?.Id, SelectedLoader)
                .ConfigureAwait(true);

            foreach (var version in versions)
            {
                OpenedProjectVersions.Add(version);
            }

            OpenedProjectPreferred = ModrinthClient.SelectPreferred(versions);
            OnPropertyChanged(nameof(OpenedProjectInstalled));
            OnPropertyChanged(nameof(OpenedProjectNotInstalled));
            OnPropertyChanged(nameof(OpenedProjectByline));

            await RefreshOpenedProjectDependenciesAsync(OpenedProjectPreferred, item.Result.Title);

            if (project is not null)
            {
                foreach (var url in project.Gallery.Take(4))
                {
                    var image = await _images.GetAsync(url).ConfigureAwait(true);

                    if (image is not null)
                    {
                        OpenedProjectGallery.Add(image);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[modrinth] project failed: {ex.Message}");
        }
        finally
        {
            IsProjectBusy = false;
        }
    }

    [RelayCommand]
    private void CloseProject()
    {
        IsProjectOpen = false;
        OpenedProject = null;
        OpenedProjectIcon = null;
        OpenedProjectPreferred = null;
        OpenedProjectVersions.Clear();
        OpenedProjectGallery.Clear();
        OpenedProjectDependencies.Clear();

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

        try
        {
            IsProjectBusy = true;
            var translated = await _translations.TranslateAsync(
                _projectOriginalDescription,
                _localization.Current);

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
            IsProjectBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallVersionAsync(ModVersion? version)
    {
        var file = version is null
            ? null
            : ModrinthClient.SelectFile(version, SelectedVersion?.Id, SelectedLoader);

        if (file is null || string.IsNullOrEmpty(file.Url) || IsProjectBusy)
        {
            return;
        }

        try
        {
            IsProjectBusy = true;

            // Another version of an installed mod replaces it rather than sits beside it.
            if (OpenedProject is not null && IsProjectInstalled(OpenedProject.Slug))
            {
                await UninstallProjectAsync(OpenedProject.Slug);
            }

            await InstallProjectWithDependenciesAsync(
                version!,
                OpenedProject?.Slug ?? string.Empty,
                OpenedProject?.Title ?? file.FileName,
                OpenedProject?.IconUrl);

            OpenedProjectPreferred = version;
            RefreshBrowserInstallState();
            RefreshHiddenItems();
            ShowOtherVersions = false;
        }
        catch (Exception ex)
        {
            Status = Localize("Error_InstallMod", "Mod install failed: {0}", ex.Message);
        }
        finally
        {
            IsProjectBusy = false;
        }
    }

    /// <summary>Modrinth bodies are Markdown; the card shows readable plain text.</summary>
    public static string StripMarkdown(string markdown)
    {
        var lines = markdown
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line
                .Replace("###", string.Empty)
                .Replace("##", string.Empty)
                .Replace("#", string.Empty)
                .Replace("**", string.Empty)
                .Replace("`", string.Empty)
                .TrimEnd())
            .ToList();

        var result = new List<string>();
        var blank = false;

        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (!blank && result.Count > 0)
                {
                    result.Add(string.Empty);
                }

                blank = true;
                continue;
            }

            result.Add(line);
            blank = false;
        }

        return string.Join('\n', result).Trim();
    }
}