using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using STlauncher.Core.Boost;
using STlauncher.Core.Instances;
using STlauncher.Core.Launch;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// A preset used to overwrite options.txt with no way back. Now it says what each key
/// was, and taking it back restores exactly the keys that still hold what it wrote -
/// without touching a byte of anything else, whatever encoding the file is in.
/// </summary>
public class PresetRevertTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "stl-preset-" + Guid.NewGuid().ToString("N"));

    public PresetRevertTests()
    {
        Directory.CreateDirectory(_game);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_game, true);
        }
        catch (Exception)
        {
        }
    }

    private string OptionsPath => Path.Combine(_game, GameOptions.FileName);

    /// <summary>What a 1.7.10 game leaves on a Russian Windows: code page 1251, CRLF.</summary>
    private static byte[] OldFile()
    {
        var bytes = new List<byte>();

        void Line(string ascii) => bytes.AddRange(Encoding.ASCII.GetBytes(ascii + "\r\n"));

        Line("renderDistance:12");
        Line("particles:0");
        bytes.AddRange(Encoding.ASCII.GetBytes("resourcePacks:[\""));
        bytes.AddRange(new byte[] { 0xCF, 0xE0, 0xEA }); // "Пак" in Windows-1251
        bytes.AddRange(Encoding.ASCII.GetBytes(".zip\"]\r\n"));
        Line("a line without a colon");
        Line("ao:2");
        Line("maxFps:120");
        Line("key_key.jump:57");

        return bytes.ToArray();
    }

    private string? Value(string key) => OptionsFile.TryLoad(OptionsPath)!.Get(key);

    [Fact]
    public void ApplyThenRevert_IsTheSameBytes_ForACodePageFile()
    {
        var original = OldFile();
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(original));
        File.WriteAllBytes(OptionsPath, original);

        var records = GameOptions.ApplyPerformancePreset(_game, PerformancePreset.Low)!;

        Assert.Equal("6", Value("renderDistance"));
        Assert.Equal("\"off\"", Value("cloudStatus"));

        // The pack's name is still the same three bytes: nothing was decoded and encoded back.
        var written = File.ReadAllBytes(OptionsPath);
        Assert.True(written.AsSpan().IndexOf(new byte[] { (byte)'"', 0xCF, 0xE0, 0xEA, (byte)'.' }) > 0);
        Assert.Contains("key_key.jump:57", Encoding.Latin1.GetString(written));

        var revert = GameOptions.RevertPerformancePreset(_game, records)!;

        Assert.Empty(revert.LeftToPlayer);
        Assert.Equal(original, File.ReadAllBytes(OptionsPath));
    }

    [Fact]
    public void Records_KeepThePreviousValue_OrThatTheKeyWasAbsent()
    {
        File.WriteAllBytes(OptionsPath, OldFile());

        var records = GameOptions.ApplyPerformancePreset(_game, PerformancePreset.Low)!;

        Assert.Equal("12", records.Single(r => r.Key == "renderDistance").Previous);
        Assert.Equal("6", records.Single(r => r.Key == "renderDistance").Written);
        Assert.Null(records.Single(r => r.Key == "simulationDistance").Previous);

        // maxFps was 120 and the preset says 120: nothing changed, nothing to take back.
        Assert.DoesNotContain(records, r => r.Key == "maxFps");
    }

    [Fact]
    public void Revert_LeavesAKeyThePlayerChangedSince()
    {
        File.WriteAllText(OptionsPath, "renderDistance:12\nparticles:0\n");
        var records = GameOptions.ApplyPerformancePreset(_game, PerformancePreset.Low)!;

        // In the game's menu, after the preset.
        var file = OptionsFile.TryLoad(OptionsPath)!;
        file.Set("renderDistance", "9");
        file.Remove("mipmapLevels");
        file.Save(OptionsPath);

        var revert = GameOptions.RevertPerformancePreset(_game, records)!;

        Assert.Contains("renderDistance", revert.LeftToPlayer);
        Assert.Contains("mipmapLevels", revert.LeftToPlayer);
        Assert.Contains("particles", revert.Restored);
        Assert.Equal("9", Value("renderDistance"));
        Assert.Equal("0", Value("particles"));

        // A key that had not existed is taken out again, not left with some value.
        Assert.Null(Value("simulationDistance"));
        Assert.Null(Value("mipmapLevels"));
    }

    [Fact]
    public void SecondPresetOverTheFirst_StillLeadsBackToBeforeTheFirst()
    {
        var original = "renderDistance:12\nparticles:0\nentityShadows:true\n";
        File.WriteAllText(OptionsPath, original);

        var low = GameOptions.ApplyPerformancePreset(_game, PerformancePreset.Low)!;

        // Between the two the player set one thing by hand: that is the value to return to.
        var file = OptionsFile.TryLoad(OptionsPath)!;
        file.Set("particles", "1");
        file.Save(OptionsPath);

        var high = GameOptions.ApplyPerformancePreset(_game, PerformancePreset.High, low)!;

        Assert.Equal("16", Value("renderDistance"));
        Assert.Equal("12", high.Single(r => r.Key == "renderDistance").Previous);
        Assert.Equal("1", high.Single(r => r.Key == "particles").Previous);

        // entityShadows went true -> false -> true: back where it started, nothing recorded.
        Assert.DoesNotContain(high, r => r.Key == "entityShadows");

        GameOptions.RevertPerformancePreset(_game, high);

        Assert.Equal("12", Value("renderDistance"));
        Assert.Equal("1", Value("particles"));
        Assert.Equal("true", Value("entityShadows"));
        Assert.Null(Value("cloudStatus"));
    }

    [Fact]
    public void FileThatIsNotText_IsLeftAlone()
    {
        var utf16 = Encoding.Unicode.GetBytes("renderDistance:12\r\n");
        File.WriteAllBytes(OptionsPath, utf16);

        Assert.Null(GameOptions.ApplyPerformancePreset(_game, PerformancePreset.Low));
        Assert.Null(GameOptions.RevertPerformancePreset(_game, new[] { new BoostOptionRecord { Key = "renderDistance", Previous = "12", Written = "6" } }));
        Assert.Equal(utf16, File.ReadAllBytes(OptionsPath));
    }

    [Fact]
    public void Record_LivesInTheBuildsOwnFile()
    {
        var instance = new Instance
        {
            Id = "a",
            Name = "a",
            Preset = new PresetRecord
            {
                Preset = "Low",
                MemoryPrevious = 4096,
                MemoryWritten = 3072,
                Options = { new BoostOptionRecord { Key = "simulationDistance", Previous = null, Written = "5" } }
            }
        };

        var json = JsonSerializer.Serialize(instance);
        var back = JsonSerializer.Deserialize<Instance>(json)!;

        Assert.Null(back.Preset!.Options.Single().Previous);
        Assert.Equal("5", back.Preset.Options.Single().Written);
        Assert.Equal(4096, back.Preset.MemoryPrevious);
        Assert.False(back.Preset.IsEmpty);

        // A build no preset was applied to carries no such key at all.
        Assert.DoesNotContain("\"preset\"", JsonSerializer.Serialize(new Instance { Id = "b", Name = "b" }));
    }
}
