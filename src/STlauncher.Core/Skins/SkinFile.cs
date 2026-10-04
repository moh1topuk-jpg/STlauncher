using System;

namespace STlauncher.Core.Skins;

/// <summary>What a file offered as a skin turned out to be.</summary>
public enum SkinFileKind
{
    /// <summary>64×64: taken as it is.</summary>
    Modern,

    /// <summary>64×32, the layout before 1.8: converted on the way in.</summary>
    Legacy,

    /// <summary>A PNG of some other size.</summary>
    WrongSize,

    /// <summary>Not a PNG at all.</summary>
    NotPng
}

/// <summary>
/// Looks at a file's first bytes and says whether it can be a skin, before anything
/// tries to decode it: the answer the player gets names the size that was found.
/// </summary>
public static class SkinFile
{
    private static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static SkinFileKind Inspect(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        // Signature, then the first chunk, which is always the header: length, "IHDR",
        // width and height as big-endian integers.
        if (bytes.Length < 24 ||
            !bytes[..8].SequenceEqual(Signature) ||
            bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
        {
            return SkinFileKind.NotPng;
        }

        width = ReadInt(bytes[16..]);
        height = ReadInt(bytes[20..]);

        if (width != SkinImage.Size)
        {
            return SkinFileKind.WrongSize;
        }

        return height switch
        {
            SkinImage.Size => SkinFileKind.Modern,
            SkinLegacy.Height => SkinFileKind.Legacy,
            _ => SkinFileKind.WrongSize
        };
    }

    private static int ReadInt(ReadOnlySpan<byte> bytes)
        => bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3];
}
