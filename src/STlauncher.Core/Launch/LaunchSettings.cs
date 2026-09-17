using System.Collections.Generic;

namespace STlauncher.Core.Launch;

public sealed class LaunchSettings
{
    public required string GameDirectory { get; init; }

    public int MaxMemoryMb { get; init; } = 2048;

    public int MinMemoryMb { get; init; } = 512;

    public int? Width { get; init; }

    public int? Height { get; init; }

    public IReadOnlyList<string> ExtraJvmArgs { get; init; } = new List<string>();

    public string? ServerAddress { get; init; }

    public string? ServerListName { get; init; }

    public string? ServerListAddress { get; init; }
}