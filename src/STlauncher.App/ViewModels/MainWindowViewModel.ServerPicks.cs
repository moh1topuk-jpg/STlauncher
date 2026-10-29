using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Content;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using STlauncher.Core.Packs;

namespace STlauncher.App.ViewModels;

/// <summary>One resource pack or shader the server recommends, as a row of the "Рекомендует сервер" panel.</summary>
public partial class ServerPickItem : ObservableObject
{
    public ServerPickItem(CatalogItem item, RecommendationStatus status, PinnedPack? pinned, bool busy)
    {
        Item = item;
        State = status.State;
        FileName = status.FileName;
        InstalledVersion = status.InstalledVersion;
        IsOn = status.Enabled;
        Pinned = pinned;
        _isBusy = busy;
    }

    public CatalogItem Item { get; }

    public CatalogItemType Kind => Item.Type;

    public bool IsShader => Kind == CatalogItemType.ShaderPack;

    public string Name => Item.Name.Length > 0 ? Item.Name : Item.Id;

    public string Description => Item.Description ?? string.Empty;

    public bool HasDescription => Description.Length > 0;

    public RecommendationState State { get; }

    public string? FileName { get; }

    public string? InstalledVersion { get; }

    public PinnedPack? Pinned { get; }

    /// <summary>For a pack: in the game's list. For a shader: the one the loader uses.</summary>
    public bool IsOn { get; }

    public bool IsMissing => State == RecommendationState.Missing;

    public bool IsInstalled => State != RecommendationState.Missing;

    public bool IsOtherVersion => State == RecommendationState.OtherVersion;

    public bool IsCurrent => State == RecommendationState.Installed;

    /// <summary>Installed and not used by the game: "Включить" is the next step.</summary>
    public bool CanEnable => IsInstalled && !IsOn;

    public string SizeLabel => Pinned is { Size: > 0 } pinned ? MainWindowViewModel.FormatPickSize(pinned.Size) : string.Empty;

    public bool HasSize => SizeLabel.Length > 0;

    /// <summary>The line under the description: size, then where it stands.</summary>
    public string MetaLabel => string.Join(" · ", new[] { SizeLabel, StateLabel }.Where(s => s.Length > 0));

    public bool HasMetaLabel => MetaLabel.Length > 0;

    /// <summary>"стоит · включён", "стоит · выключен", "стоит 1.4 · у сервера 1.5"; empty when missing.</summary>
    public string StateLabel => State switch
    {
        RecommendationState.Missing => string.Empty,
        RecommendationState.OtherVersion => MainWindowViewModel.Localize(
            "ServerPicks_OtherVersion", "installed {0} · the server has {1}",
            InstalledVersion ?? "?", Pinned?.VersionNumber ?? Item.Source.Version ?? "?"),
        _ => IsOn
            ? MainWindowViewModel.Localize(IsShader ? "ServerPicks_InstalledActive" : "ServerPicks_InstalledOn", IsShader ? "installed · active" : "installed · enabled")
            : MainWindowViewModel.Localize(IsShader ? "ServerPicks_InstalledInactive" : "ServerPicks_InstalledOff", IsShader ? "installed · not active" : "installed · disabled")
    };

    [ObservableProperty]
    private bool _isBusy;
}

/// <summary>
/// "Рекомендует сервер": resource packs and shaders the catalog suggests for a catalog
/// build, on the build's packs and shaders tabs. Nothing comes without the player: each
/// install is a plan shown first (what, how big, where), the same plan the browser
/// installs; the files are then the player's own Modrinth files, which the catalog sync
/// never removes or updates. A newer pin in the catalog is offered on the row, and
/// changes the file only when pressed.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<ServerPickItem> ServerPacks { get; } = new();

    public ObservableCollection<ServerPickItem> ServerShaders { get; } = new();

    public bool HasServerPacks => ServerPacks.Count > 0;

    public bool HasServerShaders => ServerShaders.Count > 0;

    public bool CanInstallAllServerPacks => ServerPacks.Count(p => p.IsMissing) > 1;

    public bool CanInstallAllServerShaders => ServerShaders.Count(p => p.IsMissing) > 1;

    public string InstallAllServerPacksLabel => Localize("ServerPicks_InstallAll", "Install all · {0}", ServerPacks.Count(p => p.IsMissing));

    public string InstallAllServerShadersLabel => Localize("ServerPicks_InstallAll", "Install all · {0}", ServerShaders.Count(p => p.IsMissing));

    /// <summary>The shader panel says so when the build has no shader loader to use them.</summary>
    public string ServerShadersLoaderNote => Localize("ServerPicks_NeedLoader", "Without {0} in the build these shaders will not switch on.", ExpectedShaderLoader);

    /// <summary>Lookups, plans and downloads of the panel; one at a time.</summary>
    [ObservableProperty]
    private bool _isServerPicksBusy;

    // ===================== The question before a download =====================

    /// <summary>Which panel's question is open: packs, shaders, or none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerPacksConsentOpen), nameof(IsServerShadersConsentOpen))]
    private CatalogItemType? _serverPicksConsentKind;

    public bool IsServerPacksConsentOpen => ServerPicksConsentKind == CatalogItemType.ResourcePack;

    public bool IsServerShadersConsentOpen => ServerPicksConsentKind == CatalogItemType.ShaderPack;

    [ObservableProperty]
    private string _serverPicksConsentTitle = string.Empty;

    public ObservableCollection<string> ServerPicksConsentLines { get; } = new();

    [ObservableProperty]
    private string _serverPicksConfirmLabel = string.Empty;

    /// <summary>A note under the list: no shader loader, for instance.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPicksConsentNote))]
    private string _serverPicksConsentNote = string.Empty;

    public bool HasServerPicksConsentNote => ServerPicksConsentNote.Length > 0;

    private sealed record ServerPickOperation(ServerPickItem Pick, ModVersion Version, ModInstallPlan? Plan);

    private List<ServerPickOperation>? _pendingServerPicks;
    private InstallTarget? _pendingServerPicksTarget;

    /// <summary>What Modrinth said about each pin: the version, or null for "no such version". Kept for the session.</summary>
    private readonly Dictionary<string, ModVersion?> _serverPickVersions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rows being installed, so a rebuilt list keeps showing them busy.</summary>
    private readonly HashSet<string> _serverPicksBusy = new(StringComparer.OrdinalIgnoreCase);

    private bool _resolvingServerPicks;

    private DateTime _serverPicksFailedAt = DateTime.MinValue;

    /// <summary>The catalog build the selected build follows, if it is one.</summary>
    private CatalogBuild? SelectedCatalogBuild
        => SelectedInstance?.CatalogBuildId is { Length: > 0 } id
            ? (_loadedCatalog?.Builds ?? CatalogBuilds.ToList()).FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase))
            : null;

    private string PinKey(CatalogItem item) => $"{item.Source.Project}@{item.Source.Version}@{SelectedInstance?.VersionId}";

    private PinnedPack? PinnedFor(CatalogItem item)
    {
        if (!_serverPickVersions.TryGetValue(PinKey(item), out var version) || version is null)
        {
            return null;
        }

        var file = ModrinthClient.SelectFile(version, SelectedInstance?.VersionId, LoaderKind.Vanilla);
        return file is null ? null : new PinnedPack(version.VersionNumber, file.FileName, file.Size);
    }

    /// <summary>The packs panel, from the pack lists that were just read.</summary>
    private void RefreshServerPacks()
    {
        var files = EnabledResourcePacks.Concat(DisabledResourcePacks).Select(p => p.FileName).ToList();
        var enabled = EnabledResourcePacks.Select(p => p.FileName).ToList();

        FillServerPicks(ServerPacks, CatalogItemType.ResourcePack, files, enabled);
        OnPropertyChanged(nameof(HasServerPacks));
        OnPropertyChanged(nameof(CanInstallAllServerPacks));
        OnPropertyChanged(nameof(InstallAllServerPacksLabel));
    }

    /// <summary>The shaders panel, from the shader list that was just read.</summary>
    private void RefreshServerShaders()
    {
        var files = ShaderPacks.Select(s => s.FileName).ToList();
        var active = ShaderPacks.Where(s => s.IsActive).Select(s => s.FileName).ToList();

        FillServerPicks(ServerShaders, CatalogItemType.ShaderPack, files, active);
        OnPropertyChanged(nameof(HasServerShaders));
        OnPropertyChanged(nameof(CanInstallAllServerShaders));
        OnPropertyChanged(nameof(InstallAllServerShadersLabel));
        OnPropertyChanged(nameof(ServerShadersLoaderNote));
    }

    private void FillServerPicks(ObservableCollection<ServerPickItem> list, CatalogItemType kind, IReadOnlyList<string> files, IReadOnlyList<string> enabled)
    {
        var items = CatalogRecommendations.For(_loadedCatalog, SelectedCatalogBuild, kind);
        var records = SelectedInstance?.InstalledMods.ToList() ?? new List<STlauncher.Core.Instances.InstalledModRecord>();

        list.Clear();

        foreach (var item in items)
        {
            var pinned = PinnedFor(item);
            var status = CatalogRecommendations.Evaluate(item, records, files, pinned, enabled);
            list.Add(new ServerPickItem(item, status, pinned, _serverPicksBusy.Contains(item.Id)));
        }

        // The sizes and the exact files come from Modrinth; asked once per pin, and after a
        // failed lookup not again on every redraw - the next opening of the tab asks.
        if (items.Any(i => !_serverPickVersions.ContainsKey(PinKey(i))) &&
            (IsBuildResourcePacks || IsBuildShaders) &&
            DateTime.UtcNow - _serverPicksFailedAt > TimeSpan.FromMinutes(1))
        {
            _ = ResolveServerPicksAsync();
        }
    }

    /// <summary>The tab was opened: a lookup that failed earlier is tried again.</summary>
    private void ResolveServerPicksOnOpen()
    {
        _serverPicksFailedAt = DateTime.MinValue;
        _ = ResolveServerPicksAsync();
    }

    /// <summary>Looks up the pinned versions of the open build's recommendations, then redraws both panels.</summary>
    private async Task ResolveServerPicksAsync()
    {
        if (_resolvingServerPicks)
        {
            return;
        }

        var wanted = ServerPacks.Concat(ServerShaders)
            .Select(p => p.Item)
            .Where(i => !_serverPickVersions.ContainsKey(PinKey(i)))
            .ToList();

        if (wanted.Count == 0)
        {
            return;
        }

        _resolvingServerPicks = true;

        try
        {
            var gameVersion = SelectedInstance?.VersionId;

            foreach (var item in wanted)
            {
                try
                {
                    _serverPickVersions[PinKey(item)] = await LookUpPinnedAsync(item, gameVersion);
                }
                catch (Exception ex)
                {
                    // Offline: the rows still say what is installed; the next visit asks again.
                    AppendConsole($"[server picks] {item.Id}: {ex.Message}");
                    _serverPicksFailedAt = DateTime.UtcNow;
                    return;
                }
            }
        }
        finally
        {
            _resolvingServerPicks = false;
        }

        RefreshServerPacks();
        RefreshServerShaders();
    }

    private async Task<ModVersion?> LookUpPinnedAsync(CatalogItem item, string? gameVersion)
    {
        var pin = item.Source.Version;

        // A pin by id is one request; by number, the project's list.
        if (!string.IsNullOrWhiteSpace(pin) && pin.Length == 8 && pin.All(char.IsLetterOrDigit))
        {
            if (await _modrinth.GetVersionAsync(pin) is { } byId)
            {
                return byId;
            }
        }

        var versions = await _modrinth.GetVersionsAsync(item.Source.Project!, null, LoaderKind.Vanilla);
        return CatalogRecommendations.PickPinned(versions, pin, gameVersion);
    }

    // ===================== Commands =====================

    [RelayCommand]
    private Task InstallServerPickAsync(ServerPickItem? pick)
        => pick is null ? Task.CompletedTask : PrepareServerPicksAsync(pick.Kind, new[] { pick });

    [RelayCommand]
    private Task InstallAllServerPacksAsync()
        => PrepareServerPicksAsync(CatalogItemType.ResourcePack, ServerPacks.Where(p => p.IsMissing).ToList());

    [RelayCommand]
    private Task InstallAllServerShadersAsync()
        => PrepareServerPicksAsync(CatalogItemType.ShaderPack, ServerShaders.Where(p => p.IsMissing).ToList());

    /// <summary>
    /// Draws the plan for the chosen rows and puts it on the panel as a question: every
    /// file with its size and folder, anything a pack needs besides itself included.
    /// Nothing is downloaded here.
    /// </summary>
    private async Task PrepareServerPicksAsync(CatalogItemType kind, IReadOnlyList<ServerPickItem> picks)
    {
        if (picks.Count == 0 || IsServerPicksBusy)
        {
            return;
        }

        CancelServerPicks();
        IsServerPicksBusy = true;
        Status = Localize("ServerPicks_Planning", "Checking what will be downloaded…");

        try
        {
            var target = CurrentInstallTarget();
            var operations = new List<ServerPickOperation>();
            var lines = new List<string>();
            long total = 0;

            foreach (var pick in picks)
            {
                var key = PinKey(pick.Item);

                if (!_serverPickVersions.TryGetValue(key, out var version) || version is null)
                {
                    version = await LookUpPinnedAsync(pick.Item, target.GameVersion);
                    _serverPickVersions[key] = version;
                }

                if (version is null)
                {
                    Status = Localize("ServerPicks_NoVersion", "Modrinth has no version {1} of {0}: the catalog needs a look", pick.Name, pick.Item.Source.Version ?? "?");
                    return;
                }

                if (pick.IsOtherVersion)
                {
                    var file = ModrinthClient.SelectFile(version, target.GameVersion, LoaderKind.Vanilla);
                    var size = file?.Size ?? 0;
                    total += size;
                    lines.Add(Localize("ServerPicks_LineSwap", "{0}: {1} → {2} · {3}", pick.Name, pick.InstalledVersion ?? "?", version.VersionNumber, FormatPickSize(size)));
                    operations.Add(new ServerPickOperation(pick, version, null));
                    continue;
                }

                var plan = await ResolveInstallPlanAsync(version, pick.Item.Source.Project!, pick.Name, pick.Item.IconUrl,
                    CatalogRecommendations.ProjectTypeFor(pick.Kind), target);

                if (!plan.IsComplete || plan.Root.State != ModPlanState.Install)
                {
                    Status = plan.Failure ?? Localize("Status_NoCompatibleFile", "No compatible file for this version and loader");
                    return;
                }

                foreach (var download in plan.Downloads)
                {
                    total += download.DownloadSize;
                    lines.Add(Localize("ServerPicks_Line", "{0} · {1} → {2}", download.Title, FormatPickSize(download.DownloadSize), ProjectTypes.FolderFor(download.ProjectType) + "/"));
                }

                operations.Add(new ServerPickOperation(pick, version, plan));
            }

            _pendingServerPicks = operations;
            _pendingServerPicksTarget = target;

            ServerPicksConsentLines.Clear();
            foreach (var line in lines)
            {
                ServerPicksConsentLines.Add(line);
            }

            ServerPicksConsentTitle = Localize("ServerPicks_ConsentTitle", "Will be downloaded into “{0}” from Modrinth:", SelectedInstance?.Name ?? string.Empty);
            ServerPicksConfirmLabel = Localize("ServerPicks_Confirm", "Download · {0}", FormatPickSize(total));
            ServerPicksConsentNote = kind == CatalogItemType.ShaderPack && !HasShaderLoader
                ? ServerShadersLoaderNote
                : Localize("ServerPicks_ConsentNote", "Nothing is switched on by itself; the catalog never removes or updates these files.");
            ServerPicksConsentKind = kind;
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            Status = Localize("ServerPicks_Failed", "Could not prepare the download: {0}", DescribeFailure(ex));
            AppendConsole($"[server picks] plan failed: {ex}");
        }
        finally
        {
            IsServerPicksBusy = false;
        }
    }

    [RelayCommand]
    private void CancelServerPicks()
    {
        _pendingServerPicks = null;
        _pendingServerPicksTarget = null;
        ServerPicksConsentLines.Clear();
        ServerPicksConsentKind = null;
        ServerPicksConsentNote = string.Empty;
    }

    /// <summary>The player said yes: the plans on screen, and nothing else.</summary>
    [RelayCommand]
    private async Task ConfirmServerPicksAsync()
    {
        var operations = _pendingServerPicks;
        var target = _pendingServerPicksTarget;
        CancelServerPicks();

        if (operations is null || target is null || IsServerPicksBusy)
        {
            return;
        }

        // The question was asked about one build; with another one open now, it is not answered.
        if (!IsSelectedBuild(target))
        {
            return;
        }

        IsServerPicksBusy = true;
        var done = new List<string>();

        try
        {
            await MaybeBackupAsync(BackupTrigger.BeforeModChange, target.Instance);

            foreach (var operation in operations)
            {
                _serverPicksBusy.Add(operation.Pick.Item.Id);
                RefreshServerPicksBusy();

                if (operation.Plan is { } plan)
                {
                    await InstallPlanAsync(plan, target, backup: false);
                }
                else
                {
                    await SwapServerPickAsync(operation.Pick, operation.Version);
                }

                AppendConsole($"[server picks] {operation.Pick.Item.Id} {operation.Version.VersionNumber} installed into '{target.Instance?.Name}'");
                done.Add(operation.Pick.Name);
                _serverPicksBusy.Remove(operation.Pick.Item.Id);
            }

            Status = Localize("ServerPicks_Done", "Installed: {0}", string.Join(", ", done));
        }
        catch (Exception ex)
        {
            Status = Localize("ServerPicks_Failed", "Could not prepare the download: {0}", DescribeFailure(ex));
            AppendConsole($"[server picks] install failed: {ex}");
        }
        finally
        {
            _serverPicksBusy.Clear();
            IsServerPicksBusy = false;
            RefreshMods();
        }
    }

    /// <summary>An installed pack of another version, swapped for the pinned one where it stands.</summary>
    private async Task SwapServerPickAsync(ServerPickItem pick, ModVersion version)
    {
        var folder = CatalogRecommendations.FolderFor(pick.Kind);
        var fileName = pick.FileName!;
        var path = System.IO.Path.Combine(InstanceDirectory, folder, fileName);
        var record = SelectedInstance?.InstalledMods.FirstOrDefault(m =>
            string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        var enabledNames = EnabledResourcePacks.Select(p => p.FileName).ToList();
        var position = enabledNames.FindIndex(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));

        var newFile = await ReplacePackFileAsync(version, version.ProjectId, record, path, fileName, pick.Name, folder);

        if (newFile is null)
        {
            return;
        }

        if (pick.Kind == CatalogItemType.ResourcePack && position >= 0)
        {
            enabledNames[position] = newFile;
            ResourcePackOrder.WriteEnabled(InstanceDirectory, enabledNames);
        }
        else if (pick.Kind == CatalogItemType.ShaderPack && pick.IsOn)
        {
            WriteShaderChoice(newFile);
        }
    }

    /// <summary>"Включить": the pack goes on top of the game's list, the shader becomes the one in use.</summary>
    [RelayCommand]
    private void EnableServerPick(ServerPickItem? pick)
    {
        if (pick?.FileName is not { } fileName || PacksLocked())
        {
            return;
        }

        if (pick.Kind == CatalogItemType.ResourcePack)
        {
            if (DisabledResourcePacks.FirstOrDefault(p => string.Equals(p.FileName, fileName, StringComparison.OrdinalIgnoreCase)) is { } pack)
            {
                ToggleResourcePack(pack);
            }

            RefreshServerPacks();
        }
        else if (ShaderPacks.FirstOrDefault(s => string.Equals(s.FileName, fileName, StringComparison.OrdinalIgnoreCase)) is { } shader)
        {
            ActivateShader(shader);
            RefreshServerShaders();
        }
    }

    private void RefreshServerPicksBusy()
    {
        foreach (var pick in ServerPacks.Concat(ServerShaders))
        {
            pick.IsBusy = _serverPicksBusy.Contains(pick.Item.Id);
        }
    }

    /// <summary>Sizes on the panel: a resource pack of 40 KB is not "1 MB".</summary>
    public static string FormatPickSize(long bytes)
        => bytes >= 1024L * 1024
            ? Localize("ServerPicks_SizeMb", "{0:0.#} MB", bytes / 1024d / 1024)
            : Localize("Size_Kb", "{0} KB", Math.Max(1, bytes / 1024));
}
