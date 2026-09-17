using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>Which half of the build screen is visible.</summary>
public enum BuildTab
{
    Catalog,
    Settings
}

public sealed record ModSortOption(string Value, string Display);

/// <summary>A category ready for display: the machine name plus a translated label.</summary>
public sealed record ModCategoryOption(string Name, string Display);

/// <summary>A Modrinth search hit together with its lazily loaded logo and install state.</summary>
public partial class ModBrowserItem : ObservableObject
{
    public ModBrowserItem(ModSearchResult result, bool installed)
    {
        Result = result;
        _installed = installed;
    }

    public ModSearchResult Result { get; }

    [ObservableProperty]
    private Bitmap? _icon;

    [ObservableProperty]
    private bool _installed;
}

public partial class MainWindowViewModel
{
    // ===================== Build tabs =====================

    [ObservableProperty]
    private BuildTab _buildTab = BuildTab.Catalog;

    public bool IsBuildCatalog => BuildTab == BuildTab.Catalog;
    public bool IsBuildSettings => BuildTab == BuildTab.Settings;

    partial void OnBuildTabChanged(BuildTab value)
    {
        OnPropertyChanged(nameof(IsBuildCatalog));
        OnPropertyChanged(nameof(IsBuildSettings));
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

    public ObservableCollection<ModBrowserItem> ModBrowserItems { get; } = new();

    public ObservableCollection<ModCategoryOption> ModCategories { get; } = new();

    public ObservableCollection<ModSortOption> ModSortOptions { get; } = new();

    [ObservableProperty]
    private ModCategoryOption? _selectedCategory;

    [ObservableProperty]
    private ModSortOption? _selectedModSort;

    [ObservableProperty]
    private bool _isBrowserBusy;

    [ObservableProperty]
    private bool _canLoadMore;

    [ObservableProperty]
    private string _browserSummary = string.Empty;

    private const int BrowserPageSize = 20;

    private int _browserOffset;
    private int _browserTotal;
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
            var categories = await _modrinth.GetCategoriesAsync();

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
    private async Task SearchModsAsync()
    {
        await LoadBrowserPageAsync(reset: true);
    }

    [RelayCommand]
    private async Task LoadMoreModsAsync()
    {
        await LoadBrowserPageAsync(reset: false);
    }

    /// <summary>
    /// Loads one page of the Modrinth browser. An empty query browses the whole category,
    /// which is why the catalog has content without pressing the search button.
    /// </summary>
    private async Task LoadBrowserPageAsync(bool reset)
    {
        if (IsBrowserBusy)
        {
            return;
        }

        if (!IsBuildConfigured)
        {
            ModBrowserItems.Clear();
            BrowserSummary = string.Empty;
            CanLoadMore = false;
            return;
        }

        try
        {
            IsBrowserBusy = true;

            if (reset)
            {
                _browserOffset = 0;
                Status = Localize("Status_SearchingMods", "Searching Modrinth…");
            }

#if DEBUG
            System.Console.Error.WriteLine(
                $"[browser] version={SelectedVersion?.Id ?? "-"} loader={SelectedLoader} " +
                $"category={SelectedCategory?.Name ?? "-"} sort={SelectedModSort?.Value ?? "-"} offset={_browserOffset}");
#endif

            var category = string.IsNullOrWhiteSpace(SelectedCategory?.Name) ? null : SelectedCategory!.Name;

            var page = await _modrinth.SearchAsync(
                ModSearchQuery,
                SelectedVersion!.Id,
                SelectedLoader,
                category,
                SelectedModSort?.Value ?? "relevance",
                BrowserPageSize,
                _browserOffset);

            if (reset)
            {
                ModBrowserItems.Clear();
            }

            var added = new List<ModBrowserItem>();

            foreach (var result in page.Items)
            {
                var item = new ModBrowserItem(result, IsProjectInstalled(result.Slug));
                ModBrowserItems.Add(item);
                added.Add(item);
            }

            _browserOffset += page.Items.Count;
            _browserTotal = page.TotalHits;
            CanLoadMore = page.Items.Count > 0 && _browserOffset < _browserTotal;

            BrowserSummary = Localize("Mods_ShownOfTotal", "Shown {0} of {1}", ModBrowserItems.Count, _browserTotal);
            Status = BrowserSummary;

#if DEBUG
            System.Console.Error.WriteLine($"[browser] got {page.Items.Count} of {page.TotalHits}");
#endif

            _ = LoadIconsAsync(added);
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

    /// <summary>True when the build has a game version and a mod loader to search against.</summary>
    public bool IsBuildConfigured => SelectedVersion is not null && SelectedLoader != Core.Loaders.LoaderKind.Vanilla;

    /// <summary>Re-runs the browser after the build or the filters change, with a short delay.</summary>
    private void ScheduleBrowserReload()
    {
        _browserDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _browserDebounce = cts;

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

                await Dispatcher.UIThread.InvokeAsync(async () => await LoadBrowserPageAsync(reset: true));
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    partial void OnSelectedCategoryChanged(ModCategoryOption? value) => ScheduleBrowserReload();

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

            var versions = await _modrinth.GetVersionsAsync(item.Result.ProjectId, SelectedVersion?.Id, SelectedLoader);
            var file = versions.FirstOrDefault()?.PrimaryFile;

            if (file is null || string.IsNullOrEmpty(file.Url))
            {
                Status = Localize("Status_NoCompatibleFile", "No compatible file for this version and loader");
                return;
            }

            MaybeBackup(BackupTrigger.BeforeModChange);
            Status = Localize("Status_InstallingFile", "Installing {0}…", file.FileName);

            await _mods.InstallAsync(InstanceDirectory, file.FileName, file.Url, file.Sha1, file.Size);

            RecordInstalledMod(new InstalledModRecord
            {
                FileName = file.FileName,
                Source = ModSource.Modrinth,
                Id = item.Result.Slug,
                Name = item.Result.Title,
                IconUrl = item.Result.IconUrl
            });

            item.Installed = true;
            RefreshMods();
            Status = Localize("Status_InstalledFile", "Installed {0}", file.FileName);
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

    // ===================== Installed mods bookkeeping =====================

    /// <summary>
    /// Stores what a file is, where it came from and its logo. The file on disk remains
    /// the source of truth; this only enriches the list.
    /// </summary>
    public void RecordInstalledMod(InstalledModRecord record)
    {
        if (SelectedInstance is null)
        {
            return;
        }

        SelectedInstance.InstalledMods.RemoveAll(m =>
            string.Equals(m.FileName, record.FileName, StringComparison.OrdinalIgnoreCase));

        SelectedInstance.InstalledMods.Add(record);
        _instances.Save(SelectedInstance);
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
    }

    /// <summary>Drops records whose file is no longer on disk.</summary>
    private void ReconcileInstalledMods()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var present = _mods.ListMods(InstanceDirectory)
            .Select(m => m.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = SelectedInstance.InstalledMods.RemoveAll(m => !present.Contains(m.FileName));

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
}