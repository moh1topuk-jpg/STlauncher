using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using STlauncher.App.Services;

namespace STlauncher.App.Controls;

/// <summary>
/// The knitted hat the home figure wears in the winter season, painted here texel by texel
/// like <see cref="PumpkinTexture"/>, at the size of <see cref="PlayerSkin.Enlarged"/>. Two
/// boxes share it, laid out as a skin lays out its boxes: the hat, eight wide, three high
/// and eight deep, from (0, 0), and the pompom, two each way, from (0, 12).
/// </summary>
internal static class BeanieTexture
{
    private const int Width = 32;
    private const int Height = 16;
    private const int Scale = PlayerSkin.EnlargeFactor;

    private static Bitmap? _bitmap;

    private const uint Wool = 0xFFB3243F;
    private const uint WoolDark = 0xFF8A1A30;
    private const uint Stripe = 0xFFF2EEE9;
    private const uint Cuff = 0xFFE8E2DA;
    private const uint CuffDark = 0xFFC9C1B6;
    private const uint Pompom = 0xFFFAF7F2;
    private const uint PompomDark = 0xFFDDD6CC;

    public static Bitmap Get() => _bitmap ??= Paint();

    private static Bitmap Paint()
    {
        var pixels = new uint[Width * Height];

        // The hat: top and bottom faces in the first eight rows, the sides in rows 8 to 10.
        Fill(pixels, 8, 0, 8, 8, (x, y) => (x + y) % 4 == 0 ? WoolDark : Wool);
        Fill(pixels, 16, 0, 8, 8, (_, _) => CuffDark);
        Fill(pixels, 0, 8, 32, 3, Side);

        // The pompom: two each way.
        Fill(pixels, 2, 12, 2, 2, (_, _) => Pompom);
        Fill(pixels, 4, 12, 2, 2, (_, _) => PompomDark);
        Fill(pixels, 0, 14, 8, 2, (x, y) => (x + y) % 3 == 0 ? PompomDark : Pompom);

        var bitmap = new WriteableBitmap(
            new PixelSize(Width * Scale, Height * Scale), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var buffer = bitmap.Lock())
        {
            var row = new int[Width * Scale];

            for (var y = 0; y < Height * Scale; y++)
            {
                for (var x = 0; x < Width * Scale; x++)
                {
                    row[x] = unchecked((int)pixels[y / Scale * Width + x / Scale]);
                }

                Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
            }
        }

        return bitmap;
    }

    /// <summary>A side, top row first: red knit with a row of white stitches, then the turned-up cuff.</summary>
    private static uint Side(int x, int y) => y switch
    {
        0 => x % 3 == 0 ? WoolDark : Wool,
        1 => x % 2 == 0 ? Stripe : Wool,
        _ => x % 2 == 0 ? Cuff : CuffDark
    };

    private static void Fill(uint[] pixels, int left, int top, int width, int height, Func<int, int, uint> texel)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(top + y) * Width + left + x] = texel(x, y);
            }
        }
    }
}
