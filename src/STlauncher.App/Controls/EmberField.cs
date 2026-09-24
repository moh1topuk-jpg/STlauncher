using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
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

    /// <summary>On a light ground the embers are darker and fewer: dust in sunlight, not sparks.</summary>
    public static readonly StyledProperty<bool> IsLightProperty =
        AvaloniaProperty.Register<EmberField, bool>(nameof(IsLight));

    private static readonly Color Warm = Color.Parse("#E0B83A");
    private static readonly Color Rose = Color.Parse("#E04A68");
    private static readonly Color WarmLight = Color.Parse("#A8801A");
    private static readonly Color RoseLight = Color.Parse("#A3243F");

    private readonly List<Ember> _embers = new();
    private readonly Random _random = new();
    private readonly DispatcherTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = new();

    /// <summary>The shortest gap between two drawn frames; 0 draws on every screen frame.</summary>
    private const double MinFrameMs = 0;
    private double _lastDrawMs;
    private bool _attached;
    private double _lastMs;
    private Point _pointer = new(double.NaN, double.NaN);
    private Point _lean;
    private TopLevel? _topLevel;
    private bool _paintedLight;

    // Each dot is one copy of a small picture (halo and core together) drawn once per colour
    // and depth band, so a frame is seventy image blits rather than 140 anti-aliased fills.
    private const int Bands = 4;
    private const int SpritePixels = 24;
    private readonly RenderTargetBitmap?[] _sprites = new RenderTargetBitmap?[2 * Bands];

    static EmberField()
    {
        AffectsRender<EmberField>(IsActiveProperty, ShowLinksProperty, IsLightProperty);
    }

    public EmberField()
    {
        IsHitTestVisible = false;

        // The slow lane: while the field is hidden, minimised or behind another window it is
        // stepped by this timer; while it is watched it runs in step with the screen.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Step();
            Schedule();
        };
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsLight
    {
        get => GetValue(IsLightProperty);
        set => SetValue(IsLightProperty, value);
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

        _attached = true;
        _clock.Restart();
        _lastMs = 0;
        Schedule();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
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

    /// <summary>
    /// Picks the lane for the next step. Watched: the next screen frame, so the drift is as
    /// smooth as the display allows. Behind another window: ten steps a second. Hidden or
    /// minimised: a look every half second to see whether that changed, and nothing drawn.
    /// </summary>
    private void Schedule()
    {
        if (!_attached)
        {
            return;
        }

        var window = _topLevel as Window;
        var shown = IsActive && IsEffectivelyVisible && window?.WindowState != WindowState.Minimized;

        if (!shown)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Start();
            return;
        }

        if (window is { IsActive: false })
        {
            _timer.Interval = TimeSpan.FromMilliseconds(100);
            _timer.Start();
            return;
        }

        _topLevel?.RequestAnimationFrame(_ =>
        {
            if (_clock.Elapsed.TotalMilliseconds - _lastDrawMs >= MinFrameMs - 2)
            {
                _lastDrawMs = _clock.Elapsed.TotalMilliseconds;
                Step();
            }

            Schedule();
        });
    }

    private void Step()
    {
        var now = _clock.Elapsed.TotalMilliseconds;

        // Motion is by the clock, not by the step, so every lane moves at the same speed;
        // a long pause is capped so the field does not jump on waking.
        var dt = Math.Min(now - _lastMs, 250) / 1000;
        _lastMs = now;

        // A hidden page costs nothing: no drift, no redraw.
        if (!IsActive || !IsEffectivelyVisible || Bounds.Width < 1 || Bounds.Height < 1 ||
            _topLevel is Window { WindowState: WindowState.Minimized })
        {
            return;
        }

        Seed();

        // The field leans a little away from the pointer, eased so it never jerks.
        var target = double.IsNaN(_pointer.X)
            ? new Point(0, 0)
            : new Point((_pointer.X - Bounds.Width / 2) / Bounds.Width, (_pointer.Y - Bounds.Height / 2) / Bounds.Height);

        var ease = 1 - Math.Pow(1 - 0.06, dt * 25);
        _lean = new Point(_lean.X + (target.X - _lean.X) * ease, _lean.Y + (target.Y - _lean.Y) * ease);

        foreach (var ember in _embers)
        {
            ember.Y -= ember.Speed * dt;
            ember.X += Math.Sin(ember.Phase + ember.Y * 0.01) * 3.75 * dt;
            ember.Phase += 0.25 * dt;

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
                Speed = 2 + depth * 6.25,
                Phase = _random.NextDouble() * Math.PI * 2,
                Rose = _random.NextDouble() < 0.3,
                Band = Math.Min(Bands - 1, (int)(depth * Bands))
            });
        }

        Paint(IsLight);
    }

    /// <summary>
    /// Draws the sprites once per theme: one per colour and depth band. A sprite is the dot
    /// at its band's size with the halo around it, on a transparent ground, at 2x so it
    /// stays soft on a high-density screen. On a light ground there is no halo.
    /// </summary>
    private void Paint(bool light)
    {
        _paintedLight = light;

        for (var band = 0; band < Bands; band++)
        {
            // The same opacity and size the dots had when each carried its own.
            var depth = (band + 0.5) / Bands;
            var opacity = 0.12 + depth * 0.3;
            var radius = 0.8 + depth * 1.8;

            if (light)
            {
                opacity *= 0.45;
            }

            for (var rose = 0; rose < 2; rose++)
            {
                var colour = light ? (rose == 1 ? RoseLight : WarmLight) : (rose == 1 ? Rose : Warm);
                var index = rose * Bands + band;
                _sprites[index]?.Dispose();

                // Drawn at twice the size in plain pixels and shown at half: explicit on both
                // ends, so no DPI arithmetic can shift or crop the dot.
                var sprite = new RenderTargetBitmap(new PixelSize(SpritePixels * 2, SpritePixels * 2), new Vector(96, 96));

                using (var ctx = sprite.CreateDrawingContext())
                {
                    var centre = new Point(SpritePixels, SpritePixels);

                    if (!light)
                    {
                        ctx.DrawEllipse(new ImmutableSolidColorBrush(colour, opacity * 0.25), null, centre, radius * 6, radius * 6);
                    }

                    ctx.DrawEllipse(new ImmutableSolidColorBrush(colour, opacity), null, centre, radius * 2, radius * 2);
                }

                _sprites[index] = sprite;
            }
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

        var light = IsLight;

        if (light != _paintedLight)
        {
            Paint(light);
        }

        const double half = SpritePixels / 2.0;
        var source = new Rect(0, 0, SpritePixels * 2, SpritePixels * 2);

        foreach (var ember in _embers)
        {
            if (_sprites[(ember.Rose ? Bands : 0) + ember.Band] is not { } sprite)
            {
                continue;
            }

            var at = At(ember);
            context.DrawImage(sprite, source, new Rect(at.X - half, at.Y - half, SpritePixels, SpritePixels));
        }
    }

    private sealed class Ember
    {
        public double X;
        public double Y;
        public double Depth;
        public double Radius;
        public double Speed;
        public double Phase;
        public bool Rose;
        public int Band;
    }
}
