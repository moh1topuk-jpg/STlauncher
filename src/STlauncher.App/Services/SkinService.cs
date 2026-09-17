using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace STlauncher.App.Services;

public sealed class SkinService
{
    private const string DefaultSkin = "MHF_Steve";

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Bitmap> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SkinService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<Bitmap?> GetAvatarAsync(string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        if (_cache.TryGetValue(username, out var cached))
        {
            return cached;
        }

        var bitmap = await FetchAsync(username, cancellationToken).ConfigureAwait(false)
                     ?? await FetchAsync(DefaultSkin, cancellationToken).ConfigureAwait(false);

        if (bitmap is not null)
        {
            _cache[username] = bitmap;
        }

        return bitmap;
    }

    private async Task<Bitmap?> FetchAsync(string username, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .GetAsync($"https://mc-heads.net/avatar/{Uri.EscapeDataString(username)}/128", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var stream = new System.IO.MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}