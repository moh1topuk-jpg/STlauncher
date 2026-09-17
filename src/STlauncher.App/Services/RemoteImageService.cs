using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace STlauncher.App.Services;

/// <summary>
/// Fetches and caches remote images (mod logos). A genuine failure is cached as null so a
/// broken URL is not retried on every scroll.
/// </summary>
public sealed class RemoteImageService
{
    /// <summary>Enough logos to cover browsing without growing without bound.</summary>
    private const int MaxCachedImages = 400;

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new(StringComparer.Ordinal);

    public RemoteImageService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<Bitmap?> GetAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // The fetch deliberately does not take the caller's token. Caching a task created
        // with one caller's token meant a single cancellation stored a null result for that
        // URL for the rest of the session, so the logo never came back.
        var task = _cache.GetOrAdd(url!, u => FetchAsync(u));

        try
        {
            // A caller can still stop waiting without disturbing the shared fetch.
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<Bitmap?> FetchAsync(string url)
    {
        try
        {
            using var response = await _http.GetAsync(url, CancellationToken.None).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content
                .ReadAsByteArrayAsync(CancellationToken.None)
                .ConfigureAwait(false);

            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);

            Trim();
            return bitmap;
        }
        catch (Exception)
        {
            // Cached as null on purpose: scrolling the browser must not hammer a bad URL.
            return null;
        }
    }

    /// <summary>Evicts and disposes the oldest entries once the cache grows too large.</summary>
    private void Trim()
    {
        if (_cache.Count <= MaxCachedImages)
        {
            return;
        }

        var excess = _cache.Count - MaxCachedImages;

        foreach (var key in _cache.Keys.Take(excess).ToList())
        {
            if (_cache.TryRemove(key, out var task) && task.IsCompletedSuccessfully)
            {
                task.Result?.Dispose();
            }
        }
    }
}
