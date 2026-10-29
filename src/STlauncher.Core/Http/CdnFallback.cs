using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace STlauncher.Core.Http;

/// <summary>
/// The second way to Modrinth and CurseForge: the owner's server, which passes a request
/// for a mod file (and a few read-only Modrinth API calls) on to the real site unchanged.
/// For players whose provider blocks or breaks those sites. See docs/mirror.md.
/// </summary>
/// <remarks>
/// Only the addresses <see cref="Rewrite"/> recognises ever go through it: the file
/// shapes of the two CDNs and the API calls the launcher makes, nothing else. The bytes
/// are checked against the hash exactly as before - the server is a pipe, not a source.
/// A host that failed is remembered for the session, so the next files go straight the
/// second way; after <see cref="RetestAfter"/> the direct route is tried again.
/// </remarks>
public sealed class CdnFallback
{
    public const string ModrinthCdnHost = "cdn.modrinth.com";
    public const string ModrinthApiHost = "api.modrinth.com";

    private static readonly string[] CurseForgeCdnHosts = { "edge.forgecdn.net", "mediafilez.forgecdn.net" };

    // The same shapes the server lets through (docs/mirror.md), checked on the unescaped
    // path as Caddy checks it. Anything else would only come back as a 404.
    private static readonly Regex ModrinthFile = new(
        @"^/data/[A-Za-z0-9]{8}/versions/[A-Za-z0-9]{8}/[^/]+\.(?:jar|zip|mrpack)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CurseForgeFile = new(
        @"^/files/[0-9]{1,6}/[0-9]{1,4}/[^/]+\.(?:jar|zip|mrpack)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ModrinthApi = new(
        @"^/v2/(?:search|tag/category|version/[A-Za-z0-9]{8}|project/[A-Za-z0-9._+!@$(),\-]{1,64}(?:/version)?)$",
        RegexOptions.CultureInvariant);

    private readonly Uri? _mirror;
    private readonly Func<DateTimeOffset> _now;
    private readonly ILogger<CdnFallback>? _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _announced = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="mirrorBase">The owner's server, "https://2.26.80.201/". Null or empty switches the second way off.</param>
    public CdnFallback(string? mirrorBase, ILogger<CdnFallback>? logger = null, Func<DateTimeOffset>? now = null)
    {
        if (!string.IsNullOrWhiteSpace(mirrorBase) &&
            Uri.TryCreate(mirrorBase.EndsWith('/') ? mirrorBase : mirrorBase + "/", UriKind.Absolute, out var mirror) &&
            mirror.Scheme == Uri.UriSchemeHttps)
        {
            _mirror = mirror;
        }

        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>How long a blocked host goes straight to the mirror before the direct route is tried again.</summary>
    public TimeSpan RetestAfter { get; init; } = TimeSpan.FromMinutes(10);

    public bool Enabled => _mirror is not null;

    /// <summary>
    /// Raised once per source and session ("Modrinth", "CurseForge") the first time its
    /// files go the second way. The argument is the source's name.
    /// </summary>
    public event Action<string>? FallbackStarted;

    /// <summary>
    /// The same address through the mirror, or null when it is not one the mirror carries.
    /// File addresses with a query are left alone: neither CDN uses one, so such an
    /// address is not one of theirs.
    /// </summary>
    public Uri? Rewrite(Uri url) => _mirror is null ? null : Rewrite(url, _mirror);

    /// <inheritdoc cref="Rewrite(Uri)"/>
    public static Uri? Rewrite(Uri url, Uri mirrorBase)
    {
        if (url is null || mirrorBase is null || !url.IsAbsoluteUri ||
            url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment))
        {
            return null;
        }

        var prefix = PrefixFor(url);
        if (prefix is null)
        {
            return null;
        }

        var path = url.AbsolutePath;
        string decoded;

        try
        {
            decoded = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return null;
        }

        var isApi = prefix == "mrapi";
        var shape = prefix switch { "mr" => ModrinthFile, "cf" => CurseForgeFile, _ => ModrinthApi };

        if (!shape.IsMatch(decoded) || decoded.Contains("/../", StringComparison.Ordinal) ||
            (!isApi && !string.IsNullOrEmpty(url.Query)))
        {
            return null;
        }

        var root = mirrorBase.AbsoluteUri.TrimEnd('/');
        return new Uri(root + "/" + prefix + path + (isApi ? url.Query : string.Empty));
    }

    /// <summary>The source a host belongs to, as the player knows it; null for any other host.</summary>
    public static string? SourceOf(Uri url)
        => PrefixFor(url) switch
        {
            "mr" or "mrapi" => "Modrinth",
            "cf" => "CurseForge",
            _ => null
        };

    private static string? PrefixFor(Uri url)
    {
        var host = url.Host;

        if (string.Equals(host, ModrinthCdnHost, StringComparison.OrdinalIgnoreCase))
        {
            return "mr";
        }

        if (string.Equals(host, ModrinthApiHost, StringComparison.OrdinalIgnoreCase))
        {
            return "mrapi";
        }

        foreach (var cdn in CurseForgeCdnHosts)
        {
            if (string.Equals(host, cdn, StringComparison.OrdinalIgnoreCase))
            {
                return "cf";
            }
        }

        return null;
    }

    /// <summary>True while the host is known not to answer directly and the re-test time has not come.</summary>
    public bool ShouldUseMirror(string host)
    {
        if (!_blocked.TryGetValue(host, out var since))
        {
            return false;
        }

        if (_now() - since < RetestAfter)
        {
            return true;
        }

        // Time to try the direct route again; a failure puts the host back here.
        _blocked.TryRemove(new KeyValuePair<string, DateTimeOffset>(host, since));
        return false;
    }

    /// <summary>The direct route to this host does not work: the next requests go the second way.</summary>
    public void MarkBlocked(Uri url, string reason)
    {
        if (_mirror is null || SourceOf(url) is not { } source)
        {
            return;
        }

        var host = url.Host.ToLowerInvariant();
        var fresh = !_blocked.ContainsKey(host);
        _blocked[host] = _now();

        if (fresh)
        {
            _logger?.LogWarning("{Host} is not reachable directly ({Reason}); its files go through {Mirror} for now.", host, reason, _mirror);
        }

        if (_announced.TryAdd(source, true))
        {
            FallbackStarted?.Invoke(source);
        }
    }

    /// <summary>The direct route answered: no reason to keep sending this host the second way.</summary>
    public void MarkWorking(string host) => _blocked.TryRemove(host, out _);

    /// <summary>
    /// A failure that looks like the route, not the file: no name, no connection, a cut or
    /// broken handshake, no answer in time. A 404 or a wrong hash is about the file and
    /// never sends anything the second way.
    /// </summary>
    public static bool IsRouteFailure(Exception exception)
    {
        if (exception is null)
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HashMismatchException or RedirectRefusedException)
            {
                return false;
            }

            if (current is HttpRequestException { StatusCode: { } status })
            {
                return IsBlockStatus(status);
            }
        }

        var failure = NetworkFailures.Classify(exception);

        return failure.Kind is NetworkFailureKind.NoConnection or NetworkFailureKind.Blocked or NetworkFailureKind.Timeout ||
               failure.Cause is NetworkFailureCause.NameNotResolved
                   or NetworkFailureCause.ConnectionRefused
                   or NetworkFailureCause.ConnectionReset
                   or NetworkFailureCause.Unreachable
                   or NetworkFailureCause.CertificateNotTrusted
                   or NetworkFailureCause.SecureChannelFailed
                   or NetworkFailureCause.TimedOut;
    }

    /// <summary>
    /// The statuses a filter answers with: 403 from a proxy that will not pass the site,
    /// 451 "unavailable for legal reasons". The real CDNs answer 403 too (a missing file
    /// on CurseForge's storage), which is why a 403 only counts once the mirror got the
    /// file where the direct route did not.
    /// </summary>
    public static bool IsBlockStatus(HttpStatusCode status)
        => status is HttpStatusCode.Forbidden or HttpStatusCode.UnavailableForLegalReasons;
}

/// <summary>
/// Sends a request for a Modrinth or CurseForge file the second way when the first fails
/// on the route. Sits in front of the launcher's one HTTP handler, so every download and
/// every Modrinth call passes it; only what <see cref="CdnFallback.Rewrite(Uri)"/> knows is
/// ever touched.
/// </summary>
public sealed class CdnFallbackHandler : DelegatingHandler
{
    private readonly CdnFallback _fallback;

    public CdnFallbackHandler(CdnFallback fallback)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    /// <summary>
    /// How long the direct route may take to answer before the second way is tried. A
    /// provider that lets the handshake through and then sends nothing would otherwise
    /// hold the request for the client's whole timeout.
    /// </summary>
    public TimeSpan DirectAnswerTimeout { get; init; } = TimeSpan.FromSeconds(20);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } url ||
            (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head) ||
            _fallback.Rewrite(url) is not { } mirrored)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (_fallback.ShouldUseMirror(url.Host))
        {
            return await base.SendAsync(Copy(request, mirrored), cancellationToken).ConfigureAwait(false);
        }

        HttpResponseMessage direct;

        using (var patience = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            patience.CancelAfter(DirectAnswerTimeout);

            try
            {
                direct = await base.SendAsync(request, patience.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                       (patience.IsCancellationRequested || CdnFallback.IsRouteFailure(ex)))
            {
                var reason = patience.IsCancellationRequested && ex is OperationCanceledException
                    ? "no answer in " + DirectAnswerTimeout.TotalSeconds.ToString("0") + " s"
                    : NetworkFailures.Describe(NetworkFailures.Classify(ex));

                HttpResponseMessage second;

                try
                {
                    second = await base.SendAsync(Copy(request, mirrored), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Neither way: most likely no internet at all. The direct failure is
                    // the one that explains it, and the host is not marked.
                    if (ex is OperationCanceledException)
                    {
                        throw new TimeoutException("No answer from " + url.Host + " in " + DirectAnswerTimeout.TotalSeconds.ToString("0") + " seconds.", ex);
                    }

                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
                    throw;
                }

                if (second.IsSuccessStatusCode)
                {
                    _fallback.MarkBlocked(url, reason);
                }

                return second;
            }
        }

        if (!CdnFallback.IsBlockStatus(direct.StatusCode))
        {
            if (direct.IsSuccessStatusCode)
            {
                _fallback.MarkWorking(url.Host);
            }

            return direct;
        }

        // 403 or 451: a filter, or the site itself. The mirror decides which.
        HttpResponseMessage viaMirror;

        try
        {
            viaMirror = await base.SendAsync(Copy(request, mirrored), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return direct;
        }

        if (!viaMirror.IsSuccessStatusCode)
        {
            viaMirror.Dispose();
            return direct;
        }

        direct.Dispose();
        _fallback.MarkBlocked(url, "HTTP " + (int)direct.StatusCode);
        return viaMirror;
    }

    /// <summary>A GET or HEAD has no body: the method, the headers (Range among them) and the version are the request.</summary>
    private static HttpRequestMessage Copy(HttpRequestMessage request, Uri url)
    {
        var copy = new HttpRequestMessage(request.Method, url)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
        {
            if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)copy.Options)[option.Key] = option.Value;
        }

        return copy;
    }
}
