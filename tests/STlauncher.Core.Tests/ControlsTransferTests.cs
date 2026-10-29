using System;
using System.Linq;
using System.Text;
using STlauncher.Core.Boost;
using STlauncher.Core.Launch;

namespace STlauncher.Core.Tests;

/// <summary>Controls from one build's options.txt into another's: the list, the versions, mod keys, the way back.</summary>
public class ControlsTransferTests
{
    private static OptionsFile File(params string[] lines)
        => OptionsFile.Parse(Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n"))))!;

    private static string Text(OptionsFile file) => Encoding.UTF8.GetString(file.ToBytes());

    [Fact]
    public void SafeList_HasControlsSoundChatFovGui_AndNoGraphicsOrPacks()
    {
        foreach (var key in new[] { "key_key.jump", "key_key.xaero_minimap_settings", "mouseSensitivity", "soundCategory_master",
                     "soundCategory_voice", "chatOpacity", "narrator", "fov", "guiScale", "darknessEffectScale", "toggleCrouch" })
        {
            Assert.True(ControlsTransfer.IsSafeKey(key), key);
        }

        foreach (var key in new[] { "renderDistance", "simulationDistance", "graphicsMode", "maxFps", "enableVsync", "fullscreen",
                     "resourcePacks", "incompatibleResourcePacks", "lang", "lastServer", "version", "particles", "ao",
                     "mipmapLevels", "entityShadows", "cloudStatus", "biomeBlendRadius", "gamma", "screenEffectScale",
                     "soundDevice", "overrideWidth", "skipMultiplayerWarning", "modelPart_cape", "tutorialStep" })
        {
            Assert.False(ControlsTransfer.IsSafeKey(key), key);
        }
    }

    [Fact]
    public void Plan_ListsOnlyWhatDiffers_AndLeavesGraphicsAlone()
    {
        var source = File("version:4325", "key_key.jump:key.keyboard.space", "key_key.sprint:key.keyboard.left.control",
            "mouseSensitivity:0.7", "soundCategory_master:0.4", "renderDistance:32", "fov:90", "guiScale:3", "lang:en_us");
        var target = File("version:4325", "key_key.jump:key.keyboard.space", "key_key.sprint:key.keyboard.r",
            "mouseSensitivity:0.5", "soundCategory_master:1.0", "renderDistance:8", "fov:70", "guiScale:0", "lang:ru_ru");

        var plan = ControlsTransfer.Plan(source, "1.21.11", target, "1.21.11");

        Assert.Equal(
            new[] { "key_key.sprint", "mouseSensitivity", "soundCategory_master", "fov", "guiScale" },
            plan.Changes.Select(c => c.Key));
        Assert.Equal(1, plan.Unchanged);
        Assert.Equal("key.keyboard.r", plan.Changes[0].Current);
        Assert.Equal("key.keyboard.left.control", plan.Changes[0].Value);
    }

    [Fact]
    public void Apply_IsBytePreserving_AndRevertPutsEverythingBack()
    {
        var source = File("version:4325", "key_key.sprint:key.keyboard.left.control", "soundCategory_music:0.0", "fov:95");
        var originalTarget = "version:3955\r\nlang:ru_ru\r\nresourcePacks:[\"vanilla\",\"file/Свой пак.zip\"]\r\nkey_key.sprint:key.keyboard.r\r\nsoundCategory_music:1.0\r\nfov:70\r\n";
        var target = OptionsFile.Parse(Encoding.UTF8.GetBytes(originalTarget))!;

        var plan = ControlsTransfer.Plan(source, "1.21.11", target, "1.21.1");
        var records = ControlsTransfer.Apply(target, plan.Changes);

        var written = Text(target);
        Assert.Contains("key_key.sprint:key.keyboard.left.control\r\n", written);
        Assert.Contains("fov:95\r\n", written);
        Assert.Contains("file/Свой пак.zip", written);

        var revert = ControlsTransfer.Revert(target, records);

        Assert.Equal(3, revert.Restored.Count);
        Assert.Equal(originalTarget, Text(target));
    }

    [Fact]
    public void Revert_LeavesWhatThePlayerChangedSince()
    {
        var source = File("version:4325", "fov:95", "guiScale:2");
        var target = File("version:4325", "fov:70", "guiScale:4");

        var records = ControlsTransfer.Apply(target, ControlsTransfer.Plan(source, "1.21.11", target, "1.21.11").Changes);
        target.Set("guiScale", "3");

        var revert = ControlsTransfer.Revert(target, records);

        Assert.Equal(new[] { "fov" }, revert.Restored);
        Assert.Equal(new[] { "guiScale" }, revert.LeftToPlayer);
        Assert.Equal("70", target.Get("fov"));
        Assert.Equal("3", target.Get("guiScale"));
    }

    [Fact]
    public void Apply_Twice_TheWayBackLeadsToBeforeTheFirst()
    {
        var first = File("version:4325", "fov:95");
        var second = File("version:4325", "fov:100");
        var target = File("version:4325", "fov:70");

        var records = ControlsTransfer.Apply(target, ControlsTransfer.Plan(first, "1.21.11", target, "1.21.11").Changes);
        records = ControlsTransfer.Apply(target, ControlsTransfer.Plan(second, "1.21.11", target, "1.21.11").Changes, records);

        Assert.Equal("70", Assert.Single(records).Previous);
        ControlsTransfer.Revert(target, records);
        Assert.Equal("70", target.Get("fov"));
    }

    [Fact]
    public void ModKeys_OnlyWhenTheTargetHasThem()
    {
        var source = File("version:4325", "key_key.xaero_toggle_minimap:key.keyboard.z", "key_iris.keybind.reload:key.keyboard.r",
            "key_key.jump:key.keyboard.space");
        var target = File("version:4325", "key_key.xaero_toggle_minimap:key.keyboard.y", "key_key.jump:key.keyboard.j");

        var plan = ControlsTransfer.Plan(source, "1.21.11", target, "1.21.11");

        Assert.Equal(new[] { "key_key.xaero_toggle_minimap", "key_key.jump" }, plan.Changes.Select(c => c.Key));
        Assert.Equal(ControlsGroup.ModKeys, plan.Changes[0].Group);
        Assert.Equal(new[] { "key_iris.keybind.reload" }, plan.SkippedModKeys);
    }

    [Fact]
    public void NeverStartedTarget_TakesEverything_ModKeysIncluded()
    {
        var source = File("version:4325", "key_iris.keybind.reload:key.keyboard.k", "soundCategory_voice:0.3", "renderDistance:20");

        // What the launcher writes before a first start: no "version" line from the game.
        var target = File("lang:ru_ru", "narrator:0", "onboardAccessibility:false");

        var plan = ControlsTransfer.Plan(source, "1.21.11", target, "1.21.11");

        Assert.Equal(new[] { "key_iris.keybind.reload", "soundCategory_voice" }, plan.Changes.Select(c => c.Key));
        Assert.All(plan.Changes, c => Assert.Null(c.Current));
        Assert.Empty(plan.SkippedModKeys);
    }

    [Fact]
    public void StartedTarget_DoesNotGainOptionsItsGameDoesNotKnow()
    {
        // 1.16 knows nothing of the darkness effect or social interactions' newer friends.
        var source = File("version:4325", "darknessEffectScale:0.5", "key_key.quickActions:key.keyboard.g", "chatOpacity:0.5");
        var target = File("version:2586", "chatOpacity:1.0");

        var plan = ControlsTransfer.Plan(source, "1.21.11", target, "1.16.5");

        Assert.Equal(new[] { "chatOpacity" }, plan.Changes.Select(c => c.Key));
        Assert.Empty(plan.SkippedModKeys);
    }

    [Fact]
    public void KeyBindings_AreNotCarriedAcross113()
    {
        var modern = File("version:4325", "key_key.jump:key.keyboard.space", "mouseSensitivity:0.6");
        var legacy = File("version:1343", "key_key.jump:57", "mouseSensitivity:0.5");

        var down = ControlsTransfer.Plan(modern, "1.21.11", legacy, "1.12.2");
        var up = ControlsTransfer.Plan(legacy, "1.12.2", File("version:4325", "key_key.jump:key.keyboard.j", "mouseSensitivity:0.6"), "1.21.11");

        Assert.True(down.KeysSkippedForVersion);
        Assert.Equal(new[] { "mouseSensitivity" }, down.Changes.Select(c => c.Key));
        Assert.True(up.KeysSkippedForVersion);
        Assert.Equal(new[] { "mouseSensitivity" }, up.Changes.Select(c => c.Key));
    }

    [Fact]
    public void RenamedKey_FollowsTheTargetsSpelling()
    {
        var old = File("version:2230", "key_key.swapHands:key.keyboard.x");
        var modern = File("version:4325", "key_key.swapOffhand:key.keyboard.f");

        var plan = ControlsTransfer.Plan(old, "1.15.2", modern, "1.21.11");
        var change = Assert.Single(plan.Changes);

        Assert.Equal("key_key.swapOffhand", change.Key);
        Assert.Equal("key_key.swapHands", change.SourceKey);
        Assert.Equal("key.keyboard.x", change.Value);

        var back = ControlsTransfer.Plan(modern, "1.21.11", File("version:2230", "key_key.swapHands:key.keyboard.x"), "1.15.2");
        Assert.Equal("key_key.swapHands", Assert.Single(back.Changes).Key);

        // A target never started: the spelling comes from its version.
        var fresh = ControlsTransfer.Plan(old, "1.15.2", File("lang:ru_ru"), "1.21.11");
        Assert.Equal("key_key.swapOffhand", Assert.Single(fresh.Changes).Key);
    }

    [Fact]
    public void Fov_IsConvertedBetweenTheFractionAndDegrees()
    {
        var oldFile = File("version:2586", "fov:0.5");
        var newFile = File("version:4325", "fov:70");

        var up = Assert.Single(ControlsTransfer.Plan(oldFile, "1.16.5", newFile, "1.21.11").Changes);
        Assert.Equal("90", up.Value);

        var down = Assert.Single(ControlsTransfer.Plan(File("version:4325", "fov:50"), "1.21.11", File("version:2586", "fov:0.0"), "1.16.5").Changes);
        Assert.Equal("-0.5", down.Value);
    }

    [Fact]
    public void MainHand_TakesTheTargetsQuoting()
    {
        var plan = ControlsTransfer.Plan(File("version:2586", "mainHand:left"), "1.16.5", File("version:4325", "mainHand:\"right\""), "1.21.11");
        Assert.Equal("\"left\"", Assert.Single(plan.Changes).Value);

        var back = ControlsTransfer.Plan(File("version:4325", "mainHand:\"left\""), "1.21.11", File("version:2586", "mainHand:right"), "1.16.5");
        Assert.Equal("left", Assert.Single(back.Changes).Value);
    }

    [Fact]
    public void VanillaAndModKeys_AreToldApart()
    {
        Assert.Equal(ControlsGroup.Keys, ControlsTransfer.GroupOf("key_key.hotbar.3"));
        Assert.Equal(ControlsGroup.Keys, ControlsTransfer.GroupOf("key_key.socialInteractions"));
        Assert.Equal(ControlsGroup.ModKeys, ControlsTransfer.GroupOf("key_key.emotecraft.fastchoose"));
        Assert.Equal(ControlsGroup.Sound, ControlsTransfer.GroupOf("soundCategory_record"));
        Assert.Equal(ControlsGroup.View, ControlsTransfer.GroupOf("guiScale"));
    }
}
