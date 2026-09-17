using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace STlauncher.Core.Metadata;

public sealed class RuleContext
{
    public required string OsName { get; init; }

    public string? OsVersion { get; init; }

    public required string Arch { get; init; }

    public IReadOnlyDictionary<string, bool> Features { get; init; }
        = new Dictionary<string, bool>();

    public static RuleContext Current(IReadOnlyDictionary<string, bool>? features = null)
        => new()
        {
            OsName = DetectOsName(),
            OsVersion = Environment.OSVersion.Version.ToString(),
            Arch = DetectArch(),
            Features = features ?? new Dictionary<string, bool>()
        };

    public static string DetectOsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";

        throw new PlatformNotSupportedException("Unsupported operating system.");
    }

    public static string DetectArch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => "x86_64"
    };
}