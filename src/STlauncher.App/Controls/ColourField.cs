using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using STlauncher.Core.Skins;

namespace STlauncher.App.Controls;

/// <summary>
/// The editor's colour chooser: a square where right is more colour and up is more
/// light, and a strip of hues under it. Three gradients and two markers, drawn by hand,
/// instead of a colour-picker package the launcher would carry for this one screen.
/// </summary>
public sealed class ColourField : Control
{
    public static readonly StyledProperty<Color> ColourProperty =
        AvaloniaProperty.Register<ColourField, Color>(nameof(Colour), Colors.Black, defaultBindingMode: BindingMode.TwoWay);

    private const double StripHeight = 14;
    private const double Gap = 10;
    private const double Radius = 8;

    private static readonly IBrush WhiteToClear = new ImmutableLinearGradientBrush(
        new[] { new ImmutableGradientStop(0, Colors.White), new ImmutableGradientStop(1, Color.FromArgb(0, 255, 255, 255)) },
        startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative),
        endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));

    private static readonly IBrush ClearToBlack = new ImmutableLinearGradientBrush(
        new[] { new ImmutableGradientStop(0, Color.FromArgb(0, 0, 0, 0)), new ImmutableGradientStop(1, Colors.Black) },
        startPoint: new RelativePoint(0.5, 0, RelativeUnit.Relative),
        endPoint: new RelativePoint(0.5, 1, RelativeUnit.Relative));

    private static readonly IBrush Rainbow = new ImmutableLinearGradientBrush(
        new[]
        {
            new ImmutableGradientStop(0, Color.FromRgb(255, 0, 0)),
            new ImmutableGradientStop(1 / 6.0, Color.FromRgb(255, 255, 0)),
            new ImmutableGradientStop(2 / 6.0, Color.FromRgb(0, 255, 0)),
            new ImmutableGradientStop(3 / 6.0, Color.FromRgb(0, 255, 255)),
            new ImmutableGradientStop(4 / 6.0, Color.FromRgb(0, 0, 255)),
            new ImmutableGradientStop(5 / 6.0, Color.FromRgb(255, 0, 255)),
            new ImmutableGradientStop(1, Color.FromRgb(255, 0, 0))
        },
        startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative),
        endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));

    private static readonly IPen DarkRing = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 1);
    private static readonly IPen LightRing = new ImmutablePen(Brushes.White, 2);

    private enum Drag
    {
        None,
        Square,
        Strip
    }

    // Kept beside the colour: grey has no hue and black has no saturation, and the
    // markers must not jump home every time the colour passes through one of them.
    private double _hue;
    private double _saturation;
    private double _value;
    private Drag _drag;
    private bool _setting;

    static ColourField()
    {
        AffectsRender<ColourField>(ColourProperty);
    }

    public ColourField()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public Color Colour
    {
        get => GetValue(ColourProperty);
        set => SetValue(ColourProperty, value);
    }

    private Rect Square => new(0, 0, Bounds.Width, Math.Max(0, Bounds.Height - StripHeight - Gap));

    private Rect Strip => new(0, Bounds.Height - StripHeight, Bounds.Width, StripHeight);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != ColourProperty || _setting)
        {
            return;
        }

        // Set from outside: the hex box, the pipette, a recent colour.
        var (hue, saturation, value) = SkinColour.ToHsv(Colour.ToUInt32());
        _value = value;

        if (value > 0)
        {
            _saturation = saturation;

            if (saturation > 0)
            {
                _hue = hue;
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetPosition(this);
        _drag = Strip.Inflate(new Thickness(0, Gap / 2)).Contains(point) ? Drag.Strip : Drag.Square;
        e.Pointer.Capture(this);
        Track(point);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_drag != Drag.None)
        {
            Track(e.GetPosition(this));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = Drag.None;
        e.Pointer.Capture(null);
    }

    private void Track(Point point)
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        if (_drag == Drag.Strip)
        {
            _hue = Math.Clamp(point.X / Bounds.Width, 0, 1) * 360;

            // The far right of the strip is red again; 360 would wrap the marker to the left.
            _hue = Math.Min(_hue, 359.9);
        }
        else
        {
            var square = Square;

            if (square.Height <= 0)
            {
                return;
            }

            _saturation = Math.Clamp(point.X / square.Width, 0, 1);
            _value = 1 - Math.Clamp(point.Y / square.Height, 0, 1);
        }

        _setting = true;

        try
        {
            SetCurrentValue(ColourProperty, Color.FromUInt32(SkinColour.FromHsv(_hue, _saturation, _value)));
        }
        finally
        {
            _setting = false;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var square = Square;
        var strip = Strip;

        if (square.Width <= 0 || square.Height <= 0)
        {
            return;
        }

        var pure = new ImmutableSolidColorBrush(Color.FromUInt32(SkinColour.FromHsv(_hue, 1, 1)));

        context.DrawRectangle(pure, null, square, Radius, Radius);
        context.DrawRectangle(WhiteToClear, null, square, Radius, Radius);
        context.DrawRectangle(ClearToBlack, null, square, Radius, Radius);
        context.DrawRectangle(Rainbow, null, strip, StripHeight / 2, StripHeight / 2);

        var at = new Point(
            Math.Clamp(_saturation * square.Width, 5, square.Width - 5),
            Math.Clamp((1 - _value) * square.Height, 5, square.Height - 5));

        context.DrawEllipse(new ImmutableSolidColorBrush(Colour), DarkRing, at, 7, 7);
        context.DrawEllipse(null, LightRing, at, 6, 6);

        var along = Math.Clamp(_hue / 360 * strip.Width, StripHeight / 2, strip.Width - StripHeight / 2);
        var knob = new Point(along, strip.Center.Y);

        context.DrawEllipse(pure, DarkRing, knob, StripHeight / 2 + 2, StripHeight / 2 + 2);
        context.DrawEllipse(null, LightRing, knob, StripHeight / 2 + 1, StripHeight / 2 + 1);
    }
}
