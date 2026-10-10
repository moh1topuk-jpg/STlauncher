using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using STlauncher.Core.Nbt;
using STlauncher.Core.Worlds;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// Worlds are the one thing in a build that cannot be downloaded again. These tests hold
/// the promises the worlds tab makes: nothing is overwritten, nothing is hard-deleted on
/// a click, a foreign zip cannot write outside its folder, and a rename changes one tag.
/// </summary>
public class WorldManagerTests
{
    /// <summary>The shapes level.dat has had: where the seed lives and what is recorded at all.</summary>
    public enum Shape
    {
        /// <summary>1.16 and later: the seed inside WorldGenSettings.</summary>
        Modern,

        /// <summary>1.9 to 1.15: RandomSeed beside the other settings.</summary>
        Legacy,

        /// <summary>Before 1.9: no Version block, no Difficulty.</summary>
        Ancient
    }

    private static string NewGameDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"), "game");
        Directory.CreateDirectory(Path.Combine(root, WorldManager.SavesFolder));
        return root;
    }

    private static NbtCompound LevelRoot(Shape shape, string name, long seed, long lastPlayed)
    {
        var data = new NbtCompound()
            .Set("RandomSeedPlaceholderBefore", new NbtByte(1))
            .Set("LevelName", new NbtString(name))
            .Set("GameType", new NbtInt(shape == Shape.Modern ? 1 : 0))
            .Set("hardcore", new NbtByte(shape == Shape.Legacy ? (sbyte)1 : (sbyte)0))
            .Set("LastPlayed", new NbtLong(lastPlayed));

        // Things the launcher does not model and must hand back untouched.
        data.Set("ServerBrands", new NbtList(NbtTagType.String));
        data.Set("ScheduledEvents", new NbtList(NbtTagType.End));
        data.Set("Player", new NbtCompound().Set("UUID", new NbtIntArray(new[] { 1, 2, 3, 4 })));

        switch (shape)
        {
            case Shape.Modern:
                data.Set("allowCommands", new NbtByte(1));
                data.Set("Difficulty", new NbtByte(3));
                data.Set("Version", new NbtCompound().Set("Id", new NbtInt(4189)).Set("Name", new NbtString("1.21.4")));
                data.Set("WorldGenSettings", new NbtCompound().Set("seed", new NbtLong(seed)).Set("bonus_chest", new NbtByte(0)));
                break;
            case Shape.Legacy:
                data.Set("allowCommands", new NbtByte(0));
                data.Set("Difficulty", new NbtByte(2));
                data.Set("Version", new NbtCompound().Set("Id", new NbtInt(1343)).Set("Name", new NbtString("1.12.2")));
                data.Set("RandomSeed", new NbtLong(seed));
                break;
            default:
                data.Set("RandomSeed", new NbtLong(seed));
                break;
        }

        return new NbtCompound().Set("Data", data);
    }

    private static byte[] Plain(NbtCompound root)
    {
        using var stream = new MemoryStream();
        NbtWriter.Write(stream, root);
        return stream.ToArray();
    }

    private static string CreateWorld(
        string gameDirectory,
        string folder,
        Shape shape = Shape.Modern,
        string? name = null,
        long seed = -4172144997902289642L,
        long lastPlayed = 1_760_000_000_000L)
    {
        var directory = Path.Combine(gameDirectory, WorldManager.SavesFolder, folder);
        Directory.CreateDirectory(Path.Combine(directory, "region"));

        NbtFile.Write(
            Path.Combine(directory, WorldInfo.LevelFileName),
            new NbtDocument(LevelRoot(shape, name ?? folder, seed, lastPlayed), string.Empty, Gzipped: true));

        File.WriteAllBytes(Path.Combine(directory, "region", "r.0.0.mca"), Enumerable.Repeat((byte)7, 5000).ToArray());
        File.WriteAllBytes(Path.Combine(directory, WorldInfo.SessionLockFileName), new byte[] { 0xE2, 0x98, 0x83 });
        File.WriteAllBytes(Path.Combine(directory, WorldInfo.IconFileName), new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        return directory;
    }

    private static byte[] Unpacked(string levelPath)
    {
        using var file = File.OpenRead(levelPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static List<string> FilesUnder(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).ToList()
            : new List<string>();

    // ===================== Reading =====================

    [Theory]
    [InlineData(Shape.Modern, "1.21.4", WorldGameMode.Creative, false, true, WorldDifficulty.Hard)]
    [InlineData(Shape.Legacy, "1.12.2", WorldGameMode.Survival, true, false, WorldDifficulty.Normal)]
    public void Read_UnderstandsEachVersionsShape(
        Shape shape, string version, WorldGameMode mode, bool hardcore, bool cheats, WorldDifficulty difficulty)
    {
        var game = NewGameDirectory();
        var directory = CreateWorld(game, "World", shape, name: "Моя база", seed: 123456789012345L);

        var world = WorldInfo.Read(directory);

        Assert.True(world.IsReadable);
        Assert.Equal("Моя база", world.Name);
        Assert.Equal("World", world.FolderName);
        Assert.Equal(version, world.GameVersion);
        Assert.Equal(mode, world.GameMode);
        Assert.Equal(hardcore, world.IsHardcore);
        Assert.Equal(cheats, world.AllowCheats);
        Assert.Equal(difficulty, world.Difficulty);
        Assert.Equal(123456789012345L, world.Seed);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000L), world.LastPlayed);
        Assert.Equal(Path.Combine(directory, "icon.png"), world.IconPath);
    }

    [Fact]
    public void Read_AnAncientWorld_LeavesUnknownThingsUnknown()
    {
        var game = NewGameDirectory();
        var world = WorldInfo.Read(CreateWorld(game, "Old", Shape.Ancient, seed: 42));

        Assert.True(world.IsReadable);
        Assert.Null(world.GameVersion);
        Assert.Null(world.Difficulty);
        Assert.Null(world.AllowCheats);
        Assert.Equal(42, world.Seed);
    }

    [Fact]
    public void List_KeepsAWorldWhoseLevelDatIsDamaged_AndSkipsFoldersThatAreNotWorlds()
    {
        var game = NewGameDirectory();
        var saves = WorldManager.SavesDirectory(game);

        CreateWorld(game, "Older", lastPlayed: 1_700_000_000_000L);
        CreateWorld(game, "Newer", lastPlayed: 1_760_000_000_000L);

        Directory.CreateDirectory(Path.Combine(saves, "Broken"));
        File.WriteAllText(Path.Combine(saves, "Broken", "level.dat"), "this is not nbt");
        File.WriteAllBytes(Path.Combine(saves, "Broken", "big.bin"), new byte[1000]);

        Directory.CreateDirectory(Path.Combine(saves, "JustAFolder"));
        File.WriteAllText(Path.Combine(saves, "notes.txt"), "x");

        var worlds = new WorldManager().List(game);

        Assert.Equal(3, worlds.Count);
        Assert.Equal(new[] { "Newer", "Older" }, worlds.Where(w => w.IsReadable).Select(w => w.FolderName).ToArray());

        var broken = Assert.Single(worlds, w => !w.IsReadable);
        Assert.Equal("Broken", broken.Name);
        Assert.Null(broken.Seed);
        Assert.NotNull(broken.LastPlayed);

        Assert.True(WorldManager.MeasureSize(broken.Directory) >= 1000);
    }

    [Fact]
    public void List_OfABuildThatNeverRan_IsEmpty()
    {
        var game = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));

        Assert.Empty(new WorldManager().List(game));
        Assert.Empty(new WorldManager().ListTrash(game));
    }

    // ===================== Rename =====================

    [Theory]
    [InlineData(Shape.Modern)]
    [InlineData(Shape.Legacy)]
    [InlineData(Shape.Ancient)]
    public void Rename_ChangesLevelNameAndNothingElse(Shape shape)
    {
        var game = NewGameDirectory();
        var directory = CreateWorld(game, "World", shape, name: "Before");
        var levelPath = Path.Combine(directory, "level.dat");
        var manager = new WorldManager();

        var renamed = manager.Rename(game, WorldInfo.Read(directory), "  После \U0001F30D  ");

        Assert.Equal("После \U0001F30D", renamed.Name);
        Assert.Equal("World", renamed.FolderName);
        Assert.Equal(directory, renamed.Directory);
        Assert.True(Directory.Exists(directory));

        // Still gzipped, and identical to the original with that one string swapped.
        var raw = File.ReadAllBytes(levelPath);
        Assert.True(raw[0] == 0x1f && raw[1] == 0x8b);

        var expected = LevelRoot(shape, "После \U0001F30D", -4172144997902289642L, 1_760_000_000_000L);
        Assert.Equal(Plain(expected), Unpacked(levelPath));

        // And back again is the file we started from.
        manager.Rename(game, renamed, "Before");
        Assert.Equal(Plain(LevelRoot(shape, "Before", -4172144997902289642L, 1_760_000_000_000L)), Unpacked(levelPath));

        Assert.False(File.Exists(levelPath + ".tmp"));
    }

    [Fact]
    public void Rename_RefusesAnEmptyName_AndAnUnreadableWorld()
    {
        var game = NewGameDirectory();
        var directory = CreateWorld(game, "World");
        var manager = new WorldManager();

        Assert.Throws<ArgumentException>(() => manager.Rename(game, WorldInfo.Read(directory), "   "));

        var levelPath = Path.Combine(directory, "level.dat");
        File.WriteAllText(levelPath, "garbage");

        Assert.ThrowsAny<Exception>(() => manager.Rename(game, WorldInfo.Read(directory), "Name"));
        Assert.Equal("garbage", File.ReadAllText(levelPath));
    }

    [Fact]
    public void NothingWritesIntoSaves_WhileTheGameIsRunning()
    {
        var game = NewGameDirectory();
        var directory = CreateWorld(game, "World", name: "Before");
        var before = File.ReadAllBytes(Path.Combine(directory, "level.dat"));
        var zip = Path.Combine(Path.GetDirectoryName(game)!, "world.zip");

        var idle = new WorldManager();
        idle.Export(WorldInfo.Read(directory), zip);

        var asked = new List<string>();
        var manager = new WorldManager(gameDirectory =>
        {
            asked.Add(gameDirectory);
            return true;
        });

        var world = WorldInfo.Read(directory);

        Assert.Equal(WorldBusyReason.GameRunning, Assert.Throws<WorldBusyException>(() => manager.Rename(game, world, "After")).Reason);
        Assert.Throws<WorldBusyException>(() => manager.Duplicate(game, world, "Copy"));
        Assert.Throws<WorldBusyException>(() => manager.MoveToTrash(game, world));
        Assert.Throws<WorldBusyException>(() => manager.Import(game, zip));

        Assert.All(asked, a => Assert.Equal(game, a));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "level.dat")));
        Assert.Equal(new[] { "World" }, Directory.GetDirectories(WorldManager.SavesDirectory(game)).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void AWorldTheGameHoldsOpen_IsLeftAlone()
    {
        // The byte-range lock is how the game marks a world as open. On Linux such a lock never
        // conflicts inside one process, so only another process (the game) shows there; macOS
        // has no such call in .NET.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var game = NewGameDirectory();
        var directory = CreateWorld(game, "World");
        var world = WorldInfo.Read(directory);
        var manager = new WorldManager();
        var zip = Path.Combine(Path.GetDirectoryName(game)!, "out.zip");

        Assert.False(WorldManager.IsSessionLocked(directory));

        using (var held = new FileStream(
                   Path.Combine(directory, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
#pragma warning disable CA1416
            held.Lock(0, held.Length);
#pragma warning restore CA1416

            Assert.True(WorldManager.IsSessionLocked(directory));
            Assert.Equal(WorldBusyReason.WorldOpen, Assert.Throws<WorldBusyException>(() => manager.Rename(game, world, "X")).Reason);
            Assert.Throws<WorldBusyException>(() => manager.MoveToTrash(game, world));
            Assert.Throws<WorldBusyException>(() => manager.Duplicate(game, world, "X"));
            Assert.Throws<WorldBusyException>(() => manager.Export(world, zip));
            Assert.Throws<WorldBusyException>(() => manager.Backup(world, Path.Combine(Path.GetDirectoryName(game)!, "backups")));
        }

        Assert.False(File.Exists(zip));
        Assert.False(WorldManager.IsSessionLocked(directory));
        manager.Rename(game, world, "X");
    }

    // ===================== Duplicate =====================

    [Fact]
    public void Duplicate_IsAFullCopyWithoutTheLock_AndNeverLandsOnAnExistingFolder()
    {
        var game = NewGameDirectory();
        var directory = CreateWorld(game, "World", name: "База");
        var manager = new WorldManager();
        var original = WorldInfo.Read(directory);

        var first = manager.Duplicate(game, original, "База (копия)");
        var second = manager.Duplicate(game, original, "База (копия)");

        Assert.Equal("База (копия)", first.Name);
        Assert.Equal("База (копия)", first.FolderName);
        Assert.Equal("База (копия) (2)", second.FolderName);
        Assert.Equal(original.Seed, first.Seed);

        Assert.True(File.Exists(Path.Combine(first.Directory, "region", "r.0.0.mca")));
        Assert.True(File.Exists(Path.Combine(first.Directory, "icon.png")));
        Assert.False(File.Exists(Path.Combine(first.Directory, "session.lock")));

        // The original is untouched, name included.
        Assert.Equal("База", WorldInfo.Read(directory).Name);
        Assert.True(File.Exists(Path.Combine(directory, "session.lock")));
        Assert.Equal(3, manager.List(game).Count);
    }

    [Fact]
    public void Duplicate_DoesNotFollowALinkInsideTheWorld()
    {
        var game = NewGameDirectory();
        var directory = CreateWorld(game, "World");
        var outside = Path.Combine(Path.GetDirectoryName(game)!, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.txt"), "not part of the world");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(directory, "linked"), outside);
        }
        catch (Exception)
        {
            // Creating links needs a privilege on Windows; without it there is nothing to test.
            return;
        }

        var manager = new WorldManager();
        var copy = manager.Duplicate(game, WorldInfo.Read(directory), "Copy");

        Assert.False(Directory.Exists(Path.Combine(copy.Directory, "linked")));
        Assert.Equal(5000 + 4 + 3, WorldManager.MeasureSize(directory) - new FileInfo(Path.Combine(directory, "level.dat")).Length);
    }

    // ===================== Trash =====================

    [Fact]
    public void Delete_MovesTheWorldToTheTrash_AndItComesBackWhole()
    {
        var game = NewGameDirectory();
        var directory = CreateWorld(game, "World", name: "База");
        var manager = new WorldManager();

        var trashed = manager.MoveToTrash(game, WorldInfo.Read(directory));

        Assert.False(Directory.Exists(directory));
        Assert.Empty(manager.List(game));
        Assert.StartsWith(WorldManager.TrashDirectory(game), trashed.Directory);
        Assert.Matches(@"^World-\d{8}-\d{6}$", Path.GetFileName(trashed.Directory));
        Assert.True(File.Exists(Path.Combine(trashed.Directory, "region", "r.0.0.mca")));

        var listed = Assert.Single(manager.ListTrash(game));
        Assert.Equal("World", listed.FolderName);
        Assert.Equal("База", listed.Name);
        Assert.Equal(trashed.DeletedAt, listed.DeletedAt);

        var restored = manager.RestoreFromTrash(game, listed);

        Assert.Equal(directory, restored.Directory);
        Assert.Equal("База", restored.Name);
        Assert.True(File.Exists(Path.Combine(directory, "region", "r.0.0.mca")));
        Assert.Empty(manager.ListTrash(game));
    }

    [Fact]
    public void RestoreFromTrash_DoesNotOverwriteAWorldThatTookTheName()
    {
        var game = NewGameDirectory();
        var manager = new WorldManager();

        var trashed = manager.MoveToTrash(game, WorldInfo.Read(CreateWorld(game, "World", name: "Old one")));
        var replacement = CreateWorld(game, "World", name: "New one");

        var restored = manager.RestoreFromTrash(game, trashed);

        Assert.Equal("World (2)", restored.FolderName);
        Assert.Equal("Old one", restored.Name);
        Assert.Equal("New one", WorldInfo.Read(replacement).Name);
    }

    [Fact]
    public void DeletingTwiceInOneSecond_KeepsBoth()
    {
        var game = NewGameDirectory();
        var manager = new WorldManager();

        manager.MoveToTrash(game, WorldInfo.Read(CreateWorld(game, "World", name: "First")));
        manager.MoveToTrash(game, WorldInfo.Read(CreateWorld(game, "World", name: "Second")));

        Assert.Equal(new[] { "First", "Second" }, manager.ListTrash(game).Select(t => t.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void TheTrashKeepsAWorldHoweverOldItIs()
    {
        var game = NewGameDirectory();
        var manager = new WorldManager();
        var trash = WorldManager.TrashDirectory(game);
        Directory.CreateDirectory(trash);

        // Deleted three years ago, by the name the trash gave it then.
        var oldStamp = DateTime.Now.AddDays(-1100).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var old = Path.Combine(trash, "Old-" + oldStamp);
        Directory.Move(CreateWorld(game, "Old", name: "Старый"), old);

        // Everything the Worlds tab does when it opens, and a few of the things a player does there.
        manager.List(game);
        manager.ListTrash(game);
        manager.MoveToTrash(game, WorldInfo.Read(CreateWorld(game, "Fresh")));
        manager.RestoreFromTrash(game, manager.ListTrash(game).Single(t => t.FolderName == "Fresh"));
        manager.ListTrash(game);

        Assert.True(File.Exists(Path.Combine(old, "region", "r.0.0.mca")));

        var listed = Assert.Single(manager.ListTrash(game));
        Assert.Equal("Старый", listed.Name);
        Assert.True(DateTimeOffset.Now - listed.DeletedAt > TimeSpan.FromDays(1000));

        // And it still comes back whole.
        Assert.Equal("Старый", manager.RestoreFromTrash(game, listed).Name);
    }

    [Fact]
    public void DeleteFromTrash_RemovesThatWorldAndNoOther()
    {
        var game = NewGameDirectory();
        var manager = new WorldManager();

        var kept = manager.MoveToTrash(game, WorldInfo.Read(CreateWorld(game, "Kept")));
        var doomed = manager.MoveToTrash(game, WorldInfo.Read(CreateWorld(game, "Doomed")));
        var playing = CreateWorld(game, "Playing");

        // A world unpacked from some archives carries read-only files; "for good" still means it.
        File.SetAttributes(Path.Combine(doomed.Directory, "level.dat"), FileAttributes.ReadOnly);

        Assert.True(manager.DeleteFromTrash(game, doomed));

        Assert.False(Directory.Exists(doomed.Directory));
        Assert.True(Directory.Exists(kept.Directory));
        Assert.True(Directory.Exists(playing));
        Assert.Equal("Kept", Assert.Single(manager.ListTrash(game)).FolderName);

        // Asked twice - a second click, a list gone stale - it is simply already gone.
        Assert.False(manager.DeleteFromTrash(game, doomed));
    }

    [Fact]
    public void DeleteFromTrash_RefusesAnythingThatIsNotAWorldInThisBuildsTrash()
    {
        var game = NewGameDirectory();
        var manager = new WorldManager();
        var world = CreateWorld(game, "World");
        var stamp = DateTime.Now;

        var trash = WorldManager.TrashDirectory(game);
        var foreign = Path.Combine(trash, "something else");
        Directory.CreateDirectory(foreign);

        var otherGame = NewGameDirectory();
        var elsewhere = new WorldManager().MoveToTrash(otherGame, WorldInfo.Read(CreateWorld(otherGame, "Theirs")));

        foreach (var target in new[]
                 {
                     world,                                       // a world still being played
                     WorldManager.SavesDirectory(game),           // the saves folder itself
                     game,
                     trash,                                       // the trash as a whole
                     foreign,                                     // in the trash, but not put there by the launcher
                     elsewhere.Directory,                         // another build's trash
                     Path.Combine(trash, "..", "..", "..", "saves", "World")
                 })
        {
            Assert.Throws<ArgumentException>(() => manager.DeleteFromTrash(game, new TrashedWorld(target, "World", "World", stamp)));
        }

        Assert.True(File.Exists(Path.Combine(world, "level.dat")));
        Assert.True(Directory.Exists(foreign));
        Assert.True(Directory.Exists(elsewhere.Directory));
    }

    [Fact]
    public void EmptyTrash_DeletesTheWorldsItWasGiven_AndLeavesWhatCameLater()
    {
        var game = NewGameDirectory();
        var manager = new WorldManager();
        var trash = WorldManager.TrashDirectory(game);

        manager.MoveToTrash(game, WorldInfo.Read(CreateWorld(game, "One")));
        manager.MoveToTrash(game, WorldInfo.Read(CreateWorld(game, "Two")));

        // What the player was shown, and asked about.
        var shown = manager.ListTrash(game);

        var later = manager.MoveToTrash(game, WorldInfo.Read(CreateWorld(game, "Later")));
        var foreign = Path.Combine(trash, "something else");
        Directory.CreateDirectory(foreign);
        var playing = CreateWorld(game, "Playing");

        var result = manager.EmptyTrash(game, shown);

        Assert.Equal(2, result.Removed);
        Assert.Empty(result.Failed);
        Assert.Equal("Later", Assert.Single(manager.ListTrash(game)).FolderName);
        Assert.True(Directory.Exists(later.Directory));
        Assert.True(Directory.Exists(foreign));
        Assert.True(Directory.Exists(playing));
    }

    [Fact]
    public void EmptyTrash_DoesNotReachThroughALinkInsideATrashedWorld()
    {
        var game = NewGameDirectory();
        var manager = new WorldManager();
        var world = CreateWorld(game, "World");

        var outside = Path.Combine(Path.GetDirectoryName(game)!, "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "not part of the world");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(world, "linked"), outside);
        }
        catch (Exception)
        {
            // Making a link needs a privilege this machine does not grant.
            return;
        }

        manager.MoveToTrash(game, WorldInfo.Read(world));
        var result = manager.EmptyTrash(game, manager.ListTrash(game));

        Assert.Equal(1, result.Removed);
        Assert.Empty(manager.ListTrash(game));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    // ===================== Export and import =====================

    [Fact]
    public void Export_ThenImport_GivesTheSameWorldUnderAFreeName()
    {
        var game = NewGameDirectory();
        var directory = CreateWorld(game, "World", name: "База");
        var manager = new WorldManager();
        var zip = Path.Combine(Path.GetDirectoryName(game)!, "exported.zip");

        manager.Export(WorldInfo.Read(directory), zip);

        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "World/icon.png", "World/level.dat", "World/region/r.0.0.mca" }, names);
        }

        Assert.False(File.Exists(zip + ".tmp"));

        var imported = manager.Import(game, zip);

        Assert.Equal("World (2)", imported.FolderName);
        Assert.Equal("База", imported.Name);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(directory, "region", "r.0.0.mca")),
            File.ReadAllBytes(Path.Combine(imported.Directory, "region", "r.0.0.mca")));
        Assert.Equal(2, manager.List(game).Count);
    }

    private static string MakeZip(string name, Action<ZipArchive> fill)
    {
        var directory = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        fill(archive);
        return path;
    }

    private static void Add(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(content);
    }

    private static byte[] LevelBytes(string name)
    {
        using var buffer = new MemoryStream();

        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            NbtWriter.Write(gzip, LevelRoot(Shape.Modern, name, 1, 1_760_000_000_000L));
        }

        return buffer.ToArray();
    }

    [Fact]
    public void Import_TakesLevelDatAtTheTop_NamingTheWorldAfterTheZip()
    {
        var game = NewGameDirectory();
        var zip = MakeZip("Sky Islands.zip", archive =>
        {
            Add(archive, "level.dat", LevelBytes("Острова"));
            Add(archive, "region/r.0.0.mca", new byte[100]);
            Add(archive, "session.lock", new byte[3]);
        });

        var world = new WorldManager().Import(game, zip);

        Assert.Equal("Sky Islands", world.FolderName);
        Assert.Equal("Острова", world.Name);
        Assert.True(File.Exists(Path.Combine(world.Directory, "region", "r.0.0.mca")));
        Assert.False(File.Exists(Path.Combine(world.Directory, "session.lock")));
    }

    [Fact]
    public void Import_TakesAWorldInsideOneFolder_AndLeavesTheRestOfTheZipAlone()
    {
        var game = NewGameDirectory();
        var zip = MakeZip("download.zip", archive =>
        {
            archive.CreateEntry("My Map/");
            Add(archive, "My Map\\level.dat", LevelBytes("Карта"));
            Add(archive, "My Map/data/raids.dat", new byte[10]);
            Add(archive, "__MACOSX/._My Map", new byte[10]);
            Add(archive, "readme.txt", new byte[10]);
        });

        var world = new WorldManager().Import(game, zip);

        Assert.Equal("My Map", world.FolderName);
        Assert.Equal("Карта", world.Name);
        Assert.True(File.Exists(Path.Combine(world.Directory, "data", "raids.dat")));
        Assert.Equal(new[] { "My Map" }, Directory.GetFileSystemEntries(WorldManager.SavesDirectory(game)).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Import_NeverOverwrites_AnExistingWorldOfThatName()
    {
        var game = NewGameDirectory();
        var existing = CreateWorld(game, "My Map", name: "Mine");
        var before = File.ReadAllBytes(Path.Combine(existing, "level.dat"));

        var zip = MakeZip("x.zip", archive => Add(archive, "My Map/level.dat", LevelBytes("Theirs")));
        var manager = new WorldManager();

        var first = manager.Import(game, zip);
        var second = manager.Import(game, zip);

        Assert.Equal("My Map (2)", first.FolderName);
        Assert.Equal("My Map (3)", second.FolderName);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(existing, "level.dat")));
        Assert.Equal("Mine", WorldInfo.Read(existing).Name);
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("World/../../escaped.txt")]
    [InlineData("World/region/../../../escaped.txt")]
    [InlineData("/escaped.txt")]
    [InlineData("C:/escaped.txt")]
    [InlineData("..\\escaped.txt")]
    public void Import_RefusesAZipThatClimbsOutOfItsFolder(string evil)
    {
        var game = NewGameDirectory();
        var parent = Path.GetDirectoryName(game)!;
        CreateWorld(game, "Existing");
        var before = FilesUnder(parent);

        var zip = MakeZip("evil.zip", archive =>
        {
            Add(archive, "World/level.dat", LevelBytes("Evil"));
            Add(archive, evil, Encoding.ASCII.GetBytes("gotcha"));
        });

        var error = Assert.Throws<WorldArchiveException>(() => new WorldManager().Import(game, zip));

        Assert.Equal(WorldArchiveProblem.UnsafePath, error.Problem);
        Assert.Equal(before, FilesUnder(parent));
        Assert.False(File.Exists(Path.Combine(parent, "escaped.txt")));
    }

    [Fact]
    public void Import_RefusesWhatIsNotAWorld_OrIsSeveral()
    {
        var game = NewGameDirectory();
        var manager = new WorldManager();

        var none = MakeZip("mods.zip", archive => Add(archive, "mods/sodium.jar", new byte[10]));
        var deep = MakeZip("deep.zip", archive => Add(archive, "a/b/level.dat", LevelBytes("Deep")));
        var two = MakeZip("two.zip", archive =>
        {
            Add(archive, "One/level.dat", LevelBytes("One"));
            Add(archive, "Two/level.dat", LevelBytes("Two"));
        });

        foreach (var zip in new[] { none, deep, two })
        {
            Assert.Equal(WorldArchiveProblem.NotAWorld, Assert.Throws<WorldArchiveException>(() => manager.Import(game, zip)).Problem);
        }

        Assert.Empty(FilesUnder(WorldManager.SavesDirectory(game)));
    }

    [Fact]
    public void Import_HoldsTheTotalUnderTheCeiling()
    {
        var game = NewGameDirectory();
        CreateWorld(game, "Existing");
        var before = FilesUnder(WorldManager.SavesDirectory(game));

        // Compresses to almost nothing; declares, honestly, eight megabytes.
        var zip = MakeZip("bomb.zip", archive =>
        {
            Add(archive, "World/level.dat", LevelBytes("Bomb"));

            for (var i = 0; i < 8; i++)
            {
                Add(archive, $"World/region/r.{i}.0.mca", new byte[1024 * 1024]);
            }
        });

        Assert.True(new FileInfo(zip).Length < 200 * 1024);

        var error = Assert.Throws<WorldArchiveException>(
            () => new WorldManager().Import(game, zip, maxBytes: 2 * 1024 * 1024));

        Assert.Equal(WorldArchiveProblem.TooLarge, error.Problem);
        Assert.Equal(before, FilesUnder(WorldManager.SavesDirectory(game)));

        // The same archive is fine when the ceiling allows it.
        Assert.Equal("World", new WorldManager().Import(game, zip, maxBytes: 16 * 1024 * 1024).FolderName);
    }

    [Fact]
    public void Import_DoesNotTrustTheSizeAnEntryDeclares()
    {
        var game = NewGameDirectory();
        const int real = 4 * 1024 * 1024;
        const int declared = 1000;

        var zip = MakeZip("liar.zip", archive =>
        {
            Add(archive, "World/level.dat", LevelBytes("Liar"));
            Add(archive, "World/region/big.mca", new byte[real]);
        });

        // Rewrite the uncompressed size of big.mca in both the local header and the
        // central directory, so the archive claims a kilobyte for four megabytes.
        var bytes = File.ReadAllBytes(zip);
        var name = Encoding.ASCII.GetBytes("World/region/big.mca");
        var patched = 0;

        for (var i = 0; i + 46 + name.Length <= bytes.Length; i++)
        {
            var isLocal = bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 3 && bytes[i + 3] == 4;
            var isCentral = bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 1 && bytes[i + 3] == 2;

            if (!isLocal && !isCentral)
            {
                continue;
            }

            var nameAt = i + (isLocal ? 30 : 46);
            var sizeAt = i + (isLocal ? 22 : 24);

            if (bytes.AsSpan(nameAt, name.Length).SequenceEqual(name))
            {
                BitConverter.GetBytes(declared).CopyTo(bytes, sizeAt);
                patched++;
            }
        }

        Assert.Equal(2, patched);
        File.WriteAllBytes(zip, bytes);

        var saves = WorldManager.SavesDirectory(game);

        try
        {
            // Either the entry is cut at what it declared, or the import is refused;
            // what must not happen is four megabytes from an entry that said a kilobyte.
            var world = new WorldManager().Import(game, zip);
            Assert.True(new FileInfo(Path.Combine(world.Directory, "region", "big.mca")).Length <= declared);
        }
        catch (IOException)
        {
            Assert.Empty(FilesUnder(saves));
        }
        catch (InvalidDataException)
        {
            Assert.Empty(FilesUnder(saves));
        }
    }

    [Fact]
    public void AFailedImport_RemovesOnlyWhatItCreated()
    {
        var game = NewGameDirectory();
        var existing = CreateWorld(game, "World", name: "Mine");
        var saves = WorldManager.SavesDirectory(game);
        var before = FilesUnder(saves);

        // The second entry collides with the first once unpacked, so the import fails halfway.
        var zip = MakeZip("broken.zip", archive =>
        {
            Add(archive, "World/level.dat", LevelBytes("Broken"));
            Add(archive, "World/region/r.0.0.mca", new byte[10]);
            Add(archive, "World/region/r.0.0.mca", new byte[10]);
        });

        Assert.ThrowsAny<IOException>(() => new WorldManager().Import(game, zip));

        Assert.Equal(before, FilesUnder(saves));
        Assert.Equal("Mine", WorldInfo.Read(existing).Name);
    }

    // ===================== Backups =====================

    [Fact]
    public void Backup_ThenRestore_MakesANewWorldBesideTheCurrentOne()
    {
        var game = NewGameDirectory();
        var directory = CreateWorld(game, "World", name: "База");
        var backups = WorldManager.BackupsDirectoryFor(Path.Combine(Path.GetDirectoryName(game)!, "backups"), "my-build");
        var manager = new WorldManager();
        var world = WorldInfo.Read(directory);

        var backup = manager.Backup(world, backups);

        Assert.Matches(@"^world-World-\d{8}-\d{6}\.zip$", backup.FileName);
        Assert.Equal("World", backup.FolderName);
        Assert.EndsWith(Path.Combine("backups", "worlds", "my-build"), backups);

        // The world moves on after the backup.
        manager.Rename(game, world, "База сегодня");
        File.WriteAllText(Path.Combine(directory, "new-file.txt"), "built today");

        var listed = Assert.Single(manager.ListBackups(backups, "World"));
        var restored = manager.RestoreBackup(game, listed, "База (из копии)");

        Assert.Equal("World (2)", restored.FolderName);
        Assert.Equal("База (из копии)", restored.Name);
        Assert.False(File.Exists(Path.Combine(restored.Directory, "new-file.txt")));

        // Today's world is exactly as it was left.
        Assert.Equal("База сегодня", WorldInfo.Read(directory).Name);
        Assert.True(File.Exists(Path.Combine(directory, "new-file.txt")));
    }

    [Fact]
    public void Backups_AreCountedPerWorld_EvenWhenNamesShareAPrefix()
    {
        var game = NewGameDirectory();
        var backups = Path.Combine(Path.GetDirectoryName(game)!, "backups");
        var manager = new WorldManager();

        var plain = WorldInfo.Read(CreateWorld(game, "World"));
        var dashed = WorldInfo.Read(CreateWorld(game, "World-2"));

        manager.Backup(plain, backups);
        manager.Backup(plain, backups);
        manager.Backup(dashed, backups);
        File.WriteAllText(Path.Combine(backups, "backup-build-20260101-101010.zip"), "a whole-build backup");

        Assert.Equal(2, manager.ListBackups(backups, "World").Count);
        Assert.Single(manager.ListBackups(backups, "World-2"));
        Assert.Equal(3, manager.ListBackups(backups).Count);
    }

    [Fact]
    public void Prune_KeepsTheNewestOfThatWorld_AndTouchesNoOtherWorlds()
    {
        var game = NewGameDirectory();
        var backups = Path.Combine(Path.GetDirectoryName(game)!, "backups");
        Directory.CreateDirectory(backups);
        var manager = new WorldManager();

        foreach (var stamp in new[] { "20260101-100000", "20260102-100000", "20260103-100000", "20260104-100000" })
        {
            File.WriteAllBytes(Path.Combine(backups, $"world-World-{stamp}.zip"), new byte[100]);
        }

        File.WriteAllBytes(Path.Combine(backups, "world-Other-20250101-100000.zip"), new byte[100]);

        Assert.Equal(2, manager.PruneBackups(backups, "World", maxCount: 2, maxTotalBytes: 0));
        Assert.Equal(
            new[] { "world-World-20260104-100000.zip", "world-World-20260103-100000.zip" },
            manager.ListBackups(backups, "World").Select(b => b.FileName).ToArray());

        // A size limit smaller than one backup still leaves the newest.
        Assert.Equal(1, manager.PruneBackups(backups, "World", maxCount: 0, maxTotalBytes: 10));
        Assert.Equal("world-World-20260104-100000.zip", Assert.Single(manager.ListBackups(backups, "World")).FileName);
        Assert.Single(manager.ListBackups(backups, "Other"));
    }

    [Theory]
    [InlineData("My World", "My World")]
    [InlineData("a/b:c*d", "a_b_c_d")]
    [InlineData("   ", "World")]
    [InlineData("CON", "World CON")]
    [InlineData("trailing. ", "trailing")]
    [InlineData(".hidden", "World hidden")]
    public void FreeFolderName_IsAlwaysSomethingTheDiskAccepts(string wanted, string expected)
    {
        var parent = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);

        var name = WorldManager.FreeFolderName(parent, wanted);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(expected, name);
        }

        Directory.CreateDirectory(Path.Combine(parent, name));
        Assert.Equal(name + " (2)", WorldManager.FreeFolderName(parent, wanted));
    }
}
