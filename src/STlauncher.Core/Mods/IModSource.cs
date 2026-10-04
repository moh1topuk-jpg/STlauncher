using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Mods;

/// <summary>
/// What the mod browser asks of a place mods come from. Modrinth was the only one and the
/// browser grew around its client, so this is that client's shape and nothing more: a
/// second source answers in the same records and fits the same list, the same project
/// page and the same install path. Each record says which source it is from.
/// </summary>
public interface IModSource
{
    ModSource Source { get; }

    /// <param name="category">A <see cref="ModCategory.Name"/> this source returned from <see cref="GetCategoriesAsync"/>.</param>
    /// <param name="sort">"relevance", "downloads", "newest" or "updated".</param>
    Task<ModSearchPage> SearchAsync(
        string query,
        string? gameVersion,
        LoaderKind loader,
        string? category = null,
        string sort = "relevance",
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default,
        string projectType = ProjectTypes.Mod);

    /// <summary>The project page, or null when it cannot be had; never throws for a missing project.</summary>
    Task<ModProject?> GetProjectAsync(string idOrSlug, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModCategory>> GetCategoriesAsync(
        string projectType = ProjectTypes.Mod,
        CancellationToken cancellationToken = default);

    /// <summary>Files for a game version and loader, newest first. <see cref="LoaderKind.Vanilla"/> means "any loader".</summary>
    Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        string projectIdOrSlug,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default);
}

public static class ModSearch
{
    /// <summary>
    /// One list out of two sources' pages for the same query and offset. The pages are
    /// interleaved, since each is ranked by its own source and the top of both belongs
    /// at the top. A popular mod is on both sites: the copy from <paramref name="second"/>
    /// is dropped when <paramref name="first"/> already has the same slug or title, so
    /// the player does not see Sodium twice. The total is the larger of the two, which is
    /// what paging both sources by the same offset needs.
    /// </summary>
    public static ModSearchPage Merge(ModSearchPage first, ModSearchPage second)
    {
        var seen = first.Items.SelectMany(Keys).ToHashSet(StringComparer.Ordinal);
        var extra = second.Items.Where(item => !Keys(item).Any(seen.Contains)).ToList();

        var merged = new List<ModSearchResult>(first.Items.Count + extra.Count);

        for (var i = 0; i < Math.Max(first.Items.Count, extra.Count); i++)
        {
            if (i < first.Items.Count)
            {
                merged.Add(first.Items[i]);
            }

            if (i < extra.Count)
            {
                merged.Add(extra[i]);
            }
        }

        return new ModSearchPage(merged, Math.Max(first.TotalHits, second.TotalHits));
    }

    /// <summary>What makes two hits the same mod to a player: the slug, or the title without its punctuation.</summary>
    private static IEnumerable<string> Keys(ModSearchResult item)
    {
        var slug = item.Slug.Trim().ToLowerInvariant();
        var title = new string(item.Title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        if (slug.Length > 0)
        {
            yield return "s:" + slug;
        }

        if (title.Length > 0)
        {
            yield return "t:" + title;
        }
    }
}
