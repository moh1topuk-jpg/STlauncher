using System.Collections.Generic;
using STlauncher.Core.Loaders;

namespace STlauncher.App.Services;

public sealed class AppSettings
{
    /// <summary>Catalog shipped with the launcher; editing it needs no new release.</summary>
    public const string DefaultCatalogUrl =
        "https://raw.githubusercontent.com/moh1topuk-jpg/STlauncher/main/catalog.json";

    /// <summary>
    /// The same catalog served through Cloudflare, for players who cannot reach GitHub.
    /// Built in rather than published in the catalog, because the catalog is exactly what
    /// such a player cannot fetch. See docs/updates.md.
    /// </summary>
    public const string CatalogMirrorUrl = "https://showtime-updates.moh1topuk.workers.dev/catalog.json";

    /// <summary>Previously shipped default. Treated as "not customised" and upgraded.</summary>
    public const string LegacyCatalogUrl = "https://mc.showtime.su/launcher/catalog.json";

    public string Username { get; set; } = "Player";

    /// <summary>Saved nicknames for quick switching.</summary>
    public List<string> Nicknames { get; set; } = new();

    public string? SelectedVersionId { get; set; }

    public string? SelectedInstanceId { get; set; }

    /// <summary>
    /// Catalog builds the player deleted. The startup sync skips these, so removing the
    /// recommended build stays removed instead of coming back on the next launch.
    /// </summary>
    public List<string> DismissedBuildIds { get; set; } = new();

    /// <summary>
    /// The player has seen the offer to bring builds over from other launchers and closed
    /// it. Asked once; a card that keeps coming back is nagging.
    /// </summary>
    public bool ImportSuggestionDismissed { get; set; }

    public LoaderKind Loader { get; set; } = LoaderKind.Vanilla;

    public string? LoaderVersion { get; set; }

    public int MaxMemoryMb { get; set; } = 2048;

    public int MinMemoryMb { get; set; } = 512;

    public int? Width { get; set; }

    public int? Height { get; set; }

    public bool ShowSnapshots { get; set; }

    /// <summary>Shows release versions older than 1.5.2.</summary>
    public bool ShowOldReleases { get; set; }

    public bool ShowBeta { get; set; }

    public bool ShowAlpha { get; set; }

    /// <summary>Explicit Java executable for every build. Empty means automatic.</summary>
    public string? JavaPath { get; set; }

    /// <summary>Re-download client files on the next launch, then resets itself.</summary>
    public bool ForceUpdate { get; set; }

    public AfterLaunchAction AfterLaunch { get; set; } = AfterLaunchAction.Close;

    public bool BackupsEnabled { get; set; }

    /// <summary>Back up before every launch.</summary>
    public bool BackupsBeforeLaunch { get; set; }

    /// <summary>Back up before launch, but at most once per day.</summary>
    public bool BackupsDaily { get; set; } = true;

    /// <summary>Back up before installing or changing mods.</summary>
    public bool BackupsBeforeModChanges { get; set; } = true;

    public int BackupsMaxCount { get; set; } = 10;

    public int BackupsMaxTotalMb { get; set; } = 2048;

    public string? BackupsDirectory { get; set; }

    public string ServerName { get; set; } = "Showtime";

    public string? ServerAddress { get; set; } = "mc.showtime.su";

    /// <summary>Interface language code: "ru" or "en".</summary>
    public string Language { get; set; } = "ru";

    /// <summary>Shows the technical log section. Off by default.</summary>
    public bool ShowDeveloperConsole { get; set; }

    /// <summary>
    /// Content catalog address. Defaults to the catalog shipped in the repository, so
    /// builds and mods can be updated without releasing a new launcher.
    /// </summary>
    public string? CatalogUrl { get; set; } = DefaultCatalogUrl;

    /// <summary>
    /// Server statistics collector. Normally published through the catalog; this is the
    /// manual override in the developer section.
    /// </summary>
    public string? ServerStatsUrl { get; set; }
}