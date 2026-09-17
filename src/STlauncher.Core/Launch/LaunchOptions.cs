using System.Collections.Generic;
using STlauncher.Core.Auth;
using STlauncher.Core.Metadata;

namespace STlauncher.Core.Launch;

public sealed class LaunchOptions
{
    public required ResolvedVersion Version { get; init; }

    public required OfflineAccount Account { get; init; }

    public required string JavaPath { get; init; }

    public required string GameDirectory { get; init; }

    public required string AssetsDirectory { get; init; }

    public required string NativesDirectory { get; init; }

    public required string LibrariesDirectory { get; init; }

    public required IReadOnlyList<string> Classpath { get; init; }

    public int MaxMemoryMb { get; init; } = 2048;

    public int MinMemoryMb { get; init; } = 512;

    public int? Width { get; init; }

    public int? Height { get; init; }

    public string LauncherName { get; init; } = "STlauncher";

    public string LauncherVersion { get; init; } = "0.1.0";

    public IReadOnlyList<string> ExtraJvmArgs { get; init; } = new List<string>();

    public IReadOnlyDictionary<string, bool> Features { get; init; } = new Dictionary<string, bool>();

    public string? ServerAddress { get; init; }
}

public sealed record LaunchCommand(string FileName, IReadOnlyList<string> Arguments);