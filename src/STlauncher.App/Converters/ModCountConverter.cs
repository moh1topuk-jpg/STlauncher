using System;
using System.Globalization;
using Avalonia.Data.Converters;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Converters;

/// <summary>
/// Formats a mod count with the right word for it.
/// </summary>
/// <remarks>
/// The interface showed "24 модов", which is simply wrong Russian - the number and the
/// noun have to agree, and Russian needs three forms rather than the two English gets
/// away with. Doing it in a converter keeps the rule in one place and lets both the build
/// list and the build header use it.
/// </remarks>
public sealed class ModCountConverter : IValueConverter
{
    public static readonly ModCountConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0
        };

        return $"{count} {Word(count)}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string Word(int count)
    {
        var lastTwo = count % 100;
        var last = count % 10;

        // English defines the "few" and "many" forms as the same word, so the same rule
        // produces correct output in both languages.
        if (last == 1 && lastTwo != 11)
        {
            return MainWindowViewModel.Localize("Builds_ModsOne", "mod");
        }

        if (last is >= 2 and <= 4 && lastTwo is < 12 or > 14)
        {
            return MainWindowViewModel.Localize("Builds_ModsFew", "mods");
        }

        return MainWindowViewModel.Localize("Builds_ModsMany", "mods");
    }
}
