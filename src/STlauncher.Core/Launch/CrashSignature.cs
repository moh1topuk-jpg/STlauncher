using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Launch;

/// <summary>A mod of the build as its jar names itself: the file and every id it answers to.</summary>
public sealed record CrashModInfo(string FileName, IReadOnlyList<string> Ids);

/// <summary>
/// The shape of a crash, safe to count: the class of the exception that ended the game
/// and the mod whose code it came from, or the kind of native failure and the kind of
/// library it happened in. "java.lang.NullPointerException @sodium",
/// "EXCEPTION_ACCESS_VIOLATION in nvidia-driver".
///
/// What it never carries: an exception's message (paths, nicknames and server addresses
/// live there), a stack line, a file name, or any text the game or a mod made up. Only
/// three kinds of token get through, each checked against a strict shape: a Java class
/// name in plain ASCII, a mod id the build's own jars declare (or a mixin names, in the
/// shape of an id), and a label from the fixed tables below.
/// </summary>
public static class CrashSignature
{
    /// <summary>The longest signature: what the collector keeps.</summary>
    public const int MaxLength = 120;

    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // A fully qualified class whose simple name says it is a throwable. Plain ASCII on
    // purpose: a Java identifier may be in any script, a class worth counting never is.
    private const string ClassPattern = @"(?:[A-Za-z_$][A-Za-z0-9_$]*\.)+[A-Za-z_$][A-Za-z0-9_$]*(?:Exception|Error|Throwable|Failure)";

    private static readonly Regex ExceptionHeader = new(
        @"^\s*(?:Caused by:\s*|Exception in thread ""[^""]*""\s+|\[[^\]]*\]\s*(?:\[[^\]]*\]\s*)?:?\s*)?(?<cls>" + ClassPattern + @")(?=$|:|\s)",
        Options);

    private static readonly Regex CausedBy = new(@"^\s*Caused by:\s*(?<cls>" + ClassPattern + @")(?=$|:|\s)", Options);

    private static readonly Regex StackFrame = new(@"^\s*at\s+(?<frame>\S+)", Options);

    // "#  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=..." and "#  SIGSEGV (0xb) at pc=...".
    private static readonly Regex JvmFatal = new(@"^#\s+(?<code>EXCEPTION_[A-Z_]+|SIG[A-Z]{3,6})\s+\(0x[0-9a-fA-F]+\)", Options);

    // "# C  [atio6axx.dll+0x1b5e10]", "# J 1234 c2 net.minecraft...", "# V  [jvm.dll+0x3a1b2c]".
    private static readonly Regex ProblematicFrame = new(@"^#\s+(?<kind>[CVJjAv])\s+(?:\[(?<module>[^\]+\s]+)[^\]]*\])?", Options);

    private static readonly Regex ModId = new(@"^[a-z][a-z0-9_\-]{1,63}$", Options);

    // Forge and NeoForge: "~[create-1.20.1-0.5.1.f.jar%23218!/:0.5.1.f]", "[mods/create.jar:?]".
    private static readonly Regex FrameJar = new(@"\[(?:[^\]\[/:]*/)?(?<jar>[^\]\[/:%!]+\.jar)", Options);

    // NeoForge and Forge modules: "at TRANSFORMER/create@6.0.0/com.simibubi...".
    private static readonly Regex FrameModule = new(@"^(?:TRANSFORMER|MC-BOOTSTRAP|SECURE-BOOTSTRAP|PLUGIN|GAME|LAYER\s+\w+)/(?<id>[a-z][a-z0-9_\-]{1,63})@", Options);

    // Mixin handler methods carry the owning mod: "handler$zza000$sodium$onRender".
    private static readonly Regex MixinHandler = new(@"\$[a-z0-9]{6}\$(?<id>[a-z][a-z0-9_]{1,63})\$", Options);

    // "... from mod sodium]", "Mixin apply for mod sodium failed".
    private static readonly Regex MixinOwner = new(@"(?:from mod |Mixin apply for mod )(?<id>[a-z][a-z0-9_\-]{1,63})\b", Options);

    // "Suspected Mods: Create (create), Flywheel (flywheel)".
    private static readonly Regex SuspectedMods = new(@"Suspected Mods?:\s*(?<list>.+)$", Options);
    private static readonly Regex Parenthesised = new(@"\((?<id>[a-z][a-z0-9_\-]{1,63})\)", Options);

    /// <summary>Exceptions that wrap the one that matters: the signature looks through them.</summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "java.lang.RuntimeException",
        "java.lang.Exception",
        "java.lang.Error",
        "java.lang.Throwable",
        "java.lang.reflect.InvocationTargetException",
        "java.lang.ExceptionInInitializerError",
        "java.util.concurrent.CompletionException",
        "java.util.concurrent.ExecutionException",
        "net.minecraft.ReportedException",
        "net.minecraft.CrashException",
        "net.minecraft.util.crash.CrashException",
        "net.minecraft.util.ReportedException",
        "net.minecraft.class_148",
    };

    /// <summary>Ids that are the game or the frameworks every mod runs through: a frame there blames nobody.</summary>
    private static readonly HashSet<string> PlatformIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "forge", "neoforge", "fml",
        "fabric", "fabric-api", "quilted_fabric_api", "mixinextras", "mixin", "lwjgl", "kotlin", "fabric_language_kotlin",
    };

    /// <summary>Package words that are no mod's id even when some jar claims one.</summary>
    private static readonly HashSet<string> GenericSegments = new(StringComparer.Ordinal)
    {
        "com", "net", "org", "io", "me", "dev", "de", "ru", "uk", "xyz", "gg", "tk", "cc", "info",
        "client", "common", "server", "mixin", "mixins", "impl", "api", "util", "utils", "core", "main",
        "mod", "mods", "world", "render", "renderer", "gui", "screen", "entity", "block", "item", "network",
        "config", "event", "events", "init", "lib", "library", "compat", "base", "internal", "data",
    };

    /// <summary>Frames of the game, the loaders and the libraries: skipped when looking for the mod at fault.</summary>
    private static readonly string[] PlatformPackages =
    {
        "java.", "javax.", "jdk.", "sun.", "com.sun.", "net.minecraft.", "com.mojang.", "net.fabricmc.loader.",
        "net.fabricmc.fabric.", "org.quiltmc.loader.", "cpw.mods.", "net.minecraftforge.", "net.neoforged.",
        "org.spongepowered.", "com.llamalad7.mixinextras.", "org.lwjgl.", "io.netty.", "com.google.", "org.apache.",
        "it.unimi.", "org.joml.", "oshi.", "kotlin.", "kotlinx.", "org.objectweb.", "org.slf4j.", "com.electronwill.",
        "org.jline.", "net.minecraftforge.fml.", "MC-BOOTSTRAP/", "SECURE-BOOTSTRAP/", "java.base/",
    };

    /// <summary>
    /// The signature of the crash in these texts, or null when none of them has a failure
    /// it can name. A JVM that died natively comes first: nothing after it ran. Then the
    /// game's crash report, then the log.
    /// </summary>
    /// <param name="crashReport">The game's crash report, when it wrote one.</param>
    /// <param name="log">The game's console output.</param>
    /// <param name="jvmErrorReport">The JVM's hs_err_pid file, when it wrote one.</param>
    /// <param name="mods">The build's jars and the ids they declare; frames are matched against these only.</param>
    public static string? Extract(
        IReadOnlyList<string>? crashReport,
        IReadOnlyList<string>? log,
        IReadOnlyList<string>? jvmErrorReport = null,
        IReadOnlyList<CrashModInfo>? mods = null)
    {
        var known = KnownMods.From(mods);

        var native = NativeSignature(jvmErrorReport) ?? NativeSignature(log);

        if (native is not null)
        {
            return Clip(native);
        }

        foreach (var source in new[] { crashReport, CrashReportInLog(log) })
        {
            if (source is { Count: > 0 } && DescribedException(source) is { } described)
            {
                return Clip(Compose(source, described, known));
            }
        }

        if (log is { Count: > 0 } && LastFatalException(log) is { } fatal)
        {
            return Clip(Compose(log, fatal, known));
        }

        return null;
    }

    /// <summary>"EXCEPTION_ACCESS_VIOLATION in amd-driver" from the JVM's fatal error banner.</summary>
    private static string? NativeSignature(IReadOnlyList<string>? lines)
    {
        if (lines is null)
        {
            return null;
        }

        string? code = null;
        string? where = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (code is null && JvmFatal.Match(line) is { Success: true } fatal)
            {
                code = fatal.Groups["code"].Value;
                continue;
            }

            if (code is not null && line.Contains("Problematic frame", StringComparison.Ordinal) && i + 1 < lines.Count)
            {
                where = FrameLabel(lines[i + 1]);
                break;
            }
        }

        return code is null ? null : where is null ? code : $"{code} in {where}";
    }

    /// <summary>What kind of code the problematic frame is in: a label from a fixed table, never the file's own name unless it is a known system library.</summary>
    public static string? FrameLabel(string frameLine)
    {
        var match = ProblematicFrame.Match(frameLine);

        if (!match.Success)
        {
            return null;
        }

        var kind = match.Groups["kind"].Value;

        if (kind is "J" or "j" or "A")
        {
            return "java";
        }

        if (!match.Groups["module"].Success)
        {
            return kind == "V" ? "jvm" : "native";
        }

        var module = match.Groups["module"].Value;
        var label = NativeModules.Classify(module);

        return label ?? (kind == "V" ? "jvm" : "native");
    }

    /// <summary>The crash report the game printed to its console, when it did: the last one.</summary>
    private static IReadOnlyList<string>? CrashReportInLog(IReadOnlyList<string>? log)
    {
        if (log is null)
        {
            return null;
        }

        for (var i = log.Count - 1; i >= 0; i--)
        {
            if (log[i].Contains("---- Minecraft Crash Report ----", StringComparison.Ordinal))
            {
                return log.Skip(i).Take(400).ToList();
            }
        }

        return null;
    }

    /// <summary>A crash report: the exception right under "Description:".</summary>
    private static int? DescribedException(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith("Description:", StringComparison.Ordinal))
            {
                continue;
            }

            for (var j = i + 1; j < lines.Count && j <= i + 6; j++)
            {
                if (ExceptionHeader.IsMatch(lines[j]))
                {
                    return j;
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// A log: the exception after the last fatal marker, or failing that the last
    /// exception that came with a stack trace.
    /// </summary>
    private static int? LastFatalException(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            var isMarker = line.Contains("/FATAL]", StringComparison.Ordinal) ||
                           line.Contains("Exception in thread", StringComparison.Ordinal) ||
                           line.Contains("Unreported exception thrown", StringComparison.Ordinal) ||
                           line.Contains("Failed to start the minecraft server", StringComparison.Ordinal);

            if (!isMarker)
            {
                continue;
            }

            for (var j = i; j < lines.Count && j <= i + 5; j++)
            {
                if (ExceptionHeader.IsMatch(lines[j]) && !CausedBy.IsMatch(lines[j]))
                {
                    return j;
                }
            }
        }

        for (var i = lines.Count - 2; i >= 0; i--)
        {
            if (ExceptionHeader.IsMatch(lines[i]) && !CausedBy.IsMatch(lines[i]) && StackFrame.IsMatch(lines[i + 1]))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The class that matters in the trace that starts at <paramref name="start"/>, and the mod its frames point at.</summary>
    private static string Compose(IReadOnlyList<string> lines, int start, KnownMods known)
    {
        var chain = new List<string>();
        var frames = new List<string>();
        var end = start;

        chain.Add(ExceptionHeader.Match(lines[start]).Groups["cls"].Value);

        for (var i = start + 1; i < lines.Count && i < start + 400; i++)
        {
            var line = lines[i];

            if (StackFrame.Match(line) is { Success: true } frame)
            {
                frames.Add(line.Trim());
                end = i;
                continue;
            }

            if (CausedBy.Match(line) is { Success: true } cause)
            {
                chain.Add(cause.Groups["cls"].Value);
                end = i;
                continue;
            }

            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("...", StringComparison.Ordinal) || trimmed.StartsWith("Suppressed:", StringComparison.Ordinal))
            {
                continue;
            }

            // A message that runs over several lines sits between the header and the first frame.
            if (frames.Count == 0 && i <= start + 8)
            {
                continue;
            }

            break;
        }

        var cls = chain.FirstOrDefault(c => !Wrappers.Contains(c)) ?? chain[^1];
        var mod = ModFromFrames(frames, known) ?? ModFromText(lines, start, end, known);

        return mod is null ? cls : $"{cls} @{mod}";
    }

    /// <summary>The first frame, from the top, that is a mod's own code.</summary>
    private static string? ModFromFrames(IEnumerable<string> frames, KnownMods known)
    {
        foreach (var line in frames)
        {
            var frame = StackFrame.Match(line).Groups["frame"].Value;

            if (FrameModule.Match(frame) is { Success: true } module && known.Accepts(module.Groups["id"].Value) is { } byModule)
            {
                return byModule;
            }

            if (MixinHandler.Match(frame) is { Success: true } handler && known.Accepts(handler.Groups["id"].Value) is { } byMixin)
            {
                return byMixin;
            }

            if (FrameJar.Match(line) is { Success: true } jar && known.ByFile(jar.Groups["jar"].Value) is { } byJar)
            {
                return byJar;
            }

            if (PlatformPackages.Any(p => frame.StartsWith(p, StringComparison.Ordinal)))
            {
                continue;
            }

            if (known.ByPackage(frame) is { } byPackage)
            {
                return byPackage;
            }
        }

        return null;
    }

    /// <summary>A mod the report names outright: a mixin's owner in the trace, or Forge's "Suspected Mods".</summary>
    private static string? ModFromText(IReadOnlyList<string> lines, int start, int end, KnownMods known)
    {
        for (var i = start; i <= end && i < lines.Count; i++)
        {
            if (MixinOwner.Match(lines[i]) is { Success: true } owner && known.Accepts(owner.Groups["id"].Value) is { } id)
            {
                return id;
            }
        }

        foreach (var line in lines)
        {
            if (SuspectedMods.Match(line) is not { Success: true } suspected)
            {
                continue;
            }

            foreach (Match candidate in Parenthesised.Matches(suspected.Groups["list"].Value))
            {
                if (known.Accepts(candidate.Groups["id"].Value) is { } suspect)
                {
                    return suspect;
                }
            }
        }

        return null;
    }

    private static string Clip(string signature)
        => signature.Length <= MaxLength ? signature : signature[..MaxLength];

    /// <summary>The build's mod ids, by id, by file and by package word.</summary>
    private sealed class KnownMods
    {
        private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _byFile = new(StringComparer.OrdinalIgnoreCase);

        public static KnownMods From(IReadOnlyList<CrashModInfo>? mods)
        {
            var known = new KnownMods();

            foreach (var mod in mods ?? Array.Empty<CrashModInfo>())
            {
                var own = mod.Ids.Select(Normalise).FirstOrDefault(id => id is not null && !PlatformIds.Contains(id));

                foreach (var id in mod.Ids.Select(Normalise).OfType<string>())
                {
                    known._ids.Add(id);
                }

                if (own is not null && !string.IsNullOrWhiteSpace(mod.FileName))
                {
                    known._byFile[StripDisabled(mod.FileName)] = own;
                }
            }

            return known;
        }

        /// <summary>
        /// The id, when it is safe to send: in the shape of a mod id, not the platform, and
        /// declared by a jar of the build - or, for the places that only ever print a mod
        /// id, any id when the build's jars could not be read.
        /// </summary>
        public string? Accepts(string candidate)
        {
            var id = Normalise(candidate);

            if (id is null || PlatformIds.Contains(id))
            {
                return null;
            }

            if (_ids.Count > 0)
            {
                return _ids.Contains(id) ? id : null;
            }

            return id;
        }

        public string? ByFile(string fileName)
            => _byFile.TryGetValue(StripDisabled(fileName), out var id) ? id : null;

        /// <summary>"com.simibubi.create.content.Foo.bar" names "create" when the build has a mod with that id.</summary>
        public string? ByPackage(string frame)
        {
            if (_ids.Count == 0)
            {
                return null;
            }

            var segments = frame.Split('.');

            // The last two parts are the class and the method.
            for (var i = 0; i < segments.Length - 2; i++)
            {
                var segment = segments[i];

                if (segment.Length < 3 || GenericSegments.Contains(segment) || PlatformIds.Contains(segment))
                {
                    continue;
                }

                if (_ids.Contains(segment))
                {
                    return segment.ToLowerInvariant();
                }
            }

            return null;
        }

        private static string? Normalise(string? id)
        {
            var trimmed = id?.Trim().ToLowerInvariant();
            return trimmed is not null && ModId.IsMatch(trimmed) ? trimmed : null;
        }

        private static string StripDisabled(string fileName)
            => fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? fileName[..^".disabled".Length] : fileName;
    }
}

/// <summary>The native libraries a JVM crash happens in, sorted into what a player can do about them.</summary>
public static class NativeModules
{
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly Regex Amd = new(@"^(?:atio|atiu|atid|atic|atiglpxx|amdx|amdvlk|amdihk|radeonsi|libamdgpu|amdgpu)", Options);
    private static readonly Regex Nvidia = new(@"^(?:nvoglv|nvd3dum|nvwgf2um|nvgpucomp|nvcuda|nvumdshim|libnvidia|libglx_nvidia)", Options);
    private static readonly Regex Intel = new(@"^(?:ig\d+icd|igxelpicd|igdumd|igd\d+iumd|igc\d|igdgmm|igvk|igdrcl|iris_dri|i965_dri|crocus_dri)", Options);

    /// <summary>Programs that draw over the game by getting into it, by the library they get in with.</summary>
    private static readonly (Regex Pattern, string Label, string Name)[] Overlays =
    {
        (new Regex(@"^RTSSHooks", Options), "rtss", "RivaTuner / MSI Afterburner"),
        (new Regex(@"^DiscordHook", Options), "discord-overlay", "Discord"),
        (new Regex(@"^graphics-hook", Options), "obs-hook", "OBS"),
        (new Regex(@"^gameoverlayrenderer", Options), "steam-overlay", "Steam"),
        (new Regex(@"^nvspcap", Options), "nvidia-overlay", "NVIDIA GeForce Experience"),
        (new Regex(@"^(?:overwolf|OWClient|ow-graphics-hook)", Options), "overwolf", "Overwolf"),
    };

    /// <summary>Libraries every Windows program loads: their names say nothing about anybody.</summary>
    private static readonly HashSet<string> SystemLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntdll", "kernelbase", "kernel32", "ucrtbase", "msvcrt", "vcruntime140", "msvcp140", "user32", "gdi32",
        "opengl32", "d3d9", "d3d11", "dxgi", "combase", "win32u", "libc.so.6", "libc", "libpthread",
    };

    /// <summary>The vendor whose graphics driver this library belongs to: "AMD", "NVIDIA", "Intel"; null for anything else.</summary>
    public static string? DriverVendor(string module)
        => Amd.IsMatch(module) ? "AMD"
            : Nvidia.IsMatch(module) ? "NVIDIA"
            : Intel.IsMatch(module) ? "Intel"
            : null;

    /// <summary>The overlay this library belongs to, as players know it; null for anything else.</summary>
    public static string? OverlayName(string module)
        => Overlays.FirstOrDefault(o => o.Pattern.IsMatch(module)).Name;

    /// <summary>A label from a fixed set for the library, or null for one this table does not know.</summary>
    public static string? Classify(string module)
    {
        if (DriverVendor(module) is { } vendor)
        {
            return vendor.ToLowerInvariant() + "-driver";
        }

        foreach (var overlay in Overlays)
        {
            if (overlay.Pattern.IsMatch(module))
            {
                return overlay.Label;
            }
        }

        var bare = module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? module[..^4] : module;

        if (bare.Equals("jvm", StringComparison.OrdinalIgnoreCase) || bare.Equals("libjvm.so", StringComparison.OrdinalIgnoreCase))
        {
            return "jvm";
        }

        if (bare.StartsWith("lwjgl", StringComparison.OrdinalIgnoreCase) || bare.StartsWith("glfw", StringComparison.OrdinalIgnoreCase) ||
            bare.StartsWith("OpenAL", StringComparison.OrdinalIgnoreCase) || bare.StartsWith("liblwjgl", StringComparison.OrdinalIgnoreCase) ||
            bare.StartsWith("libglfw", StringComparison.OrdinalIgnoreCase))
        {
            return "lwjgl";
        }

        if (bare.Equals("vulkan-1", StringComparison.OrdinalIgnoreCase))
        {
            return "vulkan";
        }

        return SystemLibraries.Contains(bare) ? bare.ToLowerInvariant() : null;
    }
}
