using System;

namespace STlauncher.Core.Skins;

/// <summary>Which arms the model has: the classic four pixels wide, or the slim three.</summary>
public enum SkinModel
{
    Classic,
    Slim
}

/// <summary>
/// A skin texture as plain pixels: always the modern 64×64 layout, one 0xAARRGGBB value
/// per pixel, alpha not premultiplied. Everything the editor does happens on this; a PNG
/// is only how it arrives and leaves.
/// </summary>
public sealed class SkinImage
{
    public const int Size = 64;

    private readonly uint[] _pixels;

    public SkinImage()
    {
        _pixels = new uint[Size * Size];
    }

    private SkinImage(uint[] pixels)
    {
        _pixels = pixels;
    }

    /// <summary>All pixels, row by row from the top.</summary>
    public ReadOnlySpan<uint> Pixels => _pixels;

    public uint this[int x, int y]
    {
        get => _pixels[y * Size + x];
        set => _pixels[y * Size + x] = value;
    }

    internal uint this[int index]
    {
        get => _pixels[index];
        set => _pixels[index] = value;
    }

    public static bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size;

    public SkinImage Clone() => new((uint[])_pixels.Clone());

    public bool ContentEquals(SkinImage other) => _pixels.AsSpan().SequenceEqual(other._pixels);

    /// <summary>
    /// Takes 64×64 pixels as they are, or 64×32 pixels through the legacy conversion.
    /// Null for any other size: that is not a skin.
    /// </summary>
    public static SkinImage? FromPixels(ReadOnlySpan<uint> pixels, int width, int height)
    {
        if (width != Size || pixels.Length != width * height)
        {
            return null;
        }

        if (height == Size)
        {
            return new SkinImage(pixels.ToArray());
        }

        return height == SkinLegacy.Height ? SkinLegacy.Convert(pixels) : null;
    }

    /// <summary>
    /// The slim model leaves the fourth column of each arm unused, so those columns are
    /// transparent in a slim texture: on the arm tops and down the arm sides, right arm
    /// and left arm. All four patches must be empty - one pixel is not evidence, a
    /// painted hole is.
    /// </summary>
    public bool LooksSlim()
        => IsTransparent(50, 16, 2, 4) &&
           IsTransparent(54, 20, 2, 12) &&
           IsTransparent(42, 48, 2, 4) &&
           IsTransparent(46, 52, 2, 12);

    private bool IsTransparent(int x, int y, int width, int height)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                if (this[column, row] >> 24 != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
