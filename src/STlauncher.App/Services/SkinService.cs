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

/// <summary>Where to look for a skin. Auto tries them all in the launcher's order.</summary>
public enum SkinSource
{
    Auto,
    Mojang,
    TLauncher,
    ElyBy
}

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

    /// <param name="source">
    /// One system to ask, or Auto for the launcher's own order. A player with a skin in
    /// several systems picks which one the launcher shows.
    /// </param>
    public async Task<PlayerSkin> GetSkinAsync(string username, SkinSource source = SkinSource.Auto, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return Default;
        }

        var key = source == SkinSource.Auto ? username : $"{username}@{source}";

        if (_memory.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var cachePath = Path.Combine(_cacheDirectory, SafeFileName(key) + ".png");

        // Fresh enough on disk: no request at all.
        if (LoadCached(cachePath, CacheLifetime) is { } fresh)
        {
            return Remember(key, fresh);
        }

        var fetched = await FetchAsync(username, source, cancellationToken).ConfigureAwait(false);

        if (fetched is { } found && TryDecode(found.Bytes, found.Slim, found.Source) is { } skin)
        {
            SaveCached(cachePath, found.Bytes, found.Slim, found.Source);
            return Remember(key, skin);
        }

        // Nothing reachable: the last skin we saw for this name beats a stranger's face.
        if (LoadCached(cachePath, TimeSpan.MaxValue) is { } stale)
        {
            return Remember(key, stale);
        }

        // Deliberately not remembered under the real name: a network blip must not pin
        // Steve to a player until the launcher restarts.
        return Default;
    }

    private PlayerSkin Remember(string key, PlayerSkin skin)
    {
        _memory[key] = skin;
        return skin;
    }

    /// <summary>A texture as fetched, with the model type when the source stated it, and who served it.</summary>
    private readonly record struct Fetched(byte[] Bytes, bool? Slim, string Source);

    /// <summary>
    /// The sources in order, and why in that order.
    ///
    /// Most players of an offline server have no Mojang account; their skin lives where
    /// they set it - TLauncher's or ely.by's skin system - and both answer by nickname.
    /// Mojang goes first because it states the model type and is what the mirrors copy.
    /// The mirrors (minotar, mc-heads) only ever have Mojang skins, so they are asked
    /// only when Mojang could not be reached, never when Mojang said the name does not
    /// exist. That last case is what used to go wrong: mc-heads answers any unknown name
    /// with a 200 and a Steve, which the launcher took for the player's skin and cached.
    /// </summary>
    private async Task<Fetched?> FetchAsync(string username, SkinSource source, CancellationToken cancellationToken)
    {
        var name = Uri.EscapeDataString(username);

        switch (source)
        {
            case SkinSource.Mojang:
                return (await FetchFromMojangAsync(name, cancellationToken).ConfigureAwait(false)).Skin;
            case SkinSource.TLauncher:
                return await FetchFromTextureJsonAsync(
                    $"https://auth.tlauncher.org/skin/profile/texture/login/{name}", "TLauncher", cancellationToken).ConfigureAwait(false);
            case SkinSource.ElyBy:
                return await FetchFromTextureJsonAsync(
                    $"http://skinsystem.ely.by/textures/{name}", "ely.by", cancellationToken).ConfigureAwait(false);
        }

        var (mojang, mojangKnowsTheName) = await FetchFromMojangAsync(name, cancellationToken).ConfigureAwait(false);

        if (mojang is not null)
        {
            return mojang;
        }

        var tlauncher = await FetchFromTextureJsonAsync(
            $"https://auth.tlauncher.org/skin/profile/texture/login/{name}", "TLauncher", cancellationToken).ConfigureAwait(false);

        if (tlauncher is not null)
        {
            return tlauncher;
        }

        var ely = await FetchFromTextureJsonAsync(
            $"http://skinsystem.ely.by/textures/{name}", "ely.by", cancellationToken).ConfigureAwait(false);

        if (ely is not null)
        {
            return ely;
        }

        if (mojangKnowsTheName == false)
        {
            return null;
        }

        var mirror = await GetBytesAsync($"https://minotar.net/skin/{name}", cancellationToken).ConfigureAwait(false);

        if (mirror is not null)
        {
            return new Fetched(mirror, null, "minotar");
        }

        mirror = await GetBytesAsync($"https://mc-heads.net/skin/{name}", cancellationToken).ConfigureAwait(false);
        return mirror is null ? null : new Fetched(mirror, null, "mc-heads");
    }

    /// <summary>
    /// Mojang's own answer. The second value says whether Mojang recognised the name:
    /// true, false, or null when it could not be asked at all.
    /// </summary>
    private async Task<(Fetched? Skin, bool? Known)> FetchFromMojangAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var (profile, status) = await GetJsonWithStatusAsync($"https://api.mojang.com/users/profiles/minecraft/{name}", cancellationToken).ConfigureAwait(false);

            using (profile)
            {
                if (status is 204 or 404)
                {
                    return (null, false);
                }

                if (profile is null || !profile.RootElement.TryGetProperty("id", out var idNode))
                {
                    return (null, null);
                }

                using var session = await GetJsonAsync(
                    $"https://sessionserver.mojang.com/session/minecraft/profile/{idNode.GetString()}",
                    cancellationToken).ConfigureAwait(false);

                if (session is null || !session.RootElement.TryGetProperty("properties", out var properties))
                {
                    return (null, true);
                }

                foreach (var property in properties.EnumerateArray())
                {
                    if (property.TryGetProperty("name", out var n) && n.GetString() == "textures" &&
                        property.TryGetProperty("value", out var v) && v.GetString() is { } encoded)
                    {
                        using var textures = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(encoded));

                        if (textures.RootElement.TryGetProperty("textures", out var t) && SkinFromJson(t, out var url, out var slim))
                        {
                            var bytes = await GetBytesAsync(url, cancellationToken).ConfigureAwait(false);
                            return (bytes is null ? null : new Fetched(bytes, slim, "Mojang"), true);
                        }
                    }
                }

                return (null, true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// TLauncher and ely.by publish the same shape: {"SKIN":{"url":…,"metadata":{"model":"slim"}}}.
    /// An unknown name is an empty object or a 404.
    /// </summary>
    private async Task<Fetched?> FetchFromTextureJsonAsync(string endpoint, string source, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetJsonAsync(endpoint, cancellationToken).ConfigureAwait(false);

            if (document is null || !SkinFromJson(document.RootElement, out var url, out var slim))
            {
                return null;
            }

            var bytes = await GetBytesAsync(url, cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : new Fetched(bytes, slim, source);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads SKIN.url and SKIN.metadata.model from a textures object.</summary>
    private static bool SkinFromJson(System.Text.Json.JsonElement textures, out string url, out bool? slim)
    {
        url = string.Empty;
        slim = null;

        if (textures.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !textures.TryGetProperty("SKIN", out var skin) ||
            skin.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !skin.TryGetProperty("url", out var urlNode) ||
            urlNode.GetString() is not { Length: > 0 } skinUrl)
        {
            return false;
        }

        url = skinUrl;

        // {"metadata":{"model":"slim"}} marks the slim model; absent means classic.
        slim = skin.TryGetProperty("metadata", out var metadata) &&
               metadata.ValueKind == System.Text.Json.JsonValueKind.Object &&
               metadata.TryGetProperty("model", out var model) &&
               string.Equals(model.GetString(), "slim", StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private async Task<System.Text.Json.JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
        => (await GetJsonWithStatusAsync(url, cancellationToken).ConfigureAwait(false)).Document;

    private async Task<(System.Text.Json.JsonDocument? Document, int? Status)> GetJsonWithStatusAsync(string url, CancellationToken cancellationToken)
    {
        var (bytes, status) = await GetBytesWithStatusAsync(url, cancellationToken).ConfigureAwait(false);

        try
        {
            return (bytes is null ? null : System.Text.Json.JsonDocument.Parse(bytes), status);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, status);
        }
    }

    /// <summary>One request with its own short timeout; any failure is simply "not here".</summary>
    private async Task<byte[]?> GetBytesAsync(string url, CancellationToken cancellationToken)
        => (await GetBytesWithStatusAsync(url, cancellationToken).ConfigureAwait(false)).Bytes;

    /// <summary>The same, keeping the status: a 404 and a dropped connection mean different things.</summary>
    private async Task<(byte[]? Bytes, int? Status)> GetBytesWithStatusAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var response = await _http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return (null, status);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return (bytes.Length > 0 ? bytes : null, status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller moved on (the name is being retyped); that is not a failure.
            throw;
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static PlayerSkin? TryDecode(byte[] bytes, bool? slim, string source)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);

            // A skin is 64 wide; anything else is an error page or a placeholder image.
            return bitmap.PixelSize.Width == 64 && bitmap.PixelSize.Height is 32 or 64
                ? new PlayerSkin(bitmap, isDefault: false, slim) { Source = source }
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The model type and the source are kept beside the texture in a small text file:
    /// "slim Mojang", "classic TLauncher", "? ely.by".
    /// </summary>
    private static string ModelPath(string texturePath) => Path.ChangeExtension(texturePath, ".model");

    private static PlayerSkin? LoadCached(string path, TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > maxAge)
            {
                return null;
            }

            bool? slim = null;
            var source = "cache";

            if (File.Exists(ModelPath(path)))
            {
                var parts = File.ReadAllText(ModelPath(path)).Trim().Split(' ', 2);
                slim = parts[0] switch { "slim" => true, "classic" => false, _ => null };
                source = parts.Length > 1 ? parts[1] : source;
            }

            return TryDecode(File.ReadAllBytes(path), slim, source);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SaveCached(string path, byte[] bytes, bool? slim, string source)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            File.WriteAllBytes(path, bytes);
            File.WriteAllText(ModelPath(path), $"{(slim is { } known ? (known ? "slim" : "classic") : "?")} {source}");
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
