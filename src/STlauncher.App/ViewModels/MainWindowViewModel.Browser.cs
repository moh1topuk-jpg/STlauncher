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
        _displayDescription = result.Description;
    }

    public ModSearchResult Result { get; }

    [ObservableProperty]
    private Bitmap? _icon;

    [ObservableProperty]
    private bool _installed;

    /// <summary>Description in the interface language; falls back to the original.</summary>
    [ObservableProperty]
    private string _displayDescription;
}

public partial class MainWindowViewModel
{
    /// <summary>Mods per page. Paging keeps the list light and the logos few.</summary>
    public const int BrowserPageSize = 20;

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
                offset);

            BrowserPage = page;
            BrowserTotalPages = Math.Max(1, (int)Math.Ceiling(result.TotalHits / (double)BrowserPageSize));

            ModBrowserItems.Clear();

            foreach (var item in result.Items)
            {
                ModBrowserItems.Add(new ModBrowserItem(item, IsProjectInstalled(item.Slug)));
            }

            var from = result.Items.Count == 0 ? 0 : offset + 1;
            var to = offset + result.Items.Count;

            BrowserSummary = Localize(
                "Mods_ShownOfTotal",
                "Shown {0}-{1} of {2}",
                from,
                to,
                result.TotalHits);

            // The summary belongs to the catalog footer only; writing it to Status made
            // it show up in the always-visible status bar on every section.
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

                await Dispatcher.UIThread.InvokeAsync(async () => await LoadBrowserPageAsync(1));
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
            var preferred = ModrinthClient.SelectPreferred(versions);
            var file = preferred is null ? null : ModrinthClient.SelectFile(preferred, SelectedVersion?.Id, SelectedLoader);

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