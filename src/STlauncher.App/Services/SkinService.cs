using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using STlauncher.Core;

namespace STlauncher.App.Services;

/// <summary>
/// Fetches player skins as full textures, which both the face avatar and the 3D viewer
/// are drawn from.
/// </summary>
/// <remarks>
/// Skins used to come from one site, as a pre-rendered face, with a five-minute timeout
/// inherited from the shared HttpClient and a fallback that needed the same site to be
/// up. When that site was slow or unreachable the avatar simply stayed empty. Now three
/// sources are tried in turn with a short timeout each, the last good texture is kept on
/// disk, and the default Steve ships inside the launcher so there is always something
/// to show.
/// </remarks>
public sealed class SkinService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    /// <summary>A cached texture younger than this is not re-fetched.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);

    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, PlayerSkin> _memory = new(StringComparer.OrdinalIgnoreCase);
    private PlayerSkin? _default;

    public SkinService(HttpClient http, LauncherPaths paths)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cacheDirectory = Path.Combine((paths ?? throw new ArgumentNullException(nameof(paths))).Meta, "skins");
    }

    /// <summary>The built-in Steve, used when a name has no skin anywhere.</summary>
    public PlayerSkin Default
    {
        get
        {
            if (_default is null)
            {
                using var stream = AssetLoader.Open(new Uri("avares://STlauncher.App/Assets/steve.png"));
                _default = new PlayerSkin(new Bitmap(stream), isDefault: true);
            }

            return _default;
        }
    }

    public async Task<PlayerSkin> GetSkinAsync(string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return Default;
        }

        if (_memory.TryGetValue(username, out var cached))
        {
            return cached;
        }

        var cachePath = Path.Combine(_cacheDirectory, SafeFileName(username) + ".png");

        // Fresh enough on disk: no request at all.
        if (LoadCached(cachePath, CacheLifetime) is { } fresh)
        {
            return Remember(username, fresh);
        }

        var bytes = await FetchAsync(username, cancellationToken).ConfigureAwait(false);

        if (bytes is not null && TryDecode(bytes) is { } fetched)
        {
            SaveCached(cachePath, bytes);
            return Remember(username, fetched);
        }

        // Nothing reachable: the last skin we saw for this name beats a stranger's face.
        if (LoadCached(cachePath, TimeSpan.MaxValue) is { } stale)
        {
            return Remember(username, stale);
        }

        // Deliberately not remembered under the real name: a network blip must not pin
        // Steve to a player until the launcher restarts.
        return Default;
    }

    private PlayerSkin Remember(string username, PlayerSkin skin)
    {
        _memory[username] = skin;
        return skin;
    }

    /// <summary>
    /// The sources, each a single request for the raw texture. The last is Mojang itself,
    /// which takes three requests but is the one the others copy from.
    /// </summary>
    private async Task<byte[]?> FetchAsync(string username, CancellationToken cancellationToken)
    {
        var name = Uri.EscapeDataString(username);

        return await GetBytesAsync($"https://mc-heads.net/skin/{name}", cancellationToken).ConfigureAwait(false)
               ?? await GetBytesAsync($"https://minotar.net/skin/{name}", cancellationToken).ConfigureAwait(false)
               ?? await FetchFromMojangAsync(name, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> FetchFromMojangAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var profile = await GetJsonAsync($"https://api.mojang.com/users/profiles/minecraft/{name}", cancellationToken).ConfigureAwait(false);

            if (profile is null || !profile.RootElement.TryGetProperty("id", out var idNode))
            {
                return null;
            }

            using var session = await GetJsonAsync(
                $"https://sessionserver.mojang.com/session/minecraft/profile/{idNode.GetString()}",
                cancellationToken).ConfigureAwait(false);
            profile.Dispose();

            if (session is null || !session.RootElement.TryGetProperty("properties", out var properties))
            {
                return null;
            }

            foreach (var property in properties.EnumerateArray())
            {
                if (property.TryGetProperty("name", out var n) && n.GetString() == "textures" &&
                    property.TryGetProperty("value", out var v) && v.GetString() is { } encoded)
                {
                    using var textures = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(encoded));

                    if (textures.RootElement.TryGetProperty("textures", out var t) &&
                        t.TryGetProperty("SKIN", out var skin) &&
                        skin.TryGetProperty("url", out var url) && url.GetString() is { } skinUrl)
                    {
                        return await GetBytesAsync(skinUrl, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
        }

        return null;
    }

    private async Task<System.Text.Json.JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(url, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : System.Text.Json.JsonDocument.Parse(bytes);
    }

    /// <summary>One request with its own short timeout; any failure is simply "not here".</summary>
    private async Task<byte[]?> GetBytesAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var response = await _http.GetAsync(url, timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller moved on (the name is being retyped); that is not a failure.
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static PlayerSkin? TryDecode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);

            // A skin is 64 wide; anything else is an error page or a placeholder image.
            return bitmap.PixelSize.Width == 64 && bitmap.PixelSize.Height is 32 or 64
                ? new PlayerSkin(bitmap, isDefault: false)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static PlayerSkin? LoadCached(string path, TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > maxAge)
            {
                return null;
            }

            return TryDecode(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SaveCached(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception)
        {
            // A cache that cannot be written is only a cache.
        }
    }

    private static string SafeFileName(string username)
    {
        var name = username.Trim();

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.ToLowerInvariant();
    }
}
