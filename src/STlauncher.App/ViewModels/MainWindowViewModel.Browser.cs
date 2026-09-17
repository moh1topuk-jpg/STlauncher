using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    public ObservableCollection<ModCategory> ModCategories { get; } = new();

    public IReadOnlyList<ModSortOption> ModSortOptions { get; } =
        new List<ModSortOption>
        {
            new("relevance", "По релевантности"),
            new("downloads", "По загрузкам"),
            new("newest", "Новые"),
            new("updated", "Обновлённые")
        };

    [ObservableProperty]
    private ModCategory? _selectedCategory;

    [ObservableProperty]
    private ModSortOption? _selectedModSort;

    [ObservableProperty]
    private bool _isBrowserBusy;

    private bool _categoriesLoaded;

    public async Task LoadCategoriesAsync()
    {
        if (_categoriesLoaded)
        {
            return;
        }

        _categoriesLoaded = true;

        try
        {
            var categories = await _modrinth.GetCategoriesAsync();

            ModCategories.Clear();
            ModCategories.Add(new ModCategory(string.Empty, Localize("Mods_AllCategories", "All categories")));

            foreach (var category in categories)
            {
                ModCategories.Add(category);
            }

            SelectedCategory = ModCategories[0];
        }
        catch (Exception ex)
        {
            AppendConsole($"[modrinth] categories failed: {ex.Message}");
        }

        SelectedModSort ??= ModSortOptions[0];
    }

    [RelayCommand]
    private async Task SearchModsAsync()
    {
        if (SelectedVersion is null)
        {
            Status = Localize("Status_SelectVersion", "Select a version first");
            return;
        }

        if (SelectedLoader == Core.Loaders.LoaderKind.Vanilla)
        {
            Status = Localize("Status_SelectLoader", "Select a mod loader first");
            return;
        }

        try
        {
            IsBrowserBusy = true;
            Status = Localize("Status_SearchingMods", "Searching Modrinth…");

            var category = string.IsNullOrWhiteSpace(SelectedCategory?.Name) ? null : SelectedCategory!.Name;

            var results = await _modrinth.SearchAsync(
                ModSearchQuery,
                SelectedVersion.Id,
                SelectedLoader,
                category,
                SelectedModSort?.Value ?? "relevance");

            ModBrowserItems.Clear();

            foreach (var result in results)
            {
                ModBrowserItems.Add(new ModBrowserItem(result, IsProjectInstalled(result.Slug)));
            }

            Status = Localize("Status_FoundMods", "Found {0} mods", ModBrowserItems.Count);
            _ = LoadIconsAsync(ModBrowserItems.ToList());
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