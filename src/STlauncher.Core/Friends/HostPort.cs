using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace STlauncher.Core.Friends;

/// <summary>
/// "host", "host:port" and "[ipv6]:port", read strictly. These strings come out of an
/// invite somebody pasted and end up as the address Minecraft joins, so anything that is
/// not a plain host name or an IP literal is refused rather than passed on.
/// </summary>
public static class HostPort
{
    private const int MaxLength = 255;

    /// <summary>Splits an address. The port is null when the text names none.</summary>
    public static bool TryParse(string? text, out string host, out int? port)
    {
        host = string.Empty;
        port = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();

        if (value.Length > MaxLength)
        {
            return false;
        }

        string hostPart;
        string? portPart = null;

        if (value[0] == '[')
        {
            var close = value.IndexOf(']');

            if (close < 0)
            {
                return false;
            }

            hostPart = value[1..close];
            var rest = value[(close + 1)..];

            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return false;
                }

                portPart = rest[1..];
            }

            if (!IPAddress.TryParse(hostPart, out var literal) || literal.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }
        }
        else
        {
            var colon = value.IndexOf(':');

            if (colon >= 0 && value.IndexOf(':', colon + 1) >= 0)
            {
                // More than one colon without brackets is a bare IPv6 literal, or nothing.
                if (!IPAddress.TryParse(value, out var literal) || literal.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    return false;
                }

                hostPart = value;
            }
            else if (colon >= 0)
            {
                hostPart = value[..colon];
                portPart = value[(colon + 1)..];
            }
            else
            {
                hostPart = value;
            }

            if (hostPart.IndexOf(':') < 0 && !IsHostName(hostPart))
            {
                return false;
            }
        }

        if (portPart is not null)
        {
            if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                number < 1 || number > 65535)
            {
                return false;
            }

            port = number;
        }

        host = hostPart;
        return true;
    }

    /// <summary>The same address written one way: brackets around IPv6, no stray spaces.</summary>
    public static bool TryNormalize(string? text, out string normalized)
    {
        normalized = string.Empty;

        if (!TryParse(text, out var host, out var port))
        {
            return false;
        }

        normalized = Format(host, port);
        return true;
    }

    public static string Format(string host, int? port)
    {
        var name = host.IndexOf(':') >= 0 ? "[" + host + "]" : host;
        return port is null ? name : name + ":" + port.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Letters, digits and hyphens in dot-separated labels: a DNS name or an IPv4 literal.</summary>
    private static bool IsHostName(string host)
    {
        if (host.Length == 0 || host.Length > 253)
        {
            return false;
        }

        var labelLength = 0;

        foreach (var c in host)
        {
            if (c == '.')
            {
                if (labelLength == 0)
                {
                    return false;
                }

                labelLength = 0;
                continue;
            }

            if (!(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'))
            {
                return false;
            }

            if (++labelLength > 63)
            {
                return false;
            }
        }

        return labelLength > 0;
    }
}
