using System;
using System.IO;
using System.Linq;
using STlauncher.Core.Launch;
using STlauncher.Core.Screenshots;
using Xunit;

namespace STlauncher.Core.Tests;

public class GameOptionsTests
{
    private static string Temp()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void PerformancePreset_OverwritesGraphicsKeys_KeepsTheRest()
    {
        var game = Temp();
        File.WriteAllText(Path.Combine(game, "options.txt"), "lang:ru_ru\nrenderDistance:24\nkey_key.jump:key.keyboard.space\nsoundCategory_master:0.5\n");

        GameOptions.ApplyPerformancePreset(game, PerformancePreset.Low);

        var lines = File.ReadAllLines(Path.Combine(game, "options.txt"));
        Assert.Contains("renderDistance:6", lines);
        Assert.Contains("cloudStatus:\"off\"", lines);
        Assert.Contains("key_key.jump:key.keyboard.space", lines);
        Assert.Contains("soundCategory_master:0.5", lines);
        Assert.Contains("lang:ru_ru", lines);
        Assert.Single(lines, l => l.StartsWith("renderDistance:"));
    }

    [Fact]
    public void CopySettings_TakesControlsAndSound_LeavesPacksLanguageAndServer()
    {
        var source = Temp();
        var target = Temp();
        File.WriteAllText(Path.Combine(source, "options.txt"), "lang:en_us\nresourcePacks:[\"vanilla\",\"file/x.zip\"]\nlastServer:mc.example\nkey_key.jump:key.keyboard.j\nsoundCategory_music:0.2\nfov:1.0\n");
        File.WriteAllText(Path.Combine(target, "options.txt"), "lang:ru_ru\nresourcePacks:[\"vanilla\"]\nfov:0.5\n");

        var copied = GameOptions.CopySettings(source, target);

        var lines = File.ReadAllLines(Path.Combine(target, "options.txt"));
        Assert.Equal(3, copied);
        Assert.Contains("lang:ru_ru", lines);
        Assert.Contains("resourcePacks:[\"vanilla\"]", lines);
        Assert.Contains("key_key.jump:key.keyboard.j", lines);
        Assert.Contains("soundCategory_music:0.2", lines);
        Assert.Contains("fov:1.0", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("lastServer:"));
    }

    [Fact]
    public void Screenshots_AreListedNewestFirst_ByTheNameStamp()
    {
        var game = Temp();
        var dir = ScreenshotFolder.Directory(game);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "2026-09-20_10.00.00.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir, "2026-09-24_21.15.03.png"), new byte[] { 1, 2 });
        File.WriteAllBytes(Path.Combine(dir, "2026-09-24_21.15.03_2.png"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(dir, "notes.txt"), new byte[] { 1 });

        var list = ScreenshotFolder.List(game);

        Assert.Equal(3, list.Count);
        Assert.Equal(new DateTime(2026, 9, 24, 21, 15, 3), list[0].TakenAt);
        Assert.Equal("2026-09-20_10.00.00.png", list[2].FileName);
    }
}
