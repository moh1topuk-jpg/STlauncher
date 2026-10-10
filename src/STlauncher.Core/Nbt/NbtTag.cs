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

/// <summary>
/// Named tags, kept in the order they were read or added. A file read and written back
/// must come out byte for byte the same when nothing was changed, and a plain dictionary
/// only keeps its order until the first removal.
/// </summary>
public sealed class NbtCompound : NbtTag
{
    private readonly List<string> _names = new();
    private readonly Dictionary<string, NbtTag> _tags = new(System.StringComparer.Ordinal);

    /// <summary>The tags in file order.</summary>
    public IEnumerable<KeyValuePair<string, NbtTag>> Items
    {
        get
        {
            foreach (var name in _names)
            {
                yield return new KeyValuePair<string, NbtTag>(name, _tags[name]);
            }
        }
    }

    public int Count => _names.Count;

    public override NbtTagType Type => NbtTagType.Compound;

    public NbtTag? Get(string name) => _tags.TryGetValue(name, out var tag) ? tag : null;

    public string? GetString(string name) => (Get(name) as NbtString)?.Value;

    public sbyte? GetByte(string name) => (Get(name) as NbtByte)?.Value;

    public int? GetInt(string name) => (Get(name) as NbtInt)?.Value;

    public long? GetLong(string name) => (Get(name) as NbtLong)?.Value;

    public NbtCompound? GetCompound(string name) => Get(name) as NbtCompound;

    /// <summary>Replaces a tag where it stands, or appends a new one.</summary>
    public NbtCompound Set(string name, NbtTag tag)
    {
        if (!_tags.ContainsKey(name))
        {
            _names.Add(name);
        }

        _tags[name] = tag;
        return this;
    }

    public bool Remove(string name)
    {
        if (!_tags.Remove(name))
        {
            return false;
        }

        _names.Remove(name);
        return true;
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