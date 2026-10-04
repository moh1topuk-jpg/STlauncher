using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Friends;

public enum PlayitEventKind
{
    /// <summary>The agent wants the player to open a link in a browser and approve it there.</summary>
    ClaimLink,

    /// <summary>The player approved; the agent is saving its secret.</summary>
    ClaimApproved,

    /// <summary>The player declined in the browser.</summary>
    ClaimRejected,

    /// <summary>The agent is connected to playit's network and carrying whatever tunnels the account has.</summary>
    Connected,

    /// <summary>The saved secret is no longer accepted; the agent starts a new claim by itself.</summary>
    SecretInvalid
}

public sealed record PlayitEvent(PlayitEventKind Kind, string? Url = null);

/// <summary>A tunnel of the player's playit account, as the agent's own API describes it.</summary>
/// <param name="PublicAddress">What friends type into Minecraft.</param>
/// <param name="LocalPort">The port on this machine the tunnel leads to.</param>
/// <param name="DisabledReason">Why playit is not carrying it, or null when it is.</param>
public sealed record PlayitTunnel(string PublicAddress, int? LocalPort, string? Name, string? Kind, bool CarriesTcp, string? DisabledReason)
{
    public bool Enabled => DisabledReason is null;
}

/// <param name="InvalidKey">The API refused the secret: the agent was removed from the account.</param>
public sealed record PlayitRunData(string? AgentId, IReadOnlyList<PlayitTunnel> Tunnels, int PendingTunnels, bool InvalidKey);

/// <summary>
/// Reads what the playit agent says. Written against the agent's source at v0.17.1:
/// started with --stdout it logs the first line of each status message, which is enough
/// for the claim link and for "connected", but not for tunnel addresses. Those come from
/// the same API call the agent makes itself (see <see cref="ParseRunData"/>).
/// </summary>
public static partial class PlayitOutput
{
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"https://playit\.gg/claim/[0-9a-fA-F]{6,64}")]
    private static partial Regex ClaimRegex();

    [GeneratedRegex(@"secret_key\s*=\s*""([0-9a-fA-F]+)""")]
    private static partial Regex SecretRegex();

    /// <summary>What one line of the agent's output means, or null when it is just talk.</summary>
    public static PlayitEvent? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        // On Linux the agent colours its log; the escape codes would sit inside the link.
        var text = AnsiRegex().Replace(line, string.Empty);

        var claim = ClaimRegex().Match(text);

        if (claim.Success)
        {
            return new PlayitEvent(PlayitEventKind.ClaimLink, claim.Value);
        }

        if (text.Contains("Program approved", StringComparison.OrdinalIgnoreCase))
        {
            return new PlayitEvent(PlayitEventKind.ClaimApproved);
        }

        if (text.Contains("Program rejected", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("AgentClaimRejected", StringComparison.Ordinal))
        {
            return new PlayitEvent(PlayitEventKind.ClaimRejected);
        }

        if (text.Contains("Invalid secret", StringComparison.OrdinalIgnoreCase))
        {
            return new PlayitEvent(PlayitEventKind.SecretInvalid);
        }

        // Both "tunnel running" on its own and the periodic "playit (v0.17.1): ...
        // tunnel running, N tunnels registered".
        if (text.Contains("tunnel running", StringComparison.OrdinalIgnoreCase))
        {
            return new PlayitEvent(PlayitEventKind.Connected);
        }

        return null;
    }

    /// <summary>
    /// The agent's secret out of its file: either the bare hex key or the TOML line
    /// secret_key = "...". Null when the file holds neither.
    /// </summary>
    public static string? ReadSecret(string? fileContent)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
        {
            return null;
        }

        var trimmed = fileContent.Trim();

        if (IsHex(trimmed))
        {
            return trimmed;
        }

        var match = SecretRegex().Match(fileContent);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Reads the answer of POST /v1/agents/rundata. Null when it is not that answer at
    /// all; a refused key comes back as data with <see cref="PlayitRunData.InvalidKey"/>.
    /// </summary>
    public static PlayitRunData? ParseRunData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data))
            {
                return null;
            }

            var status = Text(root, "status");

            if (status == "error")
            {
                var invalid = data.ValueKind == JsonValueKind.Object &&
                              Text(data, "type") == "auth" &&
                              Text(data, "message") is "InvalidAgentKey" or "NoLongerValid";

                return invalid ? new PlayitRunData(null, Array.Empty<PlayitTunnel>(), 0, InvalidKey: true) : null;
            }

            if (status != "success" || data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var tunnels = new List<PlayitTunnel>();

            if (data.TryGetProperty("tunnels", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && ReadTunnel(item) is { } tunnel)
                    {
                        tunnels.Add(tunnel);
                    }
                }
            }

            var pending = data.TryGetProperty("pending", out var waiting) && waiting.ValueKind == JsonValueKind.Array
                ? waiting.GetArrayLength()
                : 0;

            // The id ends up in a link the player opens, so it has to look like an id.
            var agentId = Text(data, "agent_id");

            if (!Guid.TryParse(agentId, out _))
            {
                agentId = null;
            }

            return new PlayitRunData(agentId, tunnels, pending, InvalidKey: false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PlayitTunnel? ReadTunnel(JsonElement item)
    {
        // The address goes to friends and into Minecraft: anything that is not a plain
        // host with an optional port is not shown at all.
        if (!HostPort.TryParse(Text(item, "display_address"), out var host, out var port))
        {
            return null;
        }

        int? localPort = null;

        if (item.TryGetProperty("agent_config", out var config) && config.ValueKind == JsonValueKind.Object &&
            config.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fields.EnumerateArray())
            {
                if (field.ValueKind == JsonValueKind.Object && Text(field, "name") == "local_port" &&
                    int.TryParse(Text(field, "value"), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    localPort = number;
                }
            }
        }

        // The agent does the same: a tunnel without a local port of its own leads to
        // the port its public address names.
        localPort ??= port;

        var portType = Text(item, "port_type");

        return new PlayitTunnel(
            HostPort.Format(host, port),
            localPort,
            Text(item, "name"),
            Text(item, "tunnel_type"),
            portType is "tcp" or "both",
            Text(item, "disabled_reason"));
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsHex(string text)
    {
        if (text.Length < 16)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
