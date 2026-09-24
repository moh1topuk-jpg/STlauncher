using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace STlauncher.App.Controls;

/// <summary>Where the splash is in its story; the overlay advances it, the embers draw it.</summary>
public sealed class SplashFrame
{
    /// <summary>0..1: from born-in-flight to sitting on the ring.</summary>
    public double Gather;

    /// <summary>Degrees the whole ring has turned while holding.</summary>
    public double Orbit;

    /// <summary>0..1: the moment the build is ready; the ring is pushed outwards.</summary>
    public double Flash;

    /// <summary>0..1: from the ring back out to the resting spots across the window.</summary>
    public double Release;

    /// <summary>Overall opacity of the embers, for the hand-over to the live background.</summary>
    public double Alpha = 1;
}

/// <summary>
/// The splash's embers: the same sparks that drift behind the main screen, choreographed
/// once. Each ember has three places - where it is born, its seat on the ring around the
/// mark, and where it rests afterwards - and the frame says how far between them it is.
/// </summary>
public sealed class SplashEmbers : Control
{
    private static readonly Color Warm = Color.Parse("#E0B83A");
    private static readonly Color Rose = Color.Parse("#E04A68");

    private readonly List<Ember> _embers = new();
    private readonly Random _random = new(7);
    private SplashFrame _frame = new();
    private Size _laidOutFor;

    public SplashEmbers()
    {
        IsHitTestVisible = false;
    }

    /// <summary>The centre of the ring, in this control's coordinates.</summary>
    public Point RingCentre => new(Bounds.Width / 2, Bounds.Height * 0.44);

    public void Update(SplashFrame frame)
    {
        _frame = frame;
        InvalidateVisual();
    }

    private void EnsureEmbers()
    {
        if (_embers.Count > 0 && _laidOutFor == Bounds.Size)
        {
            return;
        }

        _embers.Clear();
        _laidOutFor = Bounds.Size;

        var centre = RingCentre;
        const int count = 28;

        for (var i = 0; i < count; i++)
        {
            // Two rings, seats spread evenly with a little jitter so it reads as sparks, not a dial.
            var inner = i % 2 == 0;
            var radius = inner ? 84 : 118;
            var angle = i / (double)count * Math.PI * 2 + _random.NextDouble() * 0.25;

            var rest = new Point(
                20 + _random.NextDouble() * Math.Max(1, Bounds.Width - 40),
                20 + _random.NextDouble() * Math.Max(1, Bounds.Height - 40));

            // Born a little beyond its resting spot, so the first move is inward.
            var birth = new Point(
                centre.X + (rest.X - centre.X) * 1.15,
                centre.Y + (rest.Y - centre.Y) * 1.15 - 20);

            _embers.Add(new Ember
            {
                Birth = birth,
                Rest = rest,
                Angle = angle,
                Radius = radius,
                Size = inner ? 3.0 : 2.2,
                Rose = _random.NextDouble() < 0.3,
                Delay = _random.NextDouble() * 0.2
            });
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        EnsureEmbers();

        var centre = RingCentre;
        var frame = _frame;
        var orbit = frame.Orbit * Math.PI / 180;

        foreach (var ember in _embers)
        {
            // Gather: birth -> ring along an arc that lifts a little, each ember on its own delay.
            var gather = Math.Clamp((frame.Gather - ember.Delay) / (1 - ember.Delay), 0, 1);
            var g = EaseOutCubic(gather);

            var ringRadius = ember.Radius * (1 + 0.35 * frame.Flash);
            var seat = new Point(
                centre.X + Math.Cos(ember.Angle + orbit) * ringRadius,
                centre.Y + Math.Sin(ember.Angle + orbit) * ringRadius);

            var mid = new Point((ember.Birth.X + seat.X) / 2, (ember.Birth.Y + seat.Y) / 2 - 36);
            var onArc = Bezier(ember.Birth, mid, seat, g);

            // Release: ring -> rest, eased out, so the sparks settle rather than snap.
            var r = EaseOutCubic(frame.Release);
            var at = new Point(onArc.X + (ember.Rest.X - onArc.X) * r, onArc.Y + (ember.Rest.Y - onArc.Y) * r);

            var opacity = Math.Min(1, gather * 1.6) * (1 - 0.45 * r) * frame.Alpha;
            var scale = 0.4 + 0.6 * g + 0.4 * frame.Flash - 0.3 * r;
            var size = ember.Size * scale;

            if (opacity <= 0.01)
            {
                continue;
            }

            var colour = ember.Rose ? Rose : Warm;
            context.DrawEllipse(new SolidColorBrush(colour, opacity * 0.25), null, at, size * 3, size * 3);
            context.DrawEllipse(new SolidColorBrush(colour, opacity), null, at, size, size);
        }
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    private static Point Bezier(Point a, Point b, Point c, double t)
    {
        var u = 1 - t;
        return new Point(
            u * u * a.X + 2 * u * t * b.X + t * t * c.X,
            u * u * a.Y + 2 * u * t * b.Y + t * t * c.Y);
    }

    private sealed class Ember
    {
        public Point Birth;
        public Point Rest;
        public double Angle;
        public double Radius;
        public double Size;
        public bool Rose;
        public double Delay;
    }
}
