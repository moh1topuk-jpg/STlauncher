using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using STlauncher.App.Services;

namespace STlauncher.App.Controls;

/// <summary>
/// The player model with the skin on it, turning slowly and draggable with the mouse.
/// </summary>
/// <remarks>
/// There is no 3D API here, and none is needed: the model is nothing but boxes, and under
/// an orthographic camera every face of a box lands on the screen as a parallelogram -
/// which is exactly an affine transform of the texture rectangle it wears. So each face
/// is one DrawImage under a matrix, faces are drawn back to front, and those turned away
/// are skipped. Lighting is a translucent shade over the faces that look away from the
/// light. It runs on the ordinary drawing pipeline and costs nothing to set up.
/// </remarks>
public sealed class SkinViewer : Control
{
    public static readonly StyledProperty<PlayerSkin?> SkinProperty =
        AvaloniaProperty.Register<SkinViewer, PlayerSkin?>(nameof(Skin));

    public static readonly StyledProperty<bool> AutoRotateProperty =
        AvaloniaProperty.Register<SkinViewer, bool>(nameof(AutoRotate), true);

    private static readonly Vector3 Light = Normalize(new Vector3(-0.4, 0.8, 0.6));

    private readonly DispatcherTimer _timer;
    private double _yaw = 0.55;
    private double _pitch = 0.15;
    private double _spin = 0.008;
    private Point? _dragFrom;
    private double _idleTicks;

    static SkinViewer()
    {
        AffectsRender<SkinViewer>(SkinProperty);
    }

    public SkinViewer()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        Cursor = new Cursor(StandardCursorType.Hand);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();

        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
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

    private void Tick()
    {
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

        var faces = new List<ProjectedFace>();
        var scale = Math.Min(Bounds.Width / 24, Bounds.Height / 40);
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);

        foreach (var box in Model(skin))
        {
            foreach (var face in box.Faces())
            {
                var projected = Project(face, scale, centre);

                if (projected is not null)
                {
                    faces.Add(projected);
                }
            }
        }

        // Painter's algorithm: the furthest faces first.
        foreach (var face in faces.OrderBy(f => f.Depth))
        {
            using (context.PushTransform(face.Matrix))
            {
                context.DrawImage(skin.Texture, face.Texture, face.Texture);
            }

            if (face.Shade > 0.01)
            {
                var geometry = new PolylineGeometry(face.Corners, isFilled: true);
                context.DrawGeometry(new SolidColorBrush(Colors.Black, face.Shade), null, geometry);
            }
        }
    }

    /// <summary>A face on screen: where it goes, what it wears, how far back it is.</summary>
    private sealed record ProjectedFace(Matrix Matrix, Rect Texture, IList<Point> Corners, double Depth, double Shade);

    /// <summary>Half the model's height: it turns about its middle, not its feet.</summary>
    private const double ModelMidHeight = 16;

    private ProjectedFace? Project(Face face, double scale, Point centre)
    {
        var p = face.Corners.Select(c => Rotate(c with { Y = c.Y - ModelMidHeight })).ToArray();

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

        // Map the texture rectangle onto the parallelogram: the rect's top-left, top-right
        // and bottom-left corners land on s[0], s[1], s[3].
        var t = face.Texture;
        var ax = (s[1].X - s[0].X) / t.Width;
        var ay = (s[1].Y - s[0].Y) / t.Width;
        var bx = (s[3].X - s[0].X) / t.Height;
        var by = (s[3].Y - s[0].Y) / t.Height;
        var matrix = new Matrix(ax, ay, bx, by,
            s[0].X - ax * t.X - bx * t.Y,
            s[0].Y - ay * t.X - by * t.Y);

        var normal = Rotate(face.Normal);
        var lit = Math.Max(0, Dot(normal, Light));
        var shade = (1 - lit) * 0.45;

        return new ProjectedFace(matrix, t, s, p.Average(v => v.Z), shade);
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
            yield return new Face(new[] { new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1) }, new Vector3(1, 0, 0), Mirror ? left : left);
            yield return new Face(new[] { new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1), new Vector3(x0, y0, z0) }, new Vector3(-1, 0, 0), Mirror ? right : right);
            yield return new Face(new[] { new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1) }, new Vector3(0, 1, 0), top);
            yield return new Face(new[] { new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x0, y0, z0) }, new Vector3(0, -1, 0), bottom);
        }
    }

    /// <summary>The player: head, body, two arms, two legs, plus the outer layers a modern skin carries.</summary>
    private static IEnumerable<Box> Model(PlayerSkin skin)
    {
        var arm = skin.IsSlim ? 3 : 4;

        // Model space: origin at the feet, y up, the player facing +z. Units are pixels.
        yield return new Box(-4, 24, -4, 8, 8, 8, 0, 0);                         // head
        yield return new Box(-4, 24, -4, 8, 8, 8, 32, 0, Inflate: 0.5);          // hat
        yield return new Box(-4, 12, -2, 8, 12, 4, 16, 16);                      // body
        yield return new Box(-2, 0, -2, 4, 12, 4, 0, 16);                        // right leg (the player's right, screen left)

        if (skin.IsLegacy)
        {
            yield return new Box(-4 - 4, 12, -2, 4, 12, 4, 40, 16);              // right arm
            yield return new Box(4, 12, -2, 4, 12, 4, 40, 16, Mirror: true);     // left arm, mirrored
            yield return new Box(-2 + 4, 0, -2, 4, 12, 4, 0, 16, Mirror: true);  // left leg, mirrored
            yield break;
        }

        yield return new Box(-4 - arm, 12, -2, arm, 12, 4, 40, 16);              // right arm
        yield return new Box(4, 12, -2, arm, 12, 4, 32, 48);                     // left arm
        yield return new Box(2, 0, -2, 4, 12, 4, 16, 48);                        // left leg

        // Outer layers, slightly inflated so they sit over the base.
        yield return new Box(-4, 12, -2, 8, 12, 4, 16, 32, Inflate: 0.25);       // jacket
        yield return new Box(-4 - arm, 12, -2, arm, 12, 4, 40, 32, Inflate: 0.25); // right sleeve
        yield return new Box(4, 12, -2, arm, 12, 4, 48, 48, Inflate: 0.25);      // left sleeve
        yield return new Box(-2, 0, -2, 4, 12, 4, 0, 32, Inflate: 0.25);         // right trouser
        yield return new Box(2, 0, -2, 4, 12, 4, 0, 48, Inflate: 0.25);          // left trouser
    }
}
