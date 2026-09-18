using System;
using System.Globalization;
using Avalonia.Data.Converters;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Converters;

/// <summary>Bytes as the size a person reads: "340 KB", "12.4 MB".</summary>
public sealed class FileSizeConverter : IValueConverter
{
    public static readonly FileSizeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bytes = value switch
        {
            long l => l,
            int i => i,
            _ => 0L
        };

        if (bytes >= 1024L * 1024)
        {
            return MainWindowViewModel.Localize("Size_MbPrecise", "{0:F1} MB", bytes / 1024d / 1024);
        }

        return MainWindowViewModel.Localize("Size_Kb", "{0} KB", Math.Max(1, bytes / 1024));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
