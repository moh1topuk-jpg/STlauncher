using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using STlauncher.App.Services;
using STlauncher.App.ViewModels;
using STlauncher.Core.Emotes;

namespace STlauncher.App.Controls;

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
/// into a pose, with the joints moved as well as turned. The figure has no elbows or
/// knees, so an emote's bends are left out.
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

    private static readonly Vector3 Light = Normalize(new Vector3(-0.4, 0.8, 0.6));

    /// <summary>How long one clip takes to give way to the next.</summary>
    private const double BlendSeconds = 0.45;

    /// <summary>A looping emote is watched for whole passes until this long has gone by.</summary>
    private const double LoopWatchSeconds = 6;

    /// <summary>No emote holds the stage longer than this, however long its file says it is.</summary>
    private const double MaxEmoteSeconds = 16;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();

    private double _yaw = 0.55;
    private double _pitch = 0.15;
    private double _spin = 0.008;
    private Point? _dragFrom;
    private double _idleTicks;
    private int _skippedTicks;
    private bool _attached;

    private MotionClip _clip = Clips[0];
    private double _clipStarted;
    private double _clipLength = 6;
    private Pose _blendFrom = Pose.Rest;
    private double _blendStarted = double.NegativeInfinity;
    private int _nextFeature;
    private bool _emoteIsNext;
    private int _lastEmote = -1;
    private string _poseName = string.Empty;

    static SkinViewer()
    {
        AffectsRender<SkinViewer>(SkinProperty, AnimatedProperty);
    }

    public SkinViewer()
    {
        // Smoothing on, deliberately: the texture drawn is the pre-enlarged one, whose
        // pixels are already eight wide, so smoothing only softens the edges of each
        // face by a hair - which is exactly what removes the staircase along them.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        Cursor = new Cursor(StandardCursorType.Hand);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };

        // The moving figure is redrawn thirty times a second; the cubic filter that makes
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
                SyncTimer();
            }
        };
        _timer.Tick += (_, _) => Tick();

        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            SyncTimer();
            UpdatePoseName();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            _timer.Stop();
        };
    }

    /// <summary>
    /// A figure that neither turns by itself nor plays its clips has nothing to tick for:
    /// the skin library shows a dozen of them, and a dozen idle timers are still timers.
    /// </summary>
    private void SyncTimer()
    {
        if (_attached && (AutoRotate || Animated))
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
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

    private void Tick()
    {
        if (Animated)
        {
            TickAnimation();
        }

        if (_dragFrom is not null)
        {
            return;
        }

        // A little while after the player lets go, the model resumes turning on its own.
        _idleTicks++;

        if (AutoRotate && _idleTicks > 45)
        {
            _yaw += _spin;
            _pitch += (0.15 - _pitch) * 0.03;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Thirty frames a second while the window is in front, five while it is behind
    /// another, none while it is minimised or the page is not shown: the figure is
    /// decoration, and decoration does not get to keep a laptop's fan running.
    /// </summary>
    private void TickAnimation()
    {
        if (!IsEffectivelyVisible || Bounds.Width <= 0)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            if (window.WindowState == WindowState.Minimized)
            {
                return;
            }

            if (!window.IsActive && ++_skippedTicks % 6 != 0)
            {
                return;
            }
        }

        var now = _clock.Elapsed.TotalSeconds;

        if (now - _clipStarted >= _clipLength)
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
        var now = _clock.Elapsed.TotalSeconds;

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
        _idleTicks = 0;
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
        var pose = Animated ? CurrentPose(_clock.Elapsed.TotalSeconds) : Pose.Rest;

        // Painter's algorithm by body part, not by face. The parts are convex boxes that
        // never intersect, so ordering them by depth is exact enough; ordering single
        // faces was not - at an oblique angle the inner side of an arm, a long thin face,
        // averaged out "behind" the front of the body and was painted over it. Within a
        // part the visible faces cannot overlap, and an outer layer is drawn right after
        // the part it wraps.
        var parts = Model(skin)
            .Select(part =>
            {
                var joint = pose.For(part.Kind);
                return (Part: part, Joint: joint, Depth: Rotate(Place(part.Centre, part.Pivot, joint, pose)).Z);
            })
            .OrderBy(p => p.Depth);

        foreach (var (part, joint, _) in parts)
        {
            foreach (var box in new[] { part.Base, part.Overlay })
            {
                if (box is null)
                {
                    continue;
                }

                foreach (var face in box.Faces())
                {
                    var projected = Project(face, part.Pivot, joint, pose, scale, centre);

                    if (projected is null)
                    {
                        continue;
                    }

                    // An outer-layer face with nothing drawn on it is not there: no
                    // texture to put down, and above all no shade to lay over nothing.
                    if (ReferenceEquals(box, part.Overlay) && skin.IsBlank(face.Texture))
                    {
                        continue;
                    }

                    using (context.PushTransform(projected.Matrix))
                    {
                        context.DrawImage(skin.Enlarged, projected.Texture, projected.Texture);
                    }

                    if (projected.Shade > 0.01)
                    {
                        var geometry = new PolylineGeometry(projected.Corners, isFilled: true);
                        context.DrawGeometry(new SolidColorBrush(Colors.Black, projected.Shade), null, geometry);
                    }
                }
            }
        }
    }

    /// <summary>A face on screen: where it goes, what it wears, how far back it is.</summary>
    private sealed record ProjectedFace(Matrix Matrix, Rect Texture, IList<Point> Corners, double Depth, double Shade);

    /// <summary>Half the model's height: it turns about its middle, not its feet.</summary>
    private const double ModelMidHeight = 16;

    /// <summary>
    /// A point of a body part in the posed figure: turned about the part's joint, lifted
    /// with the body, turned with the whole figure, and brought down so the camera's own
    /// turn goes about the middle.
    /// </summary>
    private static Vector3 Place(Vector3 point, Vector3 pivot, Joint joint, in Pose pose)
    {
        var local = Turn(new Vector3(point.X - pivot.X, point.Y - pivot.Y, point.Z - pivot.Z), joint);
        var x = local.X + pivot.X + joint.Dx;
        var y = local.Y + pivot.Y + joint.Dy;
        var z = local.Z + pivot.Z + joint.Dz;

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
    private static Vector3 PlaceNormal(Vector3 normal, Joint joint, in Pose pose)
    {
        var local = Turn(Turn(normal, joint), pose.Root);
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

    private ProjectedFace? Project(Face face, Vector3 pivot, Joint joint, Pose pose, double scale, Point centre)
    {
        var p = face.Corners.Select(c => Rotate(Place(c, pivot, joint, pose))).ToArray();

        // Orthographic: x right, y up, z towards the viewer.
        Point Screen(Vector3 v) => new(centre.X + v.X * scale, centre.Y - v.Y * scale);

        var s = p.Select(Screen).ToArray();

        // Back-face culling from the on-screen winding: a face seen from behind is drawn
        // with its corners going the other way round.
        var cross = (s[1].X - s[0].X) * (s[3].Y - s[0].Y) - (s[1].Y - s[0].Y) * (s[3].X - s[0].X);

        if (cross <= 0)
        {
            return null;
        }

        // Each face is pushed a third of a pixel outwards from its centre. Neighbouring
        // faces drawn with smoothing would otherwise meet in a hairline seam where both
        // edges are half-covered.
        var cx = s.Average(v => v.X);
        var cy = s.Average(v => v.Y);

        for (var i = 0; i < s.Length; i++)
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
        var matrix = new Matrix(ax, ay, bx, by,
            s[0].X - ax * t.X - bx * t.Y,
            s[0].Y - ay * t.X - by * t.Y);

        var normal = Rotate(PlaceNormal(face.Normal, joint, pose));
        var lit = Math.Max(0, Dot(normal, Light));
        var shade = (1 - lit) * 0.45;

        return new ProjectedFace(matrix, t, s, 0, shade);
    }

    private Vector3 Rotate(Vector3 v)
    {
        // Yaw about Y, then pitch about X.
        var cy = Math.Cos(_yaw);
        var sy = Math.Sin(_yaw);
        var x = v.X * cy + v.Z * sy;
        var z = -v.X * sy + v.Z * cy;

        var cp = Math.Cos(_pitch);
        var sp = Math.Sin(_pitch);
        var y = v.Y * cp - z * sp;
        z = v.Y * sp + z * cp;

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
    private readonly record struct Joint(double Rx = 0, double Ry = 0, double Rz = 0, double Dx = 0, double Dy = 0, double Dz = 0)
    {
        public static Joint Lerp(Joint a, Joint b, double t) => new(
            a.Rx + (b.Rx - a.Rx) * t, a.Ry + (b.Ry - a.Ry) * t, a.Rz + (b.Rz - a.Rz) * t,
            a.Dx + (b.Dx - a.Dx) * t, a.Dy + (b.Dy - a.Dy) * t, a.Dz + (b.Dz - a.Dz) * t);
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
            Body: Chest(frame.Torso),
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

    private static Joint Limb(EmotePartState part)
        => FromGame(part.Pitch, -part.Yaw, -part.Roll) with { Dx = part.X, Dy = -part.Y, Dz = -part.Z };

    /// <summary>
    /// The game hangs the chest from the neck, this figure stands it on the waist. A turn
    /// about the one is the same turn about the other plus a shift: where the turn would
    /// have carried the waist.
    /// </summary>
    private static Joint Chest(EmotePartState part)
    {
        var joint = Limb(part);

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
    private sealed record Box(
        double X, double Y, double Z,
        double Width, double Height, double Depth,
        double U, double V,
        double Inflate = 0,
        bool Mirror = false)
    {
        public IEnumerable<Face> Faces()
        {
            var x0 = X - Inflate;
            var x1 = X + Width + Inflate;
            var y0 = Y - Inflate;
            var y1 = Y + Height + Inflate;
            var z0 = Z - Inflate;
            var z1 = Z + Depth + Inflate;

            var w = Width;
            var h = Height;
            var d = Depth;

            // Texture layout: top and bottom above, then right, front, left, back in a row.
            var top = new Rect(U + d, V, w, d);
            var bottom = new Rect(U + d + w, V, w, d);
            var right = new Rect(U, V + d, d, h);
            var front = new Rect(U + d, V + d, w, h);
            var left = new Rect(U + d + w, V + d, d, h);
            var back = new Rect(U + 2 * d + w, V + d, w, h);

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
            yield return new Face(new[] { new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1) }, new Vector3(0, 1, 0), top);
            yield return new Face(new[] { new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x0, y0, z0) }, new Vector3(0, -1, 0), bottom);
        }
    }

    /// <summary>A body part: the box, the outer layer wrapped around it on a modern skin, and the joint it turns about.</summary>
    private sealed record Part(Box Base, Box? Overlay, PartKind Kind, Vector3 Pivot)
    {
        public Vector3 Centre => new(Base.X + Base.Width / 2, Base.Y + Base.Height / 2, Base.Z + Base.Depth / 2);
    }

    /// <summary>The player: head, body, two arms, two legs, each with its outer layer and its joint.</summary>
    private static IEnumerable<Part> Model(PlayerSkin skin)
    {
        var arm = skin.IsSlim ? 3 : 4;

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

        if (skin.IsLegacy)
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
