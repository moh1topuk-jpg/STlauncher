using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Mods;

/// <summary>
/// Answers "does game version X satisfy this range?" for the two dialects mods use:
/// Fabric's npm-style ranges (">=1.21 <1.22", "1.21.x", "~1.21.1", "*", alternatives
/// with "||") and Forge's Maven ranges ("[1.21,1.22)", "[1.21.1]"). Anything it cannot
/// parse counts as satisfied: a false alarm before launch is worse than a missed one.
/// </summary>
public static class VersionRange
{
    private static readonly Regex Comparator = new(@"^(?<op>>=|<=|>|<|=|~|\^)?\s*(?<ver>[0-9][0-9A-Za-z.\-+]*|\*|x)$", RegexOptions.Compiled);

    public static bool Satisfies(string? range, string version)
    {
        if (string.IsNullOrWhiteSpace(range) || string.IsNullOrWhiteSpace(version))
        {
            return true;
        }

        var text = range.Trim();

        if (text is "*" or "x" or "X")
        {
            return true;
        }

        try
        {
            if (text.StartsWith('[') || text.StartsWith('('))
            {
                return SatisfiesMaven(text, version);
            }

            return text.Split("||", StringSplitOptions.RemoveEmptyEntries)
                .Any(alternative => SatisfiesAll(alternative.Trim(), version));
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Orders "1.21.4" after "1.21" and "0.6.13" after "0.6.2"; suffixes are ignored.</summary>
    public static int CompareVersions(string? a, string? b) => Compare(Parse(a ?? string.Empty), Parse(b ?? string.Empty));

    private static bool SatisfiesAll(string alternative, string version)
    {
        if (alternative.Length == 0)
        {
            return true;
        }

        var parts = alternative.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var known = false;

        foreach (var part in parts)
        {
            var match = Comparator.Match(part);

            if (!match.Success)
            {
                continue;
            }

            known = true;

            if (!SatisfiesComparator(match.Groups["op"].Value, match.Groups["ver"].Value, version))
            {
                return false;
            }
        }

        return known || true;
    }

    private static bool SatisfiesComparator(string op, string spec, string version)
    {
        if (spec is "*" or "x")
        {
            return true;
        }

        var wildcard = spec.EndsWith(".x", StringComparison.OrdinalIgnoreCase) || spec.EndsWith(".*", StringComparison.Ordinal);
        var specParts = Parse(wildcard ? spec[..^2] : spec);
        var versionParts = Parse(version);

        switch (op)
        {
            case ">=": return Compare(versionParts, specParts) >= 0;
            case ">": return Compare(versionParts, specParts) > 0;
            case "<=": return Compare(versionParts, specParts) <= 0;
            case "<": return Compare(versionParts, specParts) < 0;
            case "~":
                // Same major.minor, patch may be newer.
                return Compare(versionParts, specParts) >= 0 && SamePrefix(versionParts, specParts, Math.Max(1, Math.Min(2, specParts.Count)));
            case "^":
                return Compare(versionParts, specParts) >= 0 && SamePrefix(versionParts, specParts, 1);
            default:
                // Exact, or a prefix when the spec is shorter ("1.21" accepts 1.21.4) or has a wildcard.
                return wildcard || specParts.Count < versionParts.Count
                    ? SamePrefix(versionParts, specParts, specParts.Count)
                    : Compare(versionParts, specParts) == 0;
        }
    }

    private static bool SatisfiesMaven(string text, string version)
    {
        var v = Parse(version);

        foreach (var piece in SplitMaven(text))
        {
            var lowerInclusive = piece.StartsWith('[');
            var upperInclusive = piece.EndsWith(']');
            var inner = piece.Trim('[', ']', '(', ')');
            var bounds = inner.Split(',');

            if (bounds.Length == 1)
            {
                if (Compare(v, Parse(bounds[0].Trim())) == 0) return true;
                continue;
            }

            var lower = bounds[0].Trim();
            var upper = bounds[1].Trim();
            var okLower = lower.Length == 0 || (lowerInclusive ? Compare(v, Parse(lower)) >= 0 : Compare(v, Parse(lower)) > 0);
            var okUpper = upper.Length == 0 || (upperInclusive ? Compare(v, Parse(upper)) <= 0 : Compare(v, Parse(upper)) < 0);

            if (okLower && okUpper) return true;
        }

        return false;
    }

    /// <summary>"[1.20,1.21),[1.21.1,)" has commas inside and between pieces; split on the ones between.</summary>
    private static IEnumerable<string> SplitMaven(string text)
    {
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c is '[' or '(') depth++;
            else if (c is ']' or ')') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return text[start..i].Trim();
                start = i + 1;
            }
        }

        yield return text[start..].Trim();
    }

    private static List<int> Parse(string version)
    {
        // "1.21.4-pre1" and "1.21.4+build.2" compare by the numeric core only.
        var core = version.Split('-', '+')[0];
        var parts = new List<int>();

        foreach (var piece in core.Split('.'))
        {
            var digits = new string(piece.TakeWhile(char.IsDigit).ToArray());
            parts.Add(digits.Length == 0 ? 0 : int.Parse(digits));
        }

        return parts;
    }

    private static int Compare(List<int> a, List<int> b)
    {
        var length = Math.Max(a.Count, b.Count);

        for (var i = 0; i < length; i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;

            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    private static bool SamePrefix(List<int> version, List<int> spec, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if ((i < version.Count ? version[i] : 0) != (i < spec.Count ? spec[i] : 0))
            {
                return false;
            }
        }

        return true;
    }
}
