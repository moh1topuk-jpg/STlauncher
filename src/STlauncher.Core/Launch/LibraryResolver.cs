using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using STlauncher.Core.Http;
using STlauncher.Core.Metadata;

namespace STlauncher.Core.Launch;

public sealed record NativeExtraction(
    string ArchivePath,
    string DestinationDirectory,
    IReadOnlyList<string> Excludes);

public sealed class ResolvedLibraries
{
    public List<DownloadItem> Downloads { get; } = new();

    public List<NativeExtraction> Natives { get; } = new();

    public List<string> Classpath { get; } = new();
}

public static class LibraryResolver
{
    public static ResolvedLibraries Resolve(ResolvedVersion version, LauncherPaths paths, RuleContext context)
    {
        var result = new ResolvedLibraries();
        var nativesDirectory = Path.Combine(paths.Versions, version.Id, "natives");

        foreach (var library in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, context))
            {
                continue;
            }

            if (library.Natives is not null)
            {
                ResolveNative(library, paths, nativesDirectory, context, result);
                continue;
            }

            var artifact = library.Downloads?.Artifact;

            if (artifact?.Url is not null && artifact.Path is not null)
            {
                var destination = paths.LibraryPath(artifact.Path);
                result.Downloads.Add(new DownloadItem(artifact.Url, destination, artifact.Sha1, artifact.Size));
                result.Classpath.Add(destination);
                continue;
            }

            if (artifact is not null && artifact.Url is null)
            {
                continue;
            }

            if (library.Url is not null)
            {
                var relative = MavenPath(library.Name);
                if (relative is null)
                {
                    continue;
                }

                var fileName = Path.GetFileName(relative);
                var destination = paths.LibraryPath(relative);
                var url = library.Url.TrimEnd('/') + "/" + relative;
                result.Downloads.Add(new DownloadItem(url, destination));
                result.Classpath.Add(destination);
            }
        }

        return result;
    }

    private static void ResolveNative(
        Library library,
        LauncherPaths paths,
        string nativesDirectory,
        RuleContext context,
        ResolvedLibraries result)
    {
        if (library.Natives is null ||
            !library.Natives.TryGetValue(context.OsName, out var classifier) ||
            string.IsNullOrEmpty(classifier))
        {
            return;
        }

        if (library.Downloads?.Classifiers is null ||
            !library.Downloads.Classifiers.TryGetValue(classifier, out var artifact) ||
            artifact.Url is null ||
            artifact.Path is null)
        {
            return;
        }

        var destination = paths.LibraryPath(artifact.Path);
        result.Downloads.Add(new DownloadItem(artifact.Url, destination, artifact.Sha1, artifact.Size));

        result.Natives.Add(new NativeExtraction(
            destination,
            nativesDirectory,
            library.Extract?.Exclude ?? new List<string>()));
    }

    public static string? MavenPath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3)
        {
            return null;
        }

        var group = parts[0].Replace('.', '/');
        var artifactId = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? parts[3] : null;

        var fileName = classifier is null
            ? $"{artifactId}-{version}.jar"
            : $"{artifactId}-{version}-{classifier}.jar";

        var segments = new List<string> { group, artifactId, version, fileName };
        return string.Join('/', segments);
    }
}