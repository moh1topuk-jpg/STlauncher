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

/// <summary>A search hit from either source, together with its lazily loaded logo and install state.</summary>
public partial class ModBrowserItem : ObservableObject
{
    public ModBrowserItem(ModSearchResult result, bool installed)
    {
        Result = result;
        _installed = installed;
        _displayDescription = result.Description;
        DownloadsLabel = CompactCount(result.Downloads);
        DownloadsShort = CompactCount(result.Downloads, withWord: false);
        Byline = string.IsNullOrWhiteSpace(result.Author)
            ? DownloadsLabel
            : $"{result.Author} · {DownloadsLabel}";
        CategoryLabels = result.Categories
            .Take(2)
            .Select(c => MainWindowViewModel.Localize($"Category_{c}", Humanize(c)))
            .ToList();
        Initial = result.Title.FirstOrDefault(char.IsLetterOrDigit) is var letter && letter != default
            ? char.ToUpperInvariant(letter).ToString()
            : "?";
    }

    public ModSearchResult Result { get; }

    /// <summary>"CaffeineMC · 60 M downloads" under the title.</summary>
    public string Byline { get; }

    public string DownloadsLabel { get; }

    /// <summary>"60 M" beside the author on a card, where "60 M downloads" pushes the name out.</summary>
    public string DownloadsShort { get; }

    public IReadOnlyList<string> CategoryLabels { get; }

    /// <summary>The letter on the icon tile until the logo arrives, and for a mod that has none.</summary>
    public string Initial { get; }

    /// <summary>"Modrinth" or "CurseForge", on the card once the catalog has two sources.</summary>
    public string SourceLabel => MainWindowViewModel.SourceName(Result.Source);

    public bool IsCurseForge => Result.Source == ModSource.CurseForge;

    public bool IsModrinth => !IsCurseForge;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    /// <summary>True while this card's "Add" is being carried out; the button gives way to a progress line.</summary>
    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>A category without a translation is a slug: "armor-weapons-tools" reads better as words.</summary>
    private static string Humanize(string slug)
    {
        var words = slug.Replace('-', ' ').Replace('_', ' ').Trim();
        return words.Length == 0 ? slug : char.ToUpperInvariant(words[0]) + words[1..];
    }

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

    /// <summary>
    /// 60 000 000 → "60 M downloads", 340 000 → "340 K downloads", in the interface
    /// language; without the word where an icon beside the number says it.
    /// </summary>
    public static string CompactCount(long count, bool withWord = true)
    {
        if (count >= 1_000_000)
        {
            var millions = count / 1_000_000d;
            var text = millions >= 10 ? millions.ToString("F0", CultureInfo.CurrentCulture) : millions.ToString("F1", CultureInfo.CurrentCulture);

            return withWord
                ? MainWindowViewModel.Localize("Count_Millions", "{0} M downloads", text)
                : MainWindowViewModel.Localize("Count_MillionsShort", "{0}M", text);
        }

        if (count >= 1_000)
        {
            var text = (count / 1_000).ToString(CultureInfo.CurrentCulture);

            return withWord
                ? MainWindowViewModel.Localize("Count_Thousands", "{0} K downloads", text)
                : MainWindowViewModel.Localize("Count_ThousandsShort", "{0}K", text);
        }

        return withWord
            ? MainWindowViewModel.Localize("Count_Plain", "{0} downloads", count)
            : count.ToString(CultureInfo.CurrentCulture);
    }
}

/// <summary>One dependency of the opened mod, with whether the build already has it.</summary>
public sealed record ModDependencyItem(string Title, bool Installed, bool Required)
{
    /// <summary>Bytes of the file that would come along; zero when nothing would.</summary>
    public long Size { get; init; }

    public string SizeLabel { get; init; } = string.Empty;

    /// <summary>A CurseForge file its author keeps to the site: the launcher cannot bring it.</summary>
    public bool Blocked { get; init; }

    /// <summary>Where the player gets a blocked file by hand.</summary>
    public string? PageUrl { get; init; }

    /// <summary>The state under the name is a link: the mod is missing and only its page has it.</summary>
    public bool OffersPage => !Installed && Blocked && !string.IsNullOrEmpty(PageUrl);

    /// <summary>A required mod with no file for this game version and loader: the install cannot bring it.</summary>
    public bool Unavailable { get; init; }

    public string StateLabel => Installed
        ? MainWindowViewModel.Localize("Mods_DepInstalled", "in the build")
        : Unavailable
            ? MainWindowViewModel.Localize("Mods_DepUnavailable", "no version for this build")
        : Blocked
            ? MainWindowViewModel.Localize("Mods_DepBlocked", "only from its page")
            : Required
                ? MainWindowViewModel.Localize("Mods_DepWillInstall", "will be added too") +
                  (SizeLabel.Length > 0 ? " · " + SizeLabel : string.Empty)
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
        // The catalog is a detour among the tabs: its back button returns to the one it was opened from.
        if (value is not BuildTab.Catalog and not BuildTab.Settings)
        {
            _tabBeforeCatalog = value;
        }

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

        // A mirror that could not be reached at start gets one more question when the
        // catalog is opened; a mirror that answered is not asked again.
        if (value == BuildTab.Catalog && !IsCurseForgeAvailable)
        {
            _ = ProbeCurseForgeAsync();
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

    /// <summary>The last search came back with nothing: the page says so instead of standing empty.</summary>
    [ObservableProperty]
    private bool _isBrowserEmpty;

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
            var categories = await LoadSourceCategoriesAsync();

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
            AppendConsole($"[catalog] categories failed: {ex.Message}");
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
    /// Loads one page of the catalog. An empty query browses the whole category,
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
            IsBrowserEmpty = false;
            return;
        }

        var searching = EffectiveSource == BrowserSource.Modrinth
            ? Localize("Status_SearchingMods", "Searching Modrinth…")
            : Localize("Status_SearchingCatalog", "Searching the catalog…");

        try
        {
            IsBrowserBusy = true;
            IsBrowserEmpty = false;
            Status = searching;

            var category = string.IsNullOrWhiteSpace(SelectedCategory?.Name) ? null : SelectedCategory!.Name;
            var offset = Math.Max(0, page - 1) * BrowserPageSize;
            var merged = EffectiveSource == BrowserSource.All;

            var result = await SearchSourcesAsync(category, offset);

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
            IsBrowserEmpty = result.Items.Count == 0;
            OnPropertyChanged(nameof(CatalogScopeLabel));

            var from = result.Items.Count == 0 ? 0 : offset + 1;
            var to = offset + result.Items.Count;

            // Two sources fill one page with up to twice the cards and count their totals
            // apart, so "21-40 of 5000" would be a guess; the page says what it shows.
            BrowserSummary = merged
                ? Localize("Mods_ShownMerged", "Shown: {0}, from both sources", result.Items.Count)
                : Localize(
                    "Mods_ShownOfTotal",
                    "Shown {0}-{1} of {2}",
                    from,
                    to,
                    result.TotalHits);

            // The summary belongs to the catalog footer only; writing it to Status made
            // it show up in the always-visible status bar on every section. But the
            // "searching" line it replaced has to go too, or it stays up for good.
            if (Status == searching)
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
    private Core.Loaders.LoaderKind LoaderFor(string projectType) => LoaderFor(projectType, SelectedLoader);

    private static Core.Loaders.LoaderKind LoaderFor(string projectType, Core.Loaders.LoaderKind loader)
        => ProjectTypes.UsesLoader(projectType) ? loader : Core.Loaders.LoaderKind.Vanilla;

    /// <summary>
    /// The build an install was started for. The files take a while to come, and the
    /// player may open another build before the last of them does; every download,
    /// record, backup and list refresh of the install stays with this one.
    /// </summary>
    private sealed record InstallTarget(Instance? Instance, string Directory, string? GameVersion, Core.Loaders.LoaderKind Loader);

    private InstallTarget CurrentInstallTarget()
        => new(SelectedInstance, InstanceDirectory, SelectedVersion?.Id, SelectedLoader);

    /// <summary>True while the build an install was started for is still the one open.</summary>
    private bool IsSelectedBuild(InstallTarget target)
        => target.Instance is null ? SelectedInstance is null : IsSelectedBuild(target.Instance);

    /// <summary>Re-runs the browser after the build or the filters change, with a short delay.</summary>
    private void ScheduleBrowserReload()
    {
        var previousCts = _browserDebounce;
        var cts = new CancellationTokenSource();
        _browserDebounce = cts;
        previousCts?.Cancel();
        previousCts?.Dispose();

        // The token is taken now: a newer request disposes this source while the wait is
        // still running, and asking a disposed source for its token throws.
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token);

                // A newer request may have arrived while this one waited.
                if (token.IsCancellationRequested)
                {
                    return;
                }

                // LoadBrowserPageAsync bails out while another search is running. Dropping
                // the reload there left the grid showing results for the previous version,
                // so wait for the in-flight one to finish and then refresh.
                for (var waited = 0; waited < 2000 && IsBrowserBusy; waited += 100)
                {
                    await Task.Delay(100, token);
                }

                if (token.IsCancellationRequested)
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

        var review = false;
        var conflictOnly = false;

        try
        {
            IsBrowserBusy = true;
            item.IsInstalling = true;
            Status = Localize("Status_ResolvingMod", "Resolving {0}…", item.Result.Title);

            // The build the card was pressed for: the install goes there, whatever build
            // is open by the time the files have come.
            var target = CurrentInstallTarget();

            var versions = await SourceFor(item.Result.Source)
                .GetVersionsAsync(item.Result.ProjectId, target.GameVersion, LoaderFor(BrowserKind, target.Loader));
            var preferred = ModrinthClient.SelectPreferred(versions);

            if (preferred is null)
            {
                Status = Localize("Status_NoCompatibleFile", "No compatible file for this version and loader");
                return;
            }

            // One press adds the mod that was pressed. Anything more than that - other mods
            // it needs, or a file only its page gives out - is shown on the mod's panel
            // first, and the install waits for the button there. The plan drawn here is
            // the one carried out: nothing is resolved a second time on the way.
            var plan = await ResolveInstallPlanAsync(preferred, item.Result.Slug, item.Result.Title, item.Result.IconUrl, BrowserKind, target);

            if (plan.Root.State == ModPlanState.Unavailable)
            {
                Status = Localize("Status_NoCompatibleFile", "No compatible file for this version and loader");
                return;
            }

            review = !plan.IsComplete || plan.Root.State != ModPlanState.Install || plan.BringsOthers || plan.Conflicts.Count > 0;
            conflictOnly = plan.IsComplete && plan.Root.State == ModPlanState.Install && !plan.BringsOthers && plan.Conflicts.Count > 0;

            if (!review)
            {
                _installBatch.Clear();

                await InstallPlanAsync(plan, target);

                // The card and the list on screen belong to the open build: with another
                // one opened meanwhile, they have nothing to show for this install.
                if (IsSelectedBuild(target))
                {
                    item.Installed = true;
                    RefreshHiddenItems();

                    // The mod itself is the last file installed: its dependencies came first.
                    RevealFreshMods(_installBatch.ToList(), _installBatch.LastOrDefault());
                }
            }
        }
        catch (Exception ex)
        {
            Status = Localize("Error_InstallMod", "Mod install failed: {0}", DescribeFailure(ex));
            _ = ExplainDownloadFailureAsync(Localize("Net_WhatMod", "the mod"), ex);
            AppendConsole(ex.ToString());
        }
        finally
        {
            item.IsInstalling = false;
            IsBrowserBusy = false;
        }

        // Out here, after the busy flags are down: the panel has its own.
        if (review)
        {
            await OpenProjectAsync(item);

            Status = IsOpenedProjectBlocked
                ? Localize("Mods_ReviewBlocked", "{0} is only given out on its page: the button on the right opens it", item.Result.Title)
                : conflictOnly
                    ? Localize("Mods_ReviewConflict", "{0} may not work with a mod already in the build: the details are on the right", item.Result.Title)
                : Localize("Mods_ReviewFirst", "{0} needs other mods: the list is on the right, \"Add to build\" installs them all", item.Result.Title);
        }
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

    /// <summary>
    /// A new mod file takes over from older files of the same mod: they are switched off,
    /// never deleted, so the player can go back to the old version from the mod list. The
    /// log says which file stepped aside, because a folder with two versions of one mod
    /// is the most common reason a build refuses to start.
    /// </summary>
    private void ReplaceOtherVersions(Instance instance, string newFileName)
    {
        IReadOnlyList<string> disabled;

        try
        {
            disabled = _mods.DisableOtherVersions(_instances.GameDirectory(instance), newFileName);
        }
        catch (Exception ex)
        {
            AppendConsole($"[mods] could not check for older copies of {newFileName}: {ex.Message}");
            return;
        }

        foreach (var fileName in disabled)
        {
            AppendConsole($"[mods] switched off {fileName}: replaced by {newFileName}, kept for rolling back");
            MarkRecordDisabled(instance, fileName);
            _knownModUpdates.Remove(fileName);
        }

        if (disabled.Count > 0)
        {
            _instances.Save(instance);
        }
    }

    /// <summary>The record follows a file that was renamed to .disabled, as the player's own decision.</summary>
    private static void MarkRecordDisabled(Instance instance, string fileName)
    {
        var record = instance.InstalledMods.FirstOrDefault(m =>
            string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (record is null)
        {
            return;
        }

        record.FileName = fileName + ".disabled";
        record.DisabledByUser = true;
    }

    public bool IsProjectInstalled(string slug) => IsProjectInstalled(SelectedInstance, slug);

    /// <summary>The same, for the build an install was started for.</summary>
    private static bool IsProjectInstalled(Instance? instance, string slug)
        => instance?.InstalledMods.Any(m =>
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
        RaiseOpenedProjectActions();
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

        // Both sources describe mods in English; translating to English is a no-op.
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