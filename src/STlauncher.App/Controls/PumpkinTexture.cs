using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using STlauncher.App.Services;

namespace STlauncher.App.Controls;

/// <summary>
/// The carved pumpkin the home figure wears in the Halloween season, painted here texel by
/// texel - no picture file - in the layout of a skin's head: top and bottom in the first
/// row, then right, front, left and back. Sixteen texels a face, like a block of the game,
/// at the size of <see cref="PlayerSkin.Enlarged"/> so the viewer's arithmetic is the same.
/// Painted once, on first use, and kept.
/// </summary>
internal static class PumpkinTexture
{
    private const int Texels = 16;
    private const int FacePixels = 8 * PlayerSkin.EnlargeFactor;
    private const int TexelPixels = FacePixels / Texels;

    private static Bitmap? _bitmap;

    // The rind: ribs (dark), the rind between them, and its light side.
    private const uint Rib = 0xFFB4560E;
    private const uint Rind = 0xFFE07E17;
    private const uint Shine = 0xFFF29A2C;
    private const uint Carved = 0xFF2A1205;
    private const uint CarvedEdge = 0xFF6E2F08;
    private const uint Stem = 0xFF5C4A1C;
    private const uint StemDark = 0xFF3E3112;
    private const uint Crown = 0xFFC7650F;

    /// <summary>The face, sixteen by sixteen: '#' carved out, '+' its inner edge.</summary>
    private static readonly string[] Face =
    {
        "................",
        "................",
        "................",
        "...#.......#....",
        "..##+.....##+...",
        ".####+...####+..",
        "................",
        "................",
        "................",
        "..#..........#..",
        "..############..",
        "..###+####+###..",
        "...##########...",
        "....+#.##.#+....",
        "................",
        "................",
    };

    public static Bitmap Get() => _bitmap ??= Paint();

    private static Bitmap Paint()
    {
        const int width = 4 * FacePixels;
        const int height = 2 * FacePixels;
        var pixels = new uint[width * height];

        // Row one: top (8,0) and bottom (16,0) in skin pixels; row two: right, front, left, back.
        FillFace(pixels, width, 1, 0, Top);
        FillFace(pixels, width, 2, 0, Bottom);
        FillFace(pixels, width, 0, 1, Side);
        FillFace(pixels, width, 1, 1, Front);
        FillFace(pixels, width, 2, 1, Side);
        FillFace(pixels, width, 3, 1, Side);

        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var buffer = bitmap.Lock())
        {
            for (var y = 0; y < height; y++)
            {
                var row = new int[width];

                for (var x = 0; x < width; x++)
                {
                    row[x] = unchecked((int)pixels[y * width + x]);
                }

                Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, width);
            }
        }

        return bitmap;
    }

    private static void FillFace(uint[] pixels, int width, int column, int row, Func<int, int, uint> texel)
    {
        for (var ty = 0; ty < Texels; ty++)
        {
            for (var tx = 0; tx < Texels; tx++)
            {
                var colour = texel(tx, ty);

                for (var py = 0; py < TexelPixels; py++)
                {
                    var y = row * FacePixels + ty * TexelPixels + py;

                    for (var px = 0; px < TexelPixels; px++)
                    {
                        pixels[y * width + column * FacePixels + tx * TexelPixels + px] = colour;
                    }
                }
            }
        }
    }

    /// <summary>A side: upright ribs, with a little unevenness so it reads as a block of the game and not a flat fill.</summary>
    private static uint Side(int x, int y)
    {
        if (x is 0 or 15 || ((x is 5 or 10) && (y + x) % 7 != 0))
        {
            return Rib;
        }

        if ((x is 2 or 7 or 12) && y is > 1 and < 14 && (y * 3 + x) % 5 != 0)
        {
            return Shine;
        }

        // A few darker texels near the ends, where the rind curves in.
        return (y is 0 or 15) && (x * 7 + y) % 3 == 0 ? Crown : Rind;
    }

    private static uint Front(int x, int y) => Face[y][x] switch
    {
        '#' => Carved,
        '+' => CarvedEdge,
        _ => Side(x, y)
    };

    /// <summary>The top: rind in rings round a stem.</summary>
    private static uint Top(int x, int y)
    {
        if (x is >= 6 and <= 9 && y is >= 6 and <= 9)
        {
            return x is 6 || y is 9 ? StemDark : Stem;
        }

        var ring = Math.Max(Math.Abs(2 * x - 15), Math.Abs(2 * y - 15));
        return ring is 13 or 14 or 15 ? Rib : ring is 9 or 10 ? Crown : (x + y) % 6 == 0 ? Shine : Rind;
    }

    private static uint Bottom(int x, int y)
        => Math.Max(Math.Abs(2 * x - 15), Math.Abs(2 * y - 15)) >= 13 ? Rib : Crown;
}
