using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using STlauncher.Core.Launch;

namespace STlauncher.Core.Import;

/// <summary>
/// Settings a build had in its old launcher that come along with it. Only what the
/// player set for that build on purpose: a launcher-wide default says nothing about
/// what this build needs.
/// </summary>
/// <param name="JvmArguments">Extra JVM arguments that passed <see cref="JvmArgumentAllowlist"/>.</param>
/// <param name="DroppedJvmArguments">The ones that did not, so the player can be told.</param>
public sealed record CarriedSettings(
    int? MaxMemoryMb,
    int? MinMemoryMb,
    int? Width,
    int? Height,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<string> DroppedJvmArguments)
{
    public bool HasMemory => MaxMemoryMb is not null;

    public bool HasWindow => Width is not null && Height is not null;

    /// <summary>True when there is anything to tell the player about.</summary>
    public bool HasAnything => HasMemory || HasWindow || JvmArguments.Count > 0 || DroppedJvmArguments.Count > 0;
}

/// <summary>
/// Reads the per-build overrides Prism, MultiMC and their forks keep in instance.cfg.
/// </summary>
/// <remarks>
/// Each group sits behind a switch of its own - OverrideMemory, OverrideWindow,
/// OverrideJavaArgs - and the values below a switched-off group are leftovers of the
/// launcher's defaults, so they are ignored. Everything read here is treated as
/// untrusted: numbers are range-checked and JVM arguments go through the allowlist,
/// because an instance folder is something people download and pass around.
/// </remarks>
public static class SourceInstanceSettings
{
    public const int MinAllowedMemoryMb = 512;
    public const int MaxAllowedMemoryMb = 65536;

    private const int MinWindowWidth = 320;
    private const int MaxWindowWidth = 7680;
    private const int MinWindowHeight = 240;
    private const int MaxWindowHeight = 4320;

    /// <summary>An instance.cfg is a few kilobytes; anything much larger is not one.</summary>
    private const long MaxConfigBytes = 256 * 1024;

    public static CarriedSettings? Read(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, "instance.cfg");

        if (!LinkGuard.IsRealFile(path))
        {
            return null;
        }

        Dictionary<string, string> values;

        try
        {
            if (new FileInfo(path).Length > MaxConfigBytes)
            {
                return null;
            }

            values = ParseIni(File.ReadLines(path));
        }
        catch (Exception)
        {
            return null;
        }

        return FromValues(values);
    }

    /// <summary>The same, from keys already read. Separate so the rules can be tested without files.</summary>
    public static CarriedSettings? FromValues(IReadOnlyDictionary<string, string> values)
    {
        int? maxMemory = null;
        int? minMemory = null;

        if (IsOn(values, "OverrideMemory") &&
            Number(values, "MaxMemAlloc") is { } max &&
            max is >= MinAllowedMemoryMb and <= MaxAllowedMemoryMb)
        {
            maxMemory = max;

            // The lower bound only makes sense together with the upper one, and never above it.
            if (Number(values, "MinMemAlloc") is { } min && min >= 128 && min <= max)
            {
                minMemory = min;
            }
        }

        int? width = null;
        int? height = null;

        if (IsOn(values, "OverrideWindow") && !IsOn(values, "LaunchMaximized") &&
            Number(values, "MinecraftWinWidth") is { } w && w is >= MinWindowWidth and <= MaxWindowWidth &&
            Number(values, "MinecraftWinHeight") is { } h && h is >= MinWindowHeight and <= MaxWindowHeight)
        {
            (width, height) = (w, h);
        }

        var arguments = JvmArgumentAllowlist.Result.Empty;

        if (IsOn(values, "OverrideJavaArgs") && values.TryGetValue("JvmArgs", out var raw))
        {
            arguments = JvmArgumentAllowlist.Filter(raw);
        }

        // The heap is set by the memory setting above (or the launcher's own), which the
        // player can see and change; a second -Xmx hidden among the arguments would
        // quietly win over it.
        var kept = arguments.Kept
            .Where(a => !a.StartsWith("-Xmx", StringComparison.Ordinal) && !a.StartsWith("-Xms", StringComparison.Ordinal))
            .ToList();

        var settings = new CarriedSettings(maxMemory, minMemory, width, height, kept, arguments.Dropped);

        return settings.HasAnything ? settings : null;
    }

    /// <summary>
    /// Keys of a Qt settings file, whatever section they are in: MultiMC wrote them with no
    /// section at all, Prism puts them under [General]. The first occurrence wins.
    /// </summary>
    public static Dictionary<string, string> ParseIni(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed[0] is '[' or ';' or '#')
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();

            // Qt quotes a value with spaces or special characters and escapes inside it.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            }

            result.TryAdd(key, value);
        }

        return result;
    }

    private static bool IsOn(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) &&
           (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1");

    private static int? Number(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) &&
           int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}
