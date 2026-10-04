using System;
using System.Globalization;

namespace STlauncher.Core.Skins;

/// <summary>
/// Colour arithmetic for the editor's chooser: 0xAARRGGBB to and from hue, saturation
/// and value, and to and from the text in the hex box.
/// </summary>
public static class SkinColour
{
    /// <param name="hue">Degrees, 0 to 360.</param>
    /// <param name="saturation">0 to 1.</param>
    /// <param name="value">0 to 1.</param>
    public static uint FromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var chroma = value * saturation;
        var sector = hue / 60;
        var x = chroma * (1 - Math.Abs(sector % 2 - 1));
        var m = value - chroma;

        var (r, g, b) = (int)sector switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };

        return 0xFF000000 | Channel(r + m) << 16 | Channel(g + m) << 8 | Channel(b + m);
    }

    public static (double Hue, double Saturation, double Value) ToHsv(uint argb)
    {
        var r = (argb >> 16 & 0xFF) / 255.0;
        var g = (argb >> 8 & 0xFF) / 255.0;
        var b = (argb & 0xFF) / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue = 0;

        if (delta > 0)
        {
            if (max == r)
            {
                hue = 60 * ((g - b) / delta % 6);
            }
            else if (max == g)
            {
                hue = 60 * ((b - r) / delta + 2);
            }
            else
            {
                hue = 60 * ((r - g) / delta + 4);
            }
        }

        if (hue < 0)
        {
            hue += 360;
        }

        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    /// <summary>"#RRGGBB".</summary>
    public static string ToHex(uint argb) => "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    /// <summary>Reads "#RRGGBB", "RRGGBB" or the short "#RGB". The result is opaque.</summary>
    public static bool TryParseHex(string? text, out uint argb)
    {
        argb = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hex = text.Trim().TrimStart('#');

        if (hex.Length == 3)
        {
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        }

        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        argb = 0xFF000000 | rgb;
        return true;
    }

    private static uint Channel(double value) => (uint)Math.Clamp((int)Math.Round(value * 255), 0, 255);
}
