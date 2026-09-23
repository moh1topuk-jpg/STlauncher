using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace STlauncher.Core.Text;

/// <summary>
/// Turns a Modrinth project body into readable plain text. Bodies are Markdown with HTML
/// mixed in: YouTube iframes, centered banners, badge rows, collapsible details, tables.
/// None of that renders in a TextBlock, so it goes, and what is left is the prose the
/// author wrote, one paragraph per block.
/// </summary>
public static class MarkdownText
{
    private static readonly RegexOptions Opts = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline;

    // Whole elements that never carry prose worth keeping.
    private static readonly Regex Dropped = new(@"<(iframe|script|style|video|audio|svg|object|embed)\b[^>]*>.*?</\1\s*>|<(iframe|img|br|hr|input|source)\b[^>]*/?>", Opts);
    private static readonly Regex Comments = new(@"<!--.*?-->", Opts);
    private static readonly Regex Tags = new(@"</?[a-z][a-z0-9-]*\b[^>]*>", Opts);

    private static readonly Regex Fences = new(@"```.*?```|~~~.*?~~~", Opts);
    private static readonly Regex Images = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex ReferenceImages = new(@"!\[[^\]]*\]\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex Links = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex ReferenceLinks = new(@"\[([^\]]+)\]\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex LinkDefinitions = new(@"^\s*\[[^\]]+\]:\s*\S+.*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex AutoLinks = new(@"<(https?://[^>\s]+)>", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s*(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex SetextUnderline = new(@"^\s{0,3}(=+|-+)\s*$", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s{0,3}([-*_=~])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*[-*+]\s+", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^\s*(\d+)[.)]\s+", RegexOptions.Compiled);
    private static readonly Regex Quote = new(@"^\s{0,3}>\s?", RegexOptions.Compiled);
    private static readonly Regex TableDivider = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex Emphasis = new(@"(\*\*|__|~~)(?=\S)(.+?)(?<=\S)\1|(?<![\w*])(\*|_)(?=\S)(.+?)(?<=\S)\3(?![\w*])", RegexOptions.Compiled);
    private static readonly Regex Code = new(@"`([^`]*)`", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex DecorativeLine = new(@"^[\s\p{P}\p{S}]*$", RegexOptions.Compiled);

    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        text = Fences.Replace(text, "\n");
        text = Comments.Replace(text, string.Empty);
        text = Dropped.Replace(text, "\n");
        // Block-level tags become line breaks so neighbouring text does not run together.
        text = Regex.Replace(text, @"</?(p|div|center|details|summary|h[1-6]|li|ul|ol|table|tr|blockquote|section|article|header|footer)\b[^>]*>", "\n\n", Opts);
        text = Tags.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);

        text = LinkDefinitions.Replace(text, string.Empty);
        text = Images.Replace(text, string.Empty);
        text = ReferenceImages.Replace(text, string.Empty);
        text = Links.Replace(text, "$1");
        text = ReferenceLinks.Replace(text, "$1");
        text = AutoLinks.Replace(text, "$1");

        var blocks = new List<(string Text, bool Heading)>();
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add((string.Join(' ', paragraph), false));
                paragraph.Clear();
            }
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.Trim().Length == 0 || Rule.IsMatch(line) || SetextUnderline.IsMatch(line) || TableDivider.IsMatch(line))
            {
                Flush();
                continue;
            }

            var heading = Heading.Match(line);

            if (heading.Success)
            {
                Flush();
                var title = Inline(heading.Groups[1].Value);

                if (title.Length > 0)
                {
                    blocks.Add((title, true));
                }

                continue;
            }

            line = Quote.Replace(line, string.Empty);

            if (Bullet.IsMatch(line) || Numbered.IsMatch(line))
            {
                Flush();
                var item = Numbered.IsMatch(line)
                    ? Numbered.Replace(line, "$1. ")
                    : Bullet.Replace(line, "• ");
                item = Inline(item);

                if (!DecorativeLine.IsMatch(item))
                {
                    blocks.Add((item, false));
                }

                continue;
            }

            if (line.TrimStart().StartsWith('|'))
            {
                // A table row: cells separated by a middle dot read fine as one line.
                Flush();
                var cells = line.Trim().Trim('|').Split('|').Select(c => Inline(c)).Where(c => c.Length > 0);
                var row = string.Join(" · ", cells);

                if (row.Length > 0)
                {
                    blocks.Add((row, false));
                }

                continue;
            }

            var inline = Inline(line);

            if (inline.Length > 0 && !DecorativeLine.IsMatch(inline))
            {
                paragraph.Add(inline);
            }
        }

        Flush();

        // A heading whose whole section was a video or a banner has nothing to head.
        var kept = blocks
            .Where((b, i) => !b.Heading || (i + 1 < blocks.Count && !blocks[i + 1].Heading))
            .Select(b => b.Text);

        return string.Join("\n\n", kept).Trim();
    }

    private static string Inline(string line)
    {
        var s = Code.Replace(line, "$1");

        // Emphasis can nest (***text***), so strip until nothing changes.
        string previous;
        do
        {
            previous = s;
            s = Emphasis.Replace(s, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value);
        }
        while (s != previous);

        s = s.Replace("\\*", "*").Replace("\\_", "_").Replace("\\#", "#");
        return Spaces.Replace(s, " ").Trim();
    }
}
