using System;
using System.Collections.Generic;
using System.Text;

namespace STlauncher.Core.Launch;

/// <summary>
/// Takes the player's home folder out of text that is shown or copied. A path under it
/// carries the Windows user name, which is often a real name, and a crash report is pasted
/// into public chats.
/// </summary>
public static class PathMask
{
    /// <summary>What stands in for the home folder. Whoever reads the report knows where it points.</summary>
    public const string Placeholder = "%USERPROFILE%";

    public static string Mask(string? text)
        => Mask(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string Mask(string? text, string? home)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        home = home?.TrimEnd('\\', '/');

        // "C:" or "/" as a home would mask every path there is.
        if (string.IsNullOrEmpty(home) || home.Length < 4)
        {
            return text;
        }

        foreach (var form in Forms(home))
        {
            text = Replace(text, form);
        }

        return text;
    }

    /// <summary>The same folder as Java, JSON and URLs write it.</summary>
    private static IEnumerable<string> Forms(string home)
    {
        yield return home;

        if (home.Contains('\\'))
        {
            yield return home.Replace("\\", "\\\\", StringComparison.Ordinal);
            yield return home.Replace('\\', '/');
        }
    }

    private static string Replace(string text, string home)
    {
        var at = text.IndexOf(home, StringComparison.OrdinalIgnoreCase);

        if (at < 0)
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var from = 0;

        while (at >= 0)
        {
            var end = at + home.Length;

            // "C:\Users\ann" must not eat the start of "C:\Users\anna".
            if (end == text.Length || IsBoundary(text[end]))
            {
                result.Append(text, from, at - from).Append(Placeholder);
                from = end;
            }

            at = text.IndexOf(home, end, StringComparison.OrdinalIgnoreCase);
        }

        return result.Append(text, from, text.Length - from).ToString();
    }

    private static bool IsBoundary(char next)
        => char.IsWhiteSpace(next) || next is '\\' or '/' or '"' or '\'' or '`' or ';' or ':' or ',' or ')' or ']' or '>' or '|' or '!' or '?';
}
