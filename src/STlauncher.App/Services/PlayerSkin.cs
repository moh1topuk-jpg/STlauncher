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
    public PlayerSkin(Bitmap texture, bool isDefault)
    {
        Texture = texture ?? throw new ArgumentNullException(nameof(texture));
        IsDefault = isDefault;
        IsLegacy = texture.PixelSize.Height < 64;
        IsSlim = !IsLegacy && DetectSlim(texture);
    }

    public Bitmap Texture { get; }

    /// <summary>True for the built-in Steve shown when nothing else could be found.</summary>
    public bool IsDefault { get; }

    public bool IsLegacy { get; }

    public bool IsSlim { get; }

    /// <summary>
    /// The slim model leaves the fourth column of the arm texture unused, so a fully
    /// transparent pixel there tells the models apart. That is how the game itself does it
    /// when the profile does not say.
    /// </summary>
    private static bool DetectSlim(Bitmap texture)
    {
        try
        {
            var pixel = ReadPixel(texture, 54, 20) ?? ReadPixel(texture, 50, 16);
            return pixel is { } p && (p >> 24 & 0xFF) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static uint? ReadPixel(Bitmap texture, int x, int y)
    {
        if (x < 0 || y < 0 || x >= texture.PixelSize.Width || y >= texture.PixelSize.Height)
        {
            return null;
        }

        // A whole row rather than the one pixel: a 1×1 copy came back empty, a row is 256
        // bytes and comes back right.
        var width = texture.PixelSize.Width;
        var stride = width * 4;
        var buffer = Marshal.AllocHGlobal(stride);

        try
        {
            texture.CopyPixels(new PixelRect(0, y, width, 1), buffer, stride, stride);
            return (uint)Marshal.ReadInt32(buffer, x * 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
