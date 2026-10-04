using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace STlauncher.App.Converters;

/// <summary>
/// Gives a build or a mod a colour of its own from its id: one of the tint brushes in
/// Colors.axaml, always the same one for the same id.
/// </summary>
/// <remarks>
/// A list of builds that all carry the same icon is a list of names to read; a colour per
/// build lets the eye find "the green one". The hash is written out here because
/// string.GetHashCode changes with every start, and a build that is blue today and gold
/// tomorrow helps nobody.
/// </remarks>
public sealed class TintBrushConverter : IValueConverter
{
    public static readonly TintBrushConverter Instance = new();

    /// <summary>How many TintBrush0…N resources there are.</summary>
    public const int Count = 6;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = "TintBrush" + IndexFor(value?.ToString());

        return Application.Current is { } app && app.TryFindResource(key, out var brush) ? brush : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>
    /// FNV-1a over the text, then a stir: small and stable across runs. Without the stir
    /// ids that differ only in their last characters landed on the same colour.
    /// </summary>
    public static int IndexFor(string? text)
    {
        unchecked
        {
            var hash = 2166136261u;

            foreach (var c in text ?? string.Empty)
            {
                hash = (hash ^ c) * 16777619u;
            }

            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            hash *= 0x846CA68Bu;
            hash ^= hash >> 16;

            return (int)(hash % Count);
        }
    }
}
