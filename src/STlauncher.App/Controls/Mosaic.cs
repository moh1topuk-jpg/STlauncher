using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace STlauncher.App.Controls;

/// <summary>
/// A checkerboard of faint squares that fades out towards the left edge. Drawn as plain
/// rectangles: a tiled drawing brush under a fade costs several times more per frame, and
/// the page behind this is repainted sixty times a second.
/// </summary>
public sealed class Mosaic : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Mosaic, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<double> CellProperty =
        AvaloniaProperty.Register<Mosaic, double>(nameof(Cell), 28);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<Mosaic, CornerRadius>(nameof(CornerRadius));

    /// <summary>The share of the width, from the left, over which the squares fade in.</summary>
    public static readonly StyledProperty<double> FadeProperty =
        AvaloniaProperty.Register<Mosaic, double>(nameof(Fade), 0.7);

    private IBrush?[] _columns = [];
    private Color _paintedColor;
    private double _paintedWidth = -1;

    static Mosaic()
    {
        AffectsRender<Mosaic>(FillProperty, CellProperty, CornerRadiusProperty, FadeProperty);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double Cell
    {
        get => GetValue(CellProperty);
        set => SetValue(CellProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double Fade
    {
        get => GetValue(FadeProperty);
        set => SetValue(FadeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var cell = Cell;
        var width = Bounds.Width;
        var height = Bounds.Height;

        if (Fill is not ISolidColorBrush fill || cell < 1 || width < 1 || height < 1)
        {
            return;
        }

        var count = (int)Math.Ceiling(width / cell);
        var color = Color.FromArgb((byte)(fill.Color.A * fill.Opacity), fill.Color.R, fill.Color.G, fill.Color.B);

        // One brush per column, rebuilt only when the size or the colour changes.
        if (_columns.Length != count || color != _paintedColor || width != _paintedWidth)
        {
            _columns = new IBrush?[count];
            var reach = Math.Max(width * Fade, 1);

            for (var i = 0; i < count; i++)
            {
                var alpha = (byte)(color.A * Math.Clamp((i + 0.5) * cell / reach, 0, 1));
                _columns[i] = alpha == 0
                    ? null
                    : new ImmutableSolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            }

            _paintedColor = color;
            _paintedWidth = width;
        }

        using var clip = context.PushClip(new RoundedRect(new Rect(Bounds.Size), CornerRadius));

        for (var i = 0; i < count; i++)
        {
            if (_columns[i] is not { } brush)
            {
                continue;
            }

            for (var y = i % 2 == 0 ? 0 : cell; y < height; y += cell * 2)
            {
                context.FillRectangle(brush, new Rect(i * cell, y, cell, cell));
            }
        }
    }
}
