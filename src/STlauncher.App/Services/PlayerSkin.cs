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

    /// <summary>Who served the texture: "Mojang", "TLauncher", "ely.by", a mirror, or the cache.</summary>
    public string Source { get; init; } = string.Empty;

    public bool IsLegacy { get; }

    public bool IsSlim { get; }

    private byte[]? _alpha;

    /// <summary>
    /// True when nothing at all is drawn in this part of the texture. The viewer asks
    /// before shading a face of the outer layer: a shadow laid over a hat that is not
    /// there shows as a grey box around the head, plain to see on a light background.
    /// </summary>
    public bool IsBlank(Rect area)
    {
        _alpha ??= ReadAlpha();

        var width = Texture.PixelSize.Width;
        var height = Texture.PixelSize.Height;

        for (var y = Math.Max(0, (int)area.Y); y < Math.Min(height, (int)area.Bottom); y++)
        {
            for (var x = Math.Max(0, (int)area.X); x < Math.Min(width, (int)area.Right); x++)
            {
                if (_alpha[y * width + x] != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The alpha of every pixel, read once. Unreadable means "treat it all as drawn".</summary>
    private byte[] ReadAlpha()
    {
        var width = Texture.PixelSize.Width;
        var height = Texture.PixelSize.Height;
        var alpha = new byte[width * height];

        try
        {
            var stride = width * 4;
            var bytes = new byte[stride * height];
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

            try
            {
                Texture.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bytes.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            // Alpha is the fourth byte in both of the layouts a decoded texture comes in.
            for (var i = 0; i < alpha.Length; i++)
            {
                alpha[i] = bytes[i * 4 + 3];
            }
        }
        catch (Exception)
        {
            Array.Fill(alpha, (byte)255);
        }

        return alpha;
    }

    /// <summary>
    /// Frees the textures now rather than whenever the collector gets to them. Only for a
    /// skin nothing else shows: the editor's preview is replaced many times a second while
    /// the player draws, and each one carries a megabyte of enlarged pixels.
    /// </summary>
    public void Release()
    {
        _enlarged?.Dispose();
        _enlarged = null;
        Texture.Dispose();
    }

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
