using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Launch;

/// <summary>
/// Decides which extra JVM arguments a build may carry. The arguments come from somewhere
/// the launcher does not control - another launcher's settings file, a restored backup, a
/// definition someone handed over - and a JVM argument can load foreign code into the game
/// (<c>-javaagent</c>), replace its classes (<c>-Xbootclasspath</c>), or run a program when
/// it crashes (<c>-XX:OnError</c>).
/// </summary>
/// <remarks>
/// So this is a list of what is allowed, not of what is forbidden: an option nobody thought
/// about is dropped. What passes is what people actually tune - heap and stack sizes,
/// garbage collector switches, a few encoding and network properties, and the module
/// openings old mods need. Nothing that passes can name a file.
/// </remarks>
public static class JvmArgumentAllowlist
{
    /// <summary>What a raw argument string turned into.</summary>
    public sealed record Result(IReadOnlyList<string> Kept, IReadOnlyList<string> Dropped)
    {
        public static readonly Result Empty = new(Array.Empty<string>(), Array.Empty<string>());

        public string KeptText => string.Join(' ', Kept);
    }

    /// <summary>Longest argument string looked at; a settings file is not a place for more.</summary>
    private const int MaxLength = 8192;

    private const int MaxArguments = 64;

    private static readonly Regex Size = new(@"^-X(mx|ms|ss|mn)\d{1,9}[kKmMgG]?$", RegexOptions.CultureInvariant);

    private static readonly Regex Switch = new(@"^-XX:[+-]([A-Za-z][A-Za-z0-9]{1,63})$", RegexOptions.CultureInvariant);

    /// <summary>A named flag with a number, a size, a percentage or one bare word.</summary>
    private static readonly Regex Valued = new(
        @"^-XX:([A-Za-z][A-Za-z0-9]{1,63})=(\d{1,12}(\.\d{1,6})?[kKmMgGtT%]?|[A-Za-z][A-Za-z0-9_-]{0,31})$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Property = new(@"^-D([A-Za-z][A-Za-z0-9_.]{0,63})=([A-Za-z0-9_+-]{1,64})$", RegexOptions.CultureInvariant);

    /// <summary>"java.base/java.lang=ALL-UNNAMED": a module, a package, and who may look.</summary>
    private static readonly Regex ModuleOpening = new(
        @"^[A-Za-z][A-Za-z0-9_.]{0,127}/[A-Za-z][A-Za-z0-9_.]{0,127}=[A-Za-z][A-Za-z0-9_.,-]{0,127}$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Parts of an -XX flag's name that mean it deals in files, commands or agents, or
    /// stops the JVM on purpose. The value pattern already cannot hold a path; this keeps
    /// out the switches that need none (<c>-XX:+CrashOnOutOfMemoryError</c>).
    /// </summary>
    private static readonly string[] ForbiddenFlagParts =
    {
        "OnError", "OnOutOfMemory", "File", "Path", "Directory", "Command", "Agent", "Library",
        "Flags", "Abort", "Crash", "Attach", "Record", "Dump", "Log"
    };

    /// <summary>System properties that change how text is read or which network stack is used, and nothing else.</summary>
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "file.encoding",
        "sun.jnu.encoding",
        "stdout.encoding",
        "stderr.encoding",
        "sun.stdout.encoding",
        "sun.stderr.encoding",
        "user.language",
        "user.country",
        "java.net.preferIPv4Stack",
        "java.net.preferIPv6Addresses",
        "fml.readTimeout",
        "fml.earlyprogresswindow",
        "fml.ignoreInvalidMinecraftCertificates",
        "fml.ignorePatchDiscrepancies"
    };

    /// <summary>Splits a raw string and sorts every argument into kept or dropped.</summary>
    public static Result Filter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result.Empty;
        }

        if (raw.Length > MaxLength)
        {
            // Not worth guessing where a sane argument list ends inside something that long.
            return new Result(Array.Empty<string>(), new[] { raw[..40] + "…" });
        }

        return Filter(Split(raw));
    }

    public static Result Filter(IEnumerable<string> arguments)
    {
        if (arguments is null)
        {
            return Result.Empty;
        }

        var kept = new List<string>();
        var dropped = new List<string>();
        var tokens = arguments.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // "--add-opens java.base/java.lang=ALL-UNNAMED": the value is the next argument.
            if (token is "--add-opens" or "--add-exports")
            {
                if (i + 1 < tokens.Count && ModuleOpening.IsMatch(tokens[i + 1]) && kept.Count < MaxArguments)
                {
                    kept.Add(token + "=" + tokens[i + 1]);
                    i++;
                }
                else
                {
                    dropped.Add(token);
                }

                continue;
            }

            if (kept.Count < MaxArguments && IsAllowed(token))
            {
                if (!kept.Contains(token, StringComparer.Ordinal))
                {
                    kept.Add(token);
                }
            }
            else
            {
                dropped.Add(token);
            }
        }

        return new Result(kept, dropped);
    }

    /// <summary>True for one complete argument that is safe to hand to Java as it is.</summary>
    public static bool IsAllowed(string? argument)
    {
        if (string.IsNullOrEmpty(argument) || argument.Length > 256)
        {
            return false;
        }

        // Nothing that passes needs any of these, and every way of naming a file does.
        if (argument.IndexOfAny(new[] { '\\', '"', '\'', '@', '$', ';', '|', '&', '<', '>', '`', '\0', ' ', '\t', '\r', '\n' }) >= 0)
        {
            return false;
        }

        if (Size.IsMatch(argument))
        {
            return true;
        }

        var match = Switch.Match(argument);

        if (!match.Success)
        {
            match = Valued.Match(argument);
        }

        if (match.Success)
        {
            var name = match.Groups[1].Value;
            // "Dir" only as an ending: MaxDirectMemorySize is a size, HeapDumpDir would be a place.
            return !name.EndsWith("Dir", StringComparison.OrdinalIgnoreCase) &&
                   !ForbiddenFlagParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
        }

        match = Property.Match(argument);

        if (match.Success)
        {
            return AllowedProperties.Contains(match.Groups[1].Value);
        }

        foreach (var prefix in new[] { "--add-opens=", "--add-exports=" })
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                return ModuleOpening.IsMatch(argument[prefix.Length..]);
            }
        }

        return false;
    }

    /// <summary>
    /// Splits on whitespace, keeping a quoted stretch together the way a settings file
    /// means it. The quotes are removed: what is left is judged as one argument, and one
    /// with a space in it never passes.
    /// </summary>
    public static IReadOnlyList<string> Split(string? raw)
    {
        var result = new List<string>();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        var current = new StringBuilder();
        var quote = '\0';
        var any = false;

        foreach (var c in raw)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                any = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (any || current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    any = false;
                }

                continue;
            }

            current.Append(c);
        }

        if (any || current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
