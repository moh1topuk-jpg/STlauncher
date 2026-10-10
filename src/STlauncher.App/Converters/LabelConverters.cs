using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace STlauncher.App.Converters;

/// <summary>
/// The letter on a build's coloured square: the first letter or digit of its name.
/// </summary>
public sealed class InitialConverter : IValueConverter
{
    public static readonly InitialConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (System.Text.Rune.IsLetterOrDigit(rune))
                {
                    return rune.ToString().ToUpper(culture);
                }
            }
        }

        return "?";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The number beside a tab's name. The view model says a tab as one string, "Mods · 27"
/// or "Mods · 27 · +2"; the underlined tabs show the name and the count as two pieces of
/// text, so this takes what follows the name.
/// </summary>
public sealed class TabCountConverter : IValueConverter
{
    public static readonly TabCountConverter Instance = new();

    private const string Separator = " · ";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string label)
        {
            var at = label.IndexOf(Separator, StringComparison.Ordinal);

            if (at >= 0)
            {
                return label[(at + Separator.Length)..];
            }
        }

        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
