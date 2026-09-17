using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using STlauncher.Core.Http;

namespace STlauncher.Core.Java;

public sealed partial class JavaManager
{
    private readonly LauncherPaths _paths;
    private readonly DownloadClient _downloader;
    private readonly HttpClient _http;
    private readonly ILogger<JavaManager>? _logger;

    public JavaManager(
        LauncherPaths paths,
        DownloadClient downloader,
        HttpClient http,
        ILogger<JavaManager>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
    }

    public string RuntimeDirectory(int majorVersion) => Path.Combine(_paths.Runtime, $"java-{majorVersion}");

    [GeneratedRegex(@"version\s+""(?:1\.)?(\d+)")]
    private static partial Regex VersionRegex();

    public async Task<string> EnsureJavaAsync(int majorVersion, CancellationToken cancellationToken = default)
    {
        var cached = FindJavaExecutable(RuntimeDirectory(majorVersion));
        if (cached is not null)
        {
            _logger?.LogInformation("Using bundled Java {Major} at {Path}", majorVersion, cached);
            return cached;
        }

        var installed = DiscoverInstalled()
            .FirstOrDefault(j => j.MajorVersion == majorVersion);

        if (installed is not null)
        {
            _logger?.LogInformation("Using installed Java {Major} at {Path}", majorVersion, installed.ExecutablePath);
            return installed.ExecutablePath;
        }

        _logger?.LogInformation("Downloading Java {Major} runtime.", majorVersion);
        return await DownloadRuntimeAsync(majorVersion, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<JavaInstallation> DiscoverInstalled()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));
        }

        foreach (var root in KnownVendorRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var exe in SafeEnumerate(root, "java.exe", 4))
            {
                candidates.Add(exe);
            }
        }

        var result = new List<JavaInstallation>();

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var major = TryGetMajorVersion(candidate);
            if (major is null)
            {
                continue;
            }

            result.Add(new JavaInstallation(candidate, major.Value, Directory.GetParent(candidate)?.Parent?.Name));
        }

        return result.OrderByDescending(j => j.MajorVersion).ToList();
    }

    public static int? TryGetMajorVersion(string javaExecutable)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(javaExecutable, "-version")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // Both streams must be drained concurrently. Reading one to the end first can
            // deadlock: the child fills the other pipe and blocks while we wait for a read
            // that will never finish.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(10000))
            {
                // The wrapper is disposable but the child is not: without this a hung
                // java.exe stays alive for the rest of the session.
                TryKill(process);
                return null;
            }

            // Lets the async readers observe EOF before we read their results.
            process.WaitForExit();

            var output = stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult();

            var match = VersionRegex().Match(output);
            return match.Success ? int.Parse(match.Groups[1].Value) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or we lack the rights - nothing useful to do either way.
        }
    }

    public async Task<string> DownloadRuntimeAsync(int majorVersion, CancellationToken cancellationToken = default)
    {
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
        var os = RuleContextOs();
        var url =
            $"https://api.adoptium.net/v3/assets/latest/{majorVersion}/hotspot" +
            $"?architecture={architecture}&image_type=jre&os={os}&vendor=eclipse";

        // Shared, DI-provided client: creating one per call exhausts sockets and drops the
        // configured User-Agent and timeout.
        var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var assets = JsonSerializer.Deserialize<List<AdoptiumAsset>>(json) ?? new List<AdoptiumAsset>();

        var package = assets
            .Select(a => a.Binary?.Package)
            .FirstOrDefault(p => p?.Link is not null)
            ?? throw new InvalidOperationException($"No Java {majorVersion} runtime available for {os}/{architecture}.");

        var directory = RuntimeDirectory(majorVersion);
        Directory.CreateDirectory(directory);

        var archive = Path.Combine(directory, package.Name ?? $"java-{majorVersion}.zip");
        await _downloader.EnsureFileAsync(
                new DownloadItem(package.Link!, archive, Size: package.Size, Sha256: package.Checksum),
                cancellationToken)
            .ConfigureAwait(false);

        ExtractArchive(archive, directory);

        try
        {
            // The zip is a large one-off; leaving it inside the runtime directory keeps
            // hundreds of megabytes around for no reason.
            File.Delete(archive);
        }
        catch (IOException)
        {
            // Harmless if it is still locked.
        }

        var executable = FindJavaExecutable(directory)
                         ?? throw new InvalidOperationException("Java executable was not found after extraction.");

        _logger?.LogInformation("Java {Major} installed at {Path}", majorVersion, executable);
        return executable;
    }

    private static void ExtractArchive(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);

        var root = Path.GetFullPath(destination);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        var prefix = DetectTopLevelDirectory(archive);

        foreach (var entry in archive.Entries)
        {
            var relative = prefix is not null && entry.FullName.StartsWith(prefix, StringComparison.Ordinal)
                ? entry.FullName[prefix.Length..]
                : entry.FullName;

            if (string.IsNullOrEmpty(relative))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destination, relative));

            // The separator matters: without it "...\java-8" also matches "...\java-8-evil",
            // so a crafted archive entry could land outside the runtime directory.
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Blocked archive entry outside of destination: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static string? DetectTopLevelDirectory(ZipArchive archive)
    {
        string? prefix = null;

        foreach (var entry in archive.Entries)
        {
            var separator = entry.FullName.IndexOf('/');
            if (separator < 0)
            {
                return null;
            }

            var current = entry.FullName[..(separator + 1)];
            if (prefix is null)
            {
                prefix = current;
            }
            else if (!string.Equals(prefix, current, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return prefix;
    }

    public static string? FindJavaExecutable(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "java.exe" }
            : new[] { "java" };

        return SafeEnumerate(directory, names, 4).FirstOrDefault();
    }

    private static IEnumerable<string> SafeEnumerate(string root, string fileName, int maxDepth)
        => SafeEnumerate(root, new[] { fileName }, maxDepth);

    private static IEnumerable<string> SafeEnumerate(string root, string[] fileNames, int maxDepth)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            string[] entries;
            try
            {
                entries = Directory.GetDirectories(current);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in fileNames)
            {
                var candidate = Path.Combine(current, file);
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var directory in entries)
            {
                queue.Enqueue((directory, depth + 1));
            }
        }
    }

    private IEnumerable<string> KnownVendorRoots()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(programFiles, "Eclipse Adoptium");
        yield return Path.Combine(programFiles, "Java");
        yield return Path.Combine(programFiles, "Microsoft");
        yield return Path.Combine(programFiles, "Zulu");
        yield return Path.Combine(programFiles, "BellSoft");
        yield return Path.Combine(programFiles, "Amazon Corretto");
        yield return Path.Combine(programFiles, "Semeru");
        yield return Path.Combine(localAppData, "Programs", "Eclipse Adoptium");
    }

    private static string RuleContextOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "mac";
        return "linux";
    }

    private sealed class AdoptiumAsset
    {
        [JsonPropertyName("binary")]
        public AdoptiumBinary? Binary { get; set; }
    }

    private sealed class AdoptiumBinary
    {
        [JsonPropertyName("package")]
        public AdoptiumPackage? Package { get; set; }
    }

    private sealed class AdoptiumPackage
    {
        [JsonPropertyName("checksum")]
        public string? Checksum { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("link")]
        public string? Link { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}