using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Controls;

/// <summary>
/// The online chart: a smooth filled curve through the hourly averages, gridlines, and a
/// crosshair with the reading under the pointer. Gaps in the data break the curve
/// rather than dropping to zero - an hour with no reading is not an hour with nobody on.
/// </summary>
public sealed class OnlineChart : Control
{
    public static readonly StyledProperty<IEnumerable<ServerChartBar>?> BarsProperty =
        AvaloniaProperty.Register<OnlineChart, IEnumerable<ServerChartBar>?>(nameof(Bars));

    private static readonly Color LineColor = Color.Parse("#E04A68");
    private static readonly Color GridColor = Color.Parse("#2E252C");
    private static readonly Color TextColor = Color.Parse("#A99BA3");
    private static readonly Color TooltipBackground = Color.Parse("#221A20");
    private static readonly Color TooltipBorder = Color.Parse("#382C34");
    private static readonly Color GoldColor = Color.Parse("#E0B83A");

    private int _hoverIndex = -1;
    private INotifyCollectionChanged? _observed;

    static OnlineChart()
    {
        AffectsRender<OnlineChart>(BarsProperty);
        BarsProperty.Changed.AddClassHandler<OnlineChart>((chart, e) => chart.Observe(e.NewValue as INotifyCollectionChanged));
    }

    public IEnumerable<ServerChartBar>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    /// <summary>The bars live in an ObservableCollection that is cleared and refilled; redraw on that.</summary>
    private void Observe(INotifyCollectionChanged? collection)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnBarsChanged;
        }

        _observed = collection;

        if (_observed is not null)
        {
            _observed.CollectionChanged += OnBarsChanged;
        }
    }

    private void OnBarsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _hoverIndex = -1;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var bars = Bars?.ToList();

        if (bars is { Count: > 0 })
        {
            var x = e.GetPosition(this).X;
            var index = (int)Math.Round(x / Bounds.Width * (bars.Count - 1));
            index = Math.Clamp(index, 0, bars.Count - 1);

            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                InvalidateVisual();
            }
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hoverIndex = -1;
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var bars = Bars?.ToList() ?? new List<ServerChartBar>();

        var gridPen = new Pen(new SolidColorBrush(GridColor), 1);
        var dashedPen = new Pen(new SolidColorBrush(GridColor), 1) { DashStyle = new DashStyle(new double[] { 4, 6 }, 0) };

        context.DrawLine(gridPen, new Point(0, 0.5), new Point(width, 0.5));
        context.DrawLine(dashedPen, new Point(0, Math.Round(height / 2) + 0.5), new Point(width, Math.Round(height / 2) + 0.5));
        context.DrawLine(gridPen, new Point(0, height - 0.5), new Point(width, height - 0.5));

        if (bars.Count < 2)
        {
            return;
        }

        var step = width / (bars.Count - 1);

        Point PointAt(int i) => new(i * step, height - bars[i].Fraction * (height - 8) - 1);

        // Runs of consecutive readings, each its own curve; a gap ends the run.
        var runs = new List<List<int>>();
        List<int>? current = null;

        for (var i = 0; i < bars.Count; i++)
        {
            if (bars[i].HasData)
            {
                current ??= new List<int>();
                current.Add(i);
            }
            else if (current is not null)
            {
                runs.Add(current);
                current = null;
            }
        }

        if (current is not null)
        {
            runs.Add(current);
        }

        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x8C, LineColor.R, LineColor.G, LineColor.B), 0),
                new GradientStop(Color.FromArgb(0x00, LineColor.R, LineColor.G, LineColor.B), 1)
            }
        };

        var linePen = new Pen(new SolidColorBrush(LineColor), 2.5) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        foreach (var run in runs)
        {
            var points = run.Select(PointAt).ToList();

            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                Curve(g, points, closeToBaseline: false, height);
            }

            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                Curve(g, points, closeToBaseline: true, height);
            }

            context.DrawGeometry(fill, null, area);
            context.DrawGeometry(null, linePen, line);
        }

        // Gaps: a quiet tick on the baseline, so the missing hours are visibly missing.
        var gapBrush = new SolidColorBrush(TextColor, 0.5);

        for (var i = 0; i < bars.Count; i++)
        {
            if (!bars[i].HasData)
            {
                context.DrawRectangle(gapBrush, null, new Rect(i * step - 2, height - 4, 4, 3));
            }
        }

        if (_hoverIndex >= 0 && _hoverIndex < bars.Count && bars[_hoverIndex].HasData)
        {
            DrawCrosshair(context, PointAt(_hoverIndex), bars[_hoverIndex].Tooltip, width, height);
        }
    }

    /// <summary>
    /// A Catmull-Rom spline through the points, as cubic Béziers. Smooth enough to read as
    /// a trend, tight enough that it never overshoots the readings much.
    /// </summary>
    private static void Curve(StreamGeometryContext g, List<Point> points, bool closeToBaseline, double height)
    {
        if (points.Count == 1)
        {
            // One reading on its own: a short flat dash, so it is at least visible.
            var p = points[0];
            g.BeginFigure(new Point(p.X - 4, p.Y), closeToBaseline);
            g.LineTo(new Point(p.X + 4, p.Y));

            if (closeToBaseline)
            {
                g.LineTo(new Point(p.X + 4, height));
                g.LineTo(new Point(p.X - 4, height));
            }

            g.EndFigure(closeToBaseline);
            return;
        }

        g.BeginFigure(closeToBaseline ? new Point(points[0].X, height) : points[0], closeToBaseline);

        if (closeToBaseline)
        {
            g.LineTo(points[0]);
        }

        for (var i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[Math.Max(i - 1, 0)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(i + 2, points.Count - 1)];

            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);

            g.CubicBezierTo(c1, c2, p2);
        }

        if (closeToBaseline)
        {
            g.LineTo(new Point(points[^1].X, height));
        }

        g.EndFigure(closeToBaseline);
    }

    private void DrawCrosshair(DrawingContext context, Point at, string text, double width, double height)
    {
        var pen = new Pen(new SolidColorBrush(GoldColor, 0.8), 1) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };
        context.DrawLine(pen, new Point(at.X, 0), new Point(at.X, height));
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#0C0A0D")), new Pen(new SolidColorBrush(GoldColor), 2.5), at, 5, 5);

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            12,
            new SolidColorBrush(Color.Parse("#EFE9EC")));

        var padding = 8;
        var boxWidth = formatted.Width + padding * 2;
        var boxHeight = formatted.Height + padding * 2 - 2;

        // Keep the label inside the chart: to the right of the point unless that runs out.
        var x = at.X + 12;
        if (x + boxWidth > width)
        {
            x = at.X - 12 - boxWidth;
        }

        var y = Math.Clamp(at.Y - boxHeight - 10, 4, Math.Max(4, height - boxHeight - 4));

        var box = new Rect(x, y, boxWidth, boxHeight);
        context.DrawRectangle(new SolidColorBrush(TooltipBackground), new Pen(new SolidColorBrush(TooltipBorder), 1), box, 8, 8);
        context.DrawText(formatted, new Point(x + padding, y + padding - 1));
    }
}
