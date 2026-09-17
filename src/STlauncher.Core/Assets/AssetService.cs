using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using STlauncher.Core.Metadata;

namespace STlauncher.Core.Assets;

public sealed class AssetService
{
    public const string ResourceBaseUrl = "https://resources.download.minecraft.net";

    private readonly MetadataClient _metadata;
    private readonly LauncherPaths _paths;
    private readonly DownloadClient _downloader;

    public AssetService(MetadataClient metadata, LauncherPaths paths, DownloadClient downloader)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
    }

    public string IndexPath(string indexId) => Path.Combine(_paths.AssetIndexes, indexId + ".json");

    public async Task<AssetIndex> GetIndexAsync(AssetIndexRef reference, CancellationToken cancellationToken = default)
    {
        var path = IndexPath(reference.Id);

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(_paths.AssetIndexes);
            await _downloader.EnsureFileAsync(
                    new DownloadItem(reference.Url, path, reference.Sha1, reference.Size), cancellationToken)
                .ConfigureAwait(false);
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AssetIndex>(json, MetadataJson.Options)
               ?? throw new InvalidDataException("Asset index is empty.");
    }

    public IReadOnlyList<DownloadItem> BuildDownloadPlan(AssetIndex index)
    {
        var items = new List<DownloadItem>(index.Objects.Count);

        foreach (var (name, obj) in index.Objects)
        {
            _ = name;
            var prefix = obj.Hash.Length >= 2 ? obj.Hash[..2] : obj.Hash;
            var destination = Path.Combine(_paths.AssetObjects, prefix, obj.Hash);
            var url = $"{ResourceBaseUrl}/{prefix}/{obj.Hash}";
            items.Add(new DownloadItem(url, destination, obj.Hash, obj.Size));
        }

        return items;
    }

    public bool IsVirtual(string? assetsId)
        => assetsId is "legacy" or "pre-1.6";

    public string VirtualDirectory(string assetsId) => Path.Combine(_paths.Assets, "virtual", assetsId);

    public async Task BuildVirtualAssetsAsync(
        AssetIndex index,
        string assetsId,
        CancellationToken cancellationToken = default)
    {
        var virtualDir = VirtualDirectory(assetsId);
        Directory.CreateDirectory(virtualDir);

        foreach (var (name, obj) in index.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var prefix = obj.Hash.Length >= 2 ? obj.Hash[..2] : obj.Hash;
            var source = Path.Combine(_paths.AssetObjects, prefix, obj.Hash);
            var target = Path.Combine(virtualDir, name.Replace('/', Path.DirectorySeparatorChar));

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(target))
            {
                File.Copy(source, target);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}