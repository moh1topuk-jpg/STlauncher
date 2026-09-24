using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using STlauncher.App.Controls;

namespace STlauncher.App.Views;

/// <summary>
/// Runs the splash story frame by frame on the render loop and hides itself at the end. The hold in the
/// middle lasts until <see cref="IsReady"/> is set by the view model, but never less than
/// the minimum, so the choreography always plays out, and never more than the cap, so a
/// start that hangs still shows the screen and its error.
/// </summary>
public partial class SplashOverlay : UserControl
{
    public static readonly StyledProperty<bool> IsReadyProperty =
        AvaloniaProperty.Register<SplashOverlay, bool>(nameof(IsReady));

    private const double GatherMs = 550;
    private const double MinHoldMs = 850;
    private const double MaxHoldMs = 8000;
    private const double FlashMs = 160;
    private const double ReleaseMs = 480;
    private const double RevealMs = 420;

    private readonly Stopwatch _clock = new();
    private readonly SplashFrame _frame = new();
    private readonly ScaleTransform _markScale = new(1, 1);
    private readonly TranslateTransform _markShift = new();
    private readonly SolidColorBrush _stage = new(Stage);
    private static readonly Color Stage = Color.FromRgb(0x0C, 0x0A, 0x0D);
    private double _flashStart = -1;
    private bool _done;

    private void NextFrame()
    {
        if (_done || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        top.RequestAnimationFrame(_ =>
        {
            Tick();
            NextFrame();
        });
    }

    /// <summary>Where the mark ends up: the rail logo's centre, in window coordinates.</summary>
    public Point RailLogoCentre { get; set; } = new(44, 70);

    public SplashOverlay()
    {
        InitializeComponent();

        Mark.RenderTransform = new TransformGroup { Children = { _markScale, _markShift } };
        Root.Background = _stage;

        AttachedToVisualTree += (_, _) =>
        {
            _clock.Restart();
            NextFrame();
        };
        SizeChanged += (_, _) => Layout();
    }

    public bool IsReady
    {
        get => GetValue(IsReadyProperty);
        set => SetValue(IsReadyProperty, value);
    }

    /// <summary>Raised once, when the overlay has faded out.</summary>
    public event EventHandler? Completed;

    private void Layout()
    {
        var w = Bounds.Width;
        var h = Bounds.Height;

        if (w <= 0 || h <= 0)
        {
            return;
        }

        Embers.Width = w;
        Embers.Height = h;

        var cx = w / 2;
        var cy = h * 0.44;

        Canvas.SetLeft(Glow, cx - Glow.Width / 2);
        Canvas.SetTop(Glow, cy - Glow.Height / 2);
        Canvas.SetLeft(MarkGlow, cx - MarkGlow.Width / 2);
        Canvas.SetTop(MarkGlow, cy - MarkGlow.Height / 2);
        Canvas.SetLeft(Mark, cx - Mark.Width / 2);
        Canvas.SetTop(Mark, cy - Mark.Height / 2);
        Canvas.SetLeft(Caption, cx - Math.Max(1, Caption.Bounds.Width) / 2);
        Canvas.SetTop(Caption, cy + 150);
    }

    private void Tick()
    {
        if (_done)
        {
            return;
        }

        var t = _clock.Elapsed.TotalMilliseconds;

        // ---- gather: sparks fly in, the mark appears with a small overshoot
        var gather = Math.Clamp(t / GatherMs, 0, 1);
        _frame.Gather = gather;
        Glow.Opacity = EaseOut(gather);

        var markIn = Math.Clamp((t - 40) / (GatherMs - 40), 0, 1);
        Mark.Opacity = Math.Min(1, markIn * 1.5);
        var markScale = 0.55 + 0.51 * EaseOut(markIn) - 0.06 * Math.Sin(markIn * Math.PI);
        _markScale.ScaleX = _markScale.ScaleY = markScale;

        Caption.Opacity = Math.Clamp((t - GatherMs - 100) / 300, 0, 1);

        if (Caption.Bounds.Width > 0)
        {
            Canvas.SetLeft(Caption, Bounds.Width / 2 - Caption.Bounds.Width / 2);
        }

        // ---- hold: the ring turns slowly until the build is ready
        var holdElapsed = Math.Max(0, t - GatherMs);
        _frame.Orbit = 22 * Math.Min(1, holdElapsed / 1100) + 8 * Math.Max(0, holdElapsed - 1100) / 1000;

        var canFlash = holdElapsed >= MinHoldMs && (IsReady || holdElapsed >= MaxHoldMs);

        if (_flashStart < 0 && canFlash)
        {
            _flashStart = t;
        }

        if (_flashStart >= 0)
        {
            var since = t - _flashStart;

            // ---- flash: the mark swells, the ring is pushed out
            var flash = since < FlashMs ? since / FlashMs : Math.Max(0, 1 - (since - FlashMs) / 220);
            _frame.Flash = flash;
            _markScale.ScaleX = _markScale.ScaleY = 1 + 0.12 * flash;
            MarkGlow.Opacity = flash;

            // ---- release: sparks settle across the window, the mark flies to the rail
            var release = Math.Clamp((since - FlashMs) / ReleaseMs, 0, 1);
            _frame.Release = release;
            Caption.Opacity = 1 - Math.Min(1, release * 2);

            var fly = EaseInOut(release);
            var cx = Bounds.Width / 2;
            var cy = Bounds.Height * 0.44;
            _markShift.X = (RailLogoCentre.X - cx) * fly;
            _markShift.Y = (RailLogoCentre.Y - cy) * fly;
            var landed = 40.0 / Mark.Width;
            var scale = (1 + 0.12 * flash) * (1 - fly) + landed * fly;
            _markScale.ScaleX = _markScale.ScaleY = scale;

            // ---- reveal: the stage fades, the screen underneath is already there. The
            // pieces fade one by one rather than the overlay as a whole, which would need
            // an off-screen copy of the window on every frame.
            var reveal = Math.Clamp((since - FlashMs - ReleaseMs + 120) / RevealMs, 0, 1);
            _stage.Color = Color.FromArgb((byte)(255 * (1 - reveal)), Stage.R, Stage.G, Stage.B);
            Glow.Opacity = 1 - reveal;
            _frame.Alpha = 1 - reveal;
            Mark.Opacity = 1 - Math.Clamp((reveal - 0.5) * 2, 0, 1);

            if (reveal >= 1)
            {
                _done = true;
                IsVisible = false;
                Completed?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        Embers.Update(_frame);
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    private static double EaseInOut(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }
}
