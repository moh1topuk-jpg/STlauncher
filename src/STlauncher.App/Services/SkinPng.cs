using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using STlauncher.Core.Skins;

namespace STlauncher.App.Services;

/// <summary>
/// The bridge between a PNG and the editor's plain pixels, through the bitmap classes
/// the interface already has: no image library of its own.
/// </summary>
public static class SkinPng
{
    private static readonly Vector Dpi = new(96, 96);

    /// <summary>
    /// Decodes a skin: 64×64 as it is, 64×32 through the legacy conversion. Null when the
    /// file is not an image, not a skin's size, or in a pixel format that cannot be read back.
    /// </summary>
    public static SkinImage? Decode(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            using var bitmap = new Bitmap(stream);
            return FromBitmap(bitmap);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static SkinImage? FromBitmap(Bitmap bitmap)
    {
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;

        if (width != SkinImage.Size || height is not (SkinImage.Size or SkinLegacy.Height))
        {
            return null;
        }

        var format = bitmap.Format;
        var bgra = format == PixelFormat.Bgra8888;

        if (!bgra && format != PixelFormat.Rgba8888)
        {
            return null;
        }

        var premultiplied = bitmap.AlphaFormat == AlphaFormat.Premul;
        var opaque = bitmap.AlphaFormat == AlphaFormat.Opaque;
        var stride = width * 4;
        var bytes = new byte[stride * height];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bytes.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        var pixels = new uint[width * height];

        for (var i = 0; i < pixels.Length; i++)
        {
            uint b = bytes[i * 4 + (bgra ? 0 : 2)];
            uint g = bytes[i * 4 + 1];
            uint r = bytes[i * 4 + (bgra ? 2 : 0)];
            uint a = opaque ? 255u : bytes[i * 4 + 3];

            // Decoded pixels come with the colour already multiplied by the alpha; the
            // editor and the file want it plain. Exact for solid and for empty pixels,
            // which is all a skin normally has.
            if (premultiplied && a is > 0 and < 255)
            {
                r = Math.Min(255, (r * 255 + a / 2) / a);
                g = Math.Min(255, (g * 255 + a / 2) / a);
                b = Math.Min(255, (b * 255 + a / 2) / a);
            }

            pixels[i] = a == 0 ? 0 : a << 24 | r << 16 | g << 8 | b;
        }

        return SkinImage.FromPixels(pixels, width, height);
    }

    /// <summary>The image as PNG bytes, colours and transparency exactly as drawn.</summary>
    public static byte[] Encode(SkinImage image)
    {
        using var bitmap = ToBitmap(image, premultiplied: false);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A bitmap for the screen: the face, the figure, the editor's canvas.</summary>
    public static Bitmap ToBitmap(SkinImage image) => ToBitmap(image, premultiplied: true);

    private static Bitmap ToBitmap(SkinImage image, bool premultiplied)
    {
        var source = image.Pixels;
        var bytes = new byte[source.Length * 4];

        for (var i = 0; i < source.Length; i++)
        {
            var pixel = source[i];
            var a = pixel >> 24;
            var r = pixel >> 16 & 0xFF;
            var g = pixel >> 8 & 0xFF;
            var b = pixel & 0xFF;

            if (premultiplied && a < 255)
            {
                r = (r * a + 127) / 255;
                g = (g * a + 127) / 255;
                b = (b * a + 127) / 255;
            }

            bytes[i * 4] = (byte)b;
            bytes[i * 4 + 1] = (byte)g;
            bytes[i * 4 + 2] = (byte)r;
            bytes[i * 4 + 3] = (byte)a;
        }

        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

        try
        {
            return new Bitmap(
                PixelFormat.Bgra8888,
                premultiplied ? AlphaFormat.Premul : AlphaFormat.Unpremul,
                handle.AddrOfPinnedObject(),
                new PixelSize(SkinImage.Size, SkinImage.Size),
                Dpi,
                SkinImage.Size * 4);
        }
        finally
        {
            handle.Free();
        }
    }
}
