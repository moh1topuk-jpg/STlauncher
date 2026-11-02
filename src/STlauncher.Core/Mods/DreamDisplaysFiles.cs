using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Mods;

/// <summary>
/// The FFmpeg files Dream Displays wants, in place before the game starts. Left to itself
/// the mod fetches 270 MB from GitHub while the game runs, and joining a server waits
/// for the end of that download on the game's own thread: on a slow line the game freezes
/// for as long as the download takes, and every restart begins it again. The launcher takes
/// one small archive of just those files from the owner's server, with progress the player
/// can see. See docs/mirror.md. When this fails the game still starts, as it did before.
/// </summary>
public sealed class DreamDisplaysFiles
{
    /// <summary>The archive on the owner's server, under its root.</summary>
    public const string BundlePath = "dd/ffmpeg-9.0-win64-20261101.zip";

    public const string BundleSha256 = "1efe9b7c71cc71fce115b0ec5c335883f71ab2844e75dcd1a67c2a50eb400395";

    /// <summary>Where the mod looks, under the game's folder: Dream Displays 1.10.7 and later.</summary>
    private const string FfmpegFolder = "dreamdisplays/ffmpeg/windows-x64-9.0";

    private const string NativeFolder = "dreamdisplays/native/windows-x64";

    /// <summary>A line that has stopped for this long is not coming back.</summary>
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;

    private readonly string _expectedSha256;

    public DreamDisplaysFiles(HttpClient http)
        : this(http, BundleSha256)
    {
    }

    public DreamDisplaysFiles(HttpClient http, string expectedSha256)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _expectedSha256 = expectedSha256;
    }

    /// <summary>True when this build runs the mod and the files it would download are not there.</summary>
    public static bool AreNeeded(string gameDirectory)
        => HasMod(gameDirectory) && !ArePresent(gameDirectory);

    private static bool HasMod(string gameDirectory)
    {
        var mods = ModManager.ModsDirectory(gameDirectory);

        return Directory.Exists(mods) &&
               Directory.EnumerateFiles(mods, "dreamdisplays*.jar").Any();
    }

    /// <summary>The same two checks the mod makes before it decides to download.</summary>
    private static bool ArePresent(string gameDirectory)
    {
        var binary = new FileInfo(Path.Combine(gameDirectory, FfmpegFolder, "ffmpeg.exe"));
        var native = Path.Combine(gameDirectory, NativeFolder);

        return binary is { Exists: true, Length: > 0 } &&
               Directory.Exists(native) &&
               Directory.EnumerateFiles(native, "*avcodec*.dll").Any();
    }

    /// <summary>
    /// Downloads and unpacks the archive. A download cut off part way goes on from where it
    /// stopped the next time.
    /// </summary>
    /// <param name="progress">Bytes so far and bytes in all; all is 0 while unknown.</param>
    public async Task InstallAsync(
        Uri mirrorRoot,
        string gameDirectory,
        IProgress<(long Done, long Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(gameDirectory, "dreamdisplays");
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, "ffmpeg-bundle.part");

        await DownloadAsync(new Uri(mirrorRoot, BundlePath), archive, progress, cancellationToken).ConfigureAwait(false);

        if (!await HashMatchesAsync(archive, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(archive);
            throw new InvalidDataException("the FFmpeg archive does not match its hash");
        }

        await Task.Run(() => Unpack(archive, gameDirectory), cancellationToken).ConfigureAwait(false);
        File.Delete(archive);
    }

    private async Task DownloadAsync(
        Uri url,
        string path,
        IProgress<(long Done, long Total)>? progress,
        CancellationToken cancellationToken)
    {
        var have = File.Exists(path) ? new FileInfo(path).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (have > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // Everything is already here, or what is here is not a beginning of this file: the
        // hash decides about the first, and a fresh start deals with the second.
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return;
        }

        response.EnsureSuccessStatusCode();

        var resuming = response.StatusCode == HttpStatusCode.PartialContent;
        var done = resuming ? have : 0;
        var total = done + (response.Content.Headers.ContentLength ?? 0);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(path, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];

        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallLimit);

            var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);

            if (read == 0)
            {
                return;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            done += read;
            progress?.Report((done, total));
        }
    }

    private async Task<bool> HashMatchesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return string.Equals(Convert.ToHexString(hash), _expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The archive's two folders go to the mod's two places; nothing else is taken from it.</summary>
    private static void Unpack(string archive, string gameDirectory)
    {
        using var zip = ZipFile.OpenRead(archive);

        foreach (var entry in zip.Entries)
        {
            var name = entry.Name;

            // Name is what follows the last slash, so it cannot climb out of the folder.
            if (name.Length == 0)
            {
                continue;
            }

            var folder = Path.GetDirectoryName(entry.FullName)?.Replace('\\', '/') switch
            {
                "ffmpeg/windows-x64-9.0" => FfmpegFolder,
                "native/windows-x64" => NativeFolder,
                _ => null
            };

            if (folder is null)
            {
                continue;
            }

            var directory = Path.Combine(gameDirectory, folder);
            Directory.CreateDirectory(directory);

            var temporary = Path.Combine(directory, name + ".tmp");
            entry.ExtractToFile(temporary, overwrite: true);
            File.Move(temporary, Path.Combine(directory, name), overwrite: true);
        }
    }
}
