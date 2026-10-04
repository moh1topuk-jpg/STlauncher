using System;

namespace STlauncher.Core.Skins;

/// <summary>
/// Turns a pre-1.8 64×32 skin into the modern 64×64 layout, step for step the way the
/// game does when it loads one: the old texture has one arm and one leg, and the left
/// ones are the right ones mirrored.
/// </summary>
public static class SkinLegacy
{
    public const int Height = 32;

    public static SkinImage Convert(ReadOnlySpan<uint> pixels)
    {
        if (pixels.Length != SkinImage.Size * Height)
        {
            throw new ArgumentException("A legacy skin is 64 by 32 pixels.", nameof(pixels));
        }

        var image = new SkinImage();

        // The upper half as it is; the lower half starts empty.
        for (var i = 0; i < pixels.Length; i++)
        {
            image[i] = pixels[i];
        }

        // The left leg and the left arm: each face is the matching face of the right
        // limb, flipped - which is exactly what the mirror map of the layout describes.
        foreach (var face in SkinLayout.Faces(SkinModel.Classic))
        {
            if (face.Layer != SkinLayer.Base || face.Part is not (SkinPart.LeftArm or SkinPart.LeftLeg))
            {
                continue;
            }

            for (var y = face.Rect.Y; y < face.Rect.Bottom; y++)
            {
                for (var x = face.Rect.X; x < face.Rect.Right; x++)
                {
                    if (SkinLayout.Mirror(x, y, SkinModel.Classic) is { } source)
                    {
                        image[x, y] = image[source.X, source.Y];
                    }
                }
            }
        }

        // The base layer cannot be see-through in the game, so its alpha is forced; and
        // an old skin whose hat area was saved fully opaque never meant a hat at all.
        MakeOpaque(image, 0, 0, 32, 16);
        ClearIfFullyOpaque(image, 32, 0, 64, 32);
        MakeOpaque(image, 0, 16, 64, 32);
        MakeOpaque(image, 16, 48, 48, 64);

        return image;
    }

    private static void MakeOpaque(SkinImage image, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                image[x, y] |= 0xFF000000;
            }
        }
    }

    private static void ClearIfFullyOpaque(SkinImage image, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (image[x, y] >> 24 < 128)
                {
                    return;
                }
            }
        }

        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                image[x, y] &= 0x00FFFFFF;
            }
        }
    }
}
