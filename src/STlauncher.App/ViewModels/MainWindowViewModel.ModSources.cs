using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>Where the catalog looks for mods.</summary>
public enum BrowserSource
{
    Modrinth,
    CurseForge,

    /// <summary>Both at once, as one list with the doubles taken out.</summary>
    All
}

/// <summary>
/// The catalog's second source. CurseForge answers through the owner's mirror, which may
/// have no key yet - so the source is asked once whether it is there, and until it says
/// yes the catalog looks exactly as it did with Modrinth alone: no switch, no badges.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>True once the mirror said it can reach CurseForge; the switch exists only then.</summary>
    [ObservableProperty]
    private bool _isCurseForgeAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceAll))]
    [NotifyPropertyChangedFor(nameof(IsSourceModrinth))]
    [NotifyPropertyChangedFor(nameof(IsSourceCurseForge))]
    private BrowserSource _browserSource = BrowserSource.Modrinth;

    public bool IsSourceAll => BrowserSource == BrowserSource.All;
    public bool IsSourceModrinth => BrowserSource == BrowserSource.Modrinth;
    public bool IsSourceCurseForge => BrowserSource == BrowserSource.CurseForge;

    /// <summary>The source actually asked: a choice made while CurseForge was up does not outlive it.</summary>
    private BrowserSource EffectiveSource => IsCurseForgeAvailable ? BrowserSource : BrowserSource.Modrinth;

    [RelayCommand]
    private void SelectBrowserSource(string? source)
    {
        if (Enum.TryParse<BrowserSource>(source, ignoreCase: true, out var parsed) &&
            (parsed == BrowserSource.Modrinth || IsCurseForgeAvailable))
        {
            BrowserSource = parsed;
        }
    }

    partial void OnBrowserSourceChanged(BrowserSource value)
    {
        // Each source has its own categories: a chip left pressed from the other one
        // would search for a category this one has never heard of and show nothing.
        _categoriesLoaded = false;
        CloseProject();
        SelectedCategory = ModCategories.FirstOrDefault(c => c.Name.Length == 0);
        _ = LoadCategoriesAsync();
        ScheduleBrowserReload();
    }

    /// <summary>
    /// Points the client at the mirror the catalog names (or the built-in one) and asks
    /// whether CurseForge is there. Called once the catalog is read, since the catalog is
    /// how the owner moves or switches off the mirror without a release.
    /// </summary>
    private void ConfigureCurseForge(string? fromCatalog)
    {
        _curseForge.BaseUrl = CurseForgeClient.ResolveBaseUrl(fromCatalog, Services.AppSettings.DefaultCurseForgeUrl);
        _ = ProbeCurseForgeAsync();
    }

    /// <summary>
    /// One question to the mirror, off the UI thread. The client remembers a definite
    /// answer for the session, so asking again later costs nothing; an unreachable mirror
    /// is not remembered, and opening the catalog asks once more.
    /// </summary>
    private async Task ProbeCurseForgeAsync()
    {
        bool available;

        try
        {
            available = await Task.Run(() => _curseForge.IsAvailableAsync());
        }
        catch (Exception)
        {
            available = false;
        }

        if (available == IsCurseForgeAvailable)
        {
            return;
        }

        IsCurseForgeAvailable = available;

        if (!available && BrowserSource != BrowserSource.Modrinth)
        {
            BrowserSource = BrowserSource.Modrinth;
        }
    }

    private IModSource SourceFor(ModSource source) => source == ModSource.CurseForge ? _curseForge : _modrinth;

    public static string SourceName(ModSource source) => source == ModSource.CurseForge ? "CurseForge" : "Modrinth";

    /// <summary>The categories of the source on screen; both at once show Modrinth's, which is the longer list.</summary>
    private Task<IReadOnlyList<ModCategory>> LoadSourceCategoriesAsync()
        => EffectiveSource == BrowserSource.CurseForge
            ? _curseForge.GetCategoriesAsync(BrowserKind)
            : _modrinth.GetCategoriesAsync(BrowserKind);

    /// <summary>
    /// One page from the source on screen. With both, each is asked for the same page and
    /// the two are merged; if one of them fails the other's page is still shown, because
    /// half a catalog beats an error.
    /// </summary>
    private async Task<ModSearchPage> SearchSourcesAsync(string? category, int offset)
    {
        Task<ModSearchPage> Ask(IModSource source) => source.SearchAsync(
            ModSearchQuery,
            SelectedVersion!.Id,
            SelectedLoader,
            category,
            SelectedModSort?.Value ?? "relevance",
            BrowserPageSize,
            offset,
            projectType: BrowserKind);

        switch (EffectiveSource)
        {
            case BrowserSource.CurseForge:
                return await Ask(_curseForge);

            case BrowserSource.All:
                var modrinth = Ask(_modrinth);
                var curseForge = Ask(_curseForge);

                ModSearchPage? first = null;
                ModSearchPage? second = null;
                Exception? failure = null;

                try
                {
                    first = await modrinth;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    AppendConsole($"[modrinth] search failed: {ex.Message}");
                }

                try
                {
                    second = await curseForge;
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                    AppendConsole($"[curseforge] search failed: {ex.Message}");
                }

                if (first is null && second is null)
                {
                    throw failure!;
                }

                return first is null ? second!
                    : second is null ? first
                    : ModSearch.Merge(first, second);

            default:
                return await Ask(_modrinth);
        }
    }

    // ===================== What an install will bring =====================

    /// <summary>The opened mod's file cannot be fetched by a program: its author allows the site only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallOpenedProject))]
    [NotifyPropertyChangedFor(nameof(OffersModPageInstead))]
    private bool _isOpenedProjectBlocked;

    /// <summary>"Add" is offered for a mod that is not in the build and whose file the launcher may fetch.</summary>
    public bool CanInstallOpenedProject => OpenedProjectNotInstalled && !IsOpenedProjectBlocked;

    /// <summary>In place of "Add", for a mod that is not in the build and can only be fetched by hand.</summary>
    public bool OffersModPageInstead => OpenedProjectNotInstalled && IsOpenedProjectBlocked;

    /// <summary>The panel's main button follows what is installed and what may be fetched.</summary>
    private void RaiseOpenedProjectActions()
    {
        OnPropertyChanged(nameof(CanInstallOpenedProject));
        OnPropertyChanged(nameof(OffersModPageInstead));
    }

    /// <summary>"Modrinth" or "CurseForge": where the opened mod's file comes from.</summary>
    public string OpenedProjectSourceLabel => SourceName(OpenedProject?.Source ?? ModSource.Modrinth);

    public string OpenProjectPageLabel => Localize("Mods_OpenOnSource", "Open on {0}", OpenedProjectSourceLabel);

    /// <summary>"3 files, 4 MB in all", once the mod brings something besides itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenedProjectTotal))]
    private string _openedProjectTotalLabel = string.Empty;

    public bool HasOpenedProjectTotal => OpenedProjectTotalLabel.Length > 0;

    /// <summary>The opened mod's page on its own site.</summary>
    private string? OpenedProjectPageUrl()
    {
        if (OpenedProject is null)
        {
            return null;
        }

        if (OpenedProject.Source == ModSource.CurseForge)
        {
            // A blocked file is taken from its own page, one click closer than the project's.
            return IsOpenedProjectBlocked && OpenedProjectPreferred?.PageUrl is { Length: > 0 } filePage
                ? filePage
                : OpenedProject.PageUrl;
        }

        var kind = OpenedProject.ProjectType is ProjectTypes.ResourcePack or ProjectTypes.Shader
            ? OpenedProject.ProjectType
            : ProjectTypes.Mod;

        return $"https://modrinth.com/{kind}/{OpenedProject.Slug}";
    }

    /// <summary>How many lines the list of what a mod needs may run to; the panel is narrow.</summary>
    private const int MaxListedDependencies = 12;

    /// <summary>
    /// What a version needs beyond itself, with whether the build has it and how big the
    /// missing ones are. This is the list the player reads before agreeing to an install:
    /// a mod asked for by name must not bring files nobody mentioned.
    /// </summary>
    private async Task<IReadOnlyList<ModDependencyItem>> ResolveDependenciesAsync(ModVersion version, string title)
    {
        var result = new List<ModDependencyItem>();
        await CollectDependenciesAsync(version, title, 0, result, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private async Task CollectDependenciesAsync(
        ModVersion version,
        string title,
        int depth,
        List<ModDependencyItem> result,
        HashSet<string> seen)
    {
        var source = SourceFor(version.Source);

        foreach (var dependency in version.Dependencies.Where(d => !string.IsNullOrEmpty(d.ProjectId)))
        {
            if (result.Count >= MaxListedDependencies)
            {
                return;
            }

            // Embedded and incompatible relations are not something to install, and what a
            // dependency merely goes well with is its own business, not this mod's.
            var optional = depth == 0 && string.Equals(dependency.Type, "optional", StringComparison.OrdinalIgnoreCase);

            if (!dependency.IsRequired && !optional)
            {
                continue;
            }

            try
            {
                var project = await source.GetProjectAsync(dependency.ProjectId!).ConfigureAwait(true);

                if (project is null || !seen.Add(project.Slug))
                {
                    continue;
                }

                var installed = IsProjectInstalled(project.Slug);
                long size = 0;
                var blocked = false;
                ModVersion? pick = null;

                if (dependency.IsRequired && !installed)
                {
                    var candidates = await source
                        .GetVersionsAsync(project.Id, SelectedVersion?.Id, LoaderFor(project.ProjectType))
                        .ConfigureAwait(true);

                    pick = dependency.VersionId is { } wanted
                        ? candidates.FirstOrDefault(v => v.Id == wanted) ?? ModrinthClient.SelectPreferred(candidates)
                        : ModrinthClient.SelectPreferred(candidates);

                    var file = pick is null ? null : ModrinthClient.SelectFile(pick, SelectedVersion?.Id, LoaderFor(project.ProjectType));
                    size = file?.Size ?? 0;
                    blocked = pick is { Source: ModSource.CurseForge } &&
                              CurseForgeClient.CheckDownload(pick).State != CurseForgeFileState.Ready;
                }

                result.Add(new ModDependencyItem(project.Title, installed, dependency.IsRequired)
                {
                    Size = size,
                    SizeLabel = size > 0 ? FormatSize(size) : string.Empty,
                    Blocked = blocked
                });

                // The install goes one level further down, so the list does too: otherwise
                // a mod would arrive that the panel never named.
                if (pick is not null && depth == 0)
                {
                    await CollectDependenciesAsync(pick, project.Title, depth + 1, result, seen);
                }
            }
            catch (Exception ex)
            {
                AppendConsole($"[{SourceName(version.Source).ToLowerInvariant()}] dependency of {title}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// True when installing this version would also install a mod nobody pressed "Add" on.
    /// Asked on a card's button, where there is no room for a list: it stops at the first
    /// such mod, so the usual case - everything needed is already in the build - costs one
    /// question per dependency and no file lists.
    /// </summary>
    private async Task<bool> BringsOtherModsAsync(ModVersion version)
    {
        var source = SourceFor(version.Source);

        foreach (var dependency in version.Dependencies.Where(d => d.IsRequired && !string.IsNullOrEmpty(d.ProjectId)))
        {
            var project = await source.GetProjectAsync(dependency.ProjectId!).ConfigureAwait(true);

            if (project is not null && !IsProjectInstalled(project.Slug))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The line under the dependency list: how many files the install is, and their size together.</summary>
    private void RefreshOpenedProjectTotal()
    {
        var extra = OpenedProjectDependencies.Where(d => d.Required && !d.Installed).ToList();
        var own = OpenedProjectPreferred is null
            ? null
            : ModrinthClient.SelectFile(OpenedProjectPreferred, SelectedVersion?.Id, LoaderFor(BrowserKind));

        OpenedProjectTotalLabel = extra.Count == 0 || own is null || OpenedProjectInstalled
            ? string.Empty
            : Localize("Mods_InstallTotal", "Files to download: {0}, {1} in all", extra.Count + 1, FormatSize(own.Size + extra.Sum(d => d.Size)));
    }

    /// <summary>
    /// Whether CurseForge will hand over the file. The file list sometimes lacks an
    /// address the download-url call still gives, so this asks before calling it blocked.
    /// </summary>
    private async Task<bool> IsBlockedAsync(ModVersion? version)
    {
        if (version is not { Source: ModSource.CurseForge })
        {
            return false;
        }

        try
        {
            var outcome = await _curseForge.ResolveDownloadAsync(version).ConfigureAwait(true);
            return outcome.State != CurseForgeFileState.Ready;
        }
        catch (Exception ex)
        {
            AppendConsole($"[curseforge] download address: {ex.Message}");
            return CurseForgeClient.CheckDownload(version).State != CurseForgeFileState.Ready;
        }
    }
}
