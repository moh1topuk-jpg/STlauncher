using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Text;

/// <summary>One group of a release's notes: "Added", "Fixed"... and its bullet points.</summary>
public sealed record ReleaseNotesGroup(string Title, IReadOnlyList<string> Items);

/// <summary>What one version changed, as the changelog tells it.</summary>
public sealed record ReleaseNotes(string Version, string? Date, IReadOnlyList<ReleaseNotesGroup> Groups)
{
    public bool IsEmpty => Groups.Count == 0 || Groups.All(g => g.Items.Count == 0);
}

/// <summary>
/// Reads one version's section out of CHANGELOG.md (Keep a Changelog layout). The same
/// text CI publishes as the release notes, so the launcher never tells a different story.
/// </summary>
public static class ChangelogReader
{
    private static readonly Regex Heading = new(@"^##\s+\[(?<version>[^\]]+)\](?:\s*[—–-]\s*(?<date>\S+))?", RegexOptions.Compiled);
    private static readonly Regex GroupHeading = new(@"^###\s+(?<title>.+?)\s*$", RegexOptions.Compiled);

    public static ReleaseNotes? SectionFor(string markdown, string version)
    {
        if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var groups = new List<ReleaseNotesGroup>();
        var inSection = false;
        string? date = null;
        string? groupTitle = null;
        var items = new List<string>();

        void CloseGroup()
        {
            if (groupTitle is not null && items.Count > 0)
            {
                groups.Add(new ReleaseNotesGroup(groupTitle, items.ToList()));
            }

            items.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var heading = Heading.Match(line);

            if (heading.Success)
            {
                if (inSection)
                {
                    break;
                }

                if (string.Equals(heading.Groups["version"].Value.Trim(), version.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    date = heading.Groups["date"].Success ? heading.Groups["date"].Value : null;
                }

                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var group = GroupHeading.Match(line);

            if (group.Success)
            {
                CloseGroup();
                groupTitle = group.Groups["title"].Value;
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                items.Add(line[2..].Trim());
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal) && items.Count > 0)
            {
                // A wrapped bullet continues on an indented line.
                items[^1] = items[^1] + " " + line.Trim();
            }
        }

        if (!inSection)
        {
            return null;
        }

        CloseGroup();
        return new ReleaseNotes(version, date, groups);
    }

    /// <summary>Strips the little Markdown a bullet may carry: `code`, **bold**, [links](url).</summary>
    public static string Plain(string item)
    {
        var text = item.Replace("`", string.Empty).Replace("**", string.Empty);
        return Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
    }
}
