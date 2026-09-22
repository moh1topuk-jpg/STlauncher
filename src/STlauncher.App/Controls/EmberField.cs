using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace STlauncher.App.Controls;

/// <summary>
/// Faint embers drifting up behind the main screen, leaning away from the pointer. Kept
/// deliberately quiet: a few dozen soft dots at low opacity, no lines, so it reads as
/// atmosphere rather than a screensaver. Optionally joins close embers with hairlines
/// (<see cref="ShowLinks"/>), which turns it into the "constellation" look.
/// </summary>
public sealed class EmberField : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<EmberField, bool>(nameof(IsActive), true);

    public static readonly StyledProperty<bool> ShowLinksProperty =
        AvaloniaProperty.Register<EmberField, bool>(nameof(ShowLinks));

    public static readonly StyledProperty<int> CountProperty =
        AvaloniaProperty.Register<EmberField, int>(nameof(Count), 70);

    private static readonly Color Warm = Color.Parse("#E0B83A");
    private static readonly Color Rose = Color.Parse("#E04A68");

    private readonly List<Ember> _embers = new();
    private readonly Random _random = new();
    private readonly DispatcherTimer _timer;
    private Point _pointer = new(double.NaN, double.NaN);
    private Point _lean;
    private TopLevel? _topLevel;

    static EmberField()
    {
        AffectsRender<EmberField>(IsActiveProperty, ShowLinksProperty);
    }

    public EmberField()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Tick();
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool ShowLinks
    {
        get => GetValue(ShowLinksProperty);
        set => SetValue(ShowLinksProperty, value);
    }

    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Not hit-testable itself, so the pointer is watched at the window.
        _topLevel = TopLevel.GetTopLevel(this);

        if (_topLevel is not null)
        {
            _topLevel.PointerMoved += OnPointerMovedAnywhere;
            _topLevel.PointerExited += OnPointerLeft;
        }

        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();

        if (_topLevel is not null)
        {
            _topLevel.PointerMoved -= OnPointerMovedAnywhere;
            _topLevel.PointerExited -= OnPointerLeft;
            _topLevel = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnPointerMovedAnywhere(object? sender, PointerEventArgs e) => _pointer = e.GetPosition(this);

    private void OnPointerLeft(object? sender, PointerEventArgs e) => _pointer = new Point(double.NaN, double.NaN);

    private void Tick()
    {
        // A hidden page costs nothing: no drift, no redraw.
        if (!IsActive || !IsEffectivelyVisible || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        Seed();

        // The field leans a little away from the pointer, eased so it never jerks.
        var target = double.IsNaN(_pointer.X)
            ? new Point(0, 0)
            : new Point((_pointer.X - Bounds.Width / 2) / Bounds.Width, (_pointer.Y - Bounds.Height / 2) / Bounds.Height);

        _lean = new Point(_lean.X + (target.X - _lean.X) * 0.06, _lean.Y + (target.Y - _lean.Y) * 0.06);

        foreach (var ember in _embers)
        {
            ember.Y -= ember.Speed;
            ember.X += Math.Sin(ember.Phase + ember.Y * 0.01) * 0.15;
            ember.Phase += 0.01;

            if (ember.Y < -10)
            {
                ember.Y = Bounds.Height + 10;
                ember.X = _random.NextDouble() * Bounds.Width;
            }
        }

        InvalidateVisual();
    }

    private void Seed()
    {
        if (_embers.Count == Count)
        {
            return;
        }

        _embers.Clear();

        for (var i = 0; i < Count; i++)
        {
            var depth = _random.NextDouble();

            _embers.Add(new Ember
            {
                X = _random.NextDouble() * Bounds.Width,
                Y = _random.NextDouble() * Bounds.Height,
                Depth = depth,
                Radius = 0.8 + depth * 1.8,
                Speed = 0.08 + depth * 0.25,
                Opacity = 0.12 + depth * 0.3,
                Phase = _random.NextDouble() * Math.PI * 2,
                Rose = _random.NextDouble() < 0.3
            });
        }
    }

    public override void Render(DrawingContext context)
    {
        if (!IsActive || _embers.Count == 0)
        {
            return;
        }

        // Nearer embers lean more: that is what makes the lean read as depth.
        Point At(Ember e) => new(
            e.X - _lean.X * (20 + e.Depth * 50),
            e.Y - _lean.Y * (12 + e.Depth * 30));

        if (ShowLinks)
        {
            var pen = new Pen(new SolidColorBrush(Warm, 0.08), 1);

            for (var i = 0; i < _embers.Count; i++)
            {
                var a = At(_embers[i]);

                for (var j = i + 1; j < _embers.Count; j++)
                {
                    var b = At(_embers[j]);
                    var dx = a.X - b.X;
                    var dy = a.Y - b.Y;

                    if (dx * dx + dy * dy < 110 * 110)
                    {
                        context.DrawLine(pen, a, b);
                    }
                }
            }
        }

        foreach (var ember in _embers)
        {
            var at = At(ember);
            var colour = ember.Rose ? Rose : Warm;

            // A soft halo under a small core, so the dot glows rather than sits.
            context.DrawEllipse(new SolidColorBrush(colour, ember.Opacity * 0.25), null, at, ember.Radius * 3, ember.Radius * 3);
            context.DrawEllipse(new SolidColorBrush(colour, ember.Opacity), null, at, ember.Radius, ember.Radius);
        }
    }

    private sealed class Ember
    {
        public double X;
        public double Y;
        public double Depth;
        public double Radius;
        public double Speed;
        public double Opacity;
        public double Phase;
        public bool Rose;
    }
}
