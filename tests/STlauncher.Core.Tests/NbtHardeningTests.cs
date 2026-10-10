using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using STlauncher.Core.Nbt;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// The NBT reader is pointed at files from other people's archives, and what it reads is
/// written back into the player's world. Both directions are held here: a document comes
/// back byte for byte, and a hostile one is refused before it costs anything.
/// </summary>
public class NbtHardeningTests
{
    private static byte[] Serialize(NbtCompound root, string rootName = "")
    {
        using var stream = new MemoryStream();
        NbtWriter.Write(stream, root, rootName);
        return stream.ToArray();
    }

    private static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static NbtCompound Sample()
    {
        var inner = new NbtCompound()
            .Set("zeta", new NbtInt(1))
            .Set("alpha", new NbtString("второй"))
            .Set("mid", new NbtDouble(0.5));

        var compounds = new NbtList(NbtTagType.Compound);
        compounds.Items.Add(new NbtCompound().Set("id", new NbtShort(7)));

        return new NbtCompound()
            .Set("Zebra", new NbtByte(-3))
            .Set("apple", new NbtLong(long.MinValue))
            .Set("Float", new NbtFloat(1.25f))
            .Set("bytes", new NbtByteArray(new byte[] { 1, 2, 255 }))
            .Set("ints", new NbtIntArray(new[] { int.MinValue, 0, 9 }))
            .Set("longs", new NbtLongArray(new[] { 1L, -1L }))
            .Set("emptyOfCompounds", new NbtList(NbtTagType.Compound))
            .Set("emptyOfEnd", new NbtList(NbtTagType.End))
            .Set("emptyOfStrings", new NbtList(NbtTagType.String))
            .Set("list", compounds)
            .Set("inner", inner);
    }

    [Fact]
    public void RoundTrip_IsByteForByte_OrderAndEmptyListTypesIncluded()
    {
        var original = Serialize(Sample(), "root");

        using var stream = new MemoryStream(original);
        var read = NbtReader.Read(stream, out var rootName);

        Assert.Equal("root", rootName);
        Assert.Equal(
            new[] { "Zebra", "apple", "Float", "bytes", "ints", "longs", "emptyOfCompounds", "emptyOfEnd", "emptyOfStrings", "list", "inner" },
            read.Items.Select(i => i.Key).ToArray());
        Assert.Equal(NbtTagType.Compound, ((NbtList)read.Get("emptyOfCompounds")!).ElementType);
        Assert.Equal(NbtTagType.End, ((NbtList)read.Get("emptyOfEnd")!).ElementType);
        Assert.Equal(NbtTagType.String, ((NbtList)read.Get("emptyOfStrings")!).ElementType);

        Assert.Equal(original, Serialize(read, rootName));
    }

    [Fact]
    public void Compound_KeepsOrder_WhenATagIsReplacedOrRemoved()
    {
        var compound = new NbtCompound()
            .Set("a", new NbtInt(1))
            .Set("b", new NbtInt(2))
            .Set("c", new NbtInt(3));

        // Replacing keeps the place; this is what a rename of LevelName does.
        compound.Set("b", new NbtString("two"));
        Assert.Equal(new[] { "a", "b", "c" }, compound.Items.Select(i => i.Key).ToArray());

        Assert.True(compound.Remove("a"));
        compound.Set("d", new NbtInt(4));
        Assert.Equal(new[] { "b", "c", "d" }, compound.Items.Select(i => i.Key).ToArray());
        Assert.Equal(3, compound.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void File_RoundTrips_GzippedAndPlain(bool gzipped)
    {
        var directory = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "file.dat");

        NbtFile.Write(path, new NbtDocument(Sample(), string.Empty, gzipped));

        var head = File.ReadAllBytes(path);
        Assert.Equal(gzipped, head[0] == 0x1f && head[1] == 0x8b);

        var document = NbtFile.Read(path);

        Assert.Equal(gzipped, document.Gzipped);
        Assert.Equal(Serialize(Sample()), Serialize(document.Root));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Strings_UseJavasModifiedUtf8()
    {
        // U+0000 is C0 80; a character outside the basic plane is two three-byte surrogates.
        Assert.Equal(new byte[] { 0x61, 0xC0, 0x80, 0x62 }, ModifiedUtf8.Encode("a\0b"));
        Assert.Equal(new byte[] { 0xED, 0xA0, 0xBD, 0xED, 0xB8, 0x80 }, ModifiedUtf8.Encode("\U0001F600"));

        const string name = "Мир \U0001F600 с\0нулём";
        Assert.Equal(name, ModifiedUtf8.Decode(ModifiedUtf8.Encode(name)));

        var bytes = Serialize(new NbtCompound().Set("LevelName", new NbtString(name)));
        using var stream = new MemoryStream(bytes);
        Assert.Equal(name, NbtReader.Read(stream).GetString("LevelName"));

        // What an editor written in another language saves is read too.
        Assert.Equal("\U0001F600", ModifiedUtf8.Decode(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }));
        Assert.Throws<InvalidDataException>(() => ModifiedUtf8.Decode(new byte[] { 0xE0, 0x41 }));
    }

    [Theory]
    [InlineData(NbtTagType.Compound)]
    [InlineData(NbtTagType.List)]
    public void DeepNesting_IsRefused_NotAStackOverflow(NbtTagType kind)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 10, 0, 0 });

        for (var i = 0; i < 100_000; i++)
        {
            if (kind == NbtTagType.Compound)
            {
                // A compound named "a" inside the previous one.
                stream.Write(new byte[] { 10, 0, 1, (byte)'a' });
            }
            else if (i == 0)
            {
                // The first list hangs off the root; every next one is the single element of the last.
                stream.Write(new byte[] { 9, 0, 1, (byte)'a', 9, 0, 0, 0, 1 });
            }
            else
            {
                stream.Write(new byte[] { 9, 0, 0, 0, 1 });
            }
        }

        stream.Position = 0;
        var error = Assert.Throws<InvalidDataException>(() => NbtReader.Read(stream));
        Assert.Contains("nested", error.Message);
    }

    [Fact]
    public void NestingTheGameAllows_IsRead()
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 10, 0, 0 });
        const int depth = 400;

        for (var i = 0; i < depth; i++)
        {
            stream.Write(new byte[] { 10, 0, 1, (byte)'a' });
        }

        stream.Write(new byte[depth + 1]);
        stream.Position = 0;

        Assert.NotNull(NbtReader.Read(stream).GetCompound("a"));
    }

    [Theory]
    [InlineData(NbtTagType.ByteArray)]
    [InlineData(NbtTagType.IntArray)]
    [InlineData(NbtTagType.LongArray)]
    [InlineData(NbtTagType.List)]
    public void ALengthThatLies_IsRefused_BeforeAnyMemoryIsTaken(NbtTagType kind)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 10, 0, 0, (byte)kind, 0, 1, (byte)'a' });

        if (kind == NbtTagType.List)
        {
            stream.WriteByte((byte)NbtTagType.Compound);
        }

        // Two billion elements, and then nothing.
        stream.Write(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF });
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => NbtReader.Read(stream));
    }

    [Fact]
    public void NegativeLengths_And_ListsOfEnd_AreRefused()
    {
        var negative = new byte[] { 10, 0, 0, 7, 0, 1, (byte)'a', 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.Throws<InvalidDataException>(() => NbtReader.Read(new MemoryStream(negative)));

        // Elements of type End take no bytes at all: nothing but this check bounds the list.
        var ends = new byte[] { 10, 0, 0, 9, 0, 1, (byte)'a', 0, 0, 0x0F, 0xFF, 0xFF };
        Assert.Throws<InvalidDataException>(() => NbtReader.Read(new MemoryStream(ends)));
    }

    [Fact]
    public void AGzipBomb_StopsAtTheBudget()
    {
        // A few kilobytes on disk that inflate into 256 MB of one byte array.
        const int length = 256 * 1024 * 1024;
        using var packed = new MemoryStream();

        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(new byte[] { 10, 0, 0, 7, 0, 1, (byte)'a' });
            gzip.Write(BigEndian(length));

            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 256; i++)
            {
                gzip.Write(zeros);
            }

            gzip.WriteByte(0);
        }

        Assert.True(packed.Length < 1024 * 1024);
        packed.Position = 0;

        Assert.Throws<InvalidDataException>(() => NbtFile.Read(packed));
    }

    [Fact]
    public void ManyTinyTags_AreChargedForWhatTheyCostInMemory()
    {
        // A list of empty compounds is one byte each on disk; the per-tag charge is what stops it.
        const int count = 200_000;
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 10, 0, 0, 9, 0, 1, (byte)'a', 10 });
        stream.Write(BigEndian(count));
        stream.Write(new byte[count + 1]);

        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => NbtReader.Read(stream, out _, maxBudget: 1024 * 1024));

        stream.Position = 0;
        Assert.Equal(count, ((NbtList)NbtReader.Read(stream).Get("a")!).Items.Count);
    }

    [Fact]
    public void ATruncatedFile_Throws_InsteadOfReturningHalfATree()
    {
        var whole = Serialize(Sample());

        Assert.ThrowsAny<IOException>(() => NbtReader.Read(new MemoryStream(whole, 0, whole.Length - 5)));
        Assert.ThrowsAny<IOException>(() => NbtReader.Read(new MemoryStream(Array.Empty<byte>())));
    }
}
