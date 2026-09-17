using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace STlauncher.Core.Nbt;

public static class NbtReader
{
    public static NbtCompound Read(Stream stream, bool leaveOpen = true)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen);
        var type = (NbtTagType)reader.ReadByte();

        if (type != NbtTagType.Compound)
        {
            throw new InvalidDataException($"Unexpected root tag type: {type}.");
        }

        ReadString(reader);

        return (NbtCompound)ReadPayload(type, reader);
    }

    private static NbtTag ReadPayload(NbtTagType type, BinaryReader reader) => type switch
    {
        NbtTagType.Byte => new NbtByte(reader.ReadSByte()),
        NbtTagType.Short => new NbtShort(ReadInt16(reader)),
        NbtTagType.Int => new NbtInt(ReadInt32(reader)),
        NbtTagType.Long => new NbtLong(ReadInt64(reader)),
        NbtTagType.Float => new NbtFloat(ReadSingle(reader)),
        NbtTagType.Double => new NbtDouble(ReadDouble(reader)),
        NbtTagType.ByteArray => ReadByteArray(reader),
        NbtTagType.String => new NbtString(ReadString(reader)),
        NbtTagType.List => ReadList(reader),
        NbtTagType.Compound => ReadCompound(reader),
        NbtTagType.IntArray => ReadIntArray(reader),
        NbtTagType.LongArray => ReadLongArray(reader),
        _ => throw new InvalidDataException($"Unsupported tag type: {type}.")
    };

    private static NbtByteArray ReadByteArray(BinaryReader reader)
    {
        var length = ReadInt32(reader);
        return new NbtByteArray(reader.ReadBytes(length));
    }

    private static NbtIntArray ReadIntArray(BinaryReader reader)
    {
        var length = ReadInt32(reader);
        var values = new int[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = ReadInt32(reader);
        }

        return new NbtIntArray(values);
    }

    private static NbtLongArray ReadLongArray(BinaryReader reader)
    {
        var length = ReadInt32(reader);
        var values = new long[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = ReadInt64(reader);
        }

        return new NbtLongArray(values);
    }

    private static NbtList ReadList(BinaryReader reader)
    {
        var elementType = (NbtTagType)reader.ReadByte();
        var length = ReadInt32(reader);
        var list = new NbtList(elementType);

        for (var i = 0; i < length; i++)
        {
            list.Items.Add(ReadPayload(elementType, reader));
        }

        return list;
    }

    private static NbtCompound ReadCompound(BinaryReader reader)
    {
        var compound = new NbtCompound();

        while (true)
        {
            var type = (NbtTagType)reader.ReadByte();
            if (type == NbtTagType.End)
            {
                break;
            }

            var name = ReadString(reader);
            compound.Items[name] = ReadPayload(type, reader);
        }

        return compound;
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private static short ReadInt16(BinaryReader reader)
        => BinaryPrimitives.ReadInt16BigEndian(reader.ReadBytes(2));

    private static int ReadInt32(BinaryReader reader)
        => BinaryPrimitives.ReadInt32BigEndian(reader.ReadBytes(4));

    private static long ReadInt64(BinaryReader reader)
        => BinaryPrimitives.ReadInt64BigEndian(reader.ReadBytes(8));

    private static float ReadSingle(BinaryReader reader)
        => BitConverter.Int32BitsToSingle(ReadInt32(reader));

    private static double ReadDouble(BinaryReader reader)
        => BitConverter.Int64BitsToDouble(ReadInt64(reader));
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
        var bytes = Encoding.UTF8.GetBytes(value);
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