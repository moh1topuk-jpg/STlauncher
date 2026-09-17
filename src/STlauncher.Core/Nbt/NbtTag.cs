using System.Collections.Generic;

namespace STlauncher.Core.Nbt;

public enum NbtTagType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12
}

public abstract class NbtTag
{
    public abstract NbtTagType Type { get; }
}

public sealed class NbtByte : NbtTag
{
    public NbtByte(sbyte value) => Value = value;

    public sbyte Value { get; set; }

    public override NbtTagType Type => NbtTagType.Byte;
}

public sealed class NbtShort : NbtTag
{
    public NbtShort(short value) => Value = value;

    public short Value { get; set; }

    public override NbtTagType Type => NbtTagType.Short;
}

public sealed class NbtInt : NbtTag
{
    public NbtInt(int value) => Value = value;

    public int Value { get; set; }

    public override NbtTagType Type => NbtTagType.Int;
}

public sealed class NbtLong : NbtTag
{
    public NbtLong(long value) => Value = value;

    public long Value { get; set; }

    public override NbtTagType Type => NbtTagType.Long;
}

public sealed class NbtFloat : NbtTag
{
    public NbtFloat(float value) => Value = value;

    public float Value { get; set; }

    public override NbtTagType Type => NbtTagType.Float;
}

public sealed class NbtDouble : NbtTag
{
    public NbtDouble(double value) => Value = value;

    public double Value { get; set; }

    public override NbtTagType Type => NbtTagType.Double;
}

public sealed class NbtByteArray : NbtTag
{
    public NbtByteArray(byte[] value) => Value = value;

    public byte[] Value { get; set; }

    public override NbtTagType Type => NbtTagType.ByteArray;
}

public sealed class NbtString : NbtTag
{
    public NbtString(string value) => Value = value;

    public string Value { get; set; }

    public override NbtTagType Type => NbtTagType.String;
}

public sealed class NbtList : NbtTag
{
    public NbtList(NbtTagType elementType)
    {
        ElementType = elementType;
    }

    public NbtTagType ElementType { get; set; }

    public List<NbtTag> Items { get; } = new();

    public override NbtTagType Type => NbtTagType.List;
}

public sealed class NbtCompound : NbtTag
{
    public Dictionary<string, NbtTag> Items { get; } = new();

    public override NbtTagType Type => NbtTagType.Compound;

    public NbtTag? Get(string name) => Items.TryGetValue(name, out var tag) ? tag : null;

    public string? GetString(string name) => (Get(name) as NbtString)?.Value;

    public sbyte? GetByte(string name) => (Get(name) as NbtByte)?.Value;

    public NbtCompound Set(string name, NbtTag tag)
    {
        Items[name] = tag;
        return this;
    }
}

public sealed class NbtIntArray : NbtTag
{
    public NbtIntArray(int[] value) => Value = value;

    public int[] Value { get; set; }

    public override NbtTagType Type => NbtTagType.IntArray;
}

public sealed class NbtLongArray : NbtTag
{
    public NbtLongArray(long[] value) => Value = value;

    public long[] Value { get; set; }

    public override NbtTagType Type => NbtTagType.LongArray;
}