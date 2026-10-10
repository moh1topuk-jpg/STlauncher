using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace STlauncher.Core.Nbt;

/// <summary>
/// How much a single NBT document may ask of the reader. The files the launcher reads -
/// servers.dat, level.dat - come from the player's disk, but also from archives someone
/// else made, so a document is read on a budget rather than on trust.
/// </summary>
public static class NbtLimits
{
    /// <summary>The game itself refuses to nest deeper than this.</summary>
    public const int MaxDepth = 512;

    /// <summary>
    /// The most a document may cost: its bytes after inflating, plus a charge per tag.
    /// A heavily modded level.dat is a few megabytes; this leaves room and still stops a
    /// gzip bomb long before it hurts.
    /// </summary>
    public const long MaxBudget = 64L * 1024 * 1024;

    /// <summary>
    /// What one tag costs on top of its bytes. A list of a million empty compounds is a
    /// megabyte on disk and many times that in memory; the charge closes that gap.
    /// </summary>
    internal const int TagCharge = 16;
}

public static class NbtReader
{
    public static NbtCompound Read(Stream stream, bool leaveOpen = true)
        => Read(stream, out _, NbtLimits.MaxBudget, leaveOpen);

    /// <param name="rootName">The name of the root tag: empty in the game's own files, kept so a rewrite matches.</param>
    /// <param name="maxBudget">See <see cref="NbtLimits.MaxBudget"/>.</param>
    public static NbtCompound Read(Stream stream, out string rootName, long maxBudget = NbtLimits.MaxBudget, bool leaveOpen = true)
    {
        try
        {
            var source = new Source(stream, maxBudget);
            var type = (NbtTagType)source.ReadByte();

            if (type != NbtTagType.Compound)
            {
                throw new InvalidDataException($"Unexpected root tag type: {type}.");
            }

            rootName = ReadString(source);

            return ReadCompound(source, depth: 1);
        }
        finally
        {
            if (!leaveOpen)
            {
                stream.Dispose();
            }
        }
    }

    private static NbtTag ReadPayload(NbtTagType type, Source source, int depth)
    {
        source.Charge(NbtLimits.TagCharge);

        return type switch
        {
            NbtTagType.Byte => new NbtByte((sbyte)source.ReadByte()),
            NbtTagType.Short => new NbtShort(BinaryPrimitives.ReadInt16BigEndian(source.Read(2))),
            NbtTagType.Int => new NbtInt(source.ReadInt32()),
            NbtTagType.Long => new NbtLong(source.ReadInt64()),
            NbtTagType.Float => new NbtFloat(BitConverter.Int32BitsToSingle(source.ReadInt32())),
            NbtTagType.Double => new NbtDouble(BitConverter.Int64BitsToDouble(source.ReadInt64())),
            NbtTagType.ByteArray => new NbtByteArray(source.ReadBytes(ReadLength(source, elementSize: 1))),
            NbtTagType.String => new NbtString(ReadString(source)),
            NbtTagType.List => ReadList(source, depth),
            NbtTagType.Compound => ReadCompound(source, depth),
            NbtTagType.IntArray => ReadIntArray(source),
            NbtTagType.LongArray => ReadLongArray(source),
            _ => throw new InvalidDataException($"Unsupported tag type: {(byte)type}.")
        };
    }

    /// <summary>
    /// A length is a claim until the bytes behind it have been paid for: an array that
    /// says it holds two billion longs must not get its memory before the budget agrees.
    /// </summary>
    private static int ReadLength(Source source, int elementSize)
    {
        var length = source.ReadInt32();

        if (length < 0)
        {
            throw new InvalidDataException("Negative length in an NBT tag.");
        }

        source.Require((long)length * elementSize);
        return length;
    }

    private static NbtIntArray ReadIntArray(Source source)
    {
        var values = new int[ReadLength(source, sizeof(int))];

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = source.ReadInt32();
        }

        return new NbtIntArray(values);
    }

    private static NbtLongArray ReadLongArray(Source source)
    {
        var values = new long[ReadLength(source, sizeof(long))];

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = source.ReadInt64();
        }

        return new NbtLongArray(values);
    }

    private static NbtList ReadList(Source source, int depth)
    {
        if (depth >= NbtLimits.MaxDepth)
        {
            throw new InvalidDataException("NBT is nested too deeply.");
        }

        var elementType = (NbtTagType)source.ReadByte();
        var length = source.ReadInt32();

        if (length < 0)
        {
            throw new InvalidDataException("Negative length in an NBT list.");
        }

        // Elements of type End take no bytes, so nothing else would bound such a list.
        if (elementType == NbtTagType.End && length > 0)
        {
            throw new InvalidDataException("An NBT list of End tags cannot have elements.");
        }

        // Each element costs at least its charge; ask for all of it before the first one.
        source.Require((long)length * NbtLimits.TagCharge);

        // The element type is kept as read even when the list is empty: an empty list of
        // compounds and an empty list of End are different bytes on the way back.
        var list = new NbtList(elementType);

        for (var i = 0; i < length; i++)
        {
            list.Items.Add(ReadPayload(elementType, source, depth + 1));
        }

        return list;
    }

    private static NbtCompound ReadCompound(Source source, int depth)
    {
        if (depth >= NbtLimits.MaxDepth)
        {
            throw new InvalidDataException("NBT is nested too deeply.");
        }

        var compound = new NbtCompound();

        while (true)
        {
            var type = (NbtTagType)source.ReadByte();
            if (type == NbtTagType.End)
            {
                break;
            }

            var name = ReadString(source);
            compound.Set(name, ReadPayload(type, source, depth + 1));
        }

        return compound;
    }

    private static string ReadString(Source source)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(source.Read(2));
        return ModifiedUtf8.Decode(source.ReadBytes(length));
    }

    /// <summary>The stream, read exactly and on a budget.</summary>
    private sealed class Source
    {
        private readonly Stream _stream;
        private readonly byte[] _scratch = new byte[8];
        private long _left;

        public Source(Stream stream, long budget)
        {
            _stream = stream;
            _left = budget;
        }

        public void Charge(long amount)
        {
            _left -= amount;

            if (_left < 0)
            {
                throw new InvalidDataException("The NBT document is larger than the reader accepts.");
            }
        }

        /// <summary>Fails now if what is about to be read cannot fit, without spending anything.</summary>
        public void Require(long amount)
        {
            if (amount > _left)
            {
                throw new InvalidDataException("The NBT document is larger than the reader accepts.");
            }
        }

        public ReadOnlySpan<byte> Read(int count)
        {
            Charge(count);
            _stream.ReadExactly(_scratch, 0, count);
            return _scratch.AsSpan(0, count);
        }

        public byte ReadByte() => Read(1)[0];

        public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Read(4));

        public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Read(8));

        public byte[] ReadBytes(int count)
        {
            Charge(count);
            var bytes = new byte[count];
            _stream.ReadExactly(bytes, 0, count);
            return bytes;
        }
    }
}

public static class NbtWriter
{
    public static void Write(Stream stream, NbtCompound root, string rootName = "", bool leaveOpen = true)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen);
        writer.Write((byte)NbtTagType.Compound);
        WriteString(writer, rootName);
        WritePayload(writer, root);
    }

    private static void WritePayload(BinaryWriter writer, NbtTag tag)
    {
        switch (tag)
        {
            case NbtByte value:
                writer.Write(value.Value);
                break;
            case NbtShort value:
                WriteInt16(writer, value.Value);
                break;
            case NbtInt value:
                WriteInt32(writer, value.Value);
                break;
            case NbtLong value:
                WriteInt64(writer, value.Value);
                break;
            case NbtFloat value:
                WriteInt32(writer, BitConverter.SingleToInt32Bits(value.Value));
                break;
            case NbtDouble value:
                WriteInt64(writer, BitConverter.DoubleToInt64Bits(value.Value));
                break;
            case NbtByteArray value:
                WriteInt32(writer, value.Value.Length);
                writer.Write(value.Value);
                break;
            case NbtString value:
                WriteString(writer, value.Value);
                break;
            case NbtList value:
                writer.Write((byte)value.ElementType);
                WriteInt32(writer, value.Items.Count);
                foreach (var item in value.Items)
                {
                    if (item.Type != value.ElementType)
                    {
                        throw new InvalidOperationException(
                            $"A list of {value.ElementType} cannot hold a {item.Type} tag.");
                    }

                    WritePayload(writer, item);
                }

                break;
            case NbtCompound value:
                foreach (var (name, child) in value.Items)
                {
                    writer.Write((byte)child.Type);
                    WriteString(writer, name);
                    WritePayload(writer, child);
                }

                writer.Write((byte)NbtTagType.End);
                break;
            case NbtIntArray value:
                WriteInt32(writer, value.Value.Length);
                foreach (var item in value.Value)
                {
                    WriteInt32(writer, item);
                }

                break;
            case NbtLongArray value:
                WriteInt32(writer, value.Value.Length);
                foreach (var item in value.Value)
                {
                    WriteInt64(writer, item);
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported tag: {tag.GetType().Name}");
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = ModifiedUtf8.Encode(value);

        if (bytes.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("An NBT string cannot be longer than 65535 bytes.");
        }

        WriteUInt16(writer, (ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteUInt16(BinaryWriter writer, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        writer.Write(buffer);
    }

    private static void WriteInt16(BinaryWriter writer, short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        writer.Write(buffer);
    }

    private static void WriteInt32(BinaryWriter writer, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        writer.Write(buffer);
    }

    private static void WriteInt64(BinaryWriter writer, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        writer.Write(buffer);
    }
}

/// <summary>
/// Java's "modified UTF-8", which is what NBT strings are written in. It differs from the
/// real thing in two places: U+0000 is two bytes (C0 80), and a character outside the
/// basic plane is its two surrogates, three bytes each, rather than one four-byte
/// sequence. Reading a world name with an emoji through plain UTF-8 turned it into
/// replacement characters, and writing it back made that permanent.
/// </summary>
public static class ModifiedUtf8
{
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var chars = new StringBuilder(bytes.Length);

        for (var i = 0; i < bytes.Length;)
        {
            int lead = bytes[i];

            if (lead < 0x80)
            {
                chars.Append((char)lead);
                i++;
            }
            else if ((lead & 0xE0) == 0xC0)
            {
                chars.Append((char)(((lead & 0x1F) << 6) | Continuation(bytes, i + 1)));
                i += 2;
            }
            else if ((lead & 0xF0) == 0xE0)
            {
                chars.Append((char)(((lead & 0x0F) << 12) | (Continuation(bytes, i + 1) << 6) | Continuation(bytes, i + 2)));
                i += 3;
            }
            else if ((lead & 0xF8) == 0xF0)
            {
                // Not something Java writes, but editors written in other languages do.
                var point = ((lead & 0x07) << 18) | (Continuation(bytes, i + 1) << 12) |
                            (Continuation(bytes, i + 2) << 6) | Continuation(bytes, i + 3);

                if (point < 0x10000 || point > 0x10FFFF)
                {
                    throw new InvalidDataException("Malformed text in an NBT string.");
                }

                chars.Append(char.ConvertFromUtf32(point));
                i += 4;
            }
            else
            {
                throw new InvalidDataException("Malformed text in an NBT string.");
            }
        }

        return chars.ToString();
    }

    private static int Continuation(ReadOnlySpan<byte> bytes, int index)
    {
        if (index >= bytes.Length || (bytes[index] & 0xC0) != 0x80)
        {
            throw new InvalidDataException("Malformed text in an NBT string.");
        }

        return bytes[index] & 0x3F;
    }

    public static byte[] Encode(string value)
    {
        var length = 0;

        foreach (var c in value)
        {
            length += c is >= (char)1 and <= (char)0x7F ? 1 : c <= 0x7FF ? 2 : 3;
        }

        var bytes = new byte[length];
        var at = 0;

        foreach (var c in value)
        {
            if (c is >= (char)1 and <= (char)0x7F)
            {
                bytes[at++] = (byte)c;
            }
            else if (c <= 0x7FF)
            {
                bytes[at++] = (byte)(0xC0 | (c >> 6));
                bytes[at++] = (byte)(0x80 | (c & 0x3F));
            }
            else
            {
                bytes[at++] = (byte)(0xE0 | (c >> 12));
                bytes[at++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                bytes[at++] = (byte)(0x80 | (c & 0x3F));
            }
        }

        return bytes;
    }
}

/// <summary>One NBT file as it was on disk: the tree, the root's name and whether it was gzipped.</summary>
public sealed record NbtDocument(NbtCompound Root, string RootName, bool Gzipped);

/// <summary>Reads and writes whole NBT files, gzipped (level.dat) or plain (servers.dat).</summary>
public static class NbtFile
{
    public static NbtDocument Read(string path, long maxBudget = NbtLimits.MaxBudget)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(file, maxBudget);
    }

    /// <summary>Reads from a seekable stream, looking at the first two bytes to tell gzip from plain.</summary>
    public static NbtDocument Read(Stream stream, long maxBudget = NbtLimits.MaxBudget)
    {
        Span<byte> magic = stackalloc byte[2];
        var start = stream.Position;
        var read = stream.ReadAtLeast(magic, 2, throwOnEndOfStream: false);
        stream.Position = start;

        var gzipped = read == 2 && magic[0] == 0x1f && magic[1] == 0x8b;

        // The budget counts inflated bytes, so a small file that unpacks into gigabytes
        // is stopped at the budget and not at the end of memory.
        using var inflated = gzipped
            ? new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true)
            : null;
        using var buffered = new BufferedStream(inflated ?? stream, 16 * 1024);

        var root = NbtReader.Read(buffered, out var rootName, maxBudget);
        return new NbtDocument(root, rootName, gzipped);
    }

    /// <summary>Writes through a temporary file, so a crash mid-write leaves the old file whole.</summary>
    public static void Write(string path, NbtDocument document)
        => AtomicFile.Write(path, stream =>
        {
            if (document.Gzipped)
            {
                using var gzip = new System.IO.Compression.GZipStream(
                    stream, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true);
                NbtWriter.Write(gzip, document.Root, document.RootName);
            }
            else
            {
                NbtWriter.Write(stream, document.Root, document.RootName);
            }
        });
}
