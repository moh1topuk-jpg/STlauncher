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

namespace STlauncher.Core.Http;

public sealed class DownloadClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DownloadClient>? _logger;
    private readonly int _maxAttempts;

    public DownloadClient(HttpClient http, ILogger<DownloadClient>? logger = null, int maxAttempts = 3)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
        _maxAttempts = Math.Max(1, maxAttempts);
    }

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

        await Task.WhenAll(tasks).ConfigureAwait(false);

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

        // Unique per attempt: two items in the same batch can share a destination, and a
        // fixed ".part" name makes them fight over the same file.
        var tempPath = $"{item.DestinationPath}.{Guid.NewGuid():N}.part";
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                await DownloadToFileAsync(item.Url, tempPath, cancellationToken).ConfigureAwait(false);

                if (!VerifyHash(tempPath, item))
                {
                    throw new IOException($"Hash mismatch for {item.Url}");
                }

                File.Move(tempPath, item.DestinationPath, overwrite: true);
                return true;
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception) when (attempt < _maxAttempts)
            {
                TryDelete(tempPath);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
    }

    private async Task DownloadToFileAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(target, 81920, cancellationToken).ConfigureAwait(false);
    }

    private bool IsValid(DownloadItem item)
    {
        if (!File.Exists(item.DestinationPath))
        {
            return false;
        }

        if (item.Size > 0 && new FileInfo(item.DestinationPath).Length != item.Size)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(item.Sha1) || !string.IsNullOrEmpty(item.Sha256) ||
            !string.IsNullOrEmpty(item.Sha512))
        {
            return VerifyHash(item.DestinationPath, item);
        }

        return true;
    }

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