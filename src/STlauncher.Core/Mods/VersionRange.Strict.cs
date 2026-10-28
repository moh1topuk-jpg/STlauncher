using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Mods;

/// <summary>Which loader's grammar a version range is written in.</summary>
public enum RangeDialect
{
    /// <summary>
    /// fabric.mod.json and quilt.mod.json: "&gt;=1.21 &lt;1.22", "~1.21.1", "^1.21", "1.21.x",
    /// "*", or a bare version, which Fabric reads as "exactly this one" (1.21 is 1.21.0).
    /// An array of strings is joined with " || " by the reader.
    /// </summary>
    Fabric,

    /// <summary>
    /// mods.toml: Maven ranges "[1.21,1.22)", "[1.20.1]", "[47,)". A bare version is a
    /// "soft" requirement in Maven: any version satisfies it.
    /// </summary>
    Maven
}

/// <summary>
/// The strict side of version ranges: the question the loader itself asks before it
/// refuses to start. "Definitely outside" is true only when every part of the range was
/// understood and the version is a plain number the loader compares the same way. Any
/// doubt is an answer of "no": the launcher never says "the game will not start" on a
/// guess.
/// </summary>
public static partial class VersionRange
{
    private static readonly Regex SemanticVersion = new(
        @"^(?<core>\d+(?:\.\d+)*)(?:-(?<pre>[0-9A-Za-z.\-]*))?(?:\+(?<build>.*))?$",
        RegexOptions.Compiled);

    private static readonly Regex FabricToken = new(
        @"^(?<op>>=|<=|>|<|=|~|\^)?(?<ver>.+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// True when the loader would find <paramref name="version"/> outside
    /// <paramref name="range"/> - with certainty. Unknown shapes, non-numeric versions and
    /// snapshots give false.
    /// </summary>
    public static bool DefinitelyOutside(string? range, string? version, RangeDialect dialect)
        => Evaluate(range, version, dialect) == false;

    /// <summary>True when the version is certainly inside the range; false when outside or unknown.</summary>
    public static bool DefinitelyInside(string? range, string? version, RangeDialect dialect)
        => Evaluate(range, version, dialect) == true;

    /// <summary>Null when the answer is not certain.</summary>
    public static bool? Evaluate(string? range, string? version, RangeDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return true;
        }

        var text = range.Trim();

        if (text is "*" or "x" or "X")
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(version) || !TryParseSemantic(version.Trim(), out var v))
        {
            return null;
        }

        try
        {
            return dialect == RangeDialect.Maven ? EvaluateMaven(text, v) : EvaluateFabric(text, v);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ===================== Fabric =====================

    private static bool? EvaluateFabric(string text, Semantic version)
    {
        var alternatives = text.Split("||", StringSplitOptions.None).Select(a => a.Trim()).ToList();

        if (alternatives.Any(a => a.Length == 0))
        {
            return null;
        }

        var anyUnknown = false;

        foreach (var alternative in alternatives)
        {
            switch (EvaluateFabricAll(alternative, version))
            {
                case true:
                    return true;
                case null:
                    anyUnknown = true;
                    break;
            }
        }

        return anyUnknown ? null : false;
    }

    /// <summary>Space-separated predicates, all of which must hold.</summary>
    private static bool? EvaluateFabricAll(string alternative, Semantic version)
    {
        var tokens = alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = true;

        foreach (var token in tokens)
        {
            switch (EvaluateFabricToken(token, version))
            {
                case null:
                    return null;
                case false:
                    result = false;
                    break;
            }
        }

        return result;
    }

    private static bool? EvaluateFabricToken(string token, Semantic version)
    {
        if (token is "*" or "x" or "X")
        {
            return true;
        }

        var match = FabricToken.Match(token);

        if (!match.Success)
        {
            return null;
        }

        var op = match.Groups["op"].Value;
        var spec = match.Groups["ver"].Value;

        // "1.21.x": every version with that prefix. Fabric does not take an operator with it.
        var wildcard = WildcardPrefix(spec);

        if (wildcard is not null)
        {
            if (op.Length > 0 || wildcard.Count == 0)
            {
                return null;
            }

            return SameCore(version.Core, wildcard, wildcard.Count);
        }

        if (!TryParseSemantic(spec, out var bound))
        {
            return null;
        }

        var compared = Compare(version, bound);

        switch (op)
        {
            case ">=": return compared >= 0;
            case ">": return compared > 0;
            case "<=": return compared <= 0;
            case "<": return compared < 0;
            case "~":
                // ~1.21.1 is >=1.21.1 <1.22; a single number is read differently by the
                // loaders, so it is not judged.
                if (bound.Core.Count < 2)
                {
                    return null;
                }

                return compared >= 0 && Compare(version, Bump(bound.Core, 1)) < 0;
            case "^":
                return compared >= 0 && Compare(version, Bump(bound.Core, 0)) < 0;
            default:
                // A bare version, or "=": exactly that one. 1.21 is 1.21.0.
                return compared == 0;
        }
    }

    /// <summary>"1.21.x" / "1.21.*" / "1.21.X" gives [1, 21]; anything else gives null.</summary>
    private static List<int>? WildcardPrefix(string spec)
    {
        var parts = spec.Split('.');

        if (parts.Length < 2 || parts[^1] is not ("x" or "X" or "*"))
        {
            return null;
        }

        var prefix = new List<int>();

        foreach (var part in parts[..^1])
        {
            if (!int.TryParse(part, out var number) || number < 0)
            {
                return new List<int>();
            }

            prefix.Add(number);
        }

        return prefix;
    }

    /// <summary>The first version past the range of a tilde or caret: 1.21.4 bumped at 1 is 1.22.</summary>
    private static Semantic Bump(IReadOnlyList<int> core, int index)
    {
        var bumped = new List<int>();

        for (var i = 0; i <= index; i++)
        {
            bumped.Add(i < core.Count ? core[i] : 0);
        }

        bumped[index]++;

        // "<1.22" also keeps out 1.22's pre-releases: the lowest possible pre-release tag.
        return new Semantic(bumped, string.Empty);
    }

    // ===================== Maven =====================

    private static bool? EvaluateMaven(string text, Semantic version)
    {
        if (!(text.StartsWith('[') || text.StartsWith('(')))
        {
            // A bare version is Maven's "recommended" version: no restriction at all.
            return TryParseSemantic(text, out _) ? true : null;
        }

        var pieces = SplitMaven(text).ToList();
        var anyUnknown = false;

        foreach (var piece in pieces)
        {
            switch (EvaluateMavenPiece(piece, version))
            {
                case true:
                    return true;
                case null:
                    anyUnknown = true;
                    break;
            }
        }

        return anyUnknown ? null : false;
    }

    private static bool? EvaluateMavenPiece(string piece, Semantic version)
    {
        if (piece.Length < 2 || !(piece[0] is '[' or '(') || !(piece[^1] is ']' or ')'))
        {
            return null;
        }

        var lowerInclusive = piece[0] == '[';
        var upperInclusive = piece[^1] == ']';
        var bounds = piece[1..^1].Split(',');

        if (bounds.Length == 1)
        {
            // "[1.20.1]": exactly that one.
            return TryParseSemantic(bounds[0].Trim(), out var exact) ? Compare(version, exact) == 0 : null;
        }

        if (bounds.Length != 2)
        {
            return null;
        }

        var lowerText = bounds[0].Trim();
        var upperText = bounds[1].Trim();
        var ok = true;

        if (lowerText.Length > 0)
        {
            if (!TryParseSemantic(lowerText, out var lower))
            {
                return null;
            }

            var c = Compare(version, lower);
            ok &= lowerInclusive ? c >= 0 : c > 0;
        }

        if (upperText.Length > 0)
        {
            if (!TryParseSemantic(upperText, out var upper))
            {
                return null;
            }

            var c = Compare(version, upper);
            ok &= upperInclusive ? c <= 0 : c < 0;
        }

        return ok;
    }

    // ===================== Versions =====================

    /// <summary>A version as the loaders compare it: numbers, then an optional pre-release tag.</summary>
    private sealed record Semantic(IReadOnlyList<int> Core, string? PreRelease);

    private static bool TryParseSemantic(string text, out Semantic version)
    {
        version = new Semantic(Array.Empty<int>(), null);
        var match = SemanticVersion.Match(text.Trim());

        if (!match.Success)
        {
            return false;
        }

        var core = new List<int>();

        foreach (var piece in match.Groups["core"].Value.Split('.'))
        {
            if (!int.TryParse(piece, out var number))
            {
                return false;
            }

            core.Add(number);
        }

        version = new Semantic(core, match.Groups["pre"].Success ? match.Groups["pre"].Value : null);
        return true;
    }

    /// <summary>
    /// The numbers first, missing ones counting as 0; then a version without a
    /// pre-release tag above one with it; two tags part by part, numbers as numbers.
    /// </summary>
    private static int Compare(Semantic a, Semantic b)
    {
        var length = Math.Max(a.Core.Count, b.Core.Count);

        for (var i = 0; i < length; i++)
        {
            var x = i < a.Core.Count ? a.Core[i] : 0;
            var y = i < b.Core.Count ? b.Core[i] : 0;

            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        if (a.PreRelease is null || b.PreRelease is null)
        {
            return a.PreRelease is null ? (b.PreRelease is null ? 0 : 1) : -1;
        }

        var left = a.PreRelease.Split('.');
        var right = b.PreRelease.Split('.');

        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumber = int.TryParse(left[i], out var l);
            var rightNumber = int.TryParse(right[i], out var r);
            int c;

            if (leftNumber && rightNumber)
            {
                c = l.CompareTo(r);
            }
            else if (leftNumber != rightNumber)
            {
                c = leftNumber ? -1 : 1;
            }
            else
            {
                c = string.CompareOrdinal(left[i], right[i]);
            }

            if (c != 0)
            {
                return c;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool SameCore(IReadOnlyList<int> version, IReadOnlyList<int> prefix, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if ((i < version.Count ? version[i] : 0) != (i < prefix.Count ? prefix[i] : 0))
            {
                return false;
            }
        }

        return true;
    }
}
