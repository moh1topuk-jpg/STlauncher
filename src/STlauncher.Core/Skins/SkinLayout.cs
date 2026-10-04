using System;
using System.Collections.Generic;

namespace STlauncher.Core.Skins;

public enum SkinPart
{
    Head,
    Body,
    RightArm,
    LeftArm,
    RightLeg,
    LeftLeg
}

/// <summary>The skin itself, or the second layer worn over it: hat, jacket, sleeves, trousers.</summary>
public enum SkinLayer
{
    Base,
    Overlay
}

/// <summary>A side of a body part's box. Right and left are the player's own.</summary>
public enum SkinSide
{
    Top,
    Bottom,
    Right,
    Front,
    Left,
    Back
}

public readonly record struct SkinRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
}

/// <summary>One side of one body part on one layer, and the texture rectangle it wears.</summary>
public readonly record struct SkinPatch(SkinPart Part, SkinLayer Layer, SkinSide Side, SkinRect Rect);

/// <summary>
/// Where every body part lives in the 64×64 texture. Each part is a box unfolded the same
/// way from its origin: top and bottom side by side above, then right, front, left and
/// back in a row.
/// </summary>
public static class SkinLayout
{
    public static readonly IReadOnlyList<SkinPart> Parts = Enum.GetValues<SkinPart>();

    private static readonly SkinSide[] Sides = Enum.GetValues<SkinSide>();

    private static readonly SkinPatch[] ClassicFaces = Build(SkinModel.Classic);
    private static readonly SkinPatch[] SlimFaces = Build(SkinModel.Slim);

    /// <summary>The top-left corner of a part's unfolded box in the texture.</summary>
    public static (int U, int V) Origin(SkinPart part, SkinLayer layer) => (part, layer) switch
    {
        (SkinPart.Head, SkinLayer.Base) => (0, 0),
        (SkinPart.Head, SkinLayer.Overlay) => (32, 0),
        (SkinPart.Body, SkinLayer.Base) => (16, 16),
        (SkinPart.Body, SkinLayer.Overlay) => (16, 32),
        (SkinPart.RightArm, SkinLayer.Base) => (40, 16),
        (SkinPart.RightArm, SkinLayer.Overlay) => (40, 32),
        (SkinPart.LeftArm, SkinLayer.Base) => (32, 48),
        (SkinPart.LeftArm, SkinLayer.Overlay) => (48, 48),
        (SkinPart.RightLeg, SkinLayer.Base) => (0, 16),
        (SkinPart.RightLeg, SkinLayer.Overlay) => (0, 32),
        (SkinPart.LeftLeg, SkinLayer.Base) => (16, 48),
        _ => (0, 48)
    };

    /// <summary>Width, height and depth of a part's box, in pixels.</summary>
    public static (int Width, int Height, int Depth) Dimensions(SkinPart part, SkinModel model) => part switch
    {
        SkinPart.Head => (8, 8, 8),
        SkinPart.Body => (8, 12, 4),
        SkinPart.RightArm or SkinPart.LeftArm => (model == SkinModel.Slim ? 3 : 4, 12, 4),
        _ => (4, 12, 4)
    };

    /// <summary>The rectangle one side takes, measured from the part's own origin.</summary>
    public static SkinRect LocalRect(SkinPart part, SkinSide side, SkinModel model)
    {
        var (w, h, d) = Dimensions(part, model);

        return side switch
        {
            SkinSide.Top => new SkinRect(d, 0, w, d),
            SkinSide.Bottom => new SkinRect(d + w, 0, w, d),
            SkinSide.Right => new SkinRect(0, d, d, h),
            SkinSide.Front => new SkinRect(d, d, w, h),
            SkinSide.Left => new SkinRect(d + w, d, d, h),
            _ => new SkinRect(2 * d + w, d, w, h)
        };
    }

    /// <summary>The size of a part's whole unfolded box: what the editor reserves for it.</summary>
    public static (int Width, int Height) BlockSize(SkinPart part, SkinModel model)
    {
        var (w, h, d) = Dimensions(part, model);
        return (2 * d + 2 * w, d + h);
    }

    public static SkinPatch Face(SkinPart part, SkinLayer layer, SkinSide side, SkinModel model)
    {
        var (u, v) = Origin(part, layer);
        var local = LocalRect(part, side, model);
        return new SkinPatch(part, layer, side, new SkinRect(u + local.X, v + local.Y, local.Width, local.Height));
    }

    /// <summary>Every face of the model, both layers.</summary>
    public static IReadOnlyList<SkinPatch> Faces(SkinModel model)
        => model == SkinModel.Slim ? SlimFaces : ClassicFaces;

    /// <summary>The face a texture pixel belongs to, or null in the unused corners.</summary>
    public static SkinPatch? FaceAt(int x, int y, SkinModel model)
    {
        foreach (var face in Faces(model))
        {
            if (face.Rect.Contains(x, y))
            {
                return face;
            }
        }

        return null;
    }

    /// <summary>
    /// The same spot on the other half of the body: right arm to left arm, right leg to
    /// left leg, one cheek to the other. Null where the pixel is on no face.
    /// </summary>
    /// <remarks>
    /// Every face flips left to right. On the front, back, top and bottom that is plain;
    /// the right side runs back to front and the left side front to back, so a spot
    /// equally far from the back lands at the flipped column there too.
    /// </remarks>
    public static (int X, int Y)? Mirror(int x, int y, SkinModel model)
    {
        if (FaceAt(x, y, model) is not { } face)
        {
            return null;
        }

        var part = face.Part switch
        {
            SkinPart.RightArm => SkinPart.LeftArm,
            SkinPart.LeftArm => SkinPart.RightArm,
            SkinPart.RightLeg => SkinPart.LeftLeg,
            SkinPart.LeftLeg => SkinPart.RightLeg,
            var same => same
        };

        var side = face.Side switch
        {
            SkinSide.Right => SkinSide.Left,
            SkinSide.Left => SkinSide.Right,
            var same => same
        };

        var target = Face(part, face.Layer, side, model).Rect;
        return (target.X + face.Rect.Right - 1 - x, target.Y + y - face.Rect.Y);
    }

    private static SkinPatch[] Build(SkinModel model)
    {
        var faces = new List<SkinPatch>();

        foreach (var layer in new[] { SkinLayer.Base, SkinLayer.Overlay })
        {
            foreach (var part in Parts)
            {
                foreach (var side in Sides)
                {
                    faces.Add(Face(part, layer, side, model));
                }
            }
        }

        return faces.ToArray();
    }
}
