using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace STlauncher.Core.Http;

/// <summary>
/// The system proxy as it is now, not as it was when the launcher started.
/// </summary>
/// <remarks>
/// <para>
/// On .NET 8 the system proxy is read once. <c>HttpClient.DefaultProxy</c> builds an
/// <c>HttpWindowsProxy</c> the first time it is asked, that object reads the Windows
/// settings in its constructor and never again (the registry watch came in .NET 9), and
/// <c>SocketsHttpHandler</c> takes the proxy object once, when its first request goes out.
/// A player who starts a VPN that works as a "system proxy" while the launcher sits in
/// the tray - or stops one - kept the old route until a restart: every download went to a
/// proxy that was no longer there, or past one that had become the only way out.
/// </para>
/// <para>
/// What the handler does do on every request is ask its proxy object where to send it,
/// and it keeps a separate pool of connections for each answer. So the handler is given
/// this object, and this object looks at the Windows settings again (at most once in a
/// couple of seconds - four registry values) and, when they have changed, has the runtime
/// build its system proxy anew. The runtime's own reader is used on purpose: it knows the
/// automatic configuration scripts, the bypass list and the environment variables, and
/// none of that is worth a second implementation here. It is reached by reflection, as
/// there is no public way to ask for a fresh one; should a runtime update move it, the
/// plain address from the settings is used instead, and failing that the proxy the
/// launcher started with - which is the behaviour before this class existed.
/// </para>
/// </remarks>
public sealed class SystemProxyFollower : IWebProxy
{
    private const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private readonly object _gate = new();
    private readonly Func<string> _signature;
    private readonly Func<IWebProxy?> _build;
    private readonly Func<long> _clock;
    private readonly long _recheckMilliseconds;

    private IWebProxy _current;
    private string _seen;
    private long _checkedAt;

    public SystemProxyFollower()
        : this(null, null, null, null, null)
    {
    }

    /// <param name="signature">Everything the system proxy depends on, as one string: a different string means "read again".</param>
    /// <param name="build">Reads the system proxy anew; null from it keeps the one in use.</param>
    /// <param name="initial">The proxy to start with; the runtime's default when null.</param>
    /// <param name="recheck">How often the settings are looked at, at most.</param>
    /// <param name="clock">Milliseconds that only grow; tests pass their own.</param>
    public SystemProxyFollower(
        Func<string>? signature,
        Func<IWebProxy?>? build,
        IWebProxy? initial = null,
        TimeSpan? recheck = null,
        Func<long>? clock = null)
    {
        _signature = signature ?? ReadSystemSignature;
        _build = build ?? BuildSystemProxy;
        _clock = clock ?? (() => Environment.TickCount64);
        _recheckMilliseconds = (long)(recheck ?? TimeSpan.FromSeconds(2)).TotalMilliseconds;
        _current = initial ?? HttpClient.DefaultProxy;
        _seen = SafeSignature();
        _checkedAt = _clock();
    }

    /// <summary>How many times the settings were seen to change. For the log and the tests.</summary>
    public int Changes { get; private set; }

    /// <summary>
    /// Asked for once, when the handler starts. The system proxy carries none of its own,
    /// so this is whatever the caller sets, as with a plain <see cref="WebProxy"/>.
    /// </summary>
    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination) => Current().GetProxy(destination);

    public bool IsBypassed(Uri host) => Current().IsBypassed(host);

    private IWebProxy Current()
    {
        lock (_gate)
        {
            var now = _clock();

            if (now - _checkedAt < _recheckMilliseconds)
            {
                return _current;
            }

            _checkedAt = now;
            var signature = SafeSignature();

            if (string.Equals(signature, _seen, StringComparison.Ordinal))
            {
                return _current;
            }

            _seen = signature;
            Changes++;

            IWebProxy? fresh = null;

            try
            {
                fresh = _build();
            }
            catch (Exception)
            {
                // A reader that fails must not take the network down with it: the proxy
                // in use stays, exactly as if nobody had looked.
            }

            // The proxy that steps down is not disposed: a request that asked a moment
            // ago may still be reading it. It holds one system handle and changes are rare.
            if (fresh is not null)
            {
                _current = fresh;
            }

            return _current;
        }
    }

    private string SafeSignature()
    {
        try
        {
            return _signature() ?? string.Empty;
        }
        catch (Exception)
        {
            return _seen ?? string.Empty;
        }
    }

    // ------------------------------------------------------------------ the system side

    private static string ReadSystemSignature()
        => OperatingSystem.IsWindows() ? ReadWindowsSignature() : string.Empty;

    /// <summary>
    /// The values Windows keeps the proxy in: the manual address and its switch, the
    /// bypass list, the script address, and the blob that holds the "detect automatically"
    /// tick (it also counts every save, which costs one harmless re-read).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string ReadWindowsSignature()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);

        if (key is null)
        {
            return string.Empty;
        }

        using var connections = key.OpenSubKey("Connections");
        var blob = connections?.GetValue("DefaultConnectionSettings") as byte[];

        return string.Join(
            "|",
            key.GetValue("ProxyEnable"),
            key.GetValue("ProxyServer"),
            key.GetValue("ProxyOverride"),
            key.GetValue("AutoConfigURL"),
            blob is null ? string.Empty : Convert.ToHexString(blob));
    }

    private static IWebProxy? BuildSystemProxy()
        => ConstructRuntimeSystemProxy() ?? (OperatingSystem.IsWindows() ? ReadManualWindowsProxy() : null);

    /// <summary>
    /// <c>System.Net.Http.SystemProxyInfo.ConstructSystemProxy()</c>: what
    /// <c>HttpClient.DefaultProxy</c> itself calls, once. Null when it is not there.
    /// </summary>
    public static IWebProxy? ConstructRuntimeSystemProxy()
    {
        try
        {
            var method = typeof(HttpClient).Assembly
                .GetType("System.Net.Http.SystemProxyInfo", throwOnError: false)?
                .GetMethod("ConstructSystemProxy", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);

            return method?.Invoke(null, null) as IWebProxy;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IWebProxy? ReadManualWindowsProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);

        if (key is null)
        {
            return null;
        }

        var enabled = key.GetValue("ProxyEnable") is int flag && flag != 0;

        return ParseManual(enabled, key.GetValue("ProxyServer") as string, key.GetValue("ProxyOverride") as string);
    }

    /// <summary>
    /// The manual proxy of the Windows settings as a plain proxy: <c>host:port</c>, or
    /// <c>http=host:port;https=host:port</c>, with the bypass list beside it. Switched off
    /// gives a proxy that sends everything directly.
    /// </summary>
    public static IWebProxy ParseManual(bool enabled, string? server, string? bypass)
    {
        var address = enabled ? PickAddress(server) : null;

        if (address is null)
        {
            return new WebProxy();
        }

        var entries = (bypass ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var patterns = entries
            .Where(e => !string.Equals(e, "<local>", StringComparison.OrdinalIgnoreCase))
            .Select(e => "^(https?://)?" + Regex.Escape(e).Replace("\\*", ".*") + "(:\\d+)?(/.*)?$")
            .ToArray();

        return new WebProxy(address, entries.Length != patterns.Length, patterns);
    }

    private static Uri? PickAddress(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return null;
        }

        var parts = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Nearly everything the launcher asks for is https; a list without an entry for
        // it falls back to the http one, and an address without a scheme serves both.
        var pick = parts.FirstOrDefault(p => p.StartsWith("https=", StringComparison.OrdinalIgnoreCase))
                   ?? parts.FirstOrDefault(p => p.StartsWith("http=", StringComparison.OrdinalIgnoreCase))
                   ?? parts.FirstOrDefault(p => !p.Contains('='));

        if (pick is null)
        {
            return null;
        }

        var value = pick[(pick.IndexOf('=') + 1)..];

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }
}
