using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Controls;
using STlauncher.Core.Instances;
using STlauncher.Core.Search;

namespace STlauncher.App.ViewModels;

/// <summary>The mono caption above a group of results.</summary>
public sealed record SearchCaption(string Text);

/// <summary>
/// One result of the search for everything: what it is called, the fact shown at its
/// right, and what Enter does. A build also has a second action, "Play".
/// </summary>
public sealed partial class SearchRow : ObservableObject
{
    public SearchRow(string title, Geometry? icon, Action run)
    {
        Title = title;
        Icon = icon;
        Run = run;
    }

    public string Title { get; }

    public Geometry? Icon { get; }

    /// <summary>A version or a loader, in mono.</summary>
    public string Meta { get; init; } = string.Empty;

    /// <summary>A word about the item: the section a setting is in, "selected", "switched off".</summary>
    public string Note { get; init; } = string.Empty;

    public bool HasMeta => Meta.Length > 0;

    public bool HasNote => Note.Length > 0;

    public string SecondaryLabel { get; init; } = string.Empty;

    public bool HasSecondary => RunSecondary is not null;

    internal Action Run { get; }

    internal Action? RunSecondary { get; init; }

    /// <summary>The row Enter will act on.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Tab moved from the row to its button: Enter presses the button.</summary>
    [ObservableProperty]
    private bool _isSecondaryFocused;
}

/// <summary>
/// One search for everything, opened from the top strip or with Ctrl+K: the sections and
/// frequent commands, the builds, what the selected build holds, and the settings.
/// Everything searched is already in memory; the list of what can be found is put
/// together when the search opens and dropped when it closes, so a closed search holds
/// nothing. The matching itself is <see cref="PaletteSearch"/>.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Captions and rows, in the order shown.</summary>
    public ObservableCollection<object> SearchRows { get; } = new();

    private readonly List<SearchRow> _searchShown = new();

    private List<SearchItem>? _searchItems;

    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    /// <summary>The query found nothing.</summary>
    [ObservableProperty]
    private bool _isSearchEmpty;

    /// <summary>"Showing results for …" when the query was read in the other keyboard layout.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchNote))]
    private string _searchNote = string.Empty;

    public bool HasSearchNote => SearchNote.Length > 0;

    public SearchRow? SelectedSearchRow => _searchShown.FirstOrDefault(r => r.IsSelected);

    /// <summary>
    /// The text in the settings page's own search box. The search for everything writes a
    /// setting's title here and opens the page, and the page shows that row.
    /// </summary>
    [ObservableProperty]
    private string _settingsSearchText = string.Empty;

    /// <summary>
    /// Raised when a result names one thing on a tab of the builds page (a mod, a pack, a
    /// shader, a world), after the tab has been opened: the page may scroll to it.
    /// </summary>
    public event Action<BuildTab, string>? SearchItemRequested;

    partial void OnSearchQueryChanged(string value)
    {
        if (IsSearchOpen)
        {
            RefreshSearchRows();
        }
    }

    [RelayCommand]
    private void OpenSearch()
    {
        // Under the first-run stage or a question that waits for an answer there is nothing to search yet.
        if (IsSearchOpen || !IsStartupReady || IsOnboardingOpen || IsHostCloseOpen || IsLightboxOpen)
        {
            return;
        }

        _searchItems = BuildSearchItems();
        SearchQuery = string.Empty;
        RefreshSearchRows();
        IsSearchOpen = true;
    }

    [RelayCommand]
    private void CloseSearch()
    {
        if (!IsSearchOpen)
        {
            return;
        }

        IsSearchOpen = false;
        SearchRows.Clear();
        _searchShown.Clear();
        _searchItems = null;
        SearchNote = string.Empty;
        IsSearchEmpty = false;
    }

    /// <summary>Ctrl+K again closes what Ctrl+K opened.</summary>
    [RelayCommand]
    private void ToggleSearch()
    {
        if (IsSearchOpen)
        {
            CloseSearch();
        }
        else
        {
            OpenSearch();
        }
    }

    /// <summary>Enter, or a click on a row. With no row given it is the selected one, and its button if Tab went there.</summary>
    [RelayCommand]
    private void RunSearchRow(SearchRow? row)
    {
        var secondary = row is null;
        row ??= SelectedSearchRow;

        if (row is null)
        {
            return;
        }

        var action = secondary && row.IsSecondaryFocused && row.RunSecondary is { } other ? other : row.Run;
        CloseSearch();
        action();
    }

    [RelayCommand]
    private void RunSearchRowSecondary(SearchRow? row)
    {
        if (row?.RunSecondary is not { } action)
        {
            return;
        }

        CloseSearch();
        action();
    }

    /// <summary>Up and Down. The ends do not wrap: holding a key stops at the last row.</summary>
    public void MoveSearchSelection(int delta)
    {
        if (_searchShown.Count == 0)
        {
            return;
        }

        var current = _searchShown.FindIndex(r => r.IsSelected);
        SelectSearchRow(_searchShown[Math.Clamp(current + delta, 0, _searchShown.Count - 1)]);
    }

    public void SelectSearchRow(SearchRow row)
    {
        foreach (var other in _searchShown)
        {
            var selected = ReferenceEquals(other, row);

            if (!selected)
            {
                other.IsSecondaryFocused = false;
            }

            other.IsSelected = selected;
        }

        OnPropertyChanged(nameof(SelectedSearchRow));
    }

    /// <summary>Tab: between the selected row and its button, when it has one.</summary>
    public void ToggleSearchSecondary()
    {
        if (SelectedSearchRow is { HasSecondary: true } row)
        {
            row.IsSecondaryFocused = !row.IsSecondaryFocused;
        }
    }

    private void RefreshSearchRows()
    {
        var empty = string.IsNullOrWhiteSpace(SearchQuery);

        // Before a word is typed the places are all listed: there are seven of them at most.
        var results = PaletteSearch.Find(_searchItems ?? new List<SearchItem>(), SearchQuery, empty ? 8 : PaletteSearch.DefaultCap);

        foreach (var row in _searchShown)
        {
            row.IsSelected = false;
            row.IsSecondaryFocused = false;
        }

        SearchRows.Clear();
        _searchShown.Clear();

        SearchGroup? group = null;

        foreach (var hit in results.Hits)
        {
            if (hit.Item.Tag is not SearchRow row)
            {
                continue;
            }

            if (group != hit.Item.Group)
            {
                group = hit.Item.Group;
                SearchRows.Add(new SearchCaption(SearchGroupCaption(hit.Item.Group, empty)));
            }

            SearchRows.Add(row);
            _searchShown.Add(row);
        }

        if (_searchShown.Count > 0)
        {
            _searchShown[0].IsSelected = true;
        }

        IsSearchEmpty = !empty && _searchShown.Count == 0;
        SearchNote = results.CorrectedQuery is { } corrected
            ? Localize("Search_LayoutNote", "Showing results for “{0}”: the other keyboard layout seems to have been on.", corrected)
            : string.Empty;
        OnPropertyChanged(nameof(SelectedSearchRow));
    }

    private string SearchGroupCaption(SearchGroup group, bool recent)
    {
        var build = SelectedInstance?.Name ?? string.Empty;

        var text = group switch
        {
            SearchGroup.Places => Localize("Search_GroupPlaces", "Places and actions"),
            SearchGroup.Builds => recent
                ? Localize("Search_GroupRecentBuilds", "Recent builds")
                : Localize("Search_GroupBuilds", "Builds"),
            SearchGroup.Mods => Localize("Search_GroupMods", "Mods of “{0}”", build),
            SearchGroup.ResourcePacks => Localize("Search_GroupPacks", "Resource packs of “{0}”", build),
            SearchGroup.Shaders => Localize("Search_GroupShaders", "Shaders of “{0}”", build),
            SearchGroup.Worlds => Localize("Search_GroupWorlds", "Worlds of “{0}”", build),
            _ => Localize("Search_GroupSettings", "Settings")
        };

        return text.ToUpper(System.Globalization.CultureInfo.CurrentUICulture);
    }

    // ===================== What can be found =====================

    private List<SearchItem> BuildSearchItems()
    {
        var items = new List<SearchItem>();

        AddSearchPlaces(items);
        AddSearchBuilds(items);
        AddSearchBuildContents(items);
        AddSearchSettings(items);

        return items;
    }

    private static Geometry? SearchIcon(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var value) ? value as Geometry : null;

    private void AddSearchPlaces(List<SearchItem> items)
    {
        var sectionNote = Localize("Search_Section", "section");

        void Place(ShellSection section, string title, string icon, string keywords)
            => items.Add(new SearchItem(
                SearchGroup.Places,
                title,
                keywords,
                new SearchRow(title, SearchIcon(icon), () => Section = section) { Note = sectionNote },
                showWhenEmpty: true));

        void Command(string title, string icon, string keywords, Action run)
            => items.Add(new SearchItem(SearchGroup.Places, title, keywords, new SearchRow(title, SearchIcon(icon), run)));

        Place(ShellSection.Game, Localize("Nav_Game", "Home"), "IconHome", "главная домой дом старт home main start");
        Place(ShellSection.Builds, Localize("Nav_Builds", "Builds"), "IconBuilds", "сборки моды миры builds instances mods worlds");
        Place(ShellSection.Skins, Localize("Nav_Skins", "Skins"), "IconSkins", "скины скин редактор skins skin editor");
        Place(ShellSection.Host, Localize("Nav_Host", "Friends"), "IconHost", "друзья сервер хост мой сервер friends server host multiplayer");
        Place(ShellSection.Server, ServerName, "IconStar", "сервер онлайн server online showtime");
        Place(ShellSection.Settings, Localize("Nav_Settings", "Settings"), "IconSettings", "настройки параметры опции settings options preferences");

        if (ShowDeveloperConsole)
        {
            Place(ShellSection.Console, Localize("Nav_Console", "Console"), "IconConsole", "консоль лог журнал console log");
        }

        if (SelectedInstance is { } selected)
        {
            Command(
                Localize("Search_ActPlay", "Play “{0}”", selected.Name),
                "IconPlay",
                "играть запустить запуск старт play launch start run",
                () =>
                {
                    Section = ShellSection.Game;

                    if (PlayCommand.CanExecute(null))
                    {
                        PlayCommand.Execute(null);
                    }
                });
        }

        Command(
            Localize("Home_NewBuild", "New build"),
            "IconPlus",
            "новая сборка создать сборку добавить new build create instance add",
            () => NewBuildFromHomeCommand.Execute(null));

        Command(
            Localize("Search_ActImport", "Bring builds over from another launcher"),
            "IconDownload",
            "импорт перенос перенести tlauncher тлаунчер prism multimc curseforge modrinth import migrate transfer other launcher",
            () =>
            {
                Section = ShellSection.Builds;
                OpenImportCommand.Execute(null);
            });

        Command(
            Localize("Host_Create", "Create a server"),
            "IconServer",
            "создать сервер хост для друзей create server host",
            () =>
            {
                Section = ShellSection.Host;
                FriendsTab = FriendsTab.MyServer;
                OpenHostCreateCommand.Execute(null);
            });

        Command(
            Localize("Search_ActJoin", "Join a friend by code"),
            "IconUsers",
            "друг код приглашение присоединиться зайти friend code invite join",
            () =>
            {
                Section = ShellSection.Host;
                FriendsTab = FriendsTab.Join;
            });

        if (SelectedInstance is { } build)
        {
            Command(
                Localize("Search_ActCatalog", "Find mods in the catalog"),
                "IconSearch",
                "каталог модов скачать мод добавить modrinth curseforge catalog download add mod",
                () =>
                {
                    Section = ShellSection.Builds;
                    IsBuildSettingsOpen = false;
                    OpenCatalogForModsCommand.Execute(null);
                });

            Command(
                Localize("Search_ActBuildSettings", "Settings of the build “{0}”", build.Name),
                "IconSettings",
                "настройки сборки версия загрузчик build settings version loader",
                () => OpenBuildSettingsCommand.Execute(null));

            Command(
                Localize("Search_ActBuildFolder", "Open the build's folder"),
                "IconFolder",
                "папка сборки игры minecraft открыть build game folder open directory",
                () => OpenGameFolderCommand.Execute(null));
        }

        Command(
            Localize("Search_ActDataFolder", "Open the launcher's data folder"),
            "IconFolder",
            "папка данных лаунчера открыть data folder open directory",
            () => OpenDataDirectoryCommand.Execute(null));

        Command(
            Localize("Search_ActReport", "Collect a report for support"),
            "IconShare",
            "отчет поддержка помощь логи админ ошибка report support help logs bug",
            () => CollectSupportReportCommand.Execute(null));

        Command(
            Localize("Settings_CheckUpdates", "Check for updates"),
            "IconUpdate",
            "обновление обновить версия лаунчера update upgrade version",
            () => CheckForUpdatesCommand.Execute(null));
    }

    private void AddSearchBuilds(List<SearchItem> items)
    {
        var play = Localize("Game_Play", "Play");
        var selectedNote = Localize("Search_Selected", "selected");
        var icon = SearchIcon("IconBuilds");
        var recent = 0;

        foreach (var instance in _allInstances
                     .OrderByDescending(i => i.LastPlayedAt ?? DateTimeOffset.MinValue)
                     .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var loader = LoaderName(instance.Loader);
            var meta = string.IsNullOrWhiteSpace(instance.VersionId) ? loader : loader + " " + instance.VersionId;

            var row = new SearchRow(instance.Name, icon, () => OpenBuildFromSearch(instance))
            {
                Meta = meta,
                Note = ReferenceEquals(instance, SelectedInstance) ? selectedNote : string.Empty,
                SecondaryLabel = play,
                RunSecondary = () => _ = PlayBuildFromSearchAsync(instance)
            };

            items.Add(new SearchItem(SearchGroup.Builds, instance.Name, meta, row, showWhenEmpty: recent++ < 5));
        }
    }

    /// <summary>Mods, packs, shaders and worlds of the selected build, as the builds page has them loaded.</summary>
    private void AddSearchBuildContents(List<SearchItem> items)
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var off = Localize("Search_ModOff", "switched off");
        var modIcon = SearchIcon("IconBuilds");

        foreach (var mod in InstalledMods.Where(m => m.IsMod))
        {
            var name = mod.DisplayName;

            items.Add(new SearchItem(
                SearchGroup.Mods,
                name,
                mod.FileName,
                new SearchRow(name, modIcon, () => OpenBuildItemFromSearch(BuildTab.Mods, name))
                {
                    Meta = mod.VersionLabel,
                    Note = mod.Enabled ? string.Empty : off
                }));
        }

        var packIcon = SearchIcon("IconPalette");

        foreach (var pack in EnabledResourcePacks.Concat(DisabledResourcePacks))
        {
            var name = pack.Name;

            items.Add(new SearchItem(
                SearchGroup.ResourcePacks,
                name,
                pack.FileName,
                new SearchRow(name, packIcon, () =>
                {
                    OpenBuildItemFromSearch(BuildTab.ResourcePacks, name);
                    SelectedResourcePack = pack;
                })));
        }

        var shaderIcon = SearchIcon("IconEye");

        foreach (var shader in ShaderPacks)
        {
            var name = shader.Name;

            items.Add(new SearchItem(
                SearchGroup.Shaders,
                name,
                shader.FileName,
                new SearchRow(name, shaderIcon, () => OpenBuildItemFromSearch(BuildTab.Shaders, name))));
        }

        var worldIcon = SearchIcon("IconGlobe");

        foreach (var world in Worlds)
        {
            var name = world.Name;

            items.Add(new SearchItem(
                SearchGroup.Worlds,
                name,
                world.FolderName,
                new SearchRow(name, worldIcon, () => OpenBuildItemFromSearch(BuildTab.Worlds, name)) { Meta = world.VersionLabel }));
        }
    }

    private void AddSearchSettings(List<SearchItem> items)
    {
        var icon = SearchIcon("IconSettings");

        foreach (var entry in SettingsSearchIndex.Entries)
        {
            // The developer section is not on the page until its switch is on.
            if (entry.Developer && !ShowDeveloperConsole)
            {
                continue;
            }

            var title = Localize(entry.TitleKey, entry.TitleKey);

            items.Add(new SearchItem(
                SearchGroup.Settings,
                title,
                entry.Keywords,
                new SearchRow(title, icon, () => OpenSettingFromSearch(title)) { Note = Localize(entry.SectionKey, string.Empty) }));
        }
    }

    // ===================== What a result does =====================

    /// <summary>
    /// Makes the build the selected one. Not while the game is starting or running: the
    /// same rule as the build strip of the home screen.
    /// </summary>
    private bool SelectBuildFromSearch(Instance instance)
    {
        if (ReferenceEquals(SelectedInstance, instance))
        {
            return true;
        }

        if (IsBusy || IsGameRunning)
        {
            Status = Localize("Search_BuildBusy", "The build cannot be changed now: the game is starting or already running.");
            return false;
        }

        // The list on the builds page may be narrowed by its own search to something else.
        if (!Instances.Contains(instance))
        {
            BuildFilter = string.Empty;
        }

        SelectedInstance = instance;
        return ReferenceEquals(SelectedInstance, instance);
    }

    private void OpenBuildFromSearch(Instance instance)
    {
        SelectBuildFromSearch(instance);
        Section = ShellSection.Builds;
    }

    private async Task PlayBuildFromSearchAsync(Instance instance)
    {
        if (!SelectBuildFromSearch(instance))
        {
            return;
        }

        Section = ShellSection.Game;

        // A build that has just been selected is still reading its loader's versions; a
        // start before they arrive would let the launch pick a loader version by itself.
        for (var i = 0; i < 200 && IsLoaderBusy; i++)
        {
            await Task.Delay(50);
        }

        if (ReferenceEquals(SelectedInstance, instance) && !IsLoaderBusy && PlayCommand.CanExecute(null))
        {
            PlayCommand.Execute(null);
        }
    }

    /// <summary>
    /// Opens the tab of the builds page the item is on. The mod list has a search of its
    /// own, so the mod is shown by typing its name there; the other tabs are short lists,
    /// and the page is told the name in case it wants to scroll.
    /// </summary>
    private void OpenBuildItemFromSearch(BuildTab tab, string name)
    {
        Section = ShellSection.Builds;
        IsBuildSettingsOpen = false;
        BuildTab = tab;

        if (tab == BuildTab.Mods)
        {
            ModsScope = ModsScope.All;
            ModsFilter = name;
        }

        SearchItemRequested?.Invoke(tab, name);
    }

    private void OpenSettingFromSearch(string title)
    {
        Section = ShellSection.Settings;

        // Cleared first, so that asking for the same setting twice is still a change.
        SettingsSearchText = string.Empty;
        SettingsSearchText = title;
    }
}
