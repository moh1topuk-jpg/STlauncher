using System.Collections.Generic;

namespace STlauncher.Core.Metadata;

public sealed class ResolvedVersion
{
    public string Id { get; set; } = string.Empty;

    public string? InheritsFrom { get; set; }

    public string? Type { get; set; }

    public string MainClass { get; set; } = string.Empty;

    public string? Assets { get; set; }

    public AssetIndexRef? AssetIndex { get; set; }

    public VersionDownloads? Downloads { get; set; }

    public string ClientVersionId { get; set; } = string.Empty;

    public JavaVersionRef? JavaVersion { get; set; }

    public LoggingConfig? Logging { get; set; }

    public int ComplianceLevel { get; set; }

    public List<Library> Libraries { get; set; } = new();

    public List<GameArgument> GameArguments { get; set; } = new();

    public List<GameArgument> JvmArguments { get; set; } = new();

    public string? MinecraftArguments { get; set; }

    /// <summary>
    /// Java major version the profile asks for, falling back to 8. A profile that states
    /// nothing - or states something unreadable, which a tolerant parse turns into 0 -
    /// must not send the launcher looking for "Java 0".
    /// </summary>
    public int RequiredJavaMajor => JavaVersion is { MajorVersion: > 0 } java ? java.MajorVersion : 8;

    public static ResolvedVersion FromLeaf(VersionJson json) => Merge(null, json);

    public static ResolvedVersion Merge(ResolvedVersion? parent, VersionJson child)
    {
        var result = new ResolvedVersion
        {
            Id = child.Id ?? parent?.Id ?? string.Empty,
            InheritsFrom = child.InheritsFrom,
            Type = child.Type ?? parent?.Type,
            MainClass = child.MainClass ?? parent?.MainClass ?? string.Empty,
            Assets = child.Assets ?? parent?.Assets,
            AssetIndex = child.AssetIndex ?? parent?.AssetIndex,
            Downloads = child.Downloads?.Client is not null ? child.Downloads : parent?.Downloads ?? child.Downloads,
            JavaVersion = child.JavaVersion ?? parent?.JavaVersion,
            Logging = child.Logging ?? parent?.Logging,
            ComplianceLevel = child.ComplianceLevel != 0 ? child.ComplianceLevel : parent?.ComplianceLevel ?? 0,
            MinecraftArguments = child.MinecraftArguments ?? parent?.MinecraftArguments
        };

        result.ClientVersionId = child.Downloads?.Client is not null
            ? child.Id ?? parent?.ClientVersionId ?? string.Empty
            : parent?.ClientVersionId ?? child.Id ?? string.Empty;

        var libraries = new Dictionary<string, Library>();

        if (parent is not null)
        {
            foreach (var library in parent.Libraries)
            {
                libraries[LibraryKey(library.Name)] = library;
            }
        }

        foreach (var library in child.Libraries)
        {
            libraries[LibraryKey(library.Name)] = library;
        }

        result.Libraries.AddRange(libraries.Values);

        if (parent is not null)
        {
            result.GameArguments.AddRange(parent.GameArguments);
            result.JvmArguments.AddRange(parent.JvmArguments);
        }

        if (child.Arguments is not null)
        {
            result.GameArguments.AddRange(child.Arguments.Game);
            result.JvmArguments.AddRange(child.Arguments.Jvm);
        }

        return result;
    }

    private static string LibraryKey(string name)
    {
        var parts = name.Split(':');

        if (parts.Length < 2)
        {
            return name;
        }

        return parts.Length > 3
            ? $"{parts[0]}:{parts[1]}:{parts[3]}"
            : $"{parts[0]}:{parts[1]}";
    }
}