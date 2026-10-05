using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Hosting;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>One mod in the server's mods folder, as a row of the server's mods tab.</summary>
public sealed partial class HostModItem : ObservableObject
{
    public HostModItem(ServerModEntry entry, IReadOnlyList<ServerConfigFile> configFiles, Bitmap? icon)
    {
        Entry = entry;
        ConfigFiles = configFiles;
        _icon = icon;
        Initial = InitialOf(entry.Title);

        var parts = new List<string>
        {
            entry.Origin switch
            {
                ServerModOrigin.Build => MainWindowViewModel.Localize("HostMods_FromBuild", "from the build"),
                ServerModOrigin.Modrinth => MainWindowViewModel.Localize("HostMods_FromModrinth", "from Modrinth"),
                _ => MainWindowViewModel.Localize("HostMods_FromHand", "added by hand")
            }
        };

        if (!string.IsNullOrWhiteSpace(entry.Version))
        {
            parts.Add(entry.Version!);
        }

        if (HasSettings)
        {
            parts.Add(MainWindowViewModel.Localize("HostMods_HasSettings", "has settings"));
        }

        if (!entry.Enabled)
        {
            parts.Add(MainWindowViewModel.Localize("HostMods_Off", "switched off"));
        }

        Detail = string.Join(" · ", parts);
        RemoveQuestion = MainWindowViewModel.Localize(
            "HostMods_RemoveQuestion",
            "Take “{0}” off the server? The file is moved into the .removed folder inside the server's folder and can be put back from there.",
            entry.Title);
    }

    public ServerModEntry Entry { get; }

    public string Title => Entry.Title;

    public string? Description => Entry.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Entry.Description);

    public bool Enabled => Entry.Enabled;

    /// <summary>"from the build · 0.14.3 · has settings" under the title.</summary>
    public string Detail { get; }

    /// <summary>The colour behind the letter: the same mod keeps the same colour.</summary>
    public string TintKey => Entry.ModIds.FirstOrDefault() ?? Entry.Title;

    public string Initial { get; }

    public IReadOnlyList<ServerConfigFile> ConfigFiles { get; }

    public bool HasSettings => ConfigFiles.Count > 0;

    public string RemoveQuestion { get; }

    /// <summary>
    /// The remove command, carried by the row: the question is asked in a flyout, and a
    /// flyout's content sits in a popup where the page's view model is not an ancestor.
    /// </summary>
    public System.Windows.Input.ICommand? RemoveCommand { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    internal static string InitialOf(string title)
        => title.FirstOrDefault(char.IsLetterOrDigit) is var letter && letter != default
            ? char.ToUpperInvariant(letter).ToString()
            : "?";
}

/// <summary>A Modrinth search hit on the server's mods tab.</summary>
public sealed partial class HostModResultItem : ObservableObject
{
    public HostModResultItem(ModSearchResult result, bool installed)
    {
        Result = result;
        _installed = installed;
        Initial = HostModItem.InitialOf(result.Title);
        Byline = "Modrinth · " + ModBrowserItem.CompactCount(result.Downloads);
    }

    public ModSearchResult Result { get; }

    public string Title => Result.Title;

    public string Description => Result.Description;

    public string Byline { get; }

    public string Initial { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotInstalled))]
    private bool _installed;

    public bool NotInstalled => !Installed;

    /// <summary>The list of what it would bring is being worked out.</summary>
    [ObservableProperty]
    private bool _isPlanning;
}

/// <summary>One line of an install's list: a file to fetch, a mod the server already has, or why it cannot go ahead.</summary>
public sealed record HostModPlanLine(string Title, string Detail, HostModPlanLineKind Kind)
{
    public bool IsDownload => Kind == HostModPlanLineKind.Download;

    public bool IsPresent => Kind == HostModPlanLineKind.Present;

    public bool IsProblem => Kind == HostModPlanLineKind.Problem;
}

public enum HostModPlanLineKind
{
    Download,
    Present,
    Problem
}

/// <summary>A settings file in the list on the left of the editor.</summary>
public sealed partial class HostConfigFileItem : ObservableObject
{
    public HostConfigFileItem(ServerConfigFile file, string detail)
    {
        File = file;
        Detail = detail;
    }

    public ServerConfigFile File { get; }

    /// <summary>The path under the config folder: "voicechat/voicechat-server.properties".</summary>
    public string Label => File.RelativePath.StartsWith(ServerConfigFiles.ConfigFolderName + "/", StringComparison.OrdinalIgnoreCase)
        ? File.RelativePath[(ServerConfigFiles.ConfigFolderName.Length + 1)..]
        : File.RelativePath;

    public string Detail { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>One setting of an opened file, as a labelled field.</summary>
public sealed partial class HostConfigFieldItem : ObservableObject
{
    private readonly Func<ConfigField, string, bool> _accepts;
    private readonly Action _changed;

    public HostConfigFieldItem(ConfigField field, string sectionHeader, Func<ConfigField, string, bool> accepts, Action changed)
    {
        Field = field;
        SectionHeader = sectionHeader;
        _accepts = accepts;
        _changed = changed;
        _value = field.Value;
    }

    public ConfigField Field { get; }

    public string Key => Field.Key;

    public string Comment => Field.Comment;

    public bool HasComment => Field.Comment.Length > 0;

    /// <summary>The table or object this field opens, shown above it; empty when it continues the one before.</summary>
    public string SectionHeader { get; }

    public bool HasSectionHeader => SectionHeader.Length > 0;

    public bool IsBool => Field.Kind == ConfigValueKind.Bool;

    public bool IsTextual => !IsBool;

    /// <summary>Lists, tables and quoted strings with escapes are edited as the file writes them.</summary>
    public bool IsRaw => Field.Kind == ConfigValueKind.Raw;

    public bool IsChanged => !string.Equals(Value, Field.Value, StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    [NotifyPropertyChangedFor(nameof(BoolValue))]
    private string _value;

    [ObservableProperty]
    private bool _isInvalid;

    public bool BoolValue
    {
        get => Value.Trim() == "true";
        set => Value = value ? "true" : "false";
    }

    partial void OnValueChanged(string value)
    {
        IsInvalid = IsChanged && !_accepts(Field, value);
        _changed();
    }
}

/// <summary>
/// The server's mods tab: what is in the server's mods folder, mods from Modrinth that run
/// on a server, and the settings files of those mods. Nothing is fetched until the player
/// has seen the list of files and agreed; nothing is deleted - a switched-off mod is
/// renamed, a removed one moved into the server's .removed folder, and a saved settings
/// file keeps its previous content beside it. Changes reach the server at its next start;
/// a running server is never restarted from here.
/// </summary>
public partial class MainWindowViewModel
{
    private const int HostModPageSize = 20;

    private bool _watchingHostMods;
    private bool _resettingHostModQuery;
    private int _hostModsLoad;
    private int _hostModSearchRun;
    private CancellationTokenSource? _hostModSearchCts;
    private ServerModCatalog? _hostModCatalog;

    /// <summary>What the server has, for marking hits and planning; worked out once per listing.</summary>
    private Task<ServerModPresence>? _hostModPresence;

    private string? _hostModsServerId;
    private ServerModPlan? _hostModPlan;
    private HostModResultItem? _hostModPlanItem;
    private IReadOnlyList<ServerConfigFile> _hostOtherConfigFiles = Array.Empty<ServerConfigFile>();

    private ServerModCatalog HostModCatalog => _hostModCatalog ??= new ServerModCatalog(_modrinth, _mods);

    /// <summary>
    /// Called by the mods tab once it has a view model: from then on the tab follows the
    /// selected server, its state and the language. Nothing is read before the tab exists.
    /// </summary>
    public void WatchHostMods()
    {
        if (_watchingHostMods)
        {
            return;
        }

        _watchingHostMods = true;
        PropertyChanged += OnHostModsSourceChanged;
        _ = RefreshHostModsAsync(searchToo: true);
    }

    private void OnHostModsSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SelectedHostServer):
                CloseHostModPlan();

                // An editor with unsaved changes stays open on the server it belongs to; it
                // asks before closing like any other time. A clean one has nothing to lose.
                if (IsHostConfigOpen && !HostConfigDirty)
                {
                    CloseHostConfigNow();
                }

                HostModsNotice = string.Empty;
                _ = RefreshHostModsAsync(searchToo: true);
                break;

            case nameof(HostState):
                OnPropertyChanged(nameof(HostModsRestartNote));
                break;

            // Back on the page: files may have been changed by hand meanwhile.
            case nameof(Section) when Section == ShellSection.Host:
                _ = RefreshHostModsAsync(searchToo: false);
                break;

            case nameof(Language):
                OnPropertyChanged(nameof(HostModsRestartNote));
                OnPropertyChanged(nameof(HostModsSearchNote));
                _ = RefreshHostModsAsync(searchToo: false);
                break;
        }
    }

    private string? HostModsServerDirectory
        => SelectedHostServer is { } server ? _hosting.Store.ServerDirectory(server) : null;

    // ===================== What the server has =====================

    public ObservableCollection<HostModItem> HostMods { get; } = new();

    [ObservableProperty]
    private bool _isHostModsLoading;

    /// <summary>A server without a mod loader loads no mods; the tab says so instead of offering any.</summary>
    public bool HostModsSupported => SelectedHostServer is { } server && ServerModCatalog.SupportsMods(server.Loader);

    public bool HostModsUnsupported => SelectedHostServer is not null && !HostModsSupported;

    public string HostModsHeader => Localize("HostMods_Installed", "On the server · {0}", HostMods.Count);

    public bool HasHostMods => HostMods.Count > 0;

    public bool ShowHostModsEmpty => HostMods.Count == 0 && !IsHostModsLoading;

    public string HostModsRestartNote => IsHostOnline || IsHostWorking
        ? Localize("HostMods_RestartRunning", "The server is running: changes apply after it is restarted. The launcher does not restart it by itself.")
        : Localize("HostMods_RestartStopped", "Changes apply the next time the server starts.");

    /// <summary>The settings files no mod could be named for.</summary>
    public bool HasHostOtherConfigs => _hostOtherConfigFiles.Count > 0;

    public string HostOtherConfigsLabel => Localize("HostMods_OtherFiles", "Other settings files · {0}", _hostOtherConfigFiles.Count);

    /// <summary>What just happened, or why it could not: shown at the top of the tab until the next action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostModsNotice))]
    private string _hostModsNotice = string.Empty;

    public bool HasHostModsNotice => HostModsNotice.Length > 0;

    [RelayCommand]
    private void DismissHostModsNotice() => HostModsNotice = string.Empty;

    private void RaiseHostModsList()
    {
        OnPropertyChanged(nameof(HostModsHeader));
        OnPropertyChanged(nameof(HasHostMods));
        OnPropertyChanged(nameof(ShowHostModsEmpty));
        OnPropertyChanged(nameof(HostModsSupported));
        OnPropertyChanged(nameof(HostModsUnsupported));
        OnPropertyChanged(nameof(HasHostOtherConfigs));
        OnPropertyChanged(nameof(HostOtherConfigsLabel));
        OnPropertyChanged(nameof(HostModsSearchNote));
        OnPropertyChanged(nameof(HostModsRestartNote));
    }

    /// <summary>
    /// Reads the server's mods folder and its settings files again. The jars are opened off
    /// the thread that draws; a newer call wins over an older one still reading.
    /// </summary>
    private async Task RefreshHostModsAsync(bool searchToo)
    {
        var load = ++_hostModsLoad;
        var server = SelectedHostServer;
        var directory = HostModsServerDirectory;
        var buildDirectory = HostSourceInstance is { } instance ? SafeGameDirectory(instance) : null;
        var serverChanged = !string.Equals(_hostModsServerId, server?.Id, StringComparison.OrdinalIgnoreCase);

        _hostModsServerId = server?.Id;
        _hostModPresence = null;

        if (serverChanged)
        {
            HostMods.Clear();
            HostModResults.Clear();
            _hostOtherConfigFiles = Array.Empty<ServerConfigFile>();
            _resettingHostModQuery = true;
            HostModQueryText = string.Empty;
            _resettingHostModQuery = false;
            _hostModsTotalHits = 0;
            OnPropertyChanged(nameof(HostModsHasMore));
        }

        RaiseHostModsList();

        if (directory is null || server is null)
        {
            return;
        }

        IsHostModsLoading = true;

        try
        {
            var (rows, other) = await Task.Run(() =>
            {
                var mods = ServerMods.List(directory, buildDirectory);
                var index = ServerConfigFiles.Match(ServerConfigFiles.Discover(directory), mods);

                var items = mods.Select(m => (
                        Mod: m,
                        Files: index.ByMod.TryGetValue(m.BaseName, out var files) ? files : Array.Empty<ServerConfigFile>(),
                        Icon: DecodeIcon(m.Icon)))
                    .ToList();

                return (items, index.Other);
            });

            if (load != _hostModsLoad)
            {
                return;
            }

            HostMods.Clear();

            foreach (var (mod, files, icon) in rows)
            {
                HostMods.Add(new HostModItem(mod, files, icon) { RemoveCommand = RemoveHostModCommand });
            }

            _hostOtherConfigFiles = other;
        }
        catch (Exception ex)
        {
            if (load == _hostModsLoad)
            {
                HostModsNotice = Localize("HostMods_ListFailed", "Could not read the server's mods: {0}", ex.Message);
                AppendConsole($"[host-mods] {ex}");
            }
        }
        finally
        {
            if (load == _hostModsLoad)
            {
                IsHostModsLoading = false;
                RaiseHostModsList();
            }
        }

        if (load != _hostModsLoad || !HostModsSupported)
        {
            return;
        }

        if (searchToo && HostModResults.Count == 0)
        {
            await SearchHostModsAsync(append: false);
        }
        else
        {
            await MarkInstalledResultsAsync();
        }
    }

    private string? SafeGameDirectory(STlauncher.Core.Instances.Instance instance)
    {
        try
        {
            return _instances.GameDirectory(instance);
        }
        catch (Exception)
        {
            // Without the build's folder a mod off the record is simply of unknown origin.
            return null;
        }
    }

    /// <summary>The server's presence, worked out once per listing and shared by the search and the plan.</summary>
    private Task<ServerModPresence> HostModPresenceAsync(string directory)
        => _hostModPresence ??= HostModCatalog.PresenceAsync(directory, HostMods.Select(m => m.Entry).ToList());

    [RelayCommand]
    private async Task ToggleHostMod(HostModItem? item)
    {
        if (item is null || HostModsServerDirectory is not { } directory)
        {
            return;
        }

        var turnOn = !item.Enabled;

        try
        {
            var result = await Task.Run(() => ServerMods.SetEnabled(directory, item.Entry.FileName, turnOn));

            HostModsNotice = result is null
                ? Localize("HostMods_ToggleBlocked", "“{0}” was left as it is: the mods folder already has a file with the name it would get.", item.Title)
                : string.Empty;
        }
        catch (Exception ex)
        {
            HostModsNotice = Localize("HostMods_ToggleFailed", "Could not switch “{0}”: {1}", item.Title, ex.Message);
        }

        await RefreshHostModsAsync(searchToo: false);
    }

    /// <summary>Called from the question under the remove button, so the player has already said yes.</summary>
    [RelayCommand]
    private async Task RemoveHostMod(HostModItem? item)
    {
        if (item is null || HostModsServerDirectory is not { } directory)
        {
            return;
        }

        try
        {
            var moved = await Task.Run(() => ServerMods.Remove(directory, item.Entry.FileName));

            HostModsNotice = moved is null
                ? Localize("HostMods_RemoveMissing", "“{0}” is no longer in the mods folder.", item.Title)
                : Localize("HostMods_Removed", "“{0}” is off the server. The file is in the .removed folder inside the server's folder.", item.Title);
        }
        catch (Exception ex)
        {
            HostModsNotice = Localize("HostMods_RemoveFailed", "Could not take “{0}” off: {1}", item.Title, ex.Message);
        }

        await RefreshHostModsAsync(searchToo: false);
    }

    // ===================== Search on Modrinth =====================

    public ObservableCollection<HostModResultItem> HostModResults { get; } = new();

    [ObservableProperty]
    private string _hostModQueryText = string.Empty;

    [ObservableProperty]
    private bool _isHostModSearching;

    /// <summary>Why the list is empty or stopped: offline, or nothing found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostModSearchMessage))]
    private string _hostModSearchMessage = string.Empty;

    public bool HasHostModSearchMessage => HostModSearchMessage.Length > 0;

    private int _hostModsTotalHits;

    public bool HostModsHasMore => HostModResults.Count > 0 && HostModResults.Count < _hostModsTotalHits && !IsHostModSearching;

    public string HostModsSearchNote => SelectedHostServer is { } server
        ? Localize("HostMods_SearchNote", "Only mods that run on a server with {0} {1} are shown.", LoaderName(server.Loader), server.GameVersion)
        : string.Empty;

    private static string LoaderName(STlauncher.Core.Loaders.LoaderKind loader) => loader switch
    {
        STlauncher.Core.Loaders.LoaderKind.NeoForge => "NeoForge",
        STlauncher.Core.Loaders.LoaderKind.Forge => "Forge",
        STlauncher.Core.Loaders.LoaderKind.Fabric => "Fabric",
        _ => loader.ToString()
    };

    /// <summary>Typing searches after a short pause, so a word is one request and not one per letter.</summary>
    partial void OnHostModQueryTextChanged(string value)
    {
        if (!_watchingHostMods || _resettingHostModQuery || !HostModsSupported)
        {
            return;
        }

        var run = ++_hostModSearchRun;

        _ = Task.Delay(450).ContinueWith(
            _ =>
            {
                if (run == _hostModSearchRun)
                {
                    _ = SearchHostModsAsync(append: false);
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    [RelayCommand]
    private Task SearchHostMods()
    {
        ++_hostModSearchRun;
        return SearchHostModsAsync(append: false);
    }

    [RelayCommand]
    private Task MoreHostMods() => SearchHostModsAsync(append: true);

    private async Task SearchHostModsAsync(bool append)
    {
        if (SelectedHostServer is not { } server || HostModsServerDirectory is not { } directory || !HostModsSupported)
        {
            return;
        }

        _hostModSearchCts?.Cancel();
        var cts = _hostModSearchCts = new CancellationTokenSource();
        var token = cts.Token;

        IsHostModSearching = true;
        HostModSearchMessage = string.Empty;
        OnPropertyChanged(nameof(HostModsHasMore));

        try
        {
            var page = await HostModCatalog.SearchAsync(
                HostModQueryText,
                server.GameVersion,
                server.Loader,
                HostModPageSize,
                append ? HostModResults.Count : 0,
                token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!append)
            {
                HostModResults.Clear();
            }

            var added = new List<HostModResultItem>();

            foreach (var hit in page.Items)
            {
                if (HostModResults.Any(r => r.Result.ProjectId == hit.ProjectId))
                {
                    continue;
                }

                var item = new HostModResultItem(hit, installed: false);
                HostModResults.Add(item);
                added.Add(item);
            }

            _hostModsTotalHits = page.TotalHits;

            if (HostModResults.Count == 0)
            {
                HostModSearchMessage = Localize("HostMods_NothingFound", "Nothing found for this server. Try other words.");
            }

            _ = LoadHostModIconsAsync(added);
            await MarkInstalledResultsAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer search took over.
        }
        catch (Exception ex)
        {
            HostModSearchMessage = Localize("HostMods_SearchFailed", "Modrinth did not answer: {0}", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_hostModSearchCts, cts))
            {
                IsHostModSearching = false;
                OnPropertyChanged(nameof(HostModsHasMore));
            }
        }
    }

    private async Task LoadHostModIconsAsync(IReadOnlyList<HostModResultItem> items)
    {
        foreach (var item in items)
        {
            try
            {
                var icon = await _images.GetAsync(item.Result.IconUrl);

                if (icon is not null)
                {
                    item.Icon = icon;
                }
            }
            catch (Exception)
            {
                // The letter stays in its place.
            }
        }
    }

    /// <summary>Marks the hits the server already has. The presence may ask Modrinth about unknown jars; failing that, ids and titles answer.</summary>
    private async Task MarkInstalledResultsAsync()
    {
        if (HostModsServerDirectory is not { } directory || HostModResults.Count == 0)
        {
            return;
        }

        var load = _hostModsLoad;

        try
        {
            var presence = await HostModPresenceAsync(directory);

            if (load != _hostModsLoad)
            {
                return;
            }

            foreach (var item in HostModResults)
            {
                item.Installed = presence.Has(item.Result.ProjectId, item.Result.Slug, item.Result.Title);
            }
        }
        catch (Exception ex)
        {
            // Without it every hit offers "Install"; the plan still checks again.
            _hostModPresence = null;
            AppendConsole($"[host-mods] presence: {ex.Message}");
        }
    }

    // ===================== Install: the list first, then the player's yes =====================

    public ObservableCollection<HostModPlanLine> HostModPlanLines { get; } = new();

    [ObservableProperty]
    private bool _isHostModPlanOpen;

    [ObservableProperty]
    private string _hostModPlanTitle = string.Empty;

    [ObservableProperty]
    private string _hostModPlanTotal = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmHostModPlan))]
    private bool _hostModPlanCanInstall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmHostModPlan))]
    [NotifyPropertyChangedFor(nameof(CanCancelHostModPlan))]
    private bool _isHostModInstalling;

    [ObservableProperty]
    private string _hostModInstallText = string.Empty;

    public bool CanConfirmHostModPlan => HostModPlanCanInstall && !IsHostModInstalling;

    public bool CanCancelHostModPlan => !IsHostModInstalling;

    /// <summary>
    /// Works out what adding the mod would fetch - the mod and what it needs, with sizes and
    /// the host each file comes from - and shows that list. Nothing is downloaded here.
    /// </summary>
    [RelayCommand]
    private async Task PlanHostMod(HostModResultItem? item)
    {
        if (item is null || SelectedHostServer is not { } server || HostModsServerDirectory is not { } directory ||
            IsHostModInstalling || HostModResults.Any(r => r.IsPlanning))
        {
            return;
        }

        CloseHostModPlan();
        item.IsPlanning = true;
        HostModsNotice = string.Empty;

        try
        {
            var presence = await HostModPresenceAsync(directory);
            var plan = await HostModCatalog.PlanAsync(item.Result.ProjectId, item.Result.Title, server.GameVersion, server.Loader, presence);

            if (!ReferenceEquals(SelectedHostServer, server))
            {
                return;
            }

            ShowHostModPlan(item, plan);
        }
        catch (Exception ex)
        {
            _hostModPresence = null;
            HostModsNotice = Localize("HostMods_PlanFailed", "Could not find out what “{0}” needs: {1}", item.Title, ex.Message);
        }
        finally
        {
            item.IsPlanning = false;
        }
    }

    private void ShowHostModPlan(HostModResultItem item, ServerModPlan plan)
    {
        _hostModPlan = plan;
        _hostModPlanItem = item;
        HostModPlanLines.Clear();

        foreach (var download in plan.Downloads)
        {
            var detail = Localize(
                "HostMods_PlanFile",
                "{0} · {1} · from {2}",
                download.VersionNumber,
                Converters.FileSizeConverter.Instance.Convert(download.Size, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture),
                download.Host);

            var title = download.IsDependency
                ? Localize("HostMods_PlanNeeded", "{0} — needed by it", download.Title)
                : download.Title;

            HostModPlanLines.Add(new HostModPlanLine(title, detail, HostModPlanLineKind.Download));
        }

        foreach (var present in plan.AlreadyThere.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            HostModPlanLines.Add(new HostModPlanLine(present, Localize("HostMods_PlanPresent", "already on the server"), HostModPlanLineKind.Present));
        }

        foreach (var problem in plan.Problems)
        {
            var detail = problem.Kind == ServerModProblemKind.NoVersion
                ? Localize("HostMods_PlanNoVersion", "has no version for {0} {1}", LoaderName(SelectedHostServer?.Loader ?? default), SelectedHostServer?.GameVersion ?? string.Empty)
                : Localize("HostMods_PlanNoFile", "has no file the launcher can check after downloading");

            HostModPlanLines.Add(new HostModPlanLine(problem.Title, detail, HostModPlanLineKind.Problem));
        }

        HostModPlanTitle = Localize("HostMods_PlanTitle", "Install “{0}” on the server", plan.Title);
        HostModPlanCanInstall = plan.CanInstall;
        HostModPlanTotal = plan.CanInstall
            ? Localize(
                "HostMods_PlanTotal",
                "Files: {0}, {1} in all. Nothing has been downloaded yet.",
                plan.Downloads.Count,
                Converters.FileSizeConverter.Instance.Convert(plan.TotalBytes, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture))
            : Localize("HostMods_PlanBlocked", "It cannot be installed on this server as it is. Nothing has been downloaded.");
        HostModInstallText = string.Empty;
        IsHostModPlanOpen = true;
    }

    [RelayCommand]
    private void CancelHostModPlan()
    {
        if (!IsHostModInstalling)
        {
            CloseHostModPlan();
        }
    }

    private void CloseHostModPlan()
    {
        if (IsHostModInstalling)
        {
            return;
        }

        _hostModPlan = null;
        _hostModPlanItem = null;
        HostModPlanLines.Clear();
        IsHostModPlanOpen = false;
        HostModPlanCanInstall = false;
    }

    /// <summary>The player has seen the list and pressed the button: fetch, check each file against Modrinth's hash, record.</summary>
    [RelayCommand]
    private async Task ConfirmHostModPlan()
    {
        if (_hostModPlan is not { CanInstall: true } plan || HostModsServerDirectory is not { } directory || IsHostModInstalling)
        {
            return;
        }

        var item = _hostModPlanItem;
        IsHostModInstalling = true;

        var done = 0;
        var progress = new Progress<ServerModDownload>(download =>
        {
            done++;
            HostModInstallText = Localize("HostMods_Downloading", "Downloading “{0}” ({1} of {2})…", download.Title, done, plan.Downloads.Count);
        });

        try
        {
            var result = await HostModCatalog.InstallAsync(plan, directory, progress);

            HostModsNotice = result.SwitchedOff.Count > 0
                ? Localize("HostMods_InstalledReplaced", "“{0}” is on the server. Its older file was switched off: {1}", plan.Title, string.Join(", ", result.SwitchedOff))
                : Localize("HostMods_InstalledDone", "“{0}” is on the server.", plan.Title);

            if (item is not null)
            {
                item.Installed = true;
            }
        }
        catch (Exception ex)
        {
            HostModsNotice = Localize("HostMods_InstallFailed", "The install stopped: {0}. A file that did not match its hash was not kept.", ex.Message);
            AppendConsole($"[host-mods] install: {ex}");
        }
        finally
        {
            IsHostModInstalling = false;
            CloseHostModPlan();
        }

        await RefreshHostModsAsync(searchToo: false);
    }

    // ===================== Settings files =====================

    public ObservableCollection<HostConfigFileItem> HostConfigFiles { get; } = new();

    public ObservableCollection<HostConfigFieldItem> HostConfigFields { get; } = new();

    [ObservableProperty]
    private bool _isHostConfigOpen;

    [ObservableProperty]
    private string _hostConfigTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostConfigFile))]
    private HostConfigFileItem? _hostConfigFile;

    public bool HasHostConfigFile => HostConfigFile is not null;

    [ObservableProperty]
    private bool _isHostConfigFields;

    [ObservableProperty]
    private bool _isHostConfigText;

    /// <summary>The text of a file opened as text; the editor binds to it.</summary>
    [ObservableProperty]
    private string _hostConfigText = string.Empty;

    /// <summary>Why the file is not open: too large, not text, gone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostConfigMessage))]
    private string _hostConfigMessage = string.Empty;

    public bool HasHostConfigMessage => HostConfigMessage.Length > 0;

    /// <summary>What a save did: where the previous content was kept.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostConfigNotice))]
    private string _hostConfigNotice = string.Empty;

    public bool HasHostConfigNotice => HostConfigNotice.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveHostConfig))]
    private bool _hostConfigDirty;

    public bool CanSaveHostConfig => HostConfigDirty && !HostConfigHasInvalid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveHostConfig))]
    private bool _hostConfigHasInvalid;

    /// <summary>The question about unsaved changes, with what to do once it is answered.</summary>
    [ObservableProperty]
    private bool _isHostConfigAsking;

    private Action? _hostConfigAfterAnswer;
    private string? _hostConfigDirectory;
    private string _hostConfigLoadedText = string.Empty;
    private ConfigEncoding _hostConfigEncoding;
    private ConfigFieldDocument? _hostConfigDocument;

    public bool HostConfigCanShowText => IsHostConfigFields && !HostConfigDirty;

    partial void OnHostConfigTextChanged(string value)
    {
        if (IsHostConfigText)
        {
            HostConfigDirty = !string.Equals(value, _hostConfigLoadedText, StringComparison.Ordinal);
        }
    }

    partial void OnHostConfigDirtyChanged(bool value) => OnPropertyChanged(nameof(HostConfigCanShowText));

    partial void OnIsHostConfigFieldsChanged(bool value) => OnPropertyChanged(nameof(HostConfigCanShowText));

    [RelayCommand]
    private void OpenHostModSettings(HostModItem? item)
    {
        if (item is null || !item.HasSettings)
        {
            return;
        }

        OpenHostConfig(Localize("HostMods_SettingsOf", "Settings · {0}", item.Title), item.ConfigFiles);
    }

    [RelayCommand]
    private void OpenHostOtherSettings()
    {
        if (_hostOtherConfigFiles.Count > 0)
        {
            OpenHostConfig(Localize("HostMods_OtherTitle", "Other settings files"), _hostOtherConfigFiles);
        }
    }

    private void OpenHostConfig(string title, IReadOnlyList<ServerConfigFile> files)
    {
        if (HostModsServerDirectory is not { } directory)
        {
            return;
        }

        CloseHostConfigNow();

        _hostConfigDirectory = directory;
        HostConfigTitle = title;

        // A mod's server settings are what a server owner came for; its client or
        // translation files, also in config, come after them.
        var ordered = files
            .OrderByDescending(f => f.Name.Contains("server", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var file in ordered)
        {
            var size = (string)Converters.FileSizeConverter.Instance.Convert(file.Size, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture);
            HostConfigFiles.Add(new HostConfigFileItem(
                file,
                file.TooLarge ? Localize("HostMods_FileTooLargeShort", "{0} · too large to open here", size) : size));
        }

        IsHostConfigOpen = true;

        if (HostConfigFiles.FirstOrDefault(f => !f.File.TooLarge) is { } first)
        {
            LoadHostConfigFile(first);
        }
    }

    /// <summary>Another file of the list; with unsaved changes the player is asked first.</summary>
    [RelayCommand]
    private void OpenHostConfigFile(HostConfigFileItem? item)
    {
        if (item is null || ReferenceEquals(item, HostConfigFile))
        {
            return;
        }

        AskBeforeLeavingHostConfig(() => LoadHostConfigFile(item));
    }

    private void LoadHostConfigFile(HostConfigFileItem item)
    {
        foreach (var file in HostConfigFiles)
        {
            file.IsSelected = ReferenceEquals(file, item);
        }

        HostConfigFile = item;
        HostConfigFields.Clear();
        HostConfigMessage = string.Empty;
        HostConfigNotice = string.Empty;
        IsHostConfigFields = false;
        IsHostConfigText = false;
        _hostConfigDocument = null;
        _hostConfigLoadedText = string.Empty;
        HostConfigHasInvalid = false;

        ConfigReadResult read;

        try
        {
            read = ServerConfigFiles.Read(_hostConfigDirectory!, item.File.RelativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HostConfigMessage = Localize("HostMods_FileReadFailed", "The file could not be read: {0}", ex.Message);
            HostConfigDirty = false;
            return;
        }

        if (!read.IsOk)
        {
            HostConfigMessage = read.Status switch
            {
                ConfigFileStatus.TooLarge => Localize("HostMods_FileTooLarge", "The file is larger than the launcher opens. Open it in a text editor from the server's folder."),
                ConfigFileStatus.NotText => Localize("HostMods_FileNotText", "This is not a text file; the launcher does not edit it."),
                ConfigFileStatus.Missing => Localize("HostMods_FileMissing", "The file is no longer there."),
                _ => Localize("HostMods_FileNotAllowed", "The launcher does not open this file.")
            };
            HostConfigDirty = false;
            return;
        }

        _hostConfigLoadedText = read.Text;
        _hostConfigEncoding = read.Encoding;
        _hostConfigDocument = ConfigFieldDocument.TryParse(item.File.Format, read.Text);

        if (_hostConfigDocument is { } document)
        {
            var section = string.Empty;

            foreach (var field in document.Fields)
            {
                var header = field.Section != section ? field.Section : string.Empty;
                section = field.Section;
                HostConfigFields.Add(new HostConfigFieldItem(field, header, document.Accepts, UpdateHostConfigDirty));
            }

            IsHostConfigFields = true;
        }
        else
        {
            IsHostConfigText = true;
        }

        // Set after the mode, so the text's change handler compares against what was read.
        HostConfigText = read.Text;
        HostConfigDirty = false;
    }

    private void UpdateHostConfigDirty()
    {
        if (!IsHostConfigFields)
        {
            return;
        }

        HostConfigDirty = HostConfigFields.Any(f => f.IsChanged);
        HostConfigHasInvalid = HostConfigFields.Any(f => f.IsInvalid);
    }

    /// <summary>A file read as fields, opened as its text instead: for what the fields do not show.</summary>
    [RelayCommand]
    private void ShowHostConfigAsText()
    {
        if (!HostConfigCanShowText)
        {
            return;
        }

        HostConfigFields.Clear();
        IsHostConfigFields = false;
        IsHostConfigText = true;
        HostConfigText = _hostConfigLoadedText;
        HostConfigDirty = false;
    }

    /// <summary>Writes the file; what it held before is kept beside it as name.bak-date.</summary>
    [RelayCommand]
    private void SaveHostConfig() => TrySaveHostConfig();

    private bool TrySaveHostConfig()
    {
        if (HostConfigFile is not { } item || _hostConfigDirectory is not { } directory || !HostConfigDirty)
        {
            return !HostConfigDirty;
        }

        string text;

        if (IsHostConfigFields && _hostConfigDocument is { } document)
        {
            var invalid = HostConfigFields.FirstOrDefault(f => f.IsChanged && !document.Accepts(f.Field, f.Value));

            if (invalid is not null)
            {
                invalid.IsInvalid = true;
                HostConfigHasInvalid = true;
                HostConfigNotice = Localize("HostMods_FieldInvalid", "“{0}” does not take this value. Fix it or put back what was there.", invalid.Key);
                return false;
            }

            try
            {
                text = document.Apply(HostConfigFields.Where(f => f.IsChanged).ToDictionary(f => f.Field.Line, f => f.Value));
            }
            catch (FormatException ex)
            {
                HostConfigNotice = Localize("HostMods_FieldInvalid", "“{0}” does not take this value. Fix it or put back what was there.", ex.Message);
                return false;
            }
        }
        else
        {
            text = HostConfigText;
        }

        ConfigWriteResult result;

        try
        {
            result = ServerConfigFiles.Write(directory, item.File.RelativePath, text, _hostConfigEncoding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HostConfigNotice = Localize("HostMods_SaveFailed", "The file was not saved: {0}", ex.Message);
            return false;
        }

        if (!result.IsOk)
        {
            HostConfigNotice = result.Status == ConfigFileStatus.TooLarge
                ? Localize("HostMods_SaveTooLarge", "The file was not saved: it would be larger than the launcher writes.")
                : Localize("HostMods_FileNotAllowed", "The launcher does not open this file.");
            return false;
        }

        // Read back, so the fields stand for what is now on disk.
        LoadHostConfigFile(item);
        HostConfigNotice = result.BackupPath is { } backup
            ? Localize("HostMods_Saved", "Saved. The previous version is kept beside it: {0}. {1}", Path.GetFileName(backup), HostModsRestartNote)
            : Localize("HostMods_SavedNew", "Saved. {0}", HostModsRestartNote);
        return true;
    }

    /// <summary>Puts back what the file says, dropping the edits.</summary>
    [RelayCommand]
    private void RevertHostConfig()
    {
        if (HostConfigFile is { } item)
        {
            LoadHostConfigFile(item);
        }
    }

    [RelayCommand]
    private void CloseHostConfig() => AskBeforeLeavingHostConfig(CloseHostConfigNow);

    private void AskBeforeLeavingHostConfig(Action leave)
    {
        if (!HostConfigDirty)
        {
            leave();
            return;
        }

        _hostConfigAfterAnswer = leave;
        IsHostConfigAsking = true;
    }

    [RelayCommand]
    private void HostConfigAnswerSave()
    {
        IsHostConfigAsking = false;
        var then = _hostConfigAfterAnswer;
        _hostConfigAfterAnswer = null;

        // A save that did not go through keeps the player where the problem is shown.
        if (TrySaveHostConfig())
        {
            then?.Invoke();
        }
    }

    [RelayCommand]
    private void HostConfigAnswerDiscard()
    {
        IsHostConfigAsking = false;
        var then = _hostConfigAfterAnswer;
        _hostConfigAfterAnswer = null;
        HostConfigDirty = false;
        then?.Invoke();
    }

    [RelayCommand]
    private void HostConfigAnswerStay()
    {
        IsHostConfigAsking = false;
        _hostConfigAfterAnswer = null;
    }

    private void CloseHostConfigNow()
    {
        var wasOpen = IsHostConfigOpen;

        IsHostConfigOpen = false;
        IsHostConfigAsking = false;
        _hostConfigAfterAnswer = null;
        HostConfigFiles.Clear();
        HostConfigFields.Clear();
        HostConfigFile = null;
        HostConfigText = string.Empty;
        HostConfigMessage = string.Empty;
        HostConfigNotice = string.Empty;
        IsHostConfigFields = false;
        IsHostConfigText = false;
        HostConfigDirty = false;
        HostConfigHasInvalid = false;
        _hostConfigDocument = null;
        _hostConfigDirectory = null;

        // A settings file of a mod may have been made or saved; the "has settings" marks follow.
        if (wasOpen)
        {
            _ = RefreshHostModsAsync(searchToo: false);
        }
    }

    /// <summary>Shows the opened file in the system's file manager, for a file the launcher does not edit.</summary>
    [RelayCommand]
    private void RevealHostConfigFile()
    {
        if (HostConfigFile is not { } item || _hostConfigDirectory is not { } directory ||
            ServerConfigFiles.Resolve(directory, item.File.RelativePath) is not { } path)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.GetDirectoryName(path)!,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            HostConfigNotice = Localize("Error_OpenFolder", "Failed to open the folder: {0}", ex.Message);
        }
    }
}
