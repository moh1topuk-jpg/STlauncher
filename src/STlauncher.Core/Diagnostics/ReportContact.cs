using System.Text.RegularExpressions;

namespace STlauncher.Core.Diagnostics;

/// <summary>The player's Telegram on a support report, so the admin can write back.</summary>
public static class ReportContact
{
    // Telegram's own rule: 5 to 32 letters, digits and underscores, starting with a letter.
    private static readonly Regex Username = new(
        "^[A-Za-z][A-Za-z0-9_]{4,31}$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Prefix = new(
        @"^(?:https?://)?(?:www\.)?(?:t\.me|telegram\.me)/",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// "@name" from what was typed: "name", "@name" and a t.me link all count. Null when it
    /// is not a Telegram username; empty when nothing was typed, because the field is optional.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        var text = (typed ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return string.Empty;
        }

        var name = Prefix.Replace(text, string.Empty).TrimStart('@').TrimEnd('/');

        return Username.IsMatch(name) ? "@" + name : null;
    }

    /// <summary>The line the admin reads: the contact first, then the player's words.</summary>
    public static string Compose(string contact, string comment)
    {
        if (contact.Length == 0)
        {
            return comment;
        }

        var line = "Telegram: " + contact;

        return comment.Length == 0 ? line : line + "\n" + comment;
    }
}
