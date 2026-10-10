using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using STlauncher.Core.Storage;

namespace STlauncher.Core.Http;

public sealed class DownloadClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DownloadClient>? _logger;
    private readonly int _maxAttempts;
    private readonly VerifiedFileCache _cache;

    public DownloadClient(
        HttpClient http,
        ILogger<DownloadClient>? logger = null,
        int maxAttempts = 3,
        VerifiedFileCache? cache = null,
        SharedFileStore? sharedFiles = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
        _maxAttempts = Math.Max(1, maxAttempts);
        _cache = cache ?? new VerifiedFileCache();
        SharedFiles = sharedFiles;
    }

    /// <summary>
    /// One copy on disk for the mods and packs several builds have in common. Every
    /// download into a build passes through this class, so this is the one place that
    /// knows to ask; the store itself decides which destinations it cares about. Null
    /// means plain files everywhere.
    /// </summary>
    public SharedFileStore? SharedFiles { get; }

    /// <summary>Files verified so far, so a forced re-check can forget them all.</summary>
    public VerifiedFileCache Cache => _cache;

    public int MaxConcurrency { get; init; } = 8;

    public async Task<DownloadSummary> DownloadAllAsync(
        IReadOnlyCollection<DownloadItem> items,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var total = items.Count;
        var completed = 0;
        var failed = 0;
        long bytes = 0;
        var errors = new ConcurrentBag<(DownloadItem Item, Exception Error)>();

        using var throttle = new SemaphoreSlim(Math.Max(1, MaxConcurrency));

        var tasks = items.Select(async item =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await EnsureFileAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    Interlocked.Add(ref bytes, item.Size);
                }

                var done = Interlocked.Increment(ref completed);
                progress?.Report(new DownloadProgress(done, total, Interlocked.Read(ref bytes), failed, item.DestinationPath));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to download {Url}", item.Url);
                errors.Add((item, ex));
                var failedNow = Interlocked.Increment(ref failed);
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new DownloadProgress(done, total, Interlocked.Read(ref bytes), failedNow, item.DestinationPath));
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            // One write per batch: the verdicts of a whole launch, not a file write per asset.
            _cache.Save();
        }

        return new DownloadSummary(total, total - failed, failed, errors.ToArray());
    }

    public async Task<bool> EnsureFileAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        if (IsValid(item))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(item.DestinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Another build already has this very file: a link to it instead of a download.
        if (SharedFiles is not null && !string.IsNullOrEmpty(item.Sha1) &&
            SharedFiles.TryLinkInto(item.Sha1, item.DestinationPath, path => VerifyHash(path, item)))
        {
            _logger?.LogInformation("Linked {Path} from the shared store instead of downloading.", item.DestinationPath);
            RememberVerified(item);
            return true;
        }

        // Unique per attempt: two items in the same batch can share a destination, and a
        // fixed ".part" name makes them fight over the same file.
        var tempPath = $"{item.DestinationPath}.{Guid.NewGuid():N}.part";

        await FetchVerifiedAsync(
            item,
            tempPath,
            () => File.Move(tempPath, item.DestinationPath, overwrite: true),
            cancellationToken).ConfigureAwait(false);

        // The SHA-1 is taken on trust only when it is the hash just checked.
        SharedFiles?.Adopt(
            item.DestinationPath,
            string.Equals(ExpectedHash(item), item.Sha1, StringComparison.Ordinal) ? item.Sha1 : null);

        RememberVerified(item);
        return true;
    }

    /// <summary>
    /// Brings the file to <paramref name="stagingPath"/> and nowhere else: verified, and
    /// not yet in the build. For a replacement that must not show until it is certain -
    /// <see cref="DownloadItem.DestinationPath"/> says where the file is headed (which is
    /// what decides whether the shared store may give a link), and is not touched.
    /// </summary>
    /// <exception cref="DownloadFailedException">The file could not be fetched or is not the promised one.</exception>
    public async Task StageAsync(DownloadItem item, string stagingPath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(stagingPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (SharedFiles is not null && !string.IsNullOrEmpty(item.Sha1) &&
            SharedFiles.TryLinkStaged(item.Sha1, stagingPath, item.DestinationPath, path => VerifyHash(path, item)))
        {
            _logger?.LogInformation("Linked {Path} from the shared store instead of downloading.", stagingPath);
            return;
        }

        await FetchVerifiedAsync(item, stagingPath, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the file has the digest the item names (or the item names none).</summary>
    public static bool Matches(string path, DownloadItem item) => VerifyHash(path, item);

    /// <summary>
    /// Downloads to <paramref name="tempPath"/>, checks the digest and lets the caller put
    /// the file in place - all inside one attempt, so a file an antivirus holds for a
    /// moment after the write is fetched again rather than given up on.
    /// </summary>
    private async Task FetchVerifiedAsync(DownloadItem item, string tempPath, Action? place, CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;

            try
            {
                await DownloadToFileAsync(item.Url, tempPath, cancellationToken).ConfigureAwait(false);

                if (!VerifyHash(tempPath, item))
                {
                    throw new HashMismatchException("Hash mismatch");
                }

                place?.Invoke();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);

                if (attempt < _maxAttempts && IsWorthRetrying(ex))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (IsNetworkFailure(ex))
                {
                    // Cause first, address last: see DownloadFailedException.
                    throw new DownloadFailedException(item.Url, NetworkFailures.Classify(ex), ex);
                }

                throw;
            }
        }
    }

    /// <summary>The same request gets the same answer: a refused redirect, or a "no" that is about the address.</summary>
    private static bool IsWorthRetrying(Exception exception)
        => exception switch
        {
            RedirectRefusedException => false,
            HttpRequestException { StatusCode: { } status } => (int)status is < 400 or >= 500 or 408 or 429,
            _ => true
        };

    /// <summary>
    /// Whether the failure came from the far side. A full disk or a locked folder is told
    /// as it is; calling that "the connection was cut" would send the player to the router.
    /// </summary>
    private static bool IsNetworkFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException
                or HashMismatchException
                or System.Net.Sockets.SocketException
                or System.Security.Authentication.AuthenticationException
                or TimeoutException
                or OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How long a download may bring nothing before it is given up. The client's own
    /// timeout ends with the headers; a body whose route died under it (a VPN switched
    /// off mid-file) would otherwise wait for bytes for ever.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    private async Task DownloadToFileAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        EnsureLanded(url, response);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new byte[81920];

        while (true)
        {
            int read;
            stall.CancelAfter(StallTimeout);

            try
            {
                read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No data for {StallTimeout.TotalSeconds:0} seconds.");
            }
            catch (IOException ex)
            {
                // Marked as the network's: the file side throws IOException too.
                throw new HttpRequestException("The download was interrupted.", ex);
            }

            if (read == 0)
            {
                break;
            }

            // Never under the stall clock: a slow disk is not a silent server.
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The answer must be the file, reached over https if it was asked for over https.
    /// The handler follows redirects itself, refuses the ones that leave https and stops
    /// after <see cref="LauncherHttp.MaxRedirects"/>; in both cases it hands back the
    /// redirect it did not follow, and that is named here instead of "302".
    /// </summary>
    private static void EnsureLanded(string url, HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;

        if (status is >= 300 and < 400 && response.Headers.Location is { } location)
        {
            var target = location.IsAbsoluteUri ? location.Scheme + "://" + location.Host : location.OriginalString;
            throw new RedirectRefusedException($"redirect to {target} is not followed: not https, or too many in a row");
        }

        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            response.RequestMessage?.RequestUri is { IsAbsoluteUri: true } landed &&
            !string.Equals(landed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new RedirectRefusedException($"redirect to {landed.Scheme}://{landed.Host} left https");
        }

        if (!response.IsSuccessStatusCode)
        {
            var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase) ? response.StatusCode.ToString() : response.ReasonPhrase;
            throw new HttpRequestException($"{status} {reason}", null, response.StatusCode);
        }
    }

    private bool IsValid(DownloadItem item)
    {
        var info = new FileInfo(item.DestinationPath);

        if (!info.Exists)
        {
            _cache.Forget(item.DestinationPath);
            return false;
        }

        var expected = ExpectedHash(item);

        if (expected is null)
        {
            // Nothing better to go by than the size.
            if (item.Size > 0 && info.Length != item.Size)
            {
                _cache.Forget(item.DestinationPath);
                return false;
            }

            return true;
        }

        // With a hash the stated size decides nothing. Catalogs do publish a wrong size
        // beside a right hash, and trusting the size made such a file "invalid" on every
        // launch: downloaded again, verified by its hash, and thrown out again next time.

        // Verified before, untouched since: the hash is known to match.
        if (_cache.IsVerified(item.DestinationPath, expected, info))
        {
            return true;
        }

        if (!VerifyHash(item.DestinationPath, item))
        {
            _cache.Forget(item.DestinationPath);
            return false;
        }

        _cache.Remember(item.DestinationPath, expected, info);
        return true;
    }

    private void RememberVerified(DownloadItem item)
    {
        var expected = ExpectedHash(item);

        if (expected is not null)
        {
            _cache.Remember(item.DestinationPath, expected, new FileInfo(item.DestinationPath));
        }
    }

    /// <summary>The strongest hash the manifest gives, which is the one VerifyHash checks.</summary>
    private static string? ExpectedHash(DownloadItem item)
        => !string.IsNullOrEmpty(item.Sha512) ? item.Sha512
            : !string.IsNullOrEmpty(item.Sha256) ? item.Sha256
            : !string.IsNullOrEmpty(item.Sha1) ? item.Sha1
            : null;

    private static bool VerifyHash(string path, DownloadItem item)
    {
        using var stream = File.OpenRead(path);

        if (!string.IsNullOrEmpty(item.Sha512))
        {
            var sha512 = Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
            return string.Equals(sha512, item.Sha512, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(item.Sha256))
        {
            var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(sha256, item.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(item.Sha1))
        {
            var sha1 = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
            return string.Equals(sha1, item.Sha1, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}

public sealed record DownloadSummary(
    int Total,
    int Succeeded,
    int Failed,
    IReadOnlyList<(DownloadItem Item, Exception Error)> Errors);