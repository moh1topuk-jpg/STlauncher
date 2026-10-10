using System;
using System.Net;
using System.Net.Http;

namespace STlauncher.Core.Http;

/// <summary>
/// The one way the launcher's HTTP handler is put together, so that what it promises can
/// be checked by a test instead of by reading the composition root.
/// </summary>
public static class LauncherHttp
{
    /// <summary>A redirect chain longer than this is a loop or a trap, not a download.</summary>
    public const int MaxRedirects = 8;

    /// <param name="proxy">The proxy to ask on every request; the system's, followed as it changes, when null.</param>
    public static SocketsHttpHandler CreateHandler(IWebProxy? proxy = null)
        => new()
        {
            // A VPN switched on or off moves the route; a connection made before that
            // goes on existing and leads nowhere. Two minutes bounds how long a dead one
            // can be handed out again, and the same limit picks up a changed DNS answer.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),

            // A host that drops packets instead of refusing must not hold a request
            // for the full five minutes before the launcher tries the next address.
            ConnectTimeout = TimeSpan.FromSeconds(15),

            // Asked on every request, see SystemProxyFollower for why the default is not enough.
            UseProxy = true,
            Proxy = proxy ?? new SystemProxyFollower(),

            // The runtime never follows a redirect from https to plain http; this bounds
            // the length as well (its own default is fifty).
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = MaxRedirects

            // SslOptions is left alone on purpose. Untouched, the certificate of a site is
            // checked against the trust store of Windows - where an antivirus or a company
            // proxy that inspects HTTPS puts its own root. A validation callback or a
            // pinned list here would cut exactly those players off.
        };
}
