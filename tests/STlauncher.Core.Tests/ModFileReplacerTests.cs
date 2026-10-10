using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using STlauncher.Core.Mods;
using STlauncher.Core.Storage;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// Replacing a mod must never leave the build without it: whatever fails, the folder is
/// what it was before the attempt.
/// </summary>
public class ModFileReplacerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stl-replace-" + Guid.NewGuid().ToString("N"));
    private readonly string _instances;
    private readonly string _game;
    private readonly string _mods;
    private readonly Files _http = new();
    private DateTime _now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    public ModFileReplacerTests()
    {
        _instances = Path.Combine(_root, "instances");
        _game = Path.Combine(_instances, "build");
        _mods = Path.Combine(_game, "mods");
        Directory.CreateDirectory(_mods);
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

    private sealed class Files : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public int Requests { get; private set; }

        public string Serve(string name, byte[] bytes)
        {
            var url = "https://files.test/" + name;
            _files[url] = bytes;
            return url;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;

            return Task.FromResult(_files.TryGetValue(request.RequestUri!.ToString(), out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] Content(int seed, int size = 100_000)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Sha1Of(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private ModFileReplacer Replacer(SharedFileStore? store = null)
        => new(new DownloadClient(new HttpClient(_http), maxAttempts: 1, sharedFiles: store), () => _now);

    private string Put(string name, byte[] bytes)
    {
        var path = Path.Combine(_mods, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private ModReplaceRequest Request(string? oldPath, string newName, byte[] newBytes, string? sha1 = null)
        => new(_game, "mods", oldPath, newName, _http.Serve(newName, newBytes), sha1 ?? Sha1Of(newBytes), newBytes.Length)
        {
            Key = "sodium",
            OldLabel = "0.5.8"
        };

    /// <summary>Every file of the mods folder with what is in it: "exactly as it was" is compared on this.</summary>
    private Dictionary<string, string> Snapshot()
        => Directory.EnumerateFiles(_mods).ToDictionary(p => Path.GetFileName(p)!, p => Sha1Of(File.ReadAllBytes(p)));

    [Fact]
    public async Task The_new_file_takes_the_place_and_the_old_one_is_kept_not_deleted()
    {
        var old = Content(1);
        var fresh = Content(2);
        var oldPath = Put("sodium-0.5.8.jar", old);
        Put("lithium.jar", Content(3));
        var replacer = Replacer();

        var result = await replacer.ReplaceAsync(Request(oldPath, "sodium-0.6.0.jar", fresh));

        Assert.Equal(new[] { "lithium.jar", "sodium-0.6.0.jar" }, Snapshot().Keys.OrderBy(k => k));
        Assert.Equal(fresh, File.ReadAllBytes(result.NewPath));
        Assert.True(result.Enabled);

        var kept = Assert.IsType<ReplacedModFile>(result.Previous);
        Assert.Equal(old, File.ReadAllBytes(kept.StoredPath));
        Assert.Equal(Path.Combine(_game, ".stlauncher", "replaced", "2026-10-10", "sodium-0.5.8.jar"), kept.StoredPath);

        var found = replacer.FindPrevious(_game, "mods", "sodium-0.6.0.jar");
        Assert.NotNull(found);
        Assert.Equal("sodium-0.5.8.jar", found!.FileName);
        Assert.Equal("0.5.8", found.Label);
        Assert.Null(replacer.FindPrevious(_game, "mods", "lithium.jar"));
    }

    [Fact]
    public async Task A_mod_that_was_switched_off_stays_switched_off()
    {
        var oldPath = Put("sodium-0.5.8.jar.disabled", Content(1));
        var replacer = Replacer();

        var result = await replacer.ReplaceAsync(Request(oldPath, "sodium-0.6.0.jar", Content(2)));

        Assert.False(result.Enabled);
        Assert.Equal("sodium-0.6.0.jar.disabled", result.NewFileName);
        Assert.Equal(new[] { "sodium-0.6.0.jar.disabled" }, Snapshot().Keys);

        // Asked by either name, the row finds what it replaced.
        Assert.NotNull(replacer.FindPrevious(_game, "mods", "sodium-0.6.0.jar.disabled"));
    }

    [Fact]
    public async Task A_version_that_keeps_its_file_name_is_swapped_and_the_old_bytes_are_kept()
    {
        var old = Content(1);
        var fresh = Content(2);
        var path = Put("pack.jar", old);

        var result = await Replacer().ReplaceAsync(Request(path, "pack.jar", fresh));

        Assert.Equal(fresh, File.ReadAllBytes(path));
        Assert.Equal(old, File.ReadAllBytes(result.Previous!.StoredPath));
        Assert.Equal(new[] { "pack.jar" }, Snapshot().Keys);
    }

    [Fact]
    public async Task A_download_that_fails_leaves_the_folder_exactly_as_it_was()
    {
        var oldPath = Put("sodium-0.5.8.jar", Content(1));
        var before = Snapshot();
        var replacer = Replacer();

        // Not served at all.
        await Assert.ThrowsAsync<DownloadFailedException>(() => replacer.ReplaceAsync(
            new ModReplaceRequest(_game, "mods", oldPath, "sodium-0.6.0.jar", "https://files.test/missing.jar", null, 0)));

        Assert.Equal(before, Snapshot());
        Assert.False(Directory.Exists(Path.Combine(_game, ".stlauncher")));
    }

    [Fact]
    public async Task A_file_with_the_wrong_digest_never_reaches_the_build()
    {
        var oldPath = Put("sodium-0.5.8.jar", Content(1));
        var before = Snapshot();

        var error = await Assert.ThrowsAsync<DownloadFailedException>(
            () => Replacer().ReplaceAsync(Request(oldPath, "sodium-0.6.0.jar", Content(2), sha1: new string('0', 40))));

        Assert.Equal(NetworkFailureCause.HashMismatch, error.Failure.Cause);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task Nothing_is_downloaded_or_moved_while_the_game_runs()
    {
        var oldPath = Put("sodium-0.5.8.jar", Content(1));
        var before = Snapshot();
        var replacer = Replacer();
        string? asked = null;
        replacer.IsGameRunning = directory => { asked = directory; return true; };

        var error = await Assert.ThrowsAsync<ModReplaceException>(() => replacer.ReplaceAsync(Request(oldPath, "sodium-0.6.0.jar", Content(2))));

        Assert.Equal(ModReplaceStep.GameRunning, error.Step);
        Assert.Equal(_game, asked);
        Assert.Equal(0, _http.Requests);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task A_game_started_during_the_download_stops_the_rename()
    {
        var oldPath = Put("sodium-0.5.8.jar", Content(1));
        var before = Snapshot();
        var replacer = Replacer();
        var running = false;
        replacer.IsGameRunning = _ => running;

        var staged = await replacer.StageAsync(Request(oldPath, "sodium-0.6.0.jar", Content(2)));

        // Downloaded and waiting under a hidden name: the lists do not see it.
        Assert.True(File.Exists(staged.StagingPath));
        Assert.StartsWith(".", Path.GetFileName(staged.StagingPath));
        Assert.False(ModManager.IsModFile(staged.StagingPath));

        running = true;
        var error = Assert.Throws<ModReplaceException>(() => staged.Commit());

        Assert.Equal(ModReplaceStep.GameRunning, error.Step);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task An_old_file_that_cannot_be_moved_puts_everything_back()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var oldPath = Put("sodium-0.5.8.jar", Content(1));
        var before = Snapshot();
        var replacer = Replacer();

        var staged = await replacer.StageAsync(Request(oldPath, "sodium-0.6.0.jar", Content(2)));

        // Held the way a running game holds a jar: open, and not to be renamed.
        using (new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.Throws<ModReplaceException>(() => staged.Commit());
            Assert.Equal(ModReplaceStep.MoveOld, error.Step);
        }

        // Two versions of one mod side by side would stop the game: the new one is gone again.
        Assert.Equal(before, Snapshot());
        Assert.Null(replacer.FindPrevious(_game, "mods", "sodium-0.6.0.jar"));
    }

    [Fact]
    public async Task The_previous_version_comes_back_and_the_newer_one_is_kept_in_turn()
    {
        var old = Content(1);
        var fresh = Content(2);
        var replacer = Replacer();
        await replacer.ReplaceAsync(Request(Put("sodium-0.5.8.jar", old), "sodium-0.6.0.jar", fresh));

        var previous = replacer.FindPrevious(_game, "mods", "sodium-0.6.0.jar")!;
        var requests = _http.Requests;

        var result = replacer.Restore(_game, previous, "0.6.0");

        Assert.Equal(requests, _http.Requests);
        Assert.Equal(new[] { "sodium-0.5.8.jar" }, Snapshot().Keys);
        Assert.Equal(old, File.ReadAllBytes(result.NewPath));

        // And forth again, the same way.
        var newer = replacer.FindPrevious(_game, "mods", "sodium-0.5.8.jar");
        Assert.NotNull(newer);
        Assert.Equal("sodium-0.6.0.jar", newer!.FileName);
        Assert.Equal("0.6.0", newer.Label);

        replacer.Restore(_game, newer);
        Assert.Equal(new[] { "sodium-0.6.0.jar" }, Snapshot().Keys);
        Assert.Equal(fresh, File.ReadAllBytes(Path.Combine(_mods, "sodium-0.6.0.jar")));
    }

    [Fact]
    public async Task Going_back_keeps_the_switched_off_state_and_is_refused_while_the_game_runs()
    {
        var replacer = Replacer();
        await replacer.ReplaceAsync(Request(Put("sodium-0.5.8.jar", Content(1)), "sodium-0.6.0.jar", Content(2)));
        File.Move(Path.Combine(_mods, "sodium-0.6.0.jar"), Path.Combine(_mods, "sodium-0.6.0.jar.disabled"));

        var previous = replacer.FindPrevious(_game, "mods", "sodium-0.6.0.jar.disabled")!;
        var before = Snapshot();

        replacer.IsGameRunning = _ => true;
        Assert.Equal(ModReplaceStep.GameRunning, Assert.Throws<ModReplaceException>(() => replacer.Restore(_game, previous)).Step);
        Assert.Equal(before, Snapshot());

        replacer.IsGameRunning = _ => false;
        var result = replacer.Restore(_game, previous);

        Assert.False(result.Enabled);
        Assert.Equal(new[] { "sodium-0.5.8.jar.disabled" }, Snapshot().Keys);
    }

    [Fact]
    public async Task Only_the_last_few_versions_of_a_mod_are_kept()
    {
        var replacer = Replacer();
        var path = Put("sodium-0.jar", Content(100));

        for (var i = 1; i <= ModFileReplacer.KeepPerMod + 3; i++)
        {
            _now = _now.AddDays(1);
            path = (await replacer.ReplaceAsync(Request(path, $"sodium-{i}.jar", Content(100 + i)))).NewPath;
        }

        var kept = replacer.List(_game);

        Assert.Equal(ModFileReplacer.KeepPerMod, kept.Count);
        Assert.Equal(new[] { "sodium-5.jar", "sodium-4.jar", "sodium-3.jar" }, kept.Select(k => k.FileName));

        var onDisk = Directory.EnumerateFiles(Path.Combine(_game, ".stlauncher", "replaced"), "*.jar", SearchOption.AllDirectories).ToList();
        Assert.Equal(ModFileReplacer.KeepPerMod, onDisk.Count);

        // The days whose only file went are gone with it.
        Assert.Equal(ModFileReplacer.KeepPerMod, Directory.EnumerateDirectories(Path.Combine(_game, ".stlauncher", "replaced")).Count());
    }

    [Fact]
    public async Task A_file_shared_with_other_builds_is_replaced_without_being_written_through()
    {
        var old = Content(1);
        var shared = Path.Combine(_root, "the-same-file-in-another-build.jar");
        File.WriteAllBytes(shared, old);
        var oldPath = Path.Combine(_mods, "sodium-0.5.8.jar");

        if (!HardLink.TryCreate(shared, oldPath))
        {
            return;
        }

        var result = await Replacer().ReplaceAsync(Request(oldPath, "sodium-0.6.0.jar", Content(2)));

        // The other name of the old file still holds the old bytes, and so does the kept copy.
        Assert.Equal(old, File.ReadAllBytes(shared));
        Assert.Equal(old, File.ReadAllBytes(result.Previous!.StoredPath));

        // Same name, shared: the swap must not truncate the shared file either.
        var same = Path.Combine(_mods, "pack.jar");
        Assert.True(HardLink.TryCreate(shared, same));
        await Replacer().ReplaceAsync(Request(same, "pack.jar", Content(5)));

        Assert.Equal(old, File.ReadAllBytes(shared));
        Assert.Equal(Content(5), File.ReadAllBytes(same));
    }

    [Fact]
    public async Task A_file_another_build_already_has_is_linked_from_the_store_instead_of_downloaded()
    {
        var fresh = Content(2);
        var store = new SharedFileStore(Path.Combine(_root, "objects"), _instances);
        var objectPath = store.ObjectPath(Sha1Of(fresh))!;
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        File.WriteAllBytes(objectPath, fresh);

        if (!HardLink.TryCreate(objectPath, Path.Combine(_root, "probe")))
        {
            return;
        }

        var oldPath = Put("sodium-0.5.8.jar", Content(1));

        var result = await Replacer(store).ReplaceAsync(Request(oldPath, "sodium-0.6.0.jar", fresh));

        Assert.Equal(0, _http.Requests);
        Assert.Equal(fresh, File.ReadAllBytes(result.NewPath));
        Assert.True(HardLink.TryGetIdentity(result.NewPath)!.Value.IsSameFile(HardLink.TryGetIdentity(objectPath)!.Value));
    }

    [Fact]
    public async Task A_group_is_taken_back_whole_when_a_later_member_fails()
    {
        // An update that brings a dependency: both are staged, then both are placed. If the
        // mod itself cannot be placed, the dependency that already was is taken out again.
        var oldPath = Put("sodium-0.5.8.jar", Content(1));
        var before = Snapshot();
        var replacer = Replacer();

        var dependency = await replacer.StageAsync(Request(null, "fabric-api-2.jar", Content(7)));
        var mod = await replacer.StageAsync(Request(oldPath, "sodium-0.6.0.jar", Content(2)));

        dependency.Commit();
        Assert.True(File.Exists(Path.Combine(_mods, "fabric-api-2.jar")));

        mod.Commit();
        Assert.Equal(new[] { "fabric-api-2.jar", "sodium-0.6.0.jar" }, Snapshot().Keys.OrderBy(k => k));

        mod.Undo();
        dependency.Undo();

        Assert.Equal(before, Snapshot());
        Assert.Empty(replacer.List(_game));
    }

    [Fact]
    public async Task A_discarded_download_leaves_no_trace_and_old_leftovers_are_swept()
    {
        var oldPath = Put("sodium-0.5.8.jar", Content(1));
        var stale = Path.Combine(_mods, ".sodium-0.5.9.jar.0123.stlnew");
        File.WriteAllBytes(stale, Content(9, 10));
        File.SetLastWriteTimeUtc(stale, _now.AddDays(-3));
        var replacer = Replacer();

        var staged = await replacer.StageAsync(Request(oldPath, "sodium-0.6.0.jar", Content(2)));
        Assert.False(File.Exists(stale));

        staged.Discard();

        Assert.Equal(new[] { "sodium-0.5.8.jar" }, Snapshot().Keys);
    }

    [Fact]
    public void An_index_edited_by_hand_cannot_point_outside_the_keep()
    {
        var keep = Path.Combine(_game, ".stlauncher", "replaced");
        Directory.CreateDirectory(keep);
        var outside = Path.Combine(_root, "outside.jar");
        File.WriteAllBytes(outside, Content(1, 10));

        File.WriteAllText(
            Path.Combine(keep, "index.json"),
            """[{"Folder":"mods","FileName":"evil.jar","ReplacedBy":"sodium.jar","Stored":"../../../../outside.jar","WhenUtc":"2026-10-10T00:00:00Z"}]""");

        Assert.Empty(Replacer().List(_game));
        Assert.Null(Replacer().FindPrevious(_game, "mods", "sodium.jar"));
    }
}
