using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;

namespace STlauncher.App.Services;

/// <summary>
/// A player's skin texture with what the renderers need to know about it: whether it is
/// the modern 64×64 layout or the pre-1.8 64×32 one, and whether the arms are the slim
/// "Alex" three pixels wide or the classic four.
/// </summary>
public sealed class PlayerSkin
{
    /// <param name="slimModel">
    /// The model type when the source says so (Mojang's profile does). Null means guess
    /// from the texture, which is right for most skins but not for a classic-model skin
    /// drawn on a slim template with the unused columns left transparent.
    /// </param>
    public PlayerSkin(Bitmap texture, bool isDefault, bool? slimModel = null)
    {
        Texture = texture ?? throw new ArgumentNullException(nameof(texture));
        IsDefault = isDefault;
        IsLegacy = texture.PixelSize.Height < 64;
        IsSlim = !IsLegacy && (slimModel ?? DetectSlim(texture));
    }

    public Bitmap Texture { get; }

    /// <summary>How much larger <see cref="Enlarged"/> is than the texture.</summary>
    public const int EnlargeFactor = 8;

    private Bitmap? _enlarged;

    /// <summary>
    /// The texture blown up eight times with no smoothing, for the 3D viewer. Drawing the
    /// 64-pixel texture straight onto a turned face left every pixel a jagged staircase;
    /// drawing this copy with smoothing keeps the pixels crisp (they are eight wide
    /// already) and gives the face clean edges.
    /// </summary>
    public Bitmap Enlarged
    {
        get
        {
            _enlarged ??= Texture.CreateScaledBitmap(
                new PixelSize(Texture.PixelSize.Width * EnlargeFactor, Texture.PixelSize.Height * EnlargeFactor),
                BitmapInterpolationMode.None);

            return _enlarged;
        }
    }

    /// <summary>True for the built-in Steve shown when nothing else could be found.</summary>
    public bool IsDefault { get; }

    public bool IsLegacy { get; }

    public bool IsSlim { get; }

    /// <summary>
    /// The slim model leaves the fourth column of each arm unused, so those columns are
    /// transparent in a slim texture: two 2×4 patches on the arm tops and two 2×12 strips
    /// down the arm sides, right arm and left arm. All four must be empty - one pixel is
    /// not evidence, a painted hole is.
    /// </summary>
    private static bool DetectSlim(Bitmap texture)
    {
        try
        {
            return IsTransparent(texture, 50, 16, 2, 4) &&
                   IsTransparent(texture, 54, 20, 2, 12) &&
                   IsTransparent(texture, 42, 48, 2, 4) &&
                   IsTransparent(texture, 46, 52, 2, 12);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsTransparent(Bitmap texture, int x, int y, int width, int height)
    {
        var rowWidth = texture.PixelSize.Width;
        var stride = rowWidth * 4;
        var buffer = Marshal.AllocHGlobal(stride);

        try
        {
            for (var row = y; row < y + height; row++)
            {
                if (row >= texture.PixelSize.Height)
                {
                    return false;
                }

                // A whole row rather than the few pixels: a 1×1 copy came back empty, a
                // row is 256 bytes and comes back right.
                texture.CopyPixels(new PixelRect(0, row, rowWidth, 1), buffer, stride, stride);

                for (var column = x; column < x + width && column < rowWidth; column++)
                {
                    var alpha = (uint)Marshal.ReadInt32(buffer, column * 4) >> 24;

                    if (alpha != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
