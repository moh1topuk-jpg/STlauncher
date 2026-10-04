using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Java;

namespace STlauncher.Core.Hosting;

/// <summary>
/// The Java a server runs on. The same runtimes the game uses, found the same way; the
/// difference is that here finding and downloading are separate calls, so the screen can
/// say that a runtime is about to be downloaded, and how big it is, before it happens.
/// </summary>
public sealed class ServerJava
{
    private readonly JavaManager _java;
    private readonly HttpClient _http;

    public ServerJava(JavaManager java, HttpClient http)
    {
        _java = java ?? throw new ArgumentNullException(nameof(java));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// A Java of exactly this major version that is already on the machine - the
    /// launcher's own runtime first, then installed ones - or null. Never downloads.
    /// The first call may take a second: installed Javas are asked for their version.
    /// </summary>
    public string? Find(int majorVersion)
        => JavaManager.FindJavaExecutable(_java.RuntimeDirectory(majorVersion))
           ?? _java.DiscoverInstalled().FirstOrDefault(j => j.MajorVersion == majorVersion)?.ExecutablePath;

    /// <summary>
    /// What <see cref="EnsureAsync"/> would download when <see cref="Find"/> finds nothing:
    /// the address and the exact size. Null when Adoptium has no such runtime or cannot
    /// be reached.
    /// </summary>
    public async Task<ServerDownload?> DescribeDownloadAsync(int majorVersion, CancellationToken cancellationToken = default)
    {
        // The same question JavaManager.DownloadRuntimeAsync asks, so the answer shown
        // to the player is the file that will actually be fetched.
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "mac"
            : "linux";

        var url =
            $"https://api.adoptium.net/v3/assets/latest/{majorVersion}/hotspot" +
            $"?architecture={architecture}&image_type=jre&os={os}&vendor=eclipse";

        try
        {
            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var asset in document.RootElement.EnumerateArray())
            {
                if (asset.TryGetProperty("binary", out var binary) &&
                    binary.TryGetProperty("package", out var package) &&
                    package.TryGetProperty("link", out var link) &&
                    link.ValueKind == JsonValueKind.String)
                {
                    var size = package.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var bytes) ? bytes : 0;
                    var address = link.GetString()!;

                    return new ServerDownload(
                        ServerDownloadKind.JavaRuntime,
                        address,
                        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : address,
                        size,
                        SizeIsExact: size > 0);
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The path of a fitting Java, downloading the launcher's own runtime when there is
    /// none. Call only after the player has agreed to that download.
    /// </summary>
    public Task<string> EnsureAsync(int majorVersion, CancellationToken cancellationToken = default)
        => _java.EnsureJavaAsync(majorVersion, cancellationToken);
}
