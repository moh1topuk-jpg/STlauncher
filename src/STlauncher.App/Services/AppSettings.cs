namespace STlauncher.App.Services;

public sealed class AppSettings
{
    public string Username { get; set; } = "Player";

    public string? SelectedVersionId { get; set; }

    public string? SelectedInstanceId { get; set; }

    public STlauncher.Core.Loaders.LoaderKind Loader { get; set; } = Core.Loaders.LoaderKind.Vanilla;

    public string? LoaderVersion { get; set; }

    public int MaxMemoryMb { get; set; } = 2048;

    public int MinMemoryMb { get; set; } = 512;

    public int? Width { get; set; }

    public int? Height { get; set; }

    public bool ShowSnapshots { get; set; }

    public string ServerName { get; set; } = "Showtime";

    public string? ServerAddress { get; set; } = "mc.showtime.su";

    public string? CurseForgeApiKey { get; set; }

    /// <summary>Interface language code: "ru" or "en".</summary>
    public string Language { get; set; } = "ru";

    /// <summary>Shows the technical log section. Off by default.</summary>
    public bool ShowDeveloperConsole { get; set; }

    /// <summary>Remote content catalog address. Empty disables the Content tab.</summary>
    public string? CatalogUrl { get; set; } = "https://mc.showtime.su/launcher/catalog.json";
}