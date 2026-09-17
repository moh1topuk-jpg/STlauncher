namespace STlauncher.App.Services;

/// <summary>
/// The launcher is built for one server, so its address is fixed rather than edited
/// per build or per installation. The port defaults to 25565 and may be omitted.
/// </summary>
public static class ServerDefaults
{
    public const string Name = "Showtime";

    public const string Address = "mc.showtime.su";

    public const string Website = "https://showtime.su";
}