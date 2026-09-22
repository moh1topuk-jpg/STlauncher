using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Launch;

/// <summary>What the log says went wrong, reduced to something a player can act on.</summary>
public enum CrashCause
{
    /// <summary>Nothing recognisable in the log.</summary>
    Unknown,

    /// <summary>A mod needs another mod that is not in the build.</summary>
    MissingDependency,

    /// <summary>A mod is built for a different Minecraft or loader version.</summary>
    ModForOtherVersion,

    /// <summary>Two mods refuse to run together.</summary>
    IncompatibleMods,

    /// <summary>The same mod is in the folder twice.</summary>
    DuplicateMod,

    /// <summary>A mod's patches failed to apply - nearly always a mod for another version.</summary>
    MixinFailure,

    OutOfMemory,

    /// <summary>The Java that ran the game is older than the game needs.</summary>
    JavaTooOld,

    /// <summary>The graphics driver could not give the game a window.</summary>
    Graphics,

    /// <summary>A game file is missing or damaged.</summary>
    BrokenInstallation,

    DiskFull
}

/// <param name="Subject">The mod, file or number the message is about, when there is one.</param>
/// <param name="Detail">A second name: the mod that is missing, the Java version needed.</param>
/// <param name="Evidence">The log line the verdict rests on, for the report.</param>
public sealed record CrashDiagnosis(
    CrashCause Cause,
    string? Subject = null,
    string? Detail = null,
    string? Evidence = null)
{
    public static readonly CrashDiagnosis None = new(CrashCause.Unknown);
}

/// <summary>
/// Reads a game log and names the cause in plain terms. The log says "Mod 'Sodium Extra'
/// requires version 0.8 or later of 'sodium', which is missing" in the middle of two
/// thousand lines; the player needs "Sodium Extra needs Sodium", and a button.
/// </summary>
public static class CrashAnalyzer
{
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Fabric and Quilt: "Mod 'A' (a) 1.0 requires version 2.0 or later of 'b' (b), which is missing!"
    // and the older "requires any version of fabric-api, which is missing!".
    private static readonly Regex FabricRequires = new(
        @"Mod '(?<mod>.+?)' \((?<modId>[^)]+)\)[^\n]*? requires [^\n]*? of (?:mod )?'?(?<dep>[A-Za-z0-9_\-\.]+)'?[^\n]*?which is missing",
        Options);

    private static readonly Regex FabricIncompatible = new(
        @"Mod '(?<mod>.+?)' \((?<modId>[^)]+)\)[^\n]* is incompatible with[^\n]*'(?<dep>[^']+)'",
        Options);

    private static readonly Regex FabricReplace = new(
        @"Replace mod '(?<mod>.+?)' \((?<modId>[^)]+)\)[^\n]* with version",
        Options);

    // Forge and NeoForge: "Mod ID: 'b', Requested by: 'a', Expected range: '[1,)', Actual version: '[MISSING]'"
    private static readonly Regex ForgeDependency = new(
        @"Mod ID: '(?<dep>[^']+)', Requested by: '(?<mod>[^']+)'(?:, Expected range: '(?<range>[^']*)')?(?:, Actual version: '(?<actual>[^']*)')?",
        Options);

    private static readonly Regex DuplicateMod = new(
        @"[Dd]uplicate mod(?:s)?(?: id)?[:!]?\s*[""']?(?<modId>[a-z0-9_\-\.]+)?",
        Options);

    private static readonly Regex MixinForMod = new(
        @"Mixin apply for mod (?<modId>[a-z0-9_\-\.]+) failed|from mod (?<modId2>[a-z0-9_\-\.]+)\]",
        Options | RegexOptions.IgnoreCase);

    private static readonly Regex ClassFileVersion = new(
        @"class file version (?<version>\d+)\.0",
        Options);

    private static readonly Regex RequiresJava = new(
        @"requires (?:Java|JRE|JDK)\s*(?<version>\d+)",
        Options | RegexOptions.IgnoreCase);

    private static readonly Regex GraphicsFrame = new(
        @"\[(ig\d+icd|igd|nvoglv|nvd3dum|atio|amdvlk|atiu|vulkan-1)[^\]]*\]",
        Options | RegexOptions.IgnoreCase);

    private static readonly Regex CrashReportLine = new(
        @"Crash report saved to:\s*(?:#@!@#\s*)?(?<path>[^\r\n]+\.txt)",
        Options);

    private static readonly Regex MissingFile = new(
        @"(?:Unable to access jarfile|Could not find or load main class|NoClassDefFoundError: net/minecraft|ClassNotFoundException: net\.(?:minecraft|fabricmc|neoforged|minecraftforge)|java\.util\.zip\.ZipException|Error opening zip file|Invalid or corrupt jarfile)\s*:?\s*(?<subject>[^\r\n]*)",
        Options);

    /// <summary>Path of the crash report the game wrote, if the log mentions one.</summary>
    public static string? FindCrashReportPath(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = CrashReportLine.Match(line);

            if (match.Success)
            {
                return match.Groups["path"].Value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Names the cause. Several things usually go wrong at once - a missing library
    /// makes another mod fail its mixins and then the game reports an exception - so
    /// the verdict is the most specific finding, not the first.
    /// </summary>
    public static CrashDiagnosis Analyze(IEnumerable<string> lines)
    {
        if (lines is null)
        {
            throw new ArgumentNullException(nameof(lines));
        }

        var findings = new List<CrashDiagnosis>();

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var line = raw.Length > 4000 ? raw[..4000] : raw;

            Inspect(line, findings);

            // Enough to decide: everything that names the cause sits near the failure,
            // and a modded log runs to thousands of lines after it.
            if (findings.Count >= 64)
            {
                break;
            }
        }

        return findings.Count == 0
            ? CrashDiagnosis.None
            : findings
                .OrderBy(f => Priority(f.Cause))
                // Of two findings of one kind, the one that names a mod is the useful one.
                .ThenBy(f => f.Subject is null ? 1 : 0)
                .First();
    }

    private static void Inspect(string line, List<CrashDiagnosis> findings)
    {
        var fabric = FabricRequires.Match(line);

        if (fabric.Success)
        {
            var dep = fabric.Groups["dep"].Value.Trim();
            var mod = fabric.Groups["mod"].Value;

            findings.Add(IsPlatform(dep)
                ? new CrashDiagnosis(CrashCause.ModForOtherVersion, mod, dep, line.Trim())
                : new CrashDiagnosis(CrashCause.MissingDependency, mod, dep, line.Trim()));
            return;
        }

        var incompatible = FabricIncompatible.Match(line);

        if (incompatible.Success)
        {
            findings.Add(new CrashDiagnosis(
                CrashCause.IncompatibleMods,
                incompatible.Groups["mod"].Value,
                incompatible.Groups["dep"].Value,
                line.Trim()));
            return;
        }

        var replace = FabricReplace.Match(line);

        if (replace.Success)
        {
            findings.Add(new CrashDiagnosis(CrashCause.ModForOtherVersion, replace.Groups["mod"].Value, null, line.Trim()));
            return;
        }

        var forge = ForgeDependency.Match(line);

        if (forge.Success)
        {
            var dep = forge.Groups["dep"].Value;
            var mod = forge.Groups["mod"].Value;
            var actual = forge.Groups["actual"].Value;
            var missing = string.IsNullOrEmpty(actual) || actual.Contains("MISSING", StringComparison.OrdinalIgnoreCase);

            findings.Add(IsPlatform(dep) || !missing
                ? new CrashDiagnosis(CrashCause.ModForOtherVersion, mod, dep, line.Trim())
                : new CrashDiagnosis(CrashCause.MissingDependency, mod, dep, line.Trim()));
            return;
        }

        if (line.Contains("OutOfMemoryError", StringComparison.Ordinal) ||
            line.Contains("Out of Memory Error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("GC overhead limit exceeded", StringComparison.Ordinal))
        {
            findings.Add(new CrashDiagnosis(CrashCause.OutOfMemory, null, null, line.Trim()));
            return;
        }

        if (line.Contains("UnsupportedClassVersionError", StringComparison.Ordinal))
        {
            // "compiled by a more recent version of the Java Runtime (class file version 65.0)":
            // the first number is what the game needs, and 65 - 44 = Java 21.
            var version = ClassFileVersion.Match(line);
            var needed = version.Success && int.TryParse(version.Groups["version"].Value, out var major) && major > 44
                ? (major - 44).ToString()
                : null;

            findings.Add(new CrashDiagnosis(CrashCause.JavaTooOld, null, needed, line.Trim()));
            return;
        }

        var requiresJava = RequiresJava.Match(line);

        if (requiresJava.Success && !line.Contains("mod", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new CrashDiagnosis(CrashCause.JavaTooOld, null, requiresJava.Groups["version"].Value, line.Trim()));
            return;
        }

        if (line.Contains("GLFW error 6554", StringComparison.Ordinal) ||
            line.Contains("does not appear to support OpenGL", StringComparison.Ordinal) ||
            line.Contains("Pixel format not accepted", StringComparison.Ordinal) ||
            line.Contains("Failed to create window", StringComparison.Ordinal) ||
            line.Contains("Failed to find a suitable pixel format", StringComparison.Ordinal) ||
            (line.TrimStart().StartsWith("# C", StringComparison.Ordinal) && GraphicsFrame.IsMatch(line)))
        {
            findings.Add(new CrashDiagnosis(CrashCause.Graphics, null, null, line.Trim()));
            return;
        }

        if (line.Contains("not enough space on the disk", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("No space left on device", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new CrashDiagnosis(CrashCause.DiskFull, null, null, line.Trim()));
            return;
        }

        if (line.Contains("uplicate mod", StringComparison.Ordinal))
        {
            var duplicate = DuplicateMod.Match(line);
            var id = duplicate.Success && duplicate.Groups["modId"].Success && duplicate.Groups["modId"].Value.Length > 0
                ? duplicate.Groups["modId"].Value
                : null;

            findings.Add(new CrashDiagnosis(CrashCause.DuplicateMod, id, null, line.Trim()));
            return;
        }

        if (line.Contains("MixinApplyError", StringComparison.Ordinal) ||
            line.Contains("MixinTransformerError", StringComparison.Ordinal) ||
            line.Contains("Mixin apply for mod", StringComparison.Ordinal))
        {
            var mixin = MixinForMod.Match(line);
            var id = mixin.Groups["modId"].Success && mixin.Groups["modId"].Value.Length > 0 ? mixin.Groups["modId"].Value
                : mixin.Groups["modId2"].Success && mixin.Groups["modId2"].Value.Length > 0 ? mixin.Groups["modId2"].Value
                : null;

            findings.Add(new CrashDiagnosis(CrashCause.MixinFailure, id, null, line.Trim()));
            return;
        }

        var missingFile = MissingFile.Match(line);

        if (missingFile.Success)
        {
            var subject = missingFile.Groups["subject"].Value.Trim();
            findings.Add(new CrashDiagnosis(CrashCause.BrokenInstallation, subject.Length > 0 ? subject : null, null, line.Trim()));
        }
    }

    /// <summary>Dependencies that are the game or the loader, not another mod.</summary>
    private static bool IsPlatform(string id) => id.ToLowerInvariant() switch
    {
        "minecraft" or "fabricloader" or "fabric-loader" or "quilt_loader" or "forge" or "neoforge" or "java" => true,
        _ => false
    };

    private static int Priority(CrashCause cause) => cause switch
    {
        CrashCause.MissingDependency => 0,
        CrashCause.ModForOtherVersion => 1,
        CrashCause.IncompatibleMods => 2,
        CrashCause.DuplicateMod => 3,
        CrashCause.JavaTooOld => 4,
        CrashCause.OutOfMemory => 5,
        CrashCause.DiskFull => 6,
        CrashCause.MixinFailure => 7,
        CrashCause.Graphics => 8,
        CrashCause.BrokenInstallation => 9,
        _ => 100
    };
}
