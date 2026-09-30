using System;
using System.Linq;
using STlauncher.Core.Loaders;
using STlauncher.Core.Modpacks;
using Xunit;

namespace STlauncher.Core.Tests;

public class BuildCodeTests
{
    private static BuildCodePayload Sample() => new(
        "Showtime plus",
        "1.21.11",
        LoaderKind.Fabric,
        "0.19.5",
        new[]
        {
            new BuildCodeFile("mods/sodium.jar", "https://cdn.modrinth.com/data/AANobbMI/versions/abc/sodium.jar", "aa11", 1234),
            new BuildCodeFile("resourcepacks/pack.zip", "https://github.com/o/r/releases/download/1/pack.zip", "bb22", 99)
        },
        new[] { "my-private-mod.jar" });

    [Fact]
    public void A_code_round_trips()
    {
        var code = BuildCode.Encode(Sample());

        Assert.StartsWith("STB1.", code);
        Assert.DoesNotContain("+", code);
        Assert.DoesNotContain("/", code[5..]);

        Assert.True(BuildCode.TryDecode(code, out var back));
        Assert.Equal("Showtime plus", back!.Name);
        Assert.Equal("1.21.11", back.GameVersion);
        Assert.Equal(LoaderKind.Fabric, back.Loader);
        Assert.Equal("0.19.5", back.LoaderVersion);
        Assert.Equal(2, back.Files.Count);
        Assert.Equal("https://cdn.modrinth.com/data/AANobbMI/versions/abc/sodium.jar", back.Files[0].Url);
        Assert.Equal(1234, back.Files[0].Size);
        Assert.Equal("aa11", back.Files[0].Sha1);
        Assert.Equal(new[] { "my-private-mod.jar" }, back.Missing);
    }

    [Fact]
    public void A_code_pasted_with_text_around_it_still_reads()
    {
        var code = BuildCode.Encode(Sample());
        var message = "держи мою сборку:\n" + code + "\nставь и заходи";

        Assert.True(BuildCode.TryDecode(message, out var back));
        Assert.Equal(2, back!.Files.Count);
    }

    [Fact]
    public void Twenty_mods_fit_a_chat_message()
    {
        var files = Enumerable.Range(0, 24).Select(i => new BuildCodeFile(
            $"mods/some-mod-{i}-fabric-1.21.11-1.{i}.0.jar",
            $"https://cdn.modrinth.com/data/P{i:D7}/versions/V{i:D7}/some-mod-{i}-fabric-1.21.11-1.{i}.0.jar",
            Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant() + Convert.ToHexString(Guid.NewGuid().ToByteArray()[..4]).ToLowerInvariant(),
            100000 + i)).ToList();

        var code = BuildCode.Encode(new BuildCodePayload("Big", "1.21.11", LoaderKind.Fabric, "0.19.5", files, Array.Empty<string>()));

        Assert.InRange(code.Length, 500, 3500);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("STB1.!!!")]
    [InlineData("STB1.aGVsbG8")]
    public void Garbage_is_refused(string text)
    {
        Assert.False(BuildCode.TryDecode(text, out var payload));
        Assert.Null(payload);
    }

    [Fact]
    public void Files_from_hosts_the_format_forbids_are_dropped()
    {
        var code = BuildCode.Encode(new BuildCodePayload("x", "1.21", LoaderKind.Vanilla, null,
            new[] { new BuildCodeFile("mods/a.jar", "https://evil.example/a.jar", "aa", 1), new BuildCodeFile("../x.jar", "https://cdn.modrinth.com/data/a/b.jar", "bb", 1) },
            Array.Empty<string>()));

        Assert.True(BuildCode.TryDecode(code, out var back));
        Assert.Empty(back!.Files);
    }
}
