using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace STlauncher.Core.Search;

/// <summary>The kinds of things the one search finds, in the order their groups are shown.</summary>
public enum SearchGroup
{
    /// <summary>The launcher's sections and its frequent commands.</summary>
    Places,
    Builds,
    Mods,
    ResourcePacks,
    Shaders,
    Worlds,
    Settings
}

/// <summary>
/// One thing that can be found. The title is what the player sees and what a match is
/// ranked by; the keywords are words they might type that the title does not show
/// (synonyms in both languages, a version, a loader).
/// </summary>
public sealed class SearchItem
{
    public SearchItem(SearchGroup group, string title, string? keywords = null, object? tag = null, bool showWhenEmpty = false)
    {
        Group = group;
        Title = title;
        Tag = tag;
        ShowWhenEmpty = showWhenEmpty;

        NormalizedTitle = PaletteSearch.Normalize(title);
        TitleWords = NormalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        KeywordWords = PaletteSearch.Normalize(keywords).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public SearchGroup Group { get; }

    public string Title { get; }

    /// <summary>Whatever the caller wants back with a hit: the row, the command.</summary>
    public object? Tag { get; }

    /// <summary>Listed before anything is typed: the places, the recent builds.</summary>
    public bool ShowWhenEmpty { get; }

    internal string NormalizedTitle { get; }

    internal string[] TitleWords { get; }

    internal string[] KeywordWords { get; }
}

/// <summary>A found item and how well it matched: 0 is the best.</summary>
public readonly record struct SearchHit(SearchItem Item, int Rank);

/// <summary>
/// What a query found, groups kept together and already in the order to show. When the
/// query only made sense typed in the other keyboard layout, <see cref="CorrectedQuery"/>
/// is what it was read as.
/// </summary>
public sealed record SearchResults(IReadOnlyList<SearchHit> Hits, string? CorrectedQuery);

/// <summary>
/// The matching behind the search for everything: case and "ё" do not matter, words match
/// by their beginnings or anywhere inside, and a query typed in the wrong layout
/// ("ghbdtn" for "привет") is tried the other way round when nothing matched as typed.
/// Pure and cheap: a few hundred items take microseconds, so it runs on every keystroke.
/// </summary>
public static class PaletteSearch
{
    /// <summary>How many rows one group may take, so one kind never pushes the others off the list.</summary>
    public const int DefaultCap = 6;

    /// <summary>The title begins with the query as typed.</summary>
    public const int RankTitlePrefix = 0;

    /// <summary>Every word of the query begins a word of the title.</summary>
    public const int RankWordPrefix = 1;

    /// <summary>Every word is somewhere in the title.</summary>
    public const int RankTitleSubstring = 2;

    /// <summary>Some word was found only among the keywords, at the beginning of one.</summary>
    public const int RankKeyword = 3;

    private const string LatinKeys = "`qwertyuiop[]asdfghjkl;'zxcvbnm,.";
    private const string CyrillicKeys = "ёйцукенгшщзхъфывапролджэячсмитьбю";

    public static SearchResults Find(IReadOnlyList<SearchItem> items, string? query, int cap = DefaultCap)
    {
        var normalized = Normalize(query);

        if (normalized.Length == 0)
        {
            return new SearchResults(Order(items.Where(i => i.ShowWhenEmpty).Select(i => new SearchHit(i, RankWordPrefix)), cap), null);
        }

        var hits = Match(items, normalized);

        if (hits.Count > 0)
        {
            return new SearchResults(Order(hits, cap), null);
        }

        // Nothing as typed: the same keys in the other layout.
        var swapped = Normalize(SwapLayout(query!));

        if (swapped.Length == 0 || swapped == normalized)
        {
            return new SearchResults(Array.Empty<SearchHit>(), null);
        }

        hits = Match(items, swapped);

        return new SearchResults(Order(hits, cap), hits.Count > 0 ? swapped : null);
    }

    /// <summary>Lower case, "ё" as "е", anything that is not a letter or a digit as one space.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            var lower = char.ToLowerInvariant(c);

            if (lower == 'ё')
            {
                builder.Append('е');
            }
            else if (char.IsLetterOrDigit(lower))
            {
                builder.Append(lower);
            }
            else if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The same keys pressed in the other layout: Latin letters become the Cyrillic ones
    /// on those keys, and a text with no Latin letters goes the other way.
    /// </summary>
    public static string SwapLayout(string text)
    {
        var lower = text.ToLowerInvariant();
        var toCyrillic = lower.Any(c => c is >= 'a' and <= 'z');
        var from = toCyrillic ? LatinKeys : CyrillicKeys;
        var to = toCyrillic ? CyrillicKeys : LatinKeys;
        var builder = new StringBuilder(lower.Length);

        foreach (var c in lower)
        {
            var index = from.IndexOf(c);
            builder.Append(index >= 0 ? to[index] : c);
        }

        return builder.ToString();
    }

    private static List<SearchHit> Match(IReadOnlyList<SearchItem> items, string normalizedQuery)
    {
        var words = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<SearchHit>();

        foreach (var item in items)
        {
            var rank = Rank(item, normalizedQuery, words);

            if (rank >= 0)
            {
                hits.Add(new SearchHit(item, rank));
            }
        }

        return hits;
    }

    /// <summary>The rank of the item for the query, or -1 when some word is nowhere in it.</summary>
    private static int Rank(SearchItem item, string normalizedQuery, string[] words)
    {
        var worst = RankWordPrefix;

        foreach (var word in words)
        {
            var rank = RankWord(item, word);

            if (rank < 0)
            {
                return -1;
            }

            worst = Math.Max(worst, rank);
        }

        return item.NormalizedTitle.StartsWith(normalizedQuery, StringComparison.Ordinal) ? RankTitlePrefix : worst;
    }

    private static int RankWord(SearchItem item, string word)
    {
        if (item.TitleWords.Any(w => w.StartsWith(word, StringComparison.Ordinal)))
        {
            return RankWordPrefix;
        }

        // One letter in the middle of a word finds everything and means nothing.
        if (word.Length >= 2 && item.NormalizedTitle.Contains(word, StringComparison.Ordinal))
        {
            return RankTitleSubstring;
        }

        // Keywords are synonyms nobody sees, so they answer only to their beginnings:
        // "so" inside "resolution" would bring the window size to someone typing "sodium".
        if (item.KeywordWords.Any(w => w.StartsWith(word, StringComparison.Ordinal)))
        {
            return RankKeyword;
        }

        return -1;
    }

    /// <summary>
    /// Groups stay together. A group holding a title that begins with the query comes
    /// first, then the groups in their own order; inside a group the better match is
    /// higher and equal matches keep the order they were given in.
    /// </summary>
    private static IReadOnlyList<SearchHit> Order(IEnumerable<SearchHit> hits, int cap)
        => hits
            .Select((hit, index) => (hit, index))
            .GroupBy(x => x.hit.Item.Group)
            .OrderBy(g => g.Any(x => x.hit.Rank == RankTitlePrefix) ? 0 : 1)
            .ThenBy(g => g.Key)
            .SelectMany(g => g.OrderBy(x => x.hit.Rank).ThenBy(x => x.index).Take(cap))
            .Select(x => x.hit)
            .ToList();
}
