using System;
using System.IO;

namespace STlauncher.Core;

public sealed class LauncherPaths
{
    public LauncherPaths(string root)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));

        if (!Path.IsPathRooted(Root))
        {
            throw new ArgumentException("Launcher root must be an absolute path.", nameof(root));
        }

        Meta = Under("meta");
        Versions = Under("versions");
        Libraries = Under("libraries");
        Assets = Under("assets");
        AssetIndexes = Under("assets", "indexes");
        AssetObjects = Under("assets", "objects");
        Runtime = Under("runtime");
        Instances = Under("instances");
        Logs = Under("logs");
    }

    public string Root { get; }
    public string Meta { get; }
    public string Versions { get; }
    public string Libraries { get; }
    public string Assets { get; }
    public string AssetIndexes { get; }
    public string AssetObjects { get; }
    public string Runtime { get; }
    public string Instances { get; }
    public string Logs { get; }

    /// <summary>AppData, unless the player moved the data elsewhere (see <see cref="DataLocation"/>).</summary>
    public static LauncherPaths Default() => new(DataLocation.Resolve());

    public string VersionDirectory(string versionId) => Under("versions", versionId);

    public string VersionJsonPath(string versionId)
        => Under("versions", versionId, versionId + ".json");

    public string VersionJarPath(string versionId)
        => Under("versions", versionId, versionId + ".jar");

    public string LibraryPath(string relativePath)
        => Path.Combine(Libraries, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public string InstanceDirectory(string instanceId) => Under("instances", instanceId);

    public void EnsureCreated()
    {
        foreach (var dir in new[]
                 {
                     Root, Meta, Versions, Libraries, Assets,
                     AssetIndexes, AssetObjects, Runtime, Instances, Logs
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }

    private string Under(params string[] parts)
    {
        var result = Root;

        foreach (var part in parts)
        {
            result = Path.Combine(result, part);
        }

        return result;
    }
}