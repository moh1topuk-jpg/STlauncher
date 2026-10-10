using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using STlauncher.Core.Launch;

namespace STlauncher.Core.Diagnostics;

/// <summary>What a redaction needs to know about this machine and this launcher.</summary>
/// <param name="HomeDirectory">The player's profile folder, e.g. <c>C:\Users\name</c>.</param>
/// <param name="UserName">The system account name.</param>
/// <param name="PublicServers">Addresses that are public anyway and stay readable: the server from the catalog.</param>
public sealed record LogRedactionContext(
    string? HomeDirectory,
    string? UserName,
    IReadOnlyCollection<string>? PublicServers = null)
{
    public static LogRedactionContext ForThisMachine(IReadOnlyCollection<string>? publicServers = null)
        => new(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.UserName,
            publicServers);
}

/// <summary>
/// Takes out of a log what must not end up on a public paste: who the player is on this
/// computer, the session token, and where their friends live. The nickname stays, and so
/// does everything a helper reads a log for.
/// </summary>
public static class LogRedactor
{
    public const string Home = "<home>";
    public const string User = "<user>";
    public const string Secret = "<hidden>";
    public const string Ip = "<ip>";
    public const string Server = "<server>";

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>Where a folder name ends: not inside a longer name, and not before ".ext" of another one.</summary>
    private const string NameEnd = @"(?![\w-])(?!\.\w)";

    /// <summary>
    /// "--accessToken value", "--accessToken, value" as a loader prints its argument list,
    /// "accessToken=value", "accessToken": "value". The same for the ids that name the account.
    /// </summary>
    private static readonly Regex SecretArgument = new(
        @"(?<key>--(?:accessToken|uuid|xuid|clientId)\b|\baccessToken\b""?)(?<gap>\s*[=:,]\s*""?|\s+)(?<value>[^\s,""\]\)&]+)",
        Options | RegexOptions.Compiled);

    /// <summary>"(Session ID is token:abc:def)", the line old versions print at start.</summary>
    private static readonly Regex SessionId = new(
        @"(?<head>Session ID is )[^\s\)]+",
        Options | RegexOptions.Compiled);

    private static readonly Regex Ipv4 = new(
        @"(?<![\w.+-])(?:\d{1,3}\.){3}\d{1,3}(?![\w+-]|\.\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A dotted number right after one of these is a version, not an address.</summary>
    private static readonly Regex VersionLeadIn = new(
        @"(?:version|java|jdk|jre|jvm|lwjgl|openjdk|build|driver|opengl)[\s:=(]*$",
        Options | RegexOptions.Compiled);

    private static readonly Regex Ipv6Candidate = new(
        @"(?<![\w:.])(?:[0-9a-f]{0,4}:){2,7}(?:[0-9a-f]{1,4}|(?:\d{1,3}\.){3}\d{1,3})?(?![\w:])",
        Options | RegexOptions.Compiled);

    /// <summary>"Connecting to host, port" and the server list's "Pinging host, port".</summary>
    private static readonly Regex ServerMention = new(
        @"(?<head>\b(?:Connecting to|Pinging) )(?<host>[^\s,]+)(?=, \d{1,5}\b)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Redact(string? text, LogRedactionContext context)
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

        text = RedactServers(text, publicHosts);
        text = RedactPaths(text, context);
        text = SessionId.Replace(text, m => m.Groups["head"].Value + Secret);
        text = SecretArgument.Replace(text, m => m.Groups["key"].Value + m.Groups["gap"].Value + Secret);
        text = Ipv6Candidate.Replace(text, m => IsPrivateWorthyIpv6(m.Value, publicHosts) ? Ip : m.Value);
        text = Ipv4.Replace(text, m => IsPrivateWorthyIpv4(text, m, publicHosts) ? Ip : m.Value);

        return text;
    }

    /// <summary>
    /// A friend's server is as often a name (a dynamic DNS one, a tunnel) as a number.
    /// Every host the game was told to join or ping is taken out wherever it appears,
    /// unless it is the public server.
    /// </summary>
    private static string RedactServers(string text, HashSet<string> publicHosts)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ServerMention.Matches(text))
        {
            var host = match.Groups["host"].Value.Trim('[', ']').TrimEnd('.');

            // Numbers are left to the address rules below, which keep loopback readable.
            if (host.Length >= 3 && !IPAddress.TryParse(host, out _) && !publicHosts.Contains(host) &&
                !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                hosts.Add(host);
            }
        }

        // Longest first, so "play.home.example" does not leave a tail behind "home.example".
        foreach (var host in hosts.OrderByDescending(h => h.Length))
        {
            text = Regex.Replace(text, @"(?<![\w.-])" + Regex.Escape(host) + @"(?![\w-])", Server, Options);
        }

        return text;
    }

    private static string RedactPaths(string text, LogRedactionContext context)
    {
        // The home folder with whatever stands between its parts: "\", "/", "\\" in JSON, "%5C" is not tried.
        if (!string.IsNullOrWhiteSpace(context.HomeDirectory))
        {
            var parts = context.HomeDirectory.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                var pattern = @"(?<![\w])/?" + string.Join(@"[\\/]+", parts.Select(Regex.Escape)) + NameEnd;
                text = Regex.Replace(text, pattern, Home, Options);
            }
        }

        // The account name as a folder anywhere else: another drive, a short path, a file: URL.
        if (!string.IsNullOrWhiteSpace(context.UserName))
        {
            var pattern = @"(?<=[\\/])" + Regex.Escape(context.UserName.Trim()) + NameEnd;
            text = Regex.Replace(text, pattern, User, Options);
        }

        return text;
    }

    private static bool IsPrivateWorthyIpv4(string text, Match match, HashSet<string> publicHosts)
    {
        if (!IPAddress.TryParse(match.Value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        // Loopback and "any" say nothing about anyone, and a helper wants to see them.
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || publicHosts.Contains(match.Value))
        {
            return false;
        }

        var before = text.AsSpan(Math.Max(0, match.Index - 24), Math.Min(24, match.Index)).ToString();

        return !VersionLeadIn.IsMatch(before);
    }

    private static bool IsPrivateWorthyIpv6(string candidate, HashSet<string> publicHosts)
    {
        // A clock reading has colons too; only what really parses as an address counts.
        if (!candidate.Contains("::", StringComparison.Ordinal) && candidate.Count(c => c == ':') != 7)
        {
            return false;
        }

        return IPAddress.TryParse(candidate, out var address) &&
               address.AddressFamily == AddressFamily.InterNetworkV6 &&
               !IPAddress.IsLoopback(address) &&
               !address.Equals(IPAddress.IPv6Any) &&
               !publicHosts.Contains(address.ToString());
    }

    /// <summary>Host parts of the public addresses, without port or brackets.</summary>
    private static HashSet<string> PublicHosts(IReadOnlyCollection<string>? servers)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers ?? Array.Empty<string>())
        {
            if (GameLocation.CanonicalAddress(server) is not { } canonical)
            {
                continue;
            }

            var host = canonical.StartsWith('[')
                ? canonical[1..canonical.IndexOf(']', StringComparison.Ordinal)]
                : canonical.Split(':')[0];

            hosts.Add(IPAddress.TryParse(host, out var ip) ? ip.ToString() : host);
        }

        return hosts;
    }
}
