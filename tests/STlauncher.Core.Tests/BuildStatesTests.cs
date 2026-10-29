using System;
using System.IO;
using System.Linq;
using STlauncher.Core.Instances;
using STlauncher.Core.Storage;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// "Put the build back as it was": the round trip must give back exactly the mods and
/// settings the game worked on, never touch worlds or options, and never delete.
/// </summary>
public class BuildStatesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stl-states-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private DateTime _now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private bool _running;

    public BuildStatesTests()
    {
        _game = Path.Combine(_root, "build");
        Directory.CreateDirectory(Path.Combine(_game, "mods"));
        Directory.CreateDirectory(Path.Combine(_game, "config"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private BuildStates States(bool canLink = true)
        => new(_game, canLink ? null : (_, _) => false, _ => _running, () => _now);

    private string Write(string relative, string text, int minutesAgo = 60)
    {
        var path = Path.Combine(_game, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // A new file under the old name, as every download and update puts it there: a kept
        // link must not be written through (the launcher never does that to a mod).
        File.Delete(path);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, _now.AddMinutes(-minutesAgo));
        return path;
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_game, relative.Replace('/', Path.DirectorySeparatorChar)));

    private bool Exists(string relative) => File.Exists(Path.Combine(_game, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>A build that worked: sodium, iris 1.6, a config; then the player's changes.</summary>
    private BuildState WorkedThenBroken(BuildStates states)
    {
        Write("mods/sodium.jar", "sodium");
        Write("mods/iris-1.6.jar", "iris 1.6");
        Write("mods/old-friend.jar", "kept then");
        Write("config/sodium-options.json", "{\"fps\":60}");
        Write("saves/World/level.dat", "my world");
        Write("options.txt", "fov:70");
        Write("screenshots/a.png", "png");

        Assert.NotNull(states.BeforeLaunch("def-then"));
        states.AfterLaunch(worked: true);
        var worked = Assert.Single(states.Working());

        _now = _now.AddDays(1);

        // Added, changed and removed since; and the game wrote into the world and options.
        Write("mods/physics-mod.jar", "physics", minutesAgo: 5);
        Write("mods/iris-1.6.jar", "iris 1.7 under the old name", minutesAgo: 5);
        File.Delete(Path.Combine(_game, "mods", "old-friend.jar"));
        Write("config/sodium-options.json", "{\"fps\":240}", minutesAgo: 5);
        Write("config/physics.toml", "gravity=2", minutesAgo: 5);
        Write("saves/World/level.dat", "my world, played more", minutesAgo: 1);
        Write("options.txt", "fov:90", minutesAgo: 1);

        return worked;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Restore_brings_back_the_state_that_worked(bool canLink)
    {
        var states = States(canLink);
        var worked = WorkedThenBroken(states);

        var plan = states.Plan(worked);
        Assert.Contains(plan.Mods, c => c.Kind == BuildChangeKind.Added && c.Path == "mods/physics-mod.jar");
        Assert.Contains(plan.Mods, c => c.Kind == BuildChangeKind.Changed && c.Path == "mods/iris-1.6.jar");
        Assert.Contains(plan.Mods, c => c.Kind == BuildChangeKind.Removed && c.Path == "mods/old-friend.jar");
        Assert.Contains(plan.Config, c => c.Kind == BuildChangeKind.Changed && c.Path == "config/sodium-options.json");
        Assert.Contains(plan.Config, c => c.Kind == BuildChangeKind.Added && c.Path == "config/physics.toml");

        var result = states.Restore(worked, "def-now");

        Assert.Empty(result.Failed);
        Assert.Equal("iris 1.6", Read("mods/iris-1.6.jar"));
        Assert.Equal("kept then", Read("mods/old-friend.jar"));
        Assert.Equal("sodium", Read("mods/sodium.jar"));
        Assert.False(Exists("mods/physics-mod.jar"));
        Assert.Equal("{\"fps\":60}", Read("config/sodium-options.json"));
        Assert.False(Exists("config/physics.toml"));

        // Moved aside, not deleted.
        var aside = Path.Combine(_game, result.AsideFolder.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal("physics", File.ReadAllText(Path.Combine(aside, "mods", "physics-mod.jar")));
        Assert.Equal("iris 1.7 under the old name", File.ReadAllText(Path.Combine(aside, "mods", "iris-1.6.jar")));
        Assert.Equal("gravity=2", File.ReadAllText(Path.Combine(aside, "config", "physics.toml")));

        // The player's own things: exactly as the game left them.
        Assert.Equal("my world, played more", Read("saves/World/level.dat"));
        Assert.Equal("fov:90", Read("options.txt"));
        Assert.Equal("png", Read("screenshots/a.png"));

        Assert.False(states.Plan(worked).Any);
        Assert.Null(states.Offer());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_restore_can_be_undone(bool canLink)
    {
        var states = States(canLink);
        var worked = WorkedThenBroken(states);

        var result = states.Restore(worked, "def-now");
        Assert.NotNull(result.Undo);
        Assert.Equal("def-now", result.Undo!.Definition);

        _now = _now.AddMinutes(10);
        var undo = states.LastUndo();
        Assert.NotNull(undo);
        states.Restore(undo!);

        Assert.Equal("physics", Read("mods/physics-mod.jar"));
        Assert.Equal("iris 1.7 under the old name", Read("mods/iris-1.6.jar"));
        Assert.False(Exists("mods/old-friend.jar"));
        Assert.Equal("{\"fps\":240}", Read("config/sodium-options.json"));
        Assert.Equal("gravity=2", Read("config/physics.toml"));
        Assert.Equal("my world, played more", Read("saves/World/level.dat"));
    }

    [Fact]
    public void Mods_are_kept_as_links_where_the_disk_can_link()
    {
        var states = States();
        var path = Write("mods/sodium.jar", "sodium");

        var state = states.Capture(BuildStateKind.Working);
        var kept = Assert.Single(state.Files);

        Assert.True(kept.Linked);

        if (HardLink.TryGetIdentity(path) is { } identity)
        {
            Assert.Equal(2, identity.Links);
            Assert.Equal(0, states.CostOf(state));
        }
    }

    [Fact]
    public void A_restored_setting_is_a_copy_the_game_cannot_write_through()
    {
        var states = States();
        var worked = WorkedThenBroken(states);
        states.Restore(worked);

        // The game writes into its settings; the kept state must not change with them.
        File.WriteAllText(Path.Combine(_game, "config", "sodium-options.json"), "{\"fps\":1}");
        var again = states.Working().Single();
        var config = again.Files.Single(f => f.Path == "config/sodium-options.json");
        var blob = Path.Combine(_game, ".stlauncher", "states", "blobs", config.Blob);

        Assert.Equal("{\"fps\":60}", File.ReadAllText(blob));
    }

    [Fact]
    public void Restore_is_refused_while_the_game_runs()
    {
        var states = States();
        var worked = WorkedThenBroken(states);
        _running = true;

        var error = Assert.Throws<BuildStateException>(() => states.Restore(worked));

        Assert.Equal(BuildStateError.GameRunning, error.Error);
        Assert.Equal("physics", Read("mods/physics-mod.jar"));
        Assert.Null(states.LastUndo());
    }

    [Fact]
    public void A_failed_launch_keeps_no_state_and_the_offer_points_at_the_last_working_one()
    {
        var states = States();
        var worked = WorkedThenBroken(states);

        Assert.NotNull(states.BeforeLaunch());
        states.AfterLaunch(worked: false);

        Assert.Equal(worked.Id, Assert.Single(states.Working()).Id);
        Assert.DoesNotContain(states.All(), s => s.Kind == BuildStateKind.Pending);
        Assert.Equal(worked.Id, states.Offer()?.Id);
    }

    [Fact]
    public void The_same_mods_again_make_no_new_state()
    {
        var states = States();
        Write("mods/sodium.jar", "sodium");

        states.BeforeLaunch();
        states.AfterLaunch(worked: true);
        _now = _now.AddHours(1);

        Assert.Null(states.BeforeLaunch());
        states.AfterLaunch(worked: true);

        var only = Assert.Single(states.Working());
        Assert.Equal(_now, only.WorkedUtc);
    }

    [Fact]
    public void Only_the_last_three_working_states_are_kept_and_their_files_with_them()
    {
        var states = States();

        for (var i = 0; i < 5; i++)
        {
            Write($"mods/mod{i}.jar", "mod " + i, minutesAgo: 1);
            states.BeforeLaunch();
            states.AfterLaunch(worked: true);
            _now = _now.AddHours(1);
        }

        var kept = states.Working();
        Assert.Equal(3, kept.Count);
        Assert.Equal(new[] { 5, 4, 3 }, kept.Select(s => s.ModCount));

        // Every kept file belongs to a kept state; nothing else stays behind.
        var blobs = Directory.GetFiles(Path.Combine(_game, ".stlauncher", "states", "blobs")).Select(Path.GetFileName).ToHashSet();
        var named = kept.SelectMany(s => s.Files).Select(f => f.Blob).ToHashSet();
        Assert.True(blobs.SetEquals(named));
    }

    [Fact]
    public void Copies_beyond_the_budget_keep_nothing()
    {
        var states = new BuildStates(_game, (_, _) => false, _ => false, () => _now) { CopyBudget = 10 };
        Write("mods/big.jar", new string('x', 100));

        var error = Assert.Throws<BuildStateException>(() => states.Capture(BuildStateKind.Working));

        Assert.Equal(BuildStateError.CannotKeep, error.Error);
        Assert.Empty(states.All());
        Assert.Empty(Directory.GetFiles(Path.Combine(_game, ".stlauncher", "states", "blobs")));
    }
}
