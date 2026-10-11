using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using STlauncher.Core.Storage;

namespace STlauncher.App.Services;

/// <summary>
/// Fetches and caches remote images (mod logos). A genuine failure is cached as null so a
/// broken URL is not retried on every scroll.
/// </summary>
/// <remarks>
/// The cache is held to a number of bytes, not of pictures: a logo is forty kilobytes
/// once decoded, a gallery screenshot eight megabytes, and four hundred of "a picture"
/// meant anything between the two. The longest unused go first. A picture that leaves the
/// cache is only let go of, never disposed here: a card on screen may still be showing it,
/// and its memory is returned when nothing does.
/// </remarks>
public sealed class RemoteImageService
{
    /// <summary>Some eight hundred logos, or a few screens of gallery previews.</summary>
    private const long MaxCachedBytes = 32L * 1024 * 1024;

    /// <summary>What an entry costs before, or without, its picture: the address and the bookkeeping.</summary>
    private const long EntryOverheadBytes = 1024;

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<Bitmap?>> _cache = new(StringComparer.Ordinal);
    private readonly ByteBudget<string> _budget = new(MaxCachedBytes, StringComparer.Ordinal);

    /// <summary>Goes up when the cache is emptied, so a fetch that was on its way then does not put itself back.</summary>
    private int _epoch;

    public RemoteImageService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>Decoded bytes the cache holds now.</summary>
    public long CachedBytes
    {
        get
        {
            lock (_gate)
            {
                return _budget.TotalBytes;
            }
        }
    }

    public async Task<Bitmap?> GetAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        Task<Bitmap?> task;

        lock (_gate)
        {
            if (_cache.TryGetValue(url!, out var known))
            {
                _budget.Touch(url!);
                task = known;
            }
            else
            {
                // The fetch deliberately does not take the caller's token. Caching a task created
                // with one caller's token meant a single cancellation stored a null result for that
                // URL for the rest of the session, so the logo never came back.
                task = FetchCachedAsync(url!, _epoch);
                _cache[url!] = task;
                Forget(_budget.Add(url!, EntryOverheadBytes));
            }
        }

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

    /// <summary>
    /// Fetches a picture without keeping it: a full-size gallery original, which is
    /// megabytes and is looked at once. Whoever asked owns it and disposes it when done.
    /// </summary>
    public Task<Bitmap?> GetUncachedAsync(string? url)
        => string.IsNullOrWhiteSpace(url) ? Task.FromResult<Bitmap?>(null) : FetchAsync(url!);

    /// <summary>
    /// Lets go of every cached picture: the catalog was left, or the game is starting.
    /// Pictures still on a card stay there; the rest are freed with the next collection.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _epoch++;
            _cache.Clear();
            _budget.Clear();
        }
    }

    private async Task<Bitmap?> FetchCachedAsync(string url, int epoch)
    {
        var bitmap = await FetchAsync(url).ConfigureAwait(false);

        lock (_gate)
        {
            // Only an entry that is still this one: emptied or evicted meanwhile, it stays out.
            if (epoch == _epoch && _budget.Contains(url))
            {
                Forget(_budget.Add(url, EntryOverheadBytes + DecodedBytes(bitmap)));
            }
        }

        return bitmap;
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
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            // Cached as null on purpose: scrolling the browser must not hammer a bad URL.
            return null;
        }
    }

    private static long DecodedBytes(Bitmap? bitmap)
        => bitmap is null ? 0 : (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;

    /// <summary>Under the lock: drops the entries the budget has no room for.</summary>
    private void Forget(IReadOnlyList<string> evicted)
    {
        foreach (var key in evicted)
        {
            _cache.Remove(key);
        }
    }
}
