using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using STlauncher.Core.Launch;

namespace STlauncher.Core.Diagnostics;

/// <summary>What the launcher's screens hide while streamer mode is on.</summary>
/// <param name="HomeDirectory">The player's profile folder, e.g. <c>C:\Users\name</c>.</param>
/// <param name="UserName">The Windows account name; often the folder name, not always.</param>
/// <param name="Nickname">The player's own nickname; shown as <paramref name="NicknameStandIn"/>.</param>
/// <param name="NicknameStandIn">"Player" in the player's language.</param>
/// <param name="PublicServers">The launcher's own server: public anyway, it stays readable.</param>
/// <param name="PrivateHosts">Host names known to be private (friends' servers, the relay), hidden wherever they appear.</param>
public sealed record StreamerRedactionContext(
    string? HomeDirectory,
    string? UserName,
    string? Nickname,
    string NicknameStandIn,
    IReadOnlyCollection<string>? PublicServers = null,
    IReadOnlyCollection<string>? PrivateHosts = null);

/// <summary>
/// The display side of streamer mode: what the launcher shows while a stream is live. The
/// rules for a log (paths, the session, addresses) are <see cref="LogRedactor"/>'s; this adds
/// the nickname, invite and build codes and host names with a port, and puts dots where the
/// log puts its placeholders, because the words are read by viewers and not by a helper.
/// </summary>
public static class StreamerRedactor
{
    /// <summary>What stands for something hidden.</summary>
    public const string Dots = "•••";

    private const string Group = "•••••";

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>"ST-XXXXX-XXXXX" (an invite) and "SB-XXXXX-XXXXX" (a build).</summary>
    private static readonly Regex ShortCode = new(
        @"(?<![A-Za-z0-9])(?<kind>S[TB])-[0-9A-Za-z]{5}-[0-9A-Za-z]{5}(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The long lines: "STS1." (a server invite) and "STB1." (a build), then base64url.</summary>
    private static readonly Regex LongCode = new(
        @"(?<kind>ST[SB]1\.)[A-Za-z0-9_\-]{4,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A host name with a port: "play.example.net:25565", "my-pc.local:25565".</summary>
    private static readonly Regex NamedHostWithPort = new(
        @"(?<![\w.@/\\-])(?<host>(?:[a-z0-9](?:[a-z0-9\-]*[a-z0-9])?\.)+[a-z][a-z0-9\-]*[a-z0-9]):(?<port>\d{1,5})(?!\d)",
        Options | RegexOptions.Compiled);

    private static readonly Regex HiddenPort = new(
        Regex.Escape(Dots) + @":\d{1,5}(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ===================== One value =====================

    /// <summary>
    /// A code as it is shown: the kind stays ("ST-", "SB-", "STS1.") so the player still
    /// sees what it is, the rest becomes dots. The copy buttons copy the real one.
    /// </summary>
    public static string MaskCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var trimmed = code.Trim();
        var masked = MaskCodes(trimmed);

        return masked != trimmed ? masked : Group + "-" + Group;
    }

    /// <summary>
    /// An address as it is shown: the public server as it is, any other one as dots, with
    /// the port hidden too (a port says which of a home's machines is listening).
    /// </summary>
    public static string MaskAddress(string? address, IReadOnlyCollection<string>? publicServers = null)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        if (IsPublic(address, PublicHosts(publicServers)))
        {
            return address;
        }

        return HasPort(address.Trim()) ? Dots + ":" + Group : Dots;
    }

    /// <summary>
    /// A path as it is shown: the folder named after the Windows account becomes dots,
    /// whatever stands around it. "C:\Users\Дима\AppData" reads "C:\Users\•••\AppData".
    /// </summary>
    public static string MaskPath(string? path, string? homeDirectory, string? userName)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        foreach (var name in UserFolderNames(homeDirectory, userName))
        {
            path = Regex.Replace(
                path,
                @"(?<=[\\/])" + Regex.Escape(name) + @"(?=[\\/""'\s;,)\]]|$)",
                Dots,
                Options);
        }

        return path;
    }

    // ===================== A line of text =====================

    /// <summary>A line as a screen shows it: as it is with the mode off, redacted with it on. Display only.</summary>
    public static string Show(string? text, bool streamerMode, StreamerRedactionContext context)
        => streamerMode ? Redact(text, context) : text ?? string.Empty;

    /// <summary>
    /// Everything a line on screen can carry: paths under the profile, the session, IPs,
    /// servers the game joined, host names with a port and the known private ones, invite
    /// and build codes, and the player's nickname.
    /// </summary>
    public static string Redact(string? text, StreamerRedactionContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var publicHosts = PublicHosts(context.PublicServers);

        // Codes first: a long code is base64 and may happen to hold a dotted number.
        text = MaskCodes(text);

        // The log's own rules, with its placeholders turned into dots.
        text = LogRedactor.Redact(text, new LogRedactionContext(context.HomeDirectory, context.UserName, context.PublicServers));
        text = text
            .Replace(LogRedactor.Home, MaskedHome(context.HomeDirectory), StringComparison.Ordinal)
            .Replace(LogRedactor.User, Dots, StringComparison.Ordinal)
            .Replace(LogRedactor.Secret, Dots, StringComparison.Ordinal)
            .Replace(LogRedactor.Ip, Dots, StringComparison.Ordinal)
            .Replace(LogRedactor.Server, Dots, StringComparison.Ordinal);

        // Paths whose user folder the log rule did not reach (another drive, a short form).
        text = MaskPath(text, context.HomeDirectory, context.UserName);

        text = NamedHostWithPort.Replace(text, m =>
            publicHosts.Contains(m.Groups["host"].Value.TrimEnd('.'))
                ? m.Value
                : Dots + ":" + Group);

        foreach (var host in (context.PrivateHosts ?? Array.Empty<string>())
                     .Select(HostOf)
                     .Where(h => h is { Length: >= 3 } && !publicHosts.Contains(h!) && !IPAddress.TryParse(h, out _))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(h => h!.Length))
        {
            text = Regex.Replace(text, @"(?<![\w.-])" + Regex.Escape(host!) + @"(?![\w-])", Dots, Options);
        }

        // The port of a hidden address goes with it.
        text = HiddenPort.Replace(text, Dots + ":" + Group);

        if (!string.IsNullOrWhiteSpace(context.Nickname) && context.Nickname.Trim().Length >= 2)
        {
            text = Regex.Replace(
                text,
                @"(?<![\w])" + Regex.Escape(context.Nickname.Trim()) + @"(?![\w])",
                context.NicknameStandIn.Replace("$", "$$", StringComparison.Ordinal),
                Options);
        }

        return text;
    }

    // ===================== Pieces =====================

    private static string MaskCodes(string text)
    {
        text = LongCode.Replace(text, m => m.Groups["kind"].Value + Group);
        return ShortCode.Replace(text, m => m.Groups["kind"].Value.ToUpperInvariant() + "-" + Group + "-" + Group);
    }

    /// <summary>"C:\Users\name" as "C:\Users\•••".</summary>
    private static string MaskedHome(string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
        {
            return Dots;
        }

        home = home.TrimEnd('\\', '/');
        var cut = home.LastIndexOfAny(new[] { '\\', '/' });

        return cut < 0 ? Dots : home[..(cut + 1)] + Dots;
    }

    /// <summary>The names the account's folder goes by: the profile folder's own name and the account name.</summary>
    private static IEnumerable<string> UserFolderNames(string? home, string? userName)
    {
        var names = new List<string>();

        if (!string.IsNullOrWhiteSpace(home))
        {
            var folder = Path.GetFileName(home.TrimEnd('\\', '/'));

            if (!string.IsNullOrWhiteSpace(folder))
            {
                names.Add(folder);
            }
        }

        if (!string.IsNullOrWhiteSpace(userName))
        {
            names.Add(userName.Trim());
        }

        // Two letters as a folder name would hit "C:\Users\Al\.." and also every "\Al" anywhere.
        return names
            .Where(n => n.Length >= 2 && !n.Equals("Users", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length);
    }

    private static bool HasPort(string address)
    {
        if (address.StartsWith('['))
        {
            return address.Contains("]:", StringComparison.Ordinal);
        }

        // A bare IPv6 has many colons and no port.
        return address.Count(c => c == ':') == 1;
    }

    private static bool IsPublic(string address, HashSet<string> publicHosts)
        => HostOf(address) is { } host && publicHosts.Contains(host);

    /// <summary>The host part of "host", "host:port", "[v6]:port" or a bare IPv6, in one spelling.</summary>
    private static string? HostOf(string? address)
    {
        if (GameLocation.CanonicalAddress(address) is not { } canonical)
        {
            return null;
        }

        var host = canonical.StartsWith('[')
            ? canonical[1..canonical.IndexOf(']', StringComparison.Ordinal)]
            : canonical.Split(':')[0];

        return IPAddress.TryParse(host, out var ip) ? ip.ToString() : host;
    }

    private static HashSet<string> PublicHosts(IReadOnlyCollection<string>? servers)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers ?? Array.Empty<string>())
        {
            if (HostOf(server) is { } host)
            {
                hosts.Add(host);
            }
        }

        return hosts;
    }
}
