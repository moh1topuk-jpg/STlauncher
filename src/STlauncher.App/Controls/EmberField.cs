using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

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
    private readonly System.Diagnostics.Stopwatch _clock = new();

    /// <summary>
    /// Behind another window the field is stepped on every third tick of the clock: some
    /// eight to ten times a second, about what it always was there.
    /// </summary>
    private const int BehindEvery = 3;
    private int _ticks;
    private bool _attached;

    /// <summary>True while the field takes its steps from the clock; false while it only looks whether it may.</summary>
    private bool _fast;
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
        Run(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        FrameClock.Frame -= OnFrame;
        FrameClock.Check -= OnCheck;

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
    /// Moves the field between the two lanes of the slow clock. Watched: a step on every
    /// tick of that clock - the field covers the whole page, so each of its steps draws
    /// the page again, and dots this slow do not need the display's own rate. Not watched
    /// - the page is hidden, the window is minimised or in the tray, or the game is running: a look
    /// a few times a second to see whether that changed, and nothing drawn.
    /// </summary>
    private void Run(bool fast)
    {
        FrameClock.Frame -= OnFrame;
        FrameClock.Check -= OnCheck;
        _fast = fast && _attached;

        if (!_attached)
        {
            return;
        }

        if (fast)
        {
            FrameClock.Frame += OnFrame;
        }
        else
        {
            FrameClock.Check += OnCheck;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Switched on, or given its place on the page: no reason to wait for the next look.
        if (_attached && !_fast && (change.Property == IsActiveProperty || change.Property == BoundsProperty))
        {
            _lastMs = _clock.Elapsed.TotalMilliseconds;
            Run(true);
        }
    }

    private bool IsWatched(out bool behind)
    {
        var watching = IsActive ? FrameClock.Of(this) : FrameClock.Watching.NotAtAll;
        behind = watching == FrameClock.Watching.Behind;
        return watching != FrameClock.Watching.NotAtAll;
    }

    private void OnFrame()
    {
        if (!IsWatched(out var behind))
        {
            Run(false);
            return;
        }

        if (behind && ++_ticks % BehindEvery != 0)
        {
            return;
        }

        Step();
    }

    private void OnCheck()
    {
        if (IsWatched(out _))
        {
            // Time stood still for the embers while nobody looked; they go on from where they were.
            _lastMs = _clock.Elapsed.TotalMilliseconds;
            Run(true);
        }
    }

    private void Step()
    {
        var now = _clock.Elapsed.TotalMilliseconds;

        // Motion is by the clock, not by the step, so every lane moves at the same speed;
        // a long pause is capped so the field does not jump on waking.
        var dt = Math.Min(now - _lastMs, 250) / 1000;
        _lastMs = now;

        if (Bounds.Width < 1 || Bounds.Height < 1)
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
