using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Metadata;

namespace STlauncher.Core.Versions;

public sealed class VersionService
{
    private readonly MetadataClient _metadata;
    private readonly LauncherPaths _paths;

    public VersionService(MetadataClient metadata, LauncherPaths paths)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public Task<VersionManifest> GetManifestAsync(CancellationToken cancellationToken = default)
        => _metadata.GetVersionManifestAsync(cancellationToken);

    public async Task<VersionSummary?> FindAsync(string versionId, CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);
        return manifest.Versions.FirstOrDefault(v => v.Id == versionId);
    }

    public async Task<string> EnsureVersionJsonAsync(string versionId, CancellationToken cancellationToken = default)
    {
        var path = _paths.VersionJsonPath(versionId);

        if (File.Exists(path))
        {
            return path;
        }

        var summary = await FindAsync(versionId, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"Version '{versionId}' was not found in the manifest.");

        return await _metadata.DownloadVersionJsonAsync(versionId, summary.Url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResolvedVersion> ResolveAsync(string versionId, CancellationToken cancellationToken = default)
    {
        var path = await EnsureVersionJsonAsync(versionId, cancellationToken).ConfigureAwait(false);
        var json = _metadata.ReadVersionJsonFile(path);

        if (string.IsNullOrEmpty(json.InheritsFrom))
        {
            return ResolvedVersion.FromLeaf(json);
        }

        var parent = await ResolveAsync(json.InheritsFrom, cancellationToken).ConfigureAwait(false);
        return ResolvedVersion.Merge(parent, json);
    }
}