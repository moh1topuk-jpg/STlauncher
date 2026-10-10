using System;
using Avalonia;
using Avalonia.Controls.Primitives;

namespace STlauncher.App.Controls;

/// <summary>
/// A grid of pictures - worlds, screenshots - that decides how many go on a line from
/// the width it is given: three in the default window, two in a narrow one. XAML has no
/// width queries, and a count set from a page's code-behind is late for a list that
/// first appears in a window already resized.
/// </summary>
public sealed class TileGrid : UniformGrid
{
    public static readonly StyledProperty<double> MinTileWidthProperty =
        AvaloniaProperty.Register<TileGrid, double>(nameof(MinTileWidth), 230);

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<TileGrid, int>(nameof(MaxColumns), 4);

    /// <summary>A picture narrower than this is not worth a column of its own.</summary>
    public double MinTileWidth
    {
        get => GetValue(MinTileWidthProperty);
        set => SetValue(MinTileWidthProperty, value);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Width) && availableSize.Width > 0)
        {
            var fit = (int)Math.Floor((availableSize.Width + ColumnSpacing) / (Math.Max(1, MinTileWidth) + ColumnSpacing));
            var columns = Math.Clamp(fit, 1, Math.Max(1, MaxColumns));

            // Changing the count asks for another measure; the next pass finds it settled.
            if (Columns != columns)
            {
                Columns = columns;
            }
        }

        return base.MeasureOverride(availableSize);
    }
}
