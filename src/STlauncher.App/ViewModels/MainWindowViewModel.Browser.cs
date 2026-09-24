using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Content;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>Which half of the build screen is visible.</summary>
public enum BuildTab
{
    /// <summary>What is in the build: its mods, to switch off or remove.</summary>
    Mods,

    /// <summary>Resource packs, with the order the game will apply them in.</summary>
    ResourcePacks,

    /// <summary>Shader packs, one of which Iris loads.</summary>
    Shaders,

    /// <summary>Modrinth, to add more.</summary>
    Catalog,

    /// <summary>The pictures the game saved into this build.</summary>
    Screenshots,

    Settings
}

public sealed record ModSortOption(string Value, string Display);

/// <summary>A category chip: the machine name, a translated label, and whether it is the one pressed.</summary>
public partial class ModCategoryOption : ObservableObject
{
    public ModCategoryOption(string name, string display)
    {
        Name = name;
        Display = display;
    }

    public string Name { get; }

    public string Display { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>A Modrinth search hit together with its lazily loaded logo and install state.</summary>
public partial class ModBrowserItem : ObservableObject
{
    public ModBrowserItem(ModSearchResult result, bool installed)
    {
        Result = result;
        _installed = installed;
        _displayDescription = result.Description;
        DownloadsLabel = CompactCount(result.Downloads);
        Byline = string.IsNullOrWhiteSpace(result.Author)
            ? DownloadsLabel
            : $"{result.Author} · {DownloadsLabel}";
        CategoryLabels = result.Categories
            .Take(2)
            .Select(c => MainWindowViewModel.Localize($"Category_{c}", c))
            .ToList();
    }

    public ModSearchResult Result { get; }

    /// <summary>"CaffeineMC · 60 M downloads" under the title.</summary>
    public string Byline { get; }

    public string DownloadsLabel { get; }

    public IReadOnlyList<string> CategoryLabels { get; }

    [ObservableProperty]
    private Bitmap? _icon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotInstalled))]
    private bool _installed;

    public bool NotInstalled => !Installed;

    /// <summary>The card whose details are open on the right.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Hidden by the "hide installed" tick without leaving the page.</summary>
    [ObservableProperty]
    private bool _isHidden;

    /// <summary>Description in the interface language; falls back to the original.</summary>
    [ObservableProperty]
    private string _displayDescription;

    /// <summary>60 000 000 → "60 M", 340 000 → "340 K", in the interface language.</summary>
    public static string CompactCount(long count)
    {
        if (count >= 1_000_000)
        {
            var millions = count / 1_000_000d;
            return MainWindowViewModel.Localize("Count_Millions", "{0} M downloads",
                millions >= 10 ? millions.ToString("F0", CultureInfo.CurrentCulture) : millions.ToString("F1", CultureInfo.CurrentCulture));
        }

        if (count >= 1_000)
        {
            return MainWindowViewModel.Localize("Count_Thousands", "{0} K downloads", (count / 1_000).ToString(CultureInfo.CurrentCulture));
        }

        return MainWindowViewModel.Localize("Count_Plain", "{0} downloads", count);
    }
}

/// <summary>One dependency of the opened mod, with whether the build already has it.</summary>
public sealed record ModDependencyItem(string Title, bool Installed, bool Required)
{
    public string StateLabel => Installed
        ? MainWindowViewModel.Localize("Mods_DepInstalled", "in the build")
        : Required
            ? MainWindowViewModel.Localize("Mods_DepWillInstall", "will be added too")
            : MainWindowViewModel.Localize("Mods_DepOptional", "optional");
}

public partial class MainWindowViewModel
{
    /// <summary>Mods per page. Paging keeps the list light and the logos few.</summary>
    public const int BrowserPageSize = 20;

    // ===================== Build tabs =====================

    [ObservableProperty]
    private BuildTab _buildTab = BuildTab.Mods;

    public bool IsBuildMods => BuildTab == BuildTab.Mods;
    public bool IsBuildResourcePacks => BuildTab == BuildTab.ResourcePacks;
    public bool IsBuildShaders => BuildTab == BuildTab.Shaders;
    public bool IsBuildCatalog => BuildTab == BuildTab.Catalog;
    public bool IsBuildSettings => BuildTab == BuildTab.Settings;
    public bool IsBuildScreenshots => BuildTab == BuildTab.Screenshots;

    partial void OnBuildTabChanged(BuildTab value)
    {
        OnPropertyChanged(nameof(IsBuildMods));
        OnPropertyChanged(nameof(IsBuildResourcePacks));
        OnPropertyChanged(nameof(IsBuildShaders));
        OnPropertyChanged(nameof(IsBuildCatalog));
        OnPropertyChanged(nameof(IsBuildSettings));
        OnPropertyChanged(nameof(IsBuildScreenshots));

        // Decoding thumbnails is work; do it when the tab is opened, not on every change.
        if (value == BuildTab.Screenshots)
        {
            RefreshScreenshots();
        }
    }

    [RelayCommand]
    private void SelectBuildTab(string? tab)
    {
        if (Enum.TryParse<BuildTab>(tab, ignoreCase: true, out var parsed))
        {
            BuildTab = parsed;
        }
    }

    // ===================== Modrinth browser =====================

    /// <summary>What the browser is showing: mods, resource packs or shaders.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowsingMods))]
    [NotifyPropertyChangedFor(nameof(IsBrowsingResourcePacks))]
    [NotifyPropertyChangedFor(nameof(IsBrowsingShaders))]
    [NotifyPropertyChangedFor(nameof(IsBuildConfigured))]
    private string _browserKind = ProjectTypes.Mod;

    public bool IsBrowsingMods => BrowserKind == ProjectTypes.Mod;
    public bool IsBrowsingResourcePacks => BrowserKind == ProjectTypes.ResourcePack;
    public bool IsBrowsingShaders => BrowserKind == ProjectTypes.Shader;

    partial void OnBrowserKindChanged(string value)
    {
        // Categories differ per kind ("16x" is not a mod category), and so does the page.
        _categoriesLoaded = false;
        CloseProject();
        _ = LoadCategoriesAsync();
        ScheduleBrowserReload();
    }

    [RelayCommand]
    private void SelectBrowserKind(string? kind)
    {
        if (kind is ProjectTypes.Mod or ProjectTypes.ResourcePack or ProjectTypes.Shader)
        {
            BrowserKind = kind;
        }
    }

    public ObservableCollection<ModBrowserItem> ModBrowserItems { get; } = new();

    public ObservableCollection<ModCategoryOption> ModCategories { get; } = new();

    public ObservableCollection<ModSortOption> ModSortOptions { get; } = new();

    [ObservableProperty]
    private ModCategoryOption? _selectedCategory;

    [ObservableProperty]
    private ModSortOption? _selectedModSort;

    /// <summary>Takes the mods already in the build off the page, for finding what is missing.</summary>
    [ObservableProperty]
    private bool _hideInstalledMods;

    partial void OnHideInstalledModsChanged(bool value) => RefreshHiddenItems();

    private void RefreshHiddenItems()
    {
        foreach (var item in ModBrowserItems)
        {
            item.IsHidden = HideInstalledMods && item.Installed;
        }
    }

    /// <summary>A chip pressed; the same chip again goes back to all categories.</summary>
    [RelayCommand]
    private void SelectCategory(ModCategoryOption? category)
    {
        if (category is null)
        {
            return;
        }

        SelectedCategory = category.IsSelected && category.Name.Length > 0
            ? ModCategories.FirstOrDefault(c => c.Name.Length == 0)
            : category;
    }

    /// <summary>"into SHOWTIME 1.21.11 · Fabric · only for 1.21.11", under the catalog title.</summary>
    public string CatalogScopeLabel => SelectedInstance is null
        ? string.Empty
        : Localize(
            "Mods_Scope",
            "into {0} · {1} · only mods for {2}",
            SelectedInstance.Name,
            SelectedLoader,
            SelectedVersion?.Id ?? "?");

    [ObservableProperty]
    private bool _isBrowserBusy;

    [ObservableProperty]
    private string _browserSummary = string.Empty;

    [ObservableProperty]
    private int _browserPage = 1;

    [ObservableProperty]
    private int _browserTotalPages = 1;

    public bool CanGoToPreviousPage => BrowserPage > 1 && !IsBrowserBusy;
    public bool CanGoToNextPage => BrowserPage < BrowserTotalPages && !IsBrowserBusy;

    public string BrowserPageLabel => $"{BrowserPage} / {BrowserTotalPages}";

    partial void OnBrowserPageChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        OnPropertyChanged(nameof(BrowserPageLabel));
    }

    partial void OnBrowserTotalPagesChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        OnPropertyChanged(nameof(BrowserPageLabel));
    }

    partial void OnIsBrowserBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
    }

    private CancellationTokenSource? _browserDebounce;
    private bool _categoriesLoaded;

    public async Task LoadCategoriesAsync()
    {
        if (_categoriesLoaded)
        {
            return;
        }

        try
        {
            var categories = await _modrinth.GetCategoriesAsync(BrowserKind);

            LoadSortOptions();

            var previous = SelectedCategory?.Name ?? string.Empty;

            ModCategories.Clear();
            ModCategories.Add(new ModCategoryOption(
                string.Empty,
                Localize("Mods_AllCategories", "All categories")));

            foreach (var category in categories)
            {
                ModCategories.Add(new ModCategoryOption(
                    category.Name,
                    Localize($"Category_{category.Name}", category.Display)));
            }

            _categoriesLoaded = true;
            SelectedCategory = ModCategories.FirstOrDefault(c => c.Name == previous) ?? ModCategories[0];
        }
        catch (Exception ex)
        {
            AppendConsole($"[modrinth] categories failed: {ex.Message}");
        }

        SelectedModSort ??= ModSortOptions.FirstOrDefault();
    }

    /// <summary>Rebuilt so the labels follow the interface language.</summary>
    private void LoadSortOptions()
    {
        var previous = SelectedModSort?.Value ?? "relevance";

        ModSortOptions.Clear();
        ModSortOptions.Add(new ModSortOption("relevance", Localize("ModSort_Relevance", "By relevance")));
        ModSortOptions.Add(new ModSortOption("downloads", Localize("ModSort_Downloads", "By downloads")));
        ModSortOptions.Add(new ModSortOption("newest", Localize("ModSort_Newest", "Newest")));
        ModSortOptions.Add(new ModSortOption("updated", Localize("ModSort_Updated", "Updated")));

        SelectedModSort = ModSortOptions.FirstOrDefault(o => o.Value == previous) ?? ModSortOptions[0];
    }

    /// <summary>Re-translates the category and sort labels after a language change.</summary>
    public void ReloadLocalizedBrowserOptions()
    {
        _categoriesLoaded = false;
        _ = LoadCategoriesAsync();
    }

    [RelayCommand]
    private Task SearchModsAsync() => LoadBrowserPageAsync(1);

    [RelayCommand]
    private Task NextBrowserPageAsync()
        => BrowserPage < BrowserTotalPages ? LoadBrowserPageAsync(BrowserPage + 1) : Task.CompletedTask;

    [RelayCommand]
    private Task PreviousBrowserPageAsync()
        => BrowserPage > 1 ? LoadBrowserPageAsync(BrowserPage - 1) : Task.CompletedTask;

    /// <summary>
    /// Loads one page of the Modrinth browser. An empty query browses the whole category,
    /// which is why the catalog has content without pressing the search button.
    /// </summary>
    private async Task LoadBrowserPageAsync(int page)
    {
        if (IsBrowserBusy)
        {
            return;
        }

        if (!IsBuildConfigured)
        {
            ModBrowserItems.Clear();
            BrowserSummary = string.Empty;
            BrowserTotalPages = 1;
            return;
        }

        try
        {
            IsBrowserBusy = true;
            Status = Localize("Status_SearchingMods", "Searching Modrinth…");

            var category = string.IsNullOrWhiteSpace(SelectedCategory?.Name) ? null : SelectedCategory!.Name;
            var offset = Math.Max(0, page - 1) * BrowserPageSize;

            var result = await _modrinth.SearchAsync(
                ModSearchQuery,
                SelectedVersion!.Id,
                SelectedLoader,
                category,
                SelectedModSort?.Value ?? "relevance",
                BrowserPageSize,
                offset,
                projectType: BrowserKind);

            BrowserPage = page;
            BrowserTotalPages = Math.Max(1, (int)Math.Ceiling(result.TotalHits / (double)BrowserPageSize));

            ModBrowserItems.Clear();

            foreach (var item in result.Items)
            {
                ModBrowserItems.Add(new ModBrowserItem(item, IsProjectInstalled(item.Slug))
                {
                    IsSelected = string.Equals(item.Slug, OpenedProject?.Slug, StringComparison.OrdinalIgnoreCase)
                });
            }

            RefreshHiddenItems();
            OnPropertyChanged(nameof(CatalogScopeLabel));

            var from = result.Items.Count == 0 ? 0 : offset + 1;
            var to = offset + result.Items.Count;

            BrowserSummary = Localize(
                "Mods_ShownOfTotal",
                "Shown {0}-{1} of {2}",
                from,
                to,
                result.TotalHits);

            // The summary belongs to the catalog footer only; writing it to Status made
            // it show up in the always-visible status bar on every section. But the
            // "searching" line it replaced has to go too, or it stays up for good.
            if (Status == Localize("Status_SearchingMods", "Searching Modrinth…"))
            {
                Status = string.Empty;
            }

            var pageItems = ModBrowserItems.ToList();
            _ = LoadIconsAsync(pageItems);
            _ = TranslateBrowserItemsAsync(pageItems);
        }
        catch (Exception ex)
        {
            Status = Localize("Error_SearchMods", "Mod search failed: {0}", ex.Message);
        }
        finally
        {
            IsBrowserBusy = false;
        }
    }

    /// <summary>
    /// True when the build has what the browser needs: a game version, and for mods a
    /// loader. Packs and shaders fit a vanilla build too.
    /// </summary>
    public bool IsBuildConfigured => SelectedVersion is not null &&
                                     (SelectedLoader != Core.Loaders.LoaderKind.Vanilla || !ProjectTypes.UsesLoader(BrowserKind));

    /// <summary>The loader to filter versions by: none for packs, which have no loader.</summary>
    private Core.Loaders.LoaderKind LoaderFor(string projectType)
        => ProjectTypes.UsesLoader(projectType) ? SelectedLoader : Core.Loaders.LoaderKind.Vanilla;

    /// <summary>Re-runs the browser after the build or the filters change, with a short delay.</summary>
    private void ScheduleBrowserReload()
    {
        var previousCts = _browserDebounce;
        var cts = new CancellationTokenSource();
        _browserDebounce = cts;
        previousCts?.Cancel();
        previousCts?.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, cts.Token);

                // A newer request may have arrived while this one waited.
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                // LoadBrowserPageAsync bails out while another search is running. Dropping
                // the reload there left the grid showing results for the previous version,
                // so wait for the in-flight one to finish and then refresh.
                for (var waited = 0; waited < 2000 && IsBrowserBusy; waited += 100)
                {
                    await Task.Delay(100, cts.Token);
                }

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(async () => await LoadBrowserPageAsync(1));
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    partial void OnSelectedCategoryChanged(ModCategoryOption? value)
    {
        foreach (var category in ModCategories)
        {
            category.IsSelected = ReferenceEquals(category, value);
        }

        ScheduleBrowserReload();
    }

    partial void OnSelectedModSortChanged(ModSortOption? value) => ScheduleBrowserReload();

    [RelayCommand]
    private async Task InstallBrowserItemAsync(ModBrowserItem? item)
    {
        if (item is null || IsBrowserBusy)
        {
            return;
        }

        try
        {
            IsBrowserBusy = true;
            Status = Localize("Status_ResolvingMod", "Resolving {0}…", item.Result.Title);

            var versions = await _modrinth.GetVersionsAsync(item.Result.ProjectId, SelectedVersion?.Id, LoaderFor(BrowserKind));
            var preferred = ModrinthClient.SelectPreferred(versions);

            if (preferred is null)
            {
                Status = Localize("Status_NoCompatibleFile", "No compatible file for this version and loader");
                return;
            }

            await InstallProjectWithDependenciesAsync(
                preferred,
                item.Result.Slug,
                item.Result.Title,
                item.Result.IconUrl,
                projectType: BrowserKind);

            item.Installed = true;
            RefreshHiddenItems();
        }
        catch (Exception ex)
        {
            Status = Localize("Error_InstallMod", "Mod install failed: {0}", ex.Message);
            AppendConsole(ex.ToString());
        }
        finally
        {
            IsBrowserBusy = false;
        }
    }

    /// <summary>
    /// Installs a version and everything it requires. A mod that needs Fabric API and is
    /// installed without it crashes the game on the next start with a message the player
    /// cannot act on - so the required dependencies come along, one level down each.
    /// </summary>
    private async Task InstallProjectWithDependenciesAsync(
        ModVersion version,
        string slug,
        string title,
        string? iconUrl,
        int depth = 0,
        string? projectType = null)
    {
        projectType ??= BrowserKind;
        var file = ModrinthClient.SelectFile(version, SelectedVersion?.Id, LoaderFor(projectType));

        if (file is null || string.IsNullOrEmpty(file.Url))
        {
            Status = Localize("Status_NoCompatibleFile", "No compatible file for this version and loader");
            return;
        }

        if (depth == 0)
        {
            await MaybeBackupAsync(BackupTrigger.BeforeModChange);
        }

        // Dependencies first, so a failure there leaves the build without the mod rather
        // than with a mod that cannot start.
        if (depth < 2)
        {
            foreach (var dependency in version.Dependencies.Where(d => d.IsRequired && !string.IsNullOrEmpty(d.ProjectId)))
            {
                var project = await _modrinth.GetProjectAsync(dependency.ProjectId!);

                if (project is null || IsProjectInstalled(project.Slug))
                {
                    continue;
                }

                Status = Localize("Status_ResolvingDependency", "Adding {0}, which {1} needs…", project.Title, title);

                // A shader's dependency is a mod (Iris); the folder follows the dependency.
                var candidates = await _modrinth.GetVersionsAsync(project.Id, SelectedVersion?.Id, LoaderFor(project.ProjectType));
                var pick = dependency.VersionId is { } wanted
                    ? candidates.FirstOrDefault(v => v.Id == wanted) ?? ModrinthClient.SelectPreferred(candidates)
                    : ModrinthClient.SelectPreferred(candidates);

                if (pick is null)
                {
                    throw new InvalidOperationException(
                        Localize("Error_DependencyMissing", "{0} needs {1}, which has no version for this build", title, project.Title));
                }

                await InstallProjectWithDependenciesAsync(pick, project.Slug, project.Title, project.IconUrl, depth + 1, project.ProjectType);
            }
        }

        var folder = ProjectTypes.FolderFor(projectType);

        Status = Localize("Status_InstallingFile", "Installing {0}…", file.FileName);
        await _mods.InstallAsync(InstanceDirectory, folder, file.FileName, file.Url, file.Sha1, file.Size);

        RecordInstalledMod(new InstalledModRecord
        {
            FileName = file.FileName,
            Source = ModSource.Modrinth,
            Id = slug,
            Name = title,
            IconUrl = iconUrl,
            Version = version.VersionNumber,
            Folder = folder
        });

        RefreshMods();
        Status = Localize("Status_InstalledFile", "Installed {0}", file.FileName);
    }

    /// <summary>Takes a Modrinth project out of the build by its slug.</summary>
    private async Task UninstallProjectAsync(string slug)
    {
        var record = SelectedInstance?.InstalledMods.FirstOrDefault(m =>
            string.Equals(m.Id, slug, StringComparison.OrdinalIgnoreCase));

        if (record is null)
        {
            return;
        }

        var mod = InstalledMods.FirstOrDefault(m =>
            string.Equals(m.FileName, record.FileName, StringComparison.OrdinalIgnoreCase));

        if (mod is not null)
        {
            await UninstallModAsync(mod);
        }

        RefreshBrowserInstallState();
        RefreshHiddenItems();
    }

    // ===================== Installed mods bookkeeping =====================

    /// <summary>
    /// Stores what a file is, where it came from and its logo. The file on disk remains
    /// the source of truth; this only enriches the list.
    /// </summary>
    public void RecordInstalledMod(InstalledModRecord record)
    {
        if (SelectedInstance is not null)
        {
            RecordInstalledMod(SelectedInstance, record);
        }
    }

    /// <summary>
    /// The same, for an explicit build. The startup sync runs against the recommended
    /// build even when the player has a different one open.
    /// </summary>
    public void RecordInstalledMod(Instance instance, InstalledModRecord record)
    {
        instance.InstalledMods.RemoveAll(m =>
            string.Equals(m.FileName, record.FileName, StringComparison.OrdinalIgnoreCase));

        instance.InstalledMods.Add(record);
        _instances.Save(instance);
    }

    public bool IsProjectInstalled(string slug)
        => SelectedInstance?.InstalledMods.Any(m =>
               string.Equals(m.Id, slug, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>Re-evaluates the "installed" badge after the build or its files change.</summary>
    private void RefreshBrowserInstallState()
    {
        foreach (var item in ModBrowserItems)
        {
            item.Installed = IsProjectInstalled(item.Result.Slug);
        }

        OnPropertyChanged(nameof(OpenedProjectInstalled));
        OnPropertyChanged(nameof(OpenedProjectNotInstalled));
    }

    /// <summary>Drops records whose file is no longer on disk.</summary>
    private void ReconcileInstalledMods()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var present = _mods.ListMods(InstanceDirectory)
            .Concat(_mods.ListPacks(InstanceDirectory, CatalogPlacement.ResourcePacksFolder))
            .Concat(_mods.ListPacks(InstanceDirectory, CatalogPlacement.ShaderPacksFolder))
            .Select(m => m.Folder + "/" + m.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = SelectedInstance.InstalledMods.RemoveAll(m =>
            !present.Contains((m.Folder ?? ModManager.ModsFolderName) + "/" + m.FileName));

        if (removed > 0)
        {
            _instances.Save(SelectedInstance);
        }
    }

    private async Task LoadIconsAsync(IReadOnlyList<ModBrowserItem> items)
    {
        foreach (var item in items)
        {
            var icon = await _images.GetAsync(item.Result.IconUrl).ConfigureAwait(false);

            if (icon is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => item.Icon = icon);
            }
        }
    }

    /// <summary>
    /// Translates the short descriptions of one page into the interface language.
    /// Titles stay original on purpose.
    /// </summary>
    private async Task TranslateBrowserItemsAsync(IReadOnlyList<ModBrowserItem> items)
    {
        var target = _localization.Current;

        // Modrinth descriptions are English; translating to English is a no-op.
        if (string.Equals(target, "en", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var item in items)
        {
            var translated = await _translations
                .TranslateAsync(item.Result.Description, target)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(translated))
            {
                await Dispatcher.UIThread.InvokeAsync(() => item.DisplayDescription = translated);
            }
        }
    }
}