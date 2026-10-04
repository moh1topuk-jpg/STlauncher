using System.Globalization;
using System.Net;
using System.Text;

namespace STlauncher.Relay;

/// <summary>
/// Every limit the relay enforces, with defaults sized for a handful of friends on a
/// Minecraft server rather than for a general tunnel. Each one can be changed from the
/// command line ("--max-rooms 50") or the environment ("STRELAY_MAX_ROOMS=50"); the
/// command line wins.
/// </summary>
public sealed class RelayOptions
{
    public const int DefaultPort = 25580;

    public const string EnvironmentPrefix = "STRELAY_";

    /// <summary>The TCP port to listen on. Zero lets the system pick one, which is what the tests do.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>The address to listen on. Null is every interface, IPv4 and IPv6.</summary>
    public IPAddress? Bind { get; set; }

    /// <summary>Longest handshake line, in bytes, without the line break.</summary>
    public int MaxLineLength { get; set; } = 256;

    /// <summary>How long a new connection has to send its first line.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public int MaxRooms { get; set; } = 200;

    /// <summary>Connections of guests in one room at a time, waiting ones included.</summary>
    public int MaxGuestsPerRoom { get; set; } = 16;

    /// <summary>How long a guest waits for the host to pick its connection up.</summary>
    public TimeSpan AcceptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A tunnel that carried nothing either way for this long is closed.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>A host that has not pinged for this long is gone, and so is its room.</summary>
    public TimeSpan ControlTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Bytes per second in each direction of one tunnel. Zero or less means no cap.</summary>
    public int RateBytesPerSecond { get; set; } = 125_000;

    /// <summary>
    /// How many seconds' worth of the cap a quiet tunnel may spend at once: joining a
    /// world loads chunks in one burst, and holding that to the steady rate makes the
    /// join crawl.
    /// </summary>
    public double BurstSeconds { get; set; } = 4;

    /// <summary>Open connections from one address. A host needs one per guest plus the control one.</summary>
    public int MaxConnectionsPerIp { get; set; } = 48;

    /// <summary>New connections from one address per minute.</summary>
    public int MaxAttemptsPerMinute { get; set; } = 120;

    /// <summary>Open connections in total, as the last line of defence for the machine itself.</summary>
    public int MaxConnections { get; set; } = 2000;

    /// <summary>How long PROBE tries to connect back before answering UNREACHABLE.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How often the counters are printed. Zero turns that off.</summary>
    public TimeSpan StatsInterval { get; set; } = TimeSpan.FromMinutes(10);

    private sealed record Setting(string Name, int Min, int Max, string Help, Action<RelayOptions, int> Apply);

    private static readonly Setting[] Settings =
    {
        new("port", 0, 65535, "TCP port to listen on (25580)", (o, v) => o.Port = v),
        new("max-line", 64, 4096, "longest handshake line in bytes (256)", (o, v) => o.MaxLineLength = v),
        new("handshake-timeout", 1, 120, "seconds a connection has to send its first line (10)", (o, v) => o.HandshakeTimeout = TimeSpan.FromSeconds(v)),
        new("max-rooms", 1, 100_000, "rooms at a time (200)", (o, v) => o.MaxRooms = v),
        new("max-guests", 1, 1000, "guest connections per room (16)", (o, v) => o.MaxGuestsPerRoom = v),
        new("accept-timeout", 1, 120, "seconds a guest waits for the host (10)", (o, v) => o.AcceptTimeout = TimeSpan.FromSeconds(v)),
        new("idle-timeout", 5, 86_400, "seconds a silent tunnel stays open (120)", (o, v) => o.IdleTimeout = TimeSpan.FromSeconds(v)),
        new("control-timeout", 5, 3600, "seconds a host may go without a ping (60)", (o, v) => o.ControlTimeout = TimeSpan.FromSeconds(v)),
        new("rate-kbit", 0, 1_000_000, "cap per tunnel direction in kbit/s, 0 for none (1000)", (o, v) => o.RateBytesPerSecond = v * 125),
        new("burst-seconds", 0, 60, "seconds of the cap a quiet tunnel may spend at once (4)", (o, v) => o.BurstSeconds = v),
        new("max-per-ip", 1, 100_000, "open connections per address (48)", (o, v) => o.MaxConnectionsPerIp = v),
        new("max-attempts", 1, 1_000_000, "new connections per address per minute (120)", (o, v) => o.MaxAttemptsPerMinute = v),
        new("max-connections", 1, 1_000_000, "open connections in total (2000)", (o, v) => o.MaxConnections = v),
        new("probe-timeout", 1, 30, "seconds PROBE tries to connect back (3)", (o, v) => o.ProbeTimeout = TimeSpan.FromSeconds(v)),
        new("stats-minutes", 0, 1440, "minutes between counter lines, 0 for none (10)", (o, v) => o.StatsInterval = TimeSpan.FromMinutes(v))
    };

    /// <summary>
    /// Reads the environment first and the command line over it. A value that is not a
    /// number or is out of range is an error rather than a silent default: a relay that
    /// starts with a limit nobody asked for is worse than one that refuses to start.
    /// </summary>
    public static bool TryParse(string[] args, Func<string, string?> environment, out RelayOptions options, out string? error)
    {
        options = new RelayOptions();
        error = null;

        foreach (var setting in Settings)
        {
            var value = environment(EnvironmentName(setting.Name));

            if (!string.IsNullOrWhiteSpace(value) && !Apply(options, setting, value, EnvironmentName(setting.Name), out error))
            {
                return false;
            }
        }

        var bind = environment(EnvironmentName("bind"));

        if (!string.IsNullOrWhiteSpace(bind) && !ApplyBind(options, bind, out error))
        {
            return false;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected argument: {arg}";
                return false;
            }

            var name = arg[2..];
            string? value = null;
            var equals = name.IndexOf('=');

            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (i + 1 < args.Length)
            {
                value = args[++i];
            }

            if (value is null)
            {
                error = $"--{name} needs a value.";
                return false;
            }

            if (name == "bind")
            {
                if (!ApplyBind(options, value, out error))
                {
                    return false;
                }

                continue;
            }

            var setting = Array.Find(Settings, s => s.Name == name);

            if (setting is null)
            {
                error = $"Unknown option: --{name}";
                return false;
            }

            if (!Apply(options, setting, value, "--" + name, out error))
            {
                return false;
            }
        }

        return true;
    }

    public static string Usage()
    {
        var text = new StringBuilder();
        text.AppendLine("stlauncher-relay: the relay for STlauncher's \"a server for friends\".");
        text.AppendLine();
        text.AppendLine("Options (each also reads STRELAY_<NAME>, the command line wins):");
        text.AppendLine("  --bind <address>".PadRight(30) + "address to listen on (every interface)");

        foreach (var setting in Settings)
        {
            text.AppendLine(("  --" + setting.Name + " <n>").PadRight(30) + setting.Help);
        }

        return text.ToString();
    }

    private static string EnvironmentName(string option)
        => EnvironmentPrefix + option.ToUpperInvariant().Replace('-', '_');

    private static bool Apply(RelayOptions options, Setting setting, string value, string source, out string? error)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number < setting.Min || number > setting.Max)
        {
            error = $"{source} must be a number from {setting.Min} to {setting.Max}, got \"{value}\".";
            return false;
        }

        setting.Apply(options, number);
        error = null;
        return true;
    }

    private static bool ApplyBind(RelayOptions options, string value, out string? error)
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            error = $"--bind must be an IP address, got \"{value}\".";
            return false;
        }

        options.Bind = address;
        error = null;
        return true;
    }
}
