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

    /// <summary>Extra arguments appended after the standard Minecraft arguments.</summary>
    public IReadOnlyList<string> ExtraGameArgs { get; init; } = new List<string>();

    /// <summary>Explicit Java executable. Empty means "detect or download automatically".</summary>
    public string? JavaPath { get; init; }

    /// <summary>Re-download the client jar, natives and the vanilla version JSON.</summary>
    public bool ForceUpdate { get; init; }

    public string? ServerAddress { get; init; }

    public string? ServerListName { get; init; }

    public string? ServerListAddress { get; init; }

    /// <summary>Minecraft language code, e.g. "ru_ru". Applied on the first launch.</summary>
    public string LanguageCode { get; init; } = "ru_ru";
}