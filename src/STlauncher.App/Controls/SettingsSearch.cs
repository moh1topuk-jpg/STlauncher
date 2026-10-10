using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.LogicalTree;

namespace STlauncher.App.Controls;

/// <summary>
/// Search over a page of settings, without the page knowing: one pass over the tree
/// hides what does not match and puts everything back when the query is cleared.
/// </summary>
/// <remarks>
/// A setting is found by the text it shows (titles, hints, button captions) and by its
/// keywords. To make a new row findable by words it does not show, give its container
/// one attribute:
/// <code>&lt;StackPanel controls:SettingsSearch.Keywords="память ram озу memory"&gt;</code>
/// A row without the attribute is still found by its visible text: whatever sits next to
/// marked rows, or a whole card with no marked rows in it, counts as a row.
/// <para>
/// <c>Heading="True"</c> marks a section title: it stays while anything under it does,
/// and a query that matches it shows the whole section. <c>Expand="True"</c> marks a
/// folded part that the search may open; anything else that is hidden stays hidden, so
/// a search never reveals what the page chose not to show.
/// </para>
/// Visibility is overridden at animation priority and handed back on the next pass, so
/// the page's own bindings are never replaced.
/// </remarks>
public static class SettingsSearch
{
    public static readonly AttachedProperty<string?> KeywordsProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Keywords", typeof(SettingsSearch));

    public static readonly AttachedProperty<bool> HeadingProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Heading", typeof(SettingsSearch));

    public static readonly AttachedProperty<bool> ExpandProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Expand", typeof(SettingsSearch));

    /// <summary>The overrides of the last pass, per page root, to be undone before the next one.</summary>
    private static readonly AttachedProperty<List<IDisposable>?> OverridesProperty =
        AvaloniaProperty.RegisterAttached<Control, List<IDisposable>?>("Overrides", typeof(SettingsSearch));

    public static string? GetKeywords(Control control) => control.GetValue(KeywordsProperty);

    public static void SetKeywords(Control control, string? value) => control.SetValue(KeywordsProperty, value);

    public static bool GetHeading(Control control) => control.GetValue(HeadingProperty);

    public static void SetHeading(Control control, bool value) => control.SetValue(HeadingProperty, value);

    public static bool GetExpand(Control control) => control.GetValue(ExpandProperty);

    public static void SetExpand(Control control, bool value) => control.SetValue(ExpandProperty, value);

    /// <summary>
    /// Shows only what matches the query under <paramref name="root"/>. Returns how many
    /// rows matched; an empty query restores the page and returns -1.
    /// </summary>
    public static int Filter(Control root, string? query)
    {
        // Back to what the page itself wants, so "hidden" below means hidden by the page.
        if (root.GetValue(OverridesProperty) is { } previous)
        {
            foreach (var handle in previous)
            {
                handle.Dispose();
            }
        }

        var words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            root.SetValue(OverridesProperty, null);
            return -1;
        }

        var pass = new Pass(words);
        pass.Visit(root, showAll: false);
        root.SetValue(OverridesProperty, pass.Overrides);

        return pass.Matched;
    }

    /// <summary>Lower case, "ё" as "е", anything that is not a letter or digit as a space.</summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            var lower = char.ToLowerInvariant(c);
            builder.Append(lower == 'ё' ? 'е' : char.IsLetterOrDigit(lower) ? lower : ' ');
        }

        return builder.ToString();
    }

    private sealed class Pass
    {
        private readonly string[] _words;

        public Pass(string[] words)
        {
            _words = words;
        }

        public List<IDisposable> Overrides { get; } = new();

        public int Matched { get; private set; }

        /// <summary>True when the control ends up on screen.</summary>
        public bool Visit(Control control, bool showAll)
        {
            if (!control.IsVisible)
            {
                if (!GetExpand(control))
                {
                    return false;
                }

                // A folded part: looked into, and opened only if something inside is wanted.
                var opened = VisitInside(control, showAll);

                if (opened)
                {
                    Override(control, true);
                }

                return opened;
            }

            var shown = VisitInside(control, showAll);

            if (!shown)
            {
                Override(control, false);
            }

            return shown;
        }

        private bool VisitInside(Control control, bool showAll)
        {
            if (control.IsSet(KeywordsProperty) || !HasMarkedDescendant(control))
            {
                // A row. Under a matching heading every row is wanted.
                var wanted = showAll || Matches(control);

                if (wanted)
                {
                    Matched++;
                }

                return wanted;
            }

            var children = control.GetLogicalChildren().OfType<Control>().ToList();
            var headings = children.Where(GetHeading).ToList();
            var all = showAll || headings.Any(MatchesTitle);
            var any = false;

            foreach (var child in children)
            {
                if (!GetHeading(child))
                {
                    any |= Visit(child, all);
                }
            }

            foreach (var heading in headings)
            {
                if (!any)
                {
                    Override(heading, false);
                }
            }

            return any;
        }

        private void Override(Control control, bool visible)
        {
            if (control.IsVisible != visible &&
                control.SetValue(Visual.IsVisibleProperty, visible, BindingPriority.Animation) is { } handle)
            {
                Overrides.Add(handle);
            }
        }

        private static bool HasMarkedDescendant(Control control)
            => control.GetLogicalDescendants().OfType<Control>().Any(c =>
                c.IsSet(KeywordsProperty) || GetHeading(c) || GetExpand(c));

        private bool Matches(Control control)
        {
            var text = new StringBuilder(Normalize(GetKeywords(control)));
            Collect(control, text);
            var haystack = text.ToString();

            return _words.All(word => haystack.Contains(word, StringComparison.Ordinal));
        }

        /// <summary>
        /// A heading is matched by its title alone, the first text in it: a summary line
        /// under the title lists what is inside, and would open the whole section for
        /// any word of it.
        /// </summary>
        private bool MatchesTitle(Control heading)
        {
            var title = heading.GetSelfAndLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text;
            var haystack = Normalize(title);

            return haystack.Length > 0 && _words.All(word => haystack.Contains(word, StringComparison.Ordinal));
        }

        /// <summary>Everything the control says in words, shown at the moment or not.</summary>
        private static void Collect(Control control, StringBuilder text)
        {
            foreach (var item in control.GetSelfAndLogicalDescendants())
            {
                var words = item switch
                {
                    TextBlock block => block.Text,
                    TextBox box => box.Watermark,
                    HeaderedContentControl { Header: string header } => header,
                    ContentControl { Content: string content } => content,
                    _ => null
                };

                if (!string.IsNullOrEmpty(words))
                {
                    text.Append(' ').Append(Normalize(words));
                }

                if (item is Control { } child && !ReferenceEquals(child, control) && GetKeywords(child) is { Length: > 0 } nested)
                {
                    text.Append(' ').Append(Normalize(nested));
                }
            }
        }
    }
}
