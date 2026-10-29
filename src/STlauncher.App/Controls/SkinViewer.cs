using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using STlauncher.App.Services;
using STlauncher.App.ViewModels;
using STlauncher.Core.Emotes;

namespace STlauncher.App.Controls;

/// <summary>Something the figure wears over its head for a season. Drawn by the viewer only: the skin is never touched.</summary>
public enum HeadDecoration
{
    None,

    /// <summary>A carved pumpkin, the way the game draws one on a player's head.</summary>
    Pumpkin
}

/// <summary>
/// The player model with the skin on it: turning slowly in the small preview, alive on
/// the home screen - standing, waving, looking around, sitting down - and draggable with
/// the mouse in both.
/// </summary>
/// <remarks>
/// There is no 3D API here, and none is needed: the model is nothing but boxes, and under
/// an orthographic camera every face of a box lands on the screen as a parallelogram -
/// which is exactly an affine transform of the texture rectangle it wears. So each face
/// is one DrawImage under a matrix, faces are drawn back to front, and those turned away
/// are skipped. Lighting is a translucent shade over the faces that look away from the
/// light. It runs on the ordinary drawing pipeline and costs nothing to set up.
///
/// Movement is the same boxes turned about their joints before the camera turns the
/// whole figure: a shoulder, a hip, the neck. A pose is six such turns and a lift; a
/// clip is a pose as a function of time; and one clip fades into the next, so nothing
/// snaps. An Emotecraft emote from the build is one more clip: its keyframes sampled
/// into a pose, with the joints moved as well as turned.
///
/// Emotes also bend: an arm at the elbow, a leg at the knee, the chest at the waist. The
/// mod deforms a mesh for that; boxes do not deform, so a bent part is drawn as its two
/// halves, one of them turned about the hinge in the middle, and a thin slice between
/// them that closes the wedge the turn opens on the outside. A part that is not bent is
/// still the one box it always was.
/// </remarks>
public sealed class SkinViewer : Control
{
    public static readonly StyledProperty<PlayerSkin?> SkinProperty =
        AvaloniaProperty.Register<SkinViewer, PlayerSkin?>(nameof(Skin));

    public static readonly StyledProperty<bool> AutoRotateProperty =
        AvaloniaProperty.Register<SkinViewer, bool>(nameof(AutoRotate), true);

    /// <summary>True on the home screen: the figure plays its clips instead of standing still.</summary>
    public static readonly StyledProperty<bool> AnimatedProperty =
        AvaloniaProperty.Register<SkinViewer, bool>(nameof(Animated));

    public static readonly DirectProperty<SkinViewer, string> PoseNameProperty =
        AvaloniaProperty.RegisterDirect<SkinViewer, string>(nameof(PoseName), viewer => viewer.PoseName);

    /// <summary>The emotes of the build being played: the figure takes turns between these and its own poses.</summary>
    public static readonly StyledProperty<IReadOnlyList<Emote>?> EmotesProperty =
        AvaloniaProperty.Register<SkinViewer, IReadOnlyList<Emote>?>(nameof(Emotes));

    /// <summary>
    /// What the figure wears over its head: the home stage's seasonal pumpkin. Only the home
    /// stage sets it; the skin library, the editor and anything saved or shared never do, and
    /// the skin's texture is not changed by it - the block is drawn instead of the head.
    /// </summary>
    public static readonly StyledProperty<HeadDecoration> HeadDecorationProperty =
        AvaloniaProperty.Register<SkinViewer, HeadDecoration>(nameof(HeadDecoration));

    private static readonly Vector3 Light = Normalize(new Vector3(-0.4, 0.8, 0.6));

    /// <summary>How long one clip takes to give way to the next.</summary>
    private const double BlendSeconds = 0.45;

    /// <summary>A looping emote is watched for whole passes until this long has gone by.</summary>
    private const double LoopWatchSeconds = 6;

    /// <summary>No emote holds the stage longer than this, however long its file says it is.</summary>
    private const double MaxEmoteSeconds = 16;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>
    /// The figure's own time, in seconds: it runs while the figure is being stepped and
    /// stands still while it is not, so a figure that was out of sight goes on from the
    /// pose it stopped in instead of jumping to where the wall clock would have it.
    /// </summary>
    private double _animSeconds;

    /// <summary>When the figure was last stepped, by <see cref="_clock"/>.</summary>
    private double _lastStep;

    /// <summary>Behind another window the figure is stepped on every sixth tick of the slow clock, as before.</summary>
    private const int BehindEvery = 6;

    /// <summary>How fast the small preview turns by itself, radians a second, and how long after a drag it waits.</summary>
    private const double SpinPerSecond = 0.24;
    private const double SpinAfterSeconds = 1.5;

    /// <summary>
    /// The most frames a second the figure draws, 0 for "as many as the display shows".
    /// The player's choice in Settings: every display frame is the smoothest and costs the
    /// most, and a weak PC may prefer 60 or 30.
    /// </summary>
    public static int FrameCap { get; set; }

    /// <summary>True while a display frame has been asked for and has not come yet.</summary>
    private bool _frameRequested;

    /// <summary>The display's own time at the last frame the figure was stepped on; null before the first.</summary>
    private TimeSpan? _lastFrameTime;
    private readonly Random _random = new();

    private double _yaw = 0.55;
    private double _pitch = 0.15;
    private Point? _dragFrom;
    private double _idleSeconds;
    private int _pendingTicks;
    private bool _attached;

    // The camera's turn, worked out once a frame instead of once a corner.
    private double _cosYaw = 1;
    private double _sinYaw;
    private double _cosPitch = 1;
    private double _sinPitch;

    private MotionClip _clip = Clips[0];
    private double _clipStarted;
    private double _clipLength = 6;
    private Pose _blendFrom = Pose.Rest;
    private double _blendStarted = double.NegativeInfinity;
    private int _nextFeature;
    private bool _emoteIsNext;
    private int _lastEmote = -1;
    private string _poseName = string.Empty;

    /// <summary>The parts of the frame being drawn, kept between frames so the list is not made anew each time.</summary>
    private readonly List<Piece> _pieces = new();

    static SkinViewer()
    {
        AffectsRender<SkinViewer>(SkinProperty, AnimatedProperty, HeadDecorationProperty);
    }

    public SkinViewer()
    {
        // Smoothing on, deliberately: the texture drawn is the pre-enlarged one, whose
        // pixels are already eight wide, so smoothing only softens the edges of each
        // face by a hair - which is exactly what removes the staircase along them.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        Cursor = new Cursor(StandardCursorType.Hand);

        // The moving figure is redrawn on every frame of the display; the cubic filter that makes
        // a still one flawless is not worth that many times its price, and in motion
        // nobody can tell.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == AnimatedProperty)
            {
                RenderOptions.SetBitmapInterpolationMode(this,
                    Animated ? BitmapInterpolationMode.MediumQuality : BitmapInterpolationMode.HighQuality);
            }

            if (e.Property == AnimatedProperty || e.Property == AutoRotateProperty)
            {
                SyncClock();
            }
        };

        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            SyncClock();
            UpdatePoseName();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            SyncClock();
        };
    }

    /// <summary>
    /// A figure that neither turns by itself nor plays its clips has nothing to tick for:
    /// the skin library shows a dozen of them, and a dozen idle timers are still timers.
    /// One that does move has three ways to keep time, by how it is being watched.
    /// In front: a step on every frame the display shows, sixty or a hundred and
    /// forty-four a second, with the pose worked out from the time that has passed - the
    /// figure is as smooth as the screen is. Behind another window: a few steps a second
    /// from the slow clock, as it always was there. Not seen at all - its page hidden, the
    /// window minimised or in the tray, the game running: nothing, and a look a few times
    /// a second whether that is still so.
    /// </summary>
    private void SyncClock()
    {
        FrameClock.Frame -= OnSlowTick;
        FrameClock.Check -= OnCheck;

        if (!_attached || !(AutoRotate || Animated))
        {
            return;
        }

        switch (FrameClock.Of(this))
        {
            case FrameClock.Watching.NotAtAll:
                FrameClock.Check += OnCheck;
                break;

            case FrameClock.Watching.Behind:
                _lastStep = _clock.Elapsed.TotalSeconds;
                _pendingTicks = 0;
                FrameClock.Frame += OnSlowTick;
                break;

            default:
                _lastFrameTime = null;
                RequestFrame();
                break;
        }
    }

    private void OnCheck()
    {
        if (FrameClock.Of(this) != FrameClock.Watching.NotAtAll)
        {
            SyncClock();
        }
    }

    private void RequestFrame()
    {
        if (_frameRequested || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        _frameRequested = true;
        top.RequestAnimationFrame(OnDisplayFrame);
    }

    /// <summary>
    /// One frame of the display. The time comes from the display itself, so the figure
    /// moves at the same speed at any refresh rate and a frame that came late is a longer
    /// step, not a slower figure.
    /// </summary>
    private void OnDisplayFrame(TimeSpan time)
    {
        _frameRequested = false;

        if (!_attached || !(AutoRotate || Animated))
        {
            return;
        }

        if (FrameClock.Of(this) != FrameClock.Watching.InFront)
        {
            // No longer in front: over to the slow clock, or to waiting.
            SyncClock();
            return;
        }

        // Under a ceiling a frame that came too soon is let go by: nothing is stepped or
        // redrawn, the next one is asked for. The margin keeps 60 on a 144 Hz display from
        // falling to 48 because a frame arrived a hair early.
        if (FrameCap > 0 && _lastFrameTime is { } previous && (time - previous).TotalSeconds < 1.0 / FrameCap - 0.002)
        {
            RequestFrame();
            return;
        }

        // A long gap - the first frame, a window dragged, a stall - is capped so the figure does not leap.
        var elapsed = _lastFrameTime is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.25) : 0;
        _lastFrameTime = time;

        Step(elapsed);
        RequestFrame();
    }

    public PlayerSkin? Skin
    {
        get => GetValue(SkinProperty);
        set => SetValue(SkinProperty, value);
    }

    public bool AutoRotate
    {
        get => GetValue(AutoRotateProperty);
        set => SetValue(AutoRotateProperty, value);
    }

    public bool Animated
    {
        get => GetValue(AnimatedProperty);
        set => SetValue(AnimatedProperty, value);
    }

    /// <summary>What the figure is doing now, in the interface language: "Waving".</summary>
    public string PoseName
    {
        get => _poseName;
        private set => SetAndRaise(PoseNameProperty, ref _poseName, value);
    }

    /// <summary>Turn about the vertical axis, radians. Settable so a test can look from any side.</summary>
    public double Yaw
    {
        get => _yaw;
        set
        {
            _yaw = value;
            InvalidateVisual();
        }
    }

    public double Pitch
    {
        get => _pitch;
        set
        {
            _pitch = Math.Clamp(value, -0.9, 0.9);
            InvalidateVisual();
        }
    }

    public IReadOnlyList<Emote>? Emotes
    {
        get => GetValue(EmotesProperty);
        set => SetValue(EmotesProperty, value);
    }

    public HeadDecoration HeadDecoration
    {
        get => GetValue(HeadDecorationProperty);
        set => SetValue(HeadDecorationProperty, value);
    }

    /// <summary>The figure's own poses, standing first: the resource key of each name, and the name in English.</summary>
    public static IEnumerable<(string Key, string Fallback)> BuiltInPoses => Clips.Select(clip => (clip.Key, clip.Fallback));

    /// <summary>Skips to the next clip that is not plain standing. The button beside the figure.</summary>
    public void NextPose()
    {
        StartClip(NextFeature());
        InvalidateVisual();
    }

    /// <summary>Goes straight to one of the figure's own poses, by the key of its name.</summary>
    public void PlayPose(string key)
    {
        if (Clips.FirstOrDefault(clip => clip.Key == key) is { } clip)
        {
            StartClip(clip);
            InvalidateVisual();
        }
    }

    /// <summary>Plays an emote once (a looping one, for a few passes), then goes back to taking turns.</summary>
    public void PlayEmote(Emote emote)
    {
        StartClip(ClipOf(emote));
        InvalidateVisual();
    }

    /// <summary>
    /// What to show after standing: the figure's own poses in order and, while the build
    /// has emotes, one of those every other time, never the same one twice running.
    /// </summary>
    private MotionClip NextFeature()
    {
        var emotes = Emotes;

        if (emotes is { Count: > 0 } && _emoteIsNext)
        {
            _emoteIsNext = false;

            var pick = _random.Next(emotes.Count);

            if (pick == _lastEmote)
            {
                pick = (pick + 1) % emotes.Count;
            }

            _lastEmote = pick;
            return ClipOf(emotes[pick]);
        }

        _emoteIsNext = true;
        return Clips[1 + _nextFeature++ % (Clips.Length - 1)];
    }

    /// <summary>
    /// The lane for a window that is on screen but not in front: every sixth tick of the
    /// slow clock, a handful of steps a second. The figure is decoration, and behind
    /// another window decoration does not get to keep a laptop's fan running.
    /// </summary>
    private void OnSlowTick()
    {
        var watching = FrameClock.Of(this);

        if (watching != FrameClock.Watching.Behind)
        {
            SyncClock();
            return;
        }

        if (++_pendingTicks < BehindEvery)
        {
            return;
        }

        _pendingTicks = 0;

        // By the clock, not by the tick, so every lane plays at the same speed.
        var now = _clock.Elapsed.TotalSeconds;
        var elapsed = Math.Min(now - _lastStep, 0.25);
        _lastStep = now;

        Step(elapsed);
    }

    /// <summary>Moves the figure on by the time that has passed, whichever lane it came from.</summary>
    private void Step(double elapsed)
    {
        if (Animated)
        {
            StepAnimation(elapsed);
        }

        if (_dragFrom is not null)
        {
            return;
        }

        // A little while after the player lets go, the model resumes turning on its own.
        _idleSeconds += elapsed;

        if (AutoRotate && _idleSeconds > SpinAfterSeconds)
        {
            _yaw += SpinPerSecond * elapsed;
            _pitch += (0.15 - _pitch) * (1 - Math.Pow(0.97, elapsed * 30));
            InvalidateVisual();
        }
    }

    private void StepAnimation(double elapsed)
    {
        _animSeconds += elapsed;

        if (_animSeconds - _clipStarted >= _clipLength)
        {
            // Standing between every two things worth watching, for a different while each time.
            if (ReferenceEquals(_clip, Clips[0]))
            {
                StartClip(NextFeature());
            }
            else
            {
                StartClip(Clips[0]);
            }
        }

        InvalidateVisual();
    }

    private void StartClip(MotionClip clip)
    {
        var now = _animSeconds;

        _blendFrom = CurrentPose(now);
        _blendStarted = now;
        _clip = clip;
        _clipStarted = now;
        _clipLength = ReferenceEquals(clip, Clips[0]) ? 5 + _random.NextDouble() * 4 : clip.Seconds;
        UpdatePoseName();
    }

    /// <summary>An emote goes by the name its file gives it; the figure's own poses are translated.</summary>
    private void UpdatePoseName()
        => PoseName = _clip.Key.Length == 0 ? _clip.Fallback : MainWindowViewModel.Localize(_clip.Key, _clip.Fallback);

    private Pose CurrentPose(double now)
    {
        var pose = _clip.At(now - _clipStarted);
        var blend = (now - _blendStarted) / BlendSeconds;

        if (blend >= 1)
        {
            return pose;
        }

        // Ease in and out, so a limb neither starts nor stops with a jerk.
        var t = blend * blend * (3 - 2 * blend);
        return Pose.Lerp(_blendFrom, pose, t);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragFrom is { } from)
        {
            var now = e.GetPosition(this);
            _yaw += (now.X - from.X) * 0.012;
            _pitch = Math.Clamp(_pitch + (now.Y - from.Y) * 0.008, -0.9, 0.9);
            _dragFrom = now;
            InvalidateVisual();
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragFrom = null;
        _idleSeconds = 0;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    public override void Render(DrawingContext context)
    {
        var skin = Skin;

        if (skin is null)
        {
            return;
        }

        var scale = Math.Min(Bounds.Width / 24, Bounds.Height / 40);
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var pose = Animated ? CurrentPose(_animSeconds) : Pose.Rest;

        (_sinYaw, _cosYaw) = Math.SinCos(_yaw);
        (_sinPitch, _cosPitch) = Math.SinCos(_pitch);

        // Painter's algorithm by body part, not by face: ordering single faces fails - at
        // an oblique angle the inner side of an arm, a long thin face, averaged out
        // "behind" the front of the body and was painted over it. Within a part the
        // visible faces of a convex box cannot overlap, and an outer layer is drawn right
        // after the part it wraps. The halves of a bent part are parts of their own here:
        // a forearm can be in front of the chest while the upper arm is beside it. Which
        // part goes first is decided by the plane between them (see Order), not by
        // their middles: the middles misjudge boxes of different sizes side by side, and
        // the pumpkin, as big as a head and a half, was painted under a shoulder.
        _pieces.Clear();

        foreach (var part in Model(skin, pose))
        {
            _pieces.Add(new Piece(part, Rotate(Place(part.Centre, part, pose)).Z, _pieces.Count));
        }

        // The slice that fills a joint shares its side faces' planes with the two halves,
        // and they must be painted over it, never it over them: it goes down just before
        // whichever half goes first. The model lists it right ahead of them.
        for (var i = 0; i + 2 < _pieces.Count; i++)
        {
            if (_pieces[i].Part.FillsJoint)
            {
                _pieces[i] = _pieces[i] with { Depth = Math.Min(_pieces[i + 1].Depth, _pieces[i + 2].Depth) };
            }
        }

        var texture = skin.Enlarged;
        var pumpkin = HeadDecoration == HeadDecoration.Pumpkin ? PumpkinTexture.Get() : null;
        var count = Order(pumpkin is not null, pose);

        for (var n = 0; n < count; n++)
        {
            var piece = _pieces[_drawOrder[n]];

            // The pumpkin is the head's own part with a bigger box and another texture: it
            // turns with the neck, leans with the chest and follows every pose and emote,
            // and it wraps the head and the hat, so neither is drawn under it.
            if (pumpkin is not null && piece.Part.Kind == PartKind.Head)
            {
                DrawBox(context, skin, pumpkin, piece.Part, PumpkinBox, outer: false, pose, scale, centre);
                continue;
            }

            DrawBox(context, skin, texture, piece.Part, piece.Part.Base, outer: false, pose, scale, centre);

            if (piece.Part.Overlay is { } overlay)
            {
                DrawBox(context, skin, texture, piece.Part, overlay, outer: true, pose, scale, centre);
            }
        }
    }

    // ===================== Which part goes first =====================

    /// <summary>More parts than a figure with every limb and the chest bent can have.</summary>
    private const int MaxPieces = 32;

    /// <summary>
    /// How deep two neighbours may cut into each other and still be told apart by the
    /// plane between them, in model pixels: an arm hanging out turns its shoulder a hair
    /// into the chest, a bent elbow's halves meet at a slant, a head tipped forward dips
    /// its chin into the neck. Deeper than this the two really cross, and their middles decide.
    /// </summary>
    private const double PlaneTolerance = 1.5;

    // Kept between frames, so ordering a frame makes nothing new.
    private readonly Vector3[] _corners = new Vector3[MaxPieces * 8];
    private readonly Vector3[] _normals = new Vector3[MaxPieces * 3];
    private readonly Rect[] _shadows = new Rect[MaxPieces];
    private readonly bool[] _below = new bool[MaxPieces * MaxPieces];
    private readonly int[] _waiting = new int[MaxPieces];
    private readonly bool[] _placed = new bool[MaxPieces];
    private readonly int[] _byDepth = new int[MaxPieces];
    private readonly int[] _drawOrder = new int[MaxPieces];

    /// <summary>
    /// Puts the parts of this frame in the order they are painted, back to front, into
    /// <see cref="_drawOrder"/>, and returns how many there are.
    /// </summary>
    /// <remarks>
    /// Two convex boxes that do not cross each other are always parted by a plane, and
    /// for boxes side by side one of their own faces is that plane: the chest's side for
    /// an arm, the hips' top for the legs under it. Whichever box lies on the side of
    /// that plane that the camera is on is the nearer one, whatever their middles say.
    /// Only pairs that cover each other on the screen are compared; the order is then the
    /// one that keeps every such pair right, the nearer middle breaking ties and the rare
    /// circle. The slice that fills a joint goes just before the first of its two halves.
    /// </remarks>
    private int Order(bool pumpkin, in Pose pose)
    {
        var count = _pieces.Count;

        if (count > MaxPieces)
        {
            // Never happens with this model; if it did, the old order by middles.
            _pieces.Sort(BackToFront);

            for (var i = 0; i < count && i < MaxPieces; i++)
            {
                _drawOrder[i] = i;
            }

            return Math.Min(count, MaxPieces);
        }

        for (var i = 0; i < count; i++)
        {
            var part = _pieces[i].Part;
            Corners(i, pumpkin && part.Kind == PartKind.Head ? PumpkinBox : part.Base, part, pose);
            _byDepth[i] = i;
            _placed[i] = false;
            _waiting[i] = 0;
        }

        // Far to near by the middles: the tie-breaker below.
        for (var i = 1; i < count; i++)
        {
            var index = _byDepth[i];
            var j = i - 1;

            while (j >= 0 && Compare(_byDepth[j], index) > 0)
            {
                _byDepth[j + 1] = _byDepth[j];
                j--;
            }

            _byDepth[j + 1] = index;
        }

        for (var a = 0; a < count; a++)
        {
            for (var b = 0; b < count; b++)
            {
                _below[a * MaxPieces + b] = false;
            }
        }

        for (var a = 0; a < count; a++)
        {
            if (_pieces[a].Part.FillsJoint)
            {
                continue;
            }

            for (var b = a + 1; b < count; b++)
            {
                if (_pieces[b].Part.FillsJoint || !_shadows[a].Intersects(_shadows[b]))
                {
                    continue;
                }

                var nearer = Nearer(a, b);

                if (nearer == 0)
                {
                    continue;
                }

                // _below[x, y]: x is painted before y.
                var (back, front) = nearer > 0 ? (b, a) : (a, b);
                _below[back * MaxPieces + front] = true;
                _waiting[front]++;
            }
        }

        var placed = 0;

        while (true)
        {
            // The farthest part with nothing left to wait for; in a circle, the farthest of all.
            var next = -1;
            var fallback = -1;

            for (var k = 0; k < count; k++)
            {
                var i = _byDepth[k];

                if (_placed[i] || _pieces[i].Part.FillsJoint)
                {
                    continue;
                }

                if (fallback < 0)
                {
                    fallback = i;
                }

                if (_waiting[i] == 0)
                {
                    next = i;
                    break;
                }
            }

            if (next < 0)
            {
                next = fallback;
            }

            if (next < 0)
            {
                break;
            }

            _placed[next] = true;

            for (var i = 0; i < count; i++)
            {
                if (_below[next * MaxPieces + i])
                {
                    _waiting[i]--;
                }
            }

            // A joint's slice goes down right before the first of its halves.
            if (next > 0 && _pieces[next - 1].Part.FillsJoint && !_placed[next - 1])
            {
                _placed[next - 1] = true;
                _drawOrder[placed++] = next - 1;
            }
            else if (next > 1 && _pieces[next - 2].Part.FillsJoint && !_placed[next - 2])
            {
                _placed[next - 2] = true;
                _drawOrder[placed++] = next - 2;
            }

            _drawOrder[placed++] = next;
        }

        return placed;
    }

    private int Compare(int a, int b) => BackToFront(_pieces[a], _pieces[b]);

    /// <summary>
    /// The eight corners of a part's box as the camera sees it, the directions of its three
    /// edges and what it covers on the screen. Corner k has x from bit 0, y from bit 1, z from bit 2.
    /// </summary>
    private void Corners(int index, Box box, Part part, in Pose pose)
    {
        var fromRow = box.FromRow ?? 0;
        var toRow = box.ToRow ?? box.Height;
        var x0 = box.X - box.Inflate;
        var x1 = box.X + box.Width + box.Inflate;
        var y0 = box.ToRow is null ? box.Y - box.Inflate : box.Y + box.Height - toRow;
        var y1 = box.FromRow is null ? box.Y + box.Height + box.Inflate : box.Y + box.Height - fromRow;
        var z0 = box.Z - box.Inflate;
        var z1 = box.Z + box.Depth + box.Inflate;

        var minX = double.MaxValue;
        var maxX = double.MinValue;
        var minY = double.MaxValue;
        var maxY = double.MinValue;

        for (var k = 0; k < 8; k++)
        {
            var v = Rotate(Place(new Vector3((k & 1) == 0 ? x0 : x1, (k & 2) == 0 ? y0 : y1, (k & 4) == 0 ? z0 : z1), part, pose));
            _corners[index * 8 + k] = v;
            minX = Math.Min(minX, v.X);
            maxX = Math.Max(maxX, v.X);
            minY = Math.Min(minY, v.Y);
            maxY = Math.Max(maxY, v.Y);
        }

        // With the outer layer's half pixel around it, so boxes whose layers touch on the screen are compared.
        _shadows[index] = new Rect(minX - 0.6, minY - 0.6, maxX - minX + 1.2, maxY - minY + 1.2);

        var c = index * 8;
        var ex = Minus(_corners[c + 1], _corners[c]);
        var ey = Minus(_corners[c + 2], _corners[c]);
        var ez = Minus(_corners[c + 4], _corners[c]);

        // A box squashed to nothing along an edge has no direction there; its planes are skipped.
        _normals[index * 3] = Outward(Cross(ey, ez), ex);
        _normals[index * 3 + 1] = Outward(Cross(ez, ex), ey);
        _normals[index * 3 + 2] = Outward(Cross(ex, ey), ez);
    }

    /// <summary>
    /// Which of two boxes is nearer the camera: 1 for the first, -1 for the second, 0 when
    /// the planes cannot say (they cross, or meet edge-on to the camera) and the middles decide.
    /// </summary>
    private int Nearer(int a, int b)
    {
        var best = double.MaxValue;
        var facing = 0.0;
        var outerIsA = false;

        // The faces of each box, tried as the plane between them: the one the other box
        // cuts into least. A face whose outer side the other lies on parts them.
        for (var owner = 0; owner < 2; owner++)
        {
            var box = owner == 0 ? a : b;
            var other = owner == 0 ? b : a;

            for (var axis = 0; axis < 3; axis++)
            {
                var normal = _normals[box * 3 + axis];

                if (normal == default)
                {
                    continue;
                }

                for (var side = 0; side < 2; side++)
                {
                    var n = side == 0 ? normal : new Vector3(-normal.X, -normal.Y, -normal.Z);
                    var on = _corners[box * 8 + (side == 0 ? 1 << axis : 0)];
                    var offset = Dot(n, on);
                    var deepest = 0.0;

                    for (var k = 0; k < 8; k++)
                    {
                        deepest = Math.Max(deepest, offset - Dot(n, _corners[other * 8 + k]));
                    }

                    if (deepest < best)
                    {
                        best = deepest;
                        facing = n.Z;
                        outerIsA = owner == 1;
                    }
                }
            }
        }

        if (best > PlaneTolerance || Math.Abs(facing) < 1e-3)
        {
            return 0;
        }

        // The box on the plane's outer side is nearer when that side looks at the camera.
        var outerNearer = facing > 0;
        return outerNearer == outerIsA ? 1 : -1;
    }

    private static Vector3 Minus(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static Vector3 Cross(Vector3 a, Vector3 b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>The unit normal, turned to point along the box's edge; zero for a flat box.</summary>
    private static Vector3 Outward(Vector3 normal, Vector3 along)
    {
        var length = Math.Sqrt(Dot(normal, normal));

        if (length < 1e-9)
        {
            return default;
        }

        var sign = Dot(normal, along) < 0 ? -1 : 1;
        return new Vector3(normal.X / length * sign, normal.Y / length * sign, normal.Z / length * sign);
    }

    /// <summary>
    /// Draws the faces of one box that look at the camera: the texture under the face's
    /// matrix, and over it the shade of a face that looks away from the light.
    /// </summary>
    /// <remarks>
    /// The shade is the same rectangle as the texture, drawn under the same matrix, so it
    /// covers the face to the pixel and to the same soft edge. It used to be a polygon
    /// built for every shaded face of every frame - a path, a brush and four arrays each,
    /// a megabyte or more a second of things made to be thrown away.
    /// </remarks>
    private void DrawBox(DrawingContext context, PlayerSkin skin, Bitmap texture, Part part, Box box, bool outer, in Pose pose, double scale, Point centre)
    {
        foreach (var face in FacesOf(box))
        {
            if (!TryProject(face, part, pose, scale, centre, out var matrix, out var area, out var shade))
            {
                continue;
            }

            // An outer-layer face with nothing drawn on it is not there: no
            // texture to put down, and above all no shade to lay over nothing.
            if (outer && skin.IsBlank(face.Texture))
            {
                continue;
            }

            using (context.PushTransform(matrix))
            {
                context.DrawImage(texture, area, area);

                if (shade > 0.01)
                {
                    context.FillRectangle(ShadeBrush(shade), area);
                }
            }
        }
    }

    /// <summary>Black at every strength a face can be shaded by, each made once.</summary>
    private static readonly ImmutableSolidColorBrush?[] Shades = new ImmutableSolidColorBrush?[256];

    private static ImmutableSolidColorBrush ShadeBrush(double shade)
    {
        // Cut off, not rounded: the same byte a brush with this opacity came to.
        var alpha = (byte)(255 * Math.Clamp(shade, 0, 1));
        return Shades[alpha] ??= new ImmutableSolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
    }

    /// <summary>Half the model's height: it turns about its middle, not its feet.</summary>
    private const double ModelMidHeight = 16;

    /// <summary>
    /// A point of a body part in the posed figure: turned about the part's joint, lifted
    /// with the body, turned with the whole figure, and brought down so the camera's own
    /// turn goes about the middle.
    /// </summary>
    private static Vector3 Place(Vector3 point, Part part, in Pose pose)
    {
        // The half beyond an elbow or a knee folds first, while the part still hangs straight.
        if (part.Hinge is { } hinge)
        {
            point = hinge.Move(point);
        }

        var pivot = part.Pivot;
        var joint = pose.For(part.Kind);
        var local = Turn(new Vector3(point.X - pivot.X, point.Y - pivot.Y, point.Z - pivot.Z), joint);
        var x = local.X + pivot.X + joint.Dx;
        var y = local.Y + pivot.Y + joint.Dy;
        var z = local.Z + pivot.Z + joint.Dz;

        // The head and the arms ride on the half of the chest that a bend at the waist turns.
        if (part.Lean is { } lean)
        {
            var leant = lean.Move(new Vector3(x, y, z));
            x = leant.X;
            y = leant.Y;
            z = leant.Z;
        }

        // An emote can turn the whole figure over and carry it off its spot.
        var root = pose.Root;

        if (root != default)
        {
            var turned = Turn(new Vector3(x, y - RootHeight, z), root);
            x = turned.X + root.Dx;
            y = turned.Y + RootHeight + root.Dy;
            z = turned.Z + root.Dz;
        }

        y += pose.Lift;

        var c = Math.Cos(pose.Swing);
        var s = Math.Sin(pose.Swing);

        return new Vector3(x * c + z * s, y - ModelMidHeight, -x * s + z * c);
    }

    /// <summary>A direction under the same turns, for the light.</summary>
    private static Vector3 PlaceNormal(Vector3 normal, Part part, in Pose pose)
    {
        if (part.Hinge is { } hinge)
        {
            normal = hinge.Turn(normal);
        }

        normal = Turn(normal, pose.For(part.Kind));

        if (part.Lean is { } lean)
        {
            normal = lean.Turn(normal);
        }

        var local = Turn(normal, pose.Root);
        var c = Math.Cos(pose.Swing);
        var s = Math.Sin(pose.Swing);

        return new Vector3(local.X * c + local.Z * s, local.Y, -local.X * s + local.Z * c);
    }

    /// <summary>About Z (out to the side), then X (forward and back), then Y (twist).</summary>
    private static Vector3 Turn(Vector3 v, Joint joint)
    {
        if (joint.Rz != 0)
        {
            var c = Math.Cos(joint.Rz);
            var s = Math.Sin(joint.Rz);
            v = new Vector3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
        }

        if (joint.Rx != 0)
        {
            var c = Math.Cos(joint.Rx);
            var s = Math.Sin(joint.Rx);
            v = new Vector3(v.X, v.Y * c - v.Z * s, v.Y * s + v.Z * c);
        }

        if (joint.Ry != 0)
        {
            var c = Math.Cos(joint.Ry);
            var s = Math.Sin(joint.Ry);
            v = new Vector3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
        }

        return v;
    }

    /// <summary>
    /// Where a face lands on the screen: the matrix that carries its texture rectangle
    /// there and how deep its shade is. False for a face turned away from the camera.
    /// </summary>
    private bool TryProject(Face face, Part part, in Pose pose, double scale, Point centre, out Matrix matrix, out Rect area, out double shade)
    {
        Span<Point> s = stackalloc Point[4];
        var corners = face.Corners;

        // Orthographic: x right, y up, z towards the viewer.
        for (var i = 0; i < 4; i++)
        {
            var v = Rotate(Place(corners[i], part, pose));
            s[i] = new Point(centre.X + v.X * scale, centre.Y - v.Y * scale);
        }

        // Back-face culling from the on-screen winding: a face seen from behind is drawn
        // with its corners going the other way round.
        var cross = (s[1].X - s[0].X) * (s[3].Y - s[0].Y) - (s[1].Y - s[0].Y) * (s[3].X - s[0].X);

        if (cross <= 0)
        {
            matrix = default;
            area = default;
            shade = 0;
            return false;
        }

        // Each face is pushed a third of a pixel outwards from its centre. Neighbouring
        // faces drawn with smoothing would otherwise meet in a hairline seam where both
        // edges are half-covered.
        var cx = (s[0].X + s[1].X + s[2].X + s[3].X) / 4;
        var cy = (s[0].Y + s[1].Y + s[2].Y + s[3].Y) / 4;

        for (var i = 0; i < 4; i++)
        {
            var dx = s[i].X - cx;
            var dy = s[i].Y - cy;
            var length = Math.Sqrt(dx * dx + dy * dy);

            if (length > 0.001)
            {
                s[i] = new Point(s[i].X + dx / length * 0.35, s[i].Y + dy / length * 0.35);
            }
        }

        // Map the texture rectangle onto the parallelogram: the rect's top-left, top-right
        // and bottom-left corners land on s[0], s[1], s[3]. The rectangle is in the
        // enlarged texture's pixels.
        var f = PlayerSkin.EnlargeFactor;
        var t = new Rect(face.Texture.X * f, face.Texture.Y * f, face.Texture.Width * f, face.Texture.Height * f);
        var ax = (s[1].X - s[0].X) / t.Width;
        var ay = (s[1].Y - s[0].Y) / t.Width;
        var bx = (s[3].X - s[0].X) / t.Height;
        var by = (s[3].Y - s[0].Y) / t.Height;
        matrix = new Matrix(ax, ay, bx, by,
            s[0].X - ax * t.X - bx * t.Y,
            s[0].Y - ay * t.X - by * t.Y);
        area = t;

        var normal = Rotate(PlaceNormal(face.Normal, part, pose));
        var lit = Math.Max(0, Dot(normal, Light));
        shade = (1 - lit) * 0.45;
        return true;
    }

    private Vector3 Rotate(Vector3 v)
    {
        // Yaw about Y, then pitch about X.
        var x = v.X * _cosYaw + v.Z * _sinYaw;
        var z = -v.X * _sinYaw + v.Z * _cosYaw;

        var y = v.Y * _cosPitch - z * _sinPitch;
        z = v.Y * _sinPitch + z * _cosPitch;

        return new Vector3(x, y, z);
    }

    // ===================== Poses and clips =====================

    private enum PartKind
    {
        Head,
        Body,
        RightArm,
        LeftArm,
        RightLeg,
        LeftLeg
    }

    /// <summary>
    /// The turn of one joint, radians, and how far the joint itself has moved, in model
    /// pixels. A limb hanging down swings forward on a negative Rx. Only emotes move joints.
    /// </summary>
    /// <remarks>
    /// Bx and Bz are the bend in the middle of the part - the elbow, the knee, the waist -
    /// as an arrow lying along the hinge, as long as the angle in radians. Kept as an
    /// arrow and not as an angle and a direction so that one pose fades into another
    /// straight: half-way to "bent forward" is bent forward half as much, not bent sideways.
    /// </remarks>
    private readonly record struct Joint(
        double Rx = 0, double Ry = 0, double Rz = 0, double Dx = 0, double Dy = 0, double Dz = 0,
        double Bx = 0, double Bz = 0)
    {
        public static Joint Lerp(Joint a, Joint b, double t) => new(
            a.Rx + (b.Rx - a.Rx) * t, a.Ry + (b.Ry - a.Ry) * t, a.Rz + (b.Rz - a.Rz) * t,
            a.Dx + (b.Dx - a.Dx) * t, a.Dy + (b.Dy - a.Dy) * t, a.Dz + (b.Dz - a.Dz) * t,
            a.Bx + (b.Bx - a.Bx) * t, a.Bz + (b.Bz - a.Bz) * t);
    }

    /// <summary>
    /// The whole figure at one instant: six joints, how high it is off the ground, its
    /// own turn, and - in an emote - the turn and travel of the figure as one piece.
    /// </summary>
    private readonly record struct Pose(
        Joint Head, Joint Body, Joint RightArm, Joint LeftArm, Joint RightLeg, Joint LeftLeg,
        double Lift = 0, double Swing = 0, Joint Root = default)
    {
        public static readonly Pose Rest = new(default, default, default, default, default, default);

        public Joint For(PartKind kind) => kind switch
        {
            PartKind.Head => Head,
            PartKind.Body => Body,
            PartKind.RightArm => RightArm,
            PartKind.LeftArm => LeftArm,
            PartKind.RightLeg => RightLeg,
            _ => LeftLeg
        };

        public static Pose Lerp(Pose a, Pose b, double t) => new(
            Joint.Lerp(a.Head, b.Head, t),
            Joint.Lerp(a.Body, b.Body, t),
            Joint.Lerp(a.RightArm, b.RightArm, t),
            Joint.Lerp(a.LeftArm, b.LeftArm, t),
            Joint.Lerp(a.RightLeg, b.RightLeg, t),
            Joint.Lerp(a.LeftLeg, b.LeftLeg, t),
            a.Lift + (b.Lift - a.Lift) * t,
            a.Swing + (b.Swing - a.Swing) * t,
            Joint.Lerp(a.Root, b.Root, t));
    }

    /// <summary>A named movement: the pose at each moment of it, and how long it is worth watching.</summary>
    private sealed record MotionClip(string Key, string Fallback, double Seconds, Func<double, Pose> At);

    /// <summary>The first is plain standing; the figure returns to it between the others.</summary>
    private static readonly MotionClip[] Clips =
    {
        new("Pose_Idle", "Standing", 6, Idle),
        new("Pose_Wave", "Waving", 4.2, t =>
        {
            var idle = Idle(t);
            return idle with
            {
                LeftArm = new Joint(Rz: 2.55 + Math.Sin(t * 9) * 0.28),
                Head = new Joint(Ry: 0.22, Rz: 0.07)
            };
        }),
        new("Pose_Look", "Looking around", 4.5, t =>
        {
            var idle = Idle(t);
            return idle with { Head = new Joint(Rx: Math.Sin(t * 0.9) * 0.12, Ry: Math.Sin(t * 1.5) * 0.75) };
        }),
        new("Pose_Walk", "Walking", 5, t =>
        {
            var step = Math.Sin(t * 6.2);
            return new Pose(
                Head: new Joint(Rx: 0.03),
                Body: default,
                RightArm: new Joint(Rx: step * 0.75),
                LeftArm: new Joint(Rx: -step * 0.75),
                RightLeg: new Joint(Rx: -step * 0.7),
                LeftLeg: new Joint(Rx: step * 0.7),
                Lift: Math.Abs(Math.Cos(t * 6.2)) * 0.35);
        }),
        new("Pose_Sit", "Sitting", 6, t => new Pose(
            Head: new Joint(Rx: Math.Sin(t * 0.8) * 0.06, Ry: Math.Sin(t * 0.6) * 0.3),
            Body: default,
            RightArm: new Joint(Rx: -0.55, Rz: -0.08),
            LeftArm: new Joint(Rx: -0.55, Rz: 0.08),
            RightLeg: new Joint(Rx: -1.45, Rz: -0.12),
            LeftLeg: new Joint(Rx: -1.45, Rz: 0.12),
            Lift: -10)),
        new("Pose_Cheer", "Cheering", 3.6, t =>
        {
            var shake = Math.Sin(t * 14) * 0.14;
            return new Pose(
                Head: new Joint(Rx: -0.18),
                Body: default,
                RightArm: new Joint(Rz: -2.75 + shake),
                LeftArm: new Joint(Rz: 2.75 - shake),
                RightLeg: new Joint(Rx: 0.12),
                LeftLeg: new Joint(Rx: -0.12),
                Lift: Math.Abs(Math.Sin(t * 6.5)) * 2.4);
        }),
        new("Pose_Dance", "Dancing", 5.5, t =>
        {
            var beat = Math.Sin(t * 5.4);
            var half = Math.Sin(t * 2.7);
            return new Pose(
                Head: new Joint(Ry: beat * 0.25, Rz: half * 0.12),
                Body: default,
                RightArm: new Joint(Rx: -0.4 + beat * 0.5, Rz: -1.1 - half * 0.9),
                LeftArm: new Joint(Rx: -0.4 - beat * 0.5, Rz: 1.1 - half * 0.9),
                RightLeg: new Joint(Rx: Math.Max(0, beat) * -0.5),
                LeftLeg: new Joint(Rx: Math.Max(0, -beat) * -0.5),
                Lift: Math.Abs(beat) * 0.9,
                Swing: half * 0.4);
        })
    };

    /// <summary>Standing is not stillness: the arms hang a little out and sway, the head wanders, the chest rises.</summary>
    private static Pose Idle(double t) => new(
        Head: new Joint(Rx: Math.Sin(t * 0.45) * 0.05, Ry: Math.Sin(t * 0.6) * 0.22),
        Body: default,
        RightArm: new Joint(Rx: Math.Sin(t * 1.2) * 0.05, Rz: -0.06 - Math.Sin(t * 1.6) * 0.025),
        LeftArm: new Joint(Rx: -Math.Sin(t * 1.2) * 0.05, Rz: 0.06 + Math.Sin(t * 1.6) * 0.025),
        RightLeg: default,
        LeftLeg: default,
        Lift: Math.Sin(t * 1.6) * 0.12);

    // ===================== Emotes =====================

    /// <summary>The height the game turns the whole player about: 0.7 of a block, in model pixels.</summary>
    private const double RootHeight = 11.2;

    /// <summary>An emote as a clip: named by its file, and as long as it is worth watching.</summary>
    private static MotionClip ClipOf(Emote emote)
    {
        var seconds = Math.Max(emote.Seconds, 0.5);

        if (emote.IsLoop && seconds < LoopWatchSeconds)
        {
            // Whole passes, so the last one is not cut off mid-step.
            seconds += Math.Ceiling((LoopWatchSeconds - seconds) / emote.LoopSeconds) * emote.LoopSeconds;
        }

        return new MotionClip(string.Empty, emote.Name, Math.Min(seconds, MaxEmoteSeconds), t => FromEmote(emote, t));
    }

    /// <summary>
    /// The emote's frame in this figure's terms. The game's model has y pointing down
    /// and faces -z, this one has y up and faces +z: the same figure turned half-way
    /// round its x axis, which keeps pitch and flips yaw and roll. The player as a whole
    /// is turned in the world instead, y up but still facing -z, so there pitch and roll
    /// flip and yaw stays; and its travel is in blocks, sixteen pixels each.
    /// </summary>
    private static Pose FromEmote(Emote emote, double seconds)
    {
        var frame = emote.Sample(seconds * Emote.TicksPerSecond);

        return new Pose(
            Head: Limb(frame.Head),
            Body: Chest(frame.Torso, frame.Body),
            RightArm: Limb(frame.RightArm),
            LeftArm: Limb(frame.LeftArm),
            RightLeg: Limb(frame.RightLeg),
            LeftLeg: Limb(frame.LeftLeg),
            Root: FromGame(-frame.Body.Pitch, frame.Body.Yaw, -frame.Body.Roll) with
            {
                Dx = -frame.Body.X * 16,
                Dy = Rise(frame.Body.Y * 16),
                Dz = -frame.Body.Z * 16
            });
    }

    /// <summary>How far above its spot the figure can go before it would leave the stage, in model pixels.</summary>
    private const double StageHeadroom = 14;

    /// <summary>
    /// The game has the whole sky for a jump; the stage has a ceiling. A small hop is
    /// left as it is, and a leap a block and a half high flattens out under the top of
    /// the stage instead of carrying the figure out of the window.
    /// </summary>
    private static double Rise(double up) => up <= 0 ? up : StageHeadroom * Math.Tanh(up / StageHeadroom);

    /// <summary>
    /// The hinge of a bend is the game's x axis turned about the part's length by the
    /// bend's direction, (cos, 0, sin); seen from this figure's side, z is the other way.
    /// </summary>
    private static Joint Limb(EmotePartState part)
    {
        var joint = FromGame(part.Pitch, -part.Yaw, -part.Roll) with { Dx = part.X, Dy = -part.Y, Dz = -part.Z };

        if (part.Bend == 0)
        {
            return joint;
        }

        var bend = WholeTurnsOff(part.Bend);
        var (sin, cos) = Math.SinCos(part.BendAxis);
        return joint with { Bx = bend * cos, Bz = -bend * sin };
    }

    /// <summary>The same angle between half a turn back and half a turn on: the mod bends by no more.</summary>
    private static double WholeTurnsOff(double angle) => Math.IEEERemainder(angle, Math.PI * 2);

    /// <summary>
    /// The game hangs the chest from the neck, this figure stands it on the waist. A turn
    /// about the one is the same turn about the other plus a shift: where the turn would
    /// have carried the waist.
    /// </summary>
    /// <remarks>
    /// The chest bends by what the file gives the chest and the whole body together, and
    /// its hinge is turned the other way round from a limb's: the mod cuts the chest from
    /// the opposite end, so that it is the shoulders that move.
    /// </remarks>
    private static Joint Chest(EmotePartState part, EmotePartState body)
    {
        var joint = Limb(part with { Bend = 0 });
        var bend = WholeTurnsOff(part.Bend + body.Bend);

        if (bend != 0)
        {
            var (sin, cos) = Math.SinCos(part.BendAxis + body.BendAxis);
            joint = joint with { Bx = bend * cos, Bz = bend * sin };
        }

        if (joint.Rx == 0 && joint.Ry == 0 && joint.Rz == 0)
        {
            return joint;
        }

        var waist = Turn(new Vector3(0, -12, 0), joint);
        return joint with { Dx = joint.Dx + waist.X, Dy = joint.Dy + waist.Y + 12, Dz = joint.Dz + waist.Z };
    }

    /// <summary>
    /// The game turns a part about x, then y, then z; <see cref="Turn"/> goes z, x, y.
    /// The same rotation has angles in either order, and these are the ones for ours:
    /// read off the rotation matrix the game's three turns make.
    /// </summary>
    private static Joint FromGame(double x, double y, double z)
    {
        // A turn about one axis is the same in any order, and most of what emotes do is
        // that: no need to send a full flip through an arcsine.
        if (y == 0 && z == 0)
        {
            return new Joint(Rx: x);
        }

        if (x == 0 && z == 0)
        {
            return new Joint(Ry: y);
        }

        if (x == 0 && y == 0)
        {
            return new Joint(Rz: z);
        }

        var (sx, cx) = Math.SinCos(x);
        var (sy, cy) = Math.SinCos(y);
        var (sz, cz) = Math.SinCos(z);

        var m12 = sz * sy * cx - cz * sx;

        // Looking straight along the y axis the other two turns become one; it is given to y.
        if (Math.Abs(m12) > 0.99999)
        {
            return new Joint(Rx: m12 < 0 ? Math.PI / 2 : -Math.PI / 2, Ry: Math.Atan2(sy, cz * cy));
        }

        return new Joint(
            Rx: Math.Asin(-m12),
            Ry: Math.Atan2(cz * sy * cx + sz * sx, cy * cx),
            Rz: Math.Atan2(sz * cy, sz * sy * sx + cz * cx));
    }

    // ===================== The model =====================

    private readonly record struct Vector3(double X, double Y, double Z);

    private static Vector3 Normalize(Vector3 v)
    {
        var length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return new Vector3(v.X / length, v.Y / length, v.Z / length);
    }

    private static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>One face: four corners in model space (counter-clockwise seen from outside) and its texture rect.</summary>
    private sealed record Face(Vector3[] Corners, Vector3 Normal, Rect Texture);

    /// <summary>
    /// A textured box in the standard skin layout: the six faces unfold from the texture
    /// origin (u, v) the way every skin editor draws them.
    /// </summary>
    /// <remarks>
    /// <c>FromRow</c> and <c>ToRow</c> cut a piece out of the box's height, in pixel rows
    /// counted down from its top: half of a part that bends. An end that is a cut has no
    /// cap and is not inflated - there is more of the same part beyond it.
    /// </remarks>
    private sealed record Box(
        double X, double Y, double Z,
        double Width, double Height, double Depth,
        double U, double V,
        double Inflate = 0,
        bool Mirror = false,
        double? FromRow = null,
        double? ToRow = null)
    {
        public IEnumerable<Face> Faces()
        {
            var fromRow = FromRow ?? 0;
            var toRow = ToRow ?? Height;

            var x0 = X - Inflate;
            var x1 = X + Width + Inflate;
            var y0 = ToRow is null ? Y - Inflate : Y + Height - toRow;
            var y1 = FromRow is null ? Y + Height + Inflate : Y + Height - fromRow;
            var z0 = Z - Inflate;
            var z1 = Z + Depth + Inflate;

            var w = Width;
            var h = toRow - fromRow;
            var d = Depth;
            var v = V + d + fromRow;

            // Texture layout: top and bottom above, then right, front, left, back in a row.
            var top = new Rect(U + d, V, w, d);
            var bottom = new Rect(U + d + w, V, w, d);
            var right = new Rect(U, v, d, h);
            var front = new Rect(U + d, v, w, h);
            var left = new Rect(U + d + w, v, d, h);
            var back = new Rect(U + 2 * d + w, v, w, h);

            if (Mirror)
            {
                // Legacy skins have one arm and one leg; the other side is the same
                // texture flipped, which is what the game does too.
                (right, left) = (left, right);
            }

            // Corners are listed top-left, top-right, bottom-right, bottom-left as seen
            // from outside the box, matching the texture's orientation.
            yield return new Face(new[] { new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1) }, new Vector3(0, 0, 1), front);
            yield return new Face(new[] { new Vector3(x1, y1, z0), new Vector3(x0, y1, z0), new Vector3(x0, y0, z0), new Vector3(x1, y0, z0) }, new Vector3(0, 0, -1), back);
            yield return new Face(new[] { new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1) }, new Vector3(1, 0, 0), left);
            yield return new Face(new[] { new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1), new Vector3(x0, y0, z0) }, new Vector3(-1, 0, 0), right);
            if (FromRow is null)
            {
                yield return new Face(new[] { new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1) }, new Vector3(0, 1, 0), top);
            }

            if (ToRow is null)
            {
                yield return new Face(new[] { new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x0, y0, z0) }, new Vector3(0, -1, 0), bottom);
            }
        }

        public Box Rows(double? from, double? to) => this with { FromRow = from, ToRow = to };
    }

    /// <summary>
    /// What a bend does to the points beyond it: a turn about a line through
    /// <c>Pivot</c> that lies across the part, along (Ax, 0, Az).
    /// </summary>
    /// <remarks>
    /// <c>Squash</c> and <c>Stretch</c> are for the slice that fills a joint, and one for
    /// everything else: before the turn the slice is made narrower across the hinge and
    /// as tall as the gap it has to close.
    /// </remarks>
    private readonly record struct Hinge(Vector3 Pivot, double Ax, double Az, double Sin, double Cos, double Squash = 1, double Stretch = 1)
    {
        public Vector3 Move(Vector3 point)
        {
            var x = point.X - Pivot.X;
            var y = point.Y - Pivot.Y;
            var z = point.Z - Pivot.Z;

            if (Squash != 1 || Stretch != 1)
            {
                var across = (z * Ax - x * Az) * (Squash - 1);
                x -= Az * across;
                z += Ax * across;
                y *= Stretch;
            }

            var turned = Turn(new Vector3(x, y, z));
            return new Vector3(turned.X + Pivot.X, turned.Y + Pivot.Y, turned.Z + Pivot.Z);
        }

        /// <summary>A direction under the turn alone.</summary>
        public Vector3 Turn(Vector3 v)
        {
            var along = (v.X * Ax + v.Z * Az) * (1 - Cos);

            return new Vector3(
                v.X * Cos - Az * v.Y * Sin + Ax * along,
                v.Y * Cos + (Az * v.X - Ax * v.Z) * Sin,
                v.Z * Cos + Ax * v.Y * Sin + Az * along);
        }
    }

    /// <summary>
    /// A body part: the box, the outer layer wrapped around it on a modern skin, and the
    /// joint it turns about. Half of a bent part is a part too, with the hinge it has
    /// folded on; <c>Lean</c> is the fold of the chest, for what rides on the shoulders.
    /// </summary>
    private sealed record Part(Box Base, Box? Overlay, PartKind Kind, Vector3 Pivot)
    {
        public Hinge? Hinge { get; init; }

        public Hinge? Lean { get; init; }

        /// <summary>True for the slice between the two halves of a bent part.</summary>
        public bool FillsJoint { get; init; }

        public Vector3 Centre
        {
            get
            {
                var from = Base.FromRow ?? 0;
                var to = Base.ToRow ?? Base.Height;
                return new Vector3(Base.X + Base.Width / 2, Base.Y + Base.Height - (from + to) / 2, Base.Z + Base.Depth / 2);
            }
        }
    }

    /// <summary>A part with how far back it is in this frame, and its place in the model for parts equally far.</summary>
    private readonly record struct Piece(Part Part, double Depth, int Order);

    /// <summary>
    /// The faces of every box drawn so far. A box is the same box on every frame - and the
    /// halves of a bent one are the same few halves - so its faces are worked out once and
    /// not thirty times a second. Boxes are compared by what they are, not by which object.
    /// </summary>
    private static readonly Dictionary<Box, Face[]> FaceCache = new();

    private static Face[] FacesOf(Box box)
    {
        if (!FaceCache.TryGetValue(box, out var faces))
        {
            faces = box.Faces().ToArray();
            FaceCache[box] = faces;
        }

        return faces;
    }

    private static readonly Comparison<Piece> BackToFront = (a, b) =>
    {
        var byDepth = a.Depth.CompareTo(b.Depth);
        return byDepth != 0 ? byDepth : a.Order.CompareTo(b.Order);
    };

    /// <summary>Below this a bend is no bend, as in the mod: the part stays one box.</summary>
    private const double SmallestBend = 0.0001;

    /// <summary>Where the chest folds: the middle of its box, six pixels under the neck, as in the mod.</summary>
    private static readonly Vector3 ChestMiddle = new(0, 18, 0);

    /// <summary>The fold a joint's bend makes about the middle of its part, or none.</summary>
    private static Hinge? Fold(Joint joint, Vector3 middle)
    {
        var angle = Math.Sqrt(joint.Bx * joint.Bx + joint.Bz * joint.Bz);

        if (angle < SmallestBend)
        {
            return null;
        }

        var (sin, cos) = Math.SinCos(angle);
        return new Hinge(middle, joint.Bx / angle, joint.Bz / angle, sin, cos);
    }

    /// <summary>
    /// The player as posed: every part, and each part that the pose bends as its two
    /// halves with the slice that fills the joint listed ahead of them.
    /// </summary>
    private static IEnumerable<Part> Model(PlayerSkin skin, Pose pose)
    {
        var lean = Fold(pose.Body, ChestMiddle);

        foreach (var part in ModelOf(skin))
        {
            var box = part.Base;
            var middle = box.Height / 2;
            var chest = part.Kind == PartKind.Body;
            var rides = part.Kind is PartKind.Head or PartKind.RightArm or PartKind.LeftArm ? lean : null;
            var bent = chest ? lean
                : part.Kind == PartKind.Head ? null
                : Fold(pose.For(part.Kind), new Vector3(box.X + box.Width / 2, box.Y + middle, box.Z + box.Depth / 2));

            if (bent is not { } fold)
            {
                yield return rides is null ? part : part with { Lean = rides };
                continue;
            }

            // The turn opens a wedge on the outer side of the joint: as wide, at the
            // skin, as the part reaches out from the hinge. The slice between the halves
            // is turned half-way, drawn in until its outer face runs from the edge of
            // one half to the edge of the other, and made as tall as that: the elbow
            // comes out cut off at a slant, with no gap, and nothing sticks out of it.
            var sinHalf = Math.Sqrt(Math.Max(0, (1 - fold.Cos) / 2));
            var cosHalf = Math.Sqrt(Math.Max(0, (1 + fold.Cos) / 2));
            var inflate = part.Overlay?.Inflate ?? 0;
            var reach = (box.Width / 2 + inflate) * Math.Abs(fold.Az) + (box.Depth / 2 + inflate) * Math.Abs(fold.Ax);
            var fill = fold with { Sin = sinHalf, Cos = cosHalf, Squash = cosHalf, Stretch = reach * sinHalf };

            // It is the chest's upper half that moves, a limb's lower one.
            yield return new Part(box.Rows(middle - 1, middle + 1), part.Overlay?.Rows(middle - 1, middle + 1), part.Kind, part.Pivot)
            {
                Hinge = fill, Lean = rides, FillsJoint = true
            };
            yield return new Part(box.Rows(null, middle), part.Overlay?.Rows(null, middle), part.Kind, part.Pivot)
            {
                Hinge = chest ? fold : null, Lean = rides
            };
            yield return new Part(box.Rows(middle, null), part.Overlay?.Rows(middle, null), part.Kind, part.Pivot)
            {
                Hinge = chest ? null : fold, Lean = rides
            };
        }
    }

    /// <summary>
    /// The pumpkin worn on the head: a block a pixel larger than the head each way, as the
    /// game draws one, its faces laid out in <see cref="PumpkinTexture"/> where a head's are
    /// in a skin. Raised a pixel so that it sits on the shoulders instead of sinking into
    /// them: a box that cuts into the arms has no plane to be ordered by. One box for every
    /// frame, so its faces come from the cache.
    /// </summary>
    private static readonly Box PumpkinBox = new(-4, 25, -4, 8, 8, 8, 0, 0, Inflate: 1);

    /// <summary>The four figures there are: classic or slim arms, modern or pre-1.8 texture. Each is built once.</summary>
    private static readonly Part[]?[] Models = new Part[]?[4];

    private static Part[] ModelOf(PlayerSkin skin)
        => Models[(skin.IsSlim ? 1 : 0) | (skin.IsLegacy ? 2 : 0)] ??= Model(skin.IsSlim, skin.IsLegacy).ToArray();

    /// <summary>
    /// The player: head, body, two arms, two legs, each with its outer layer and its
    /// joint. The body comes before the arms: what it bends by, they lean by.
    /// </summary>
    private static IEnumerable<Part> Model(bool slim, bool legacy)
    {
        var arm = slim ? 3 : 4;

        // Joints: the neck, the shoulders two pixels below the top of the arm, the hips.
        var neck = new Vector3(0, 24, 0);
        var waist = new Vector3(0, 12, 0);
        var rightShoulder = new Vector3(-4 - arm / 2.0, 22, 0);
        var leftShoulder = new Vector3(4 + arm / 2.0, 22, 0);
        var rightHip = new Vector3(-2, 12, 0);
        var leftHip = new Vector3(2, 12, 0);

        // Model space: origin at the feet, y up, the player facing +z. Units are pixels.
        // Outer layers are slightly inflated so they sit over the base.
        yield return new Part(
            new Box(-4, 24, -4, 8, 8, 8, 0, 0),
            new Box(-4, 24, -4, 8, 8, 8, 32, 0, Inflate: 0.5),
            PartKind.Head, neck);                                                       // head + hat

        if (legacy)
        {
            // One arm and one leg in the texture; the other side is the same, mirrored.
            yield return new Part(new Box(-4, 12, -2, 8, 12, 4, 16, 16), null, PartKind.Body, waist);
            yield return new Part(new Box(-8, 12, -2, 4, 12, 4, 40, 16), null, PartKind.RightArm, rightShoulder);
            yield return new Part(new Box(4, 12, -2, 4, 12, 4, 40, 16, Mirror: true), null, PartKind.LeftArm, leftShoulder);
            yield return new Part(new Box(-4, 0, -2, 4, 12, 4, 0, 16), null, PartKind.RightLeg, rightHip);
            yield return new Part(new Box(0, 0, -2, 4, 12, 4, 0, 16, Mirror: true), null, PartKind.LeftLeg, leftHip);
            yield break;
        }

        yield return new Part(
            new Box(-4, 12, -2, 8, 12, 4, 16, 16),
            new Box(-4, 12, -2, 8, 12, 4, 16, 32, Inflate: 0.25),
            PartKind.Body, waist);                                                      // body + jacket
        yield return new Part(
            new Box(-4 - arm, 12, -2, arm, 12, 4, 40, 16),
            new Box(-4 - arm, 12, -2, arm, 12, 4, 40, 32, Inflate: 0.25),
            PartKind.RightArm, rightShoulder);                                          // right arm + sleeve
        yield return new Part(
            new Box(4, 12, -2, arm, 12, 4, 32, 48),
            new Box(4, 12, -2, arm, 12, 4, 48, 48, Inflate: 0.25),
            PartKind.LeftArm, leftShoulder);                                            // left arm + sleeve
        yield return new Part(
            new Box(-4, 0, -2, 4, 12, 4, 0, 16),
            new Box(-4, 0, -2, 4, 12, 4, 0, 32, Inflate: 0.25),
            PartKind.RightLeg, rightHip);                                               // right leg + trouser
        yield return new Part(
            new Box(0, 0, -2, 4, 12, 4, 16, 48),
            new Box(0, 0, -2, 4, 12, 4, 0, 48, Inflate: 0.25),
            PartKind.LeftLeg, leftHip);                                                 // left leg + trouser
    }
}
