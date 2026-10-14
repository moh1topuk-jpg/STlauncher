using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Friends;
using STlauncher.Core.Loaders;
using STlauncher.Core.Modpacks;
using STlauncher.Relay;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// A build under a short code: the launcher's side and the relay's together on the
/// loopback address, with the relay keeping builds in a scratch folder of its own.
/// </summary>
public sealed class BuildShareTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "stlauncher-share-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private RelayServer StartRelay(Action<RelayOptions>? tweak = null, bool keepsBuilds = true)
    {
        var options = new RelayOptions
        {
            Port = 0,
            Bind = IPAddress.Loopback,
            StatsInterval = TimeSpan.Zero,
            MaxAttemptsPerMinute = 10_000,
            ShareDirectory = keepsBuilds ? _directory : null,
            ShareMissDelay = TimeSpan.Zero
        };

        tweak?.Invoke(options);

        var relay = new RelayServer(options);
        relay.Start();
        return relay;
    }

    private static RelayEndpoint EndpointOf(RelayServer relay) => new("127.0.0.1", relay.Port);

    private static string LongCode(string name = "Vanilla+", int files = 24)
        => BuildCode.Encode(new BuildCodePayload(
            name,
            "1.21.1",
            LoaderKind.Fabric,
            "0.16.5",
            Enumerable.Range(0, files)
                .Select(i => new BuildCodeFile(
                    $"mods/mod-{i}.jar",
                    $"https://cdn.modrinth.com/data/AbCd{i:0000}/versions/XyZ{i:0000}/mod-{i}-1.0.{i}.jar",
                    Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.ASCII.GetBytes("file" + i))).ToLowerInvariant(),
                    100_000 + i))
                .ToList(),
            new[] { "my-own-mod.jar" }));

    private string[] StoredFiles() => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.stb") : Array.Empty<string>();

    /// <summary>Says the lines one after another on one connection and returns every line the relay said back.</summary>
    private static async Task<List<string>> Talk(int port, params string[] lines)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(string.Join("\n", lines) + "\n"));

        using var limit = new CancellationTokenSource(8000);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var answers = new List<string>();

        try
        {
            while (await reader.ReadLineAsync(limit.Token) is { } line)
            {
                answers.Add(line);
            }
        }
        catch (IOException)
        {
        }

        return answers;
    }

    [Fact]
    public async Task A_shared_build_comes_back_by_its_short_code()
    {
        await using var relay = StartRelay();
        var longCode = LongCode();

        var (code, failure) = await BuildShare.PublishAsync(EndpointOf(relay), longCode);

        Assert.Equal(BuildShareFailure.None, failure);
        Assert.NotNull(code);
        Assert.Equal(10, code!.Length);
        Assert.Matches("^SB-[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$", BuildShare.Format(code));

        // The friend types it the way it was sent, lower case and all.
        var (back, fetchFailure) = await BuildShare.FetchAsync(EndpointOf(relay), "вот сборка: " + BuildShare.Format(code).ToLowerInvariant());

        Assert.Equal(BuildShareFailure.None, fetchFailure);
        Assert.Equal(longCode, back);
        Assert.True(BuildCode.TryDecode(back, out var payload));
        Assert.Equal("Vanilla+", payload!.Name);
        Assert.Equal(24, payload.Files.Count);
    }

    [Fact]
    public async Task A_big_build_travels_whole()
    {
        await using var relay = StartRelay();

        // Random bytes do not deflate, so this is a code of well over a hundred kilobytes.
        var random = new Random(7);
        var body = new byte[120_000];
        random.NextBytes(body);
        var longCode = BuildCode.Prefix + Convert.ToBase64String(body).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var (code, failure) = await BuildShare.PublishAsync(EndpointOf(relay), longCode);
        Assert.Equal(BuildShareFailure.None, failure);

        var (back, _) = await BuildShare.FetchAsync(EndpointOf(relay), code!);
        Assert.Equal(longCode, back);
    }

    [Fact]
    public async Task The_same_build_shared_again_keeps_its_code()
    {
        await using var relay = StartRelay();
        var longCode = LongCode();

        var (first, _) = await BuildShare.PublishAsync(EndpointOf(relay), longCode);
        var (second, _) = await BuildShare.PublishAsync(EndpointOf(relay), longCode);
        var (other, _) = await BuildShare.PublishAsync(EndpointOf(relay), LongCode("Another"));

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal(2, relay.SharedBuilds);
        Assert.Equal(2, StoredFiles().Length);
    }

    [Fact]
    public async Task Only_a_build_code_is_kept()
    {
        await using var relay = StartRelay();

        // The launcher does not even send what is not a build code.
        Assert.Equal(BuildShareFailure.Rejected, (await BuildShare.PublishAsync(EndpointOf(relay), "hello, world")).Failure);

        // Somebody else's client may try. Text without the prefix:
        Assert.Equal(new[] { "OK", "ERR BADDATA" }, await Talk(relay.Port, "SHARE BEGIN", "SHARE + any-text-at-all", "SHARE END"));

        // The prefix and then something that is not base64url:
        Assert.Equal(new[] { "OK", "ERR BADDATA" }, await Talk(relay.Port, "SHARE BEGIN", "SHARE + STB1.abc def", "SHARE END"));
        Assert.Equal(new[] { "OK", "ERR BADDATA" }, await Talk(relay.Port, "SHARE BEGIN", "SHARE + STB1.abc.def", "SHARE END"));

        // The prefix alone, and nothing at all:
        Assert.Equal(new[] { "OK", "ERR BADDATA" }, await Talk(relay.Port, "SHARE BEGIN", "SHARE + STB1.", "SHARE END"));
        Assert.Equal(new[] { "OK", "ERR BADDATA" }, await Talk(relay.Port, "SHARE BEGIN", "SHARE END"));

        // A line that is not part of the upload ends it without a word.
        Assert.Equal(new[] { "OK" }, await Talk(relay.Port, "SHARE BEGIN", "PING"));

        Assert.Empty(StoredFiles());
    }

    [Fact]
    public async Task A_build_longer_than_the_limit_is_refused()
    {
        await using var relay = StartRelay(o => o.ShareMaxBytes = 1024);

        var (code, failure) = await BuildShare.PublishAsync(EndpointOf(relay), BuildCode.Prefix + new string('A', 5000));

        Assert.Null(code);
        Assert.Equal(BuildShareFailure.TooBig, failure);
        Assert.Empty(StoredFiles());

        // Just under the limit goes through.
        Assert.Equal(BuildShareFailure.None, (await BuildShare.PublishAsync(EndpointOf(relay), BuildCode.Prefix + new string('A', 1000))).Failure);
    }

    [Fact]
    public async Task A_build_longer_than_any_relay_keeps_is_not_sent()
    {
        // Nothing listens here: had the launcher tried, the answer would be "unreachable".
        var nowhere = new RelayEndpoint("127.0.0.1", 9);

        var (_, failure) = await BuildShare.PublishAsync(nowhere, BuildCode.Prefix + new string('A', BuildShare.MaxLength));

        Assert.Equal(BuildShareFailure.TooBig, failure);
    }

    [Fact]
    public async Task Sharing_is_limited_per_address_per_hour()
    {
        await using var relay = StartRelay(o => o.SharesPerHour = 2);

        Assert.Equal(BuildShareFailure.None, (await BuildShare.PublishAsync(EndpointOf(relay), LongCode("One"))).Failure);
        Assert.Equal(BuildShareFailure.None, (await BuildShare.PublishAsync(EndpointOf(relay), LongCode("Two"))).Failure);
        Assert.Equal(BuildShareFailure.RateLimited, (await BuildShare.PublishAsync(EndpointOf(relay), LongCode("Three"))).Failure);
        Assert.Equal(2, StoredFiles().Length);
    }

    [Fact]
    public async Task Asking_is_limited_per_address_per_hour_misses_included()
    {
        await using var relay = StartRelay(o => o.ShareFetchesPerHour = 3);
        var (code, _) = await BuildShare.PublishAsync(EndpointOf(relay), LongCode());

        Assert.Equal(BuildShareFailure.None, (await BuildShare.FetchAsync(EndpointOf(relay), code!)).Failure);
        Assert.Equal(BuildShareFailure.NotFound, (await BuildShare.FetchAsync(EndpointOf(relay), "0000000000")).Failure);
        Assert.Equal(BuildShareFailure.NotFound, (await BuildShare.FetchAsync(EndpointOf(relay), "0000000001")).Failure);
        Assert.Equal(BuildShareFailure.RateLimited, (await BuildShare.FetchAsync(EndpointOf(relay), code!)).Failure);
    }

    [Fact]
    public async Task An_unknown_code_is_not_found()
    {
        await using var relay = StartRelay();

        Assert.Equal(BuildShareFailure.NotFound, (await BuildShare.FetchAsync(EndpointOf(relay), "SB-7KQ2M-X9P1A")).Failure);

        // Not shaped like a code at all: the same answer, and no file name is ever made of it.
        Assert.Equal(new[] { "ERR NOTFOUND" }, await Talk(relay.Port, "GETSHARE ../../etc/passwd"));
        Assert.Equal(new[] { "ERR NOTFOUND" }, await Talk(relay.Port, "GETSHARE SB-7KQ2M-X9P1A"));
    }

    [Fact]
    public async Task A_miss_is_answered_slowly()
    {
        await using var relay = StartRelay(o => o.ShareMissDelay = TimeSpan.FromMilliseconds(400));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (_, failure) = await BuildShare.FetchAsync(EndpointOf(relay), "0000000000");

        Assert.Equal(BuildShareFailure.NotFound, failure);
        Assert.True(watch.ElapsedMilliseconds >= 350, $"answered in {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Builds_survive_a_restart_of_the_relay()
    {
        var longCode = LongCode();
        string code;

        await using (var relay = StartRelay())
        {
            code = (await BuildShare.PublishAsync(EndpointOf(relay), longCode)).Code!;
        }

        await using (var again = StartRelay())
        {
            Assert.Equal(1, again.SharedBuilds);
            Assert.Equal(longCode, (await BuildShare.FetchAsync(EndpointOf(again), code)).LongCode);

            // And it is still the one this text answers to.
            Assert.Equal(code, (await BuildShare.PublishAsync(EndpointOf(again), longCode)).Code);
        }
    }

    [Fact]
    public async Task A_build_nobody_asked_for_in_its_lifetime_is_removed_at_start()
    {
        string old;
        string fresh;

        await using (var relay = StartRelay())
        {
            old = (await BuildShare.PublishAsync(EndpointOf(relay), LongCode("Old"))).Code!;
            fresh = (await BuildShare.PublishAsync(EndpointOf(relay), LongCode("Fresh"))).Code!;
        }

        var file = StoredFiles().Single(f => Path.GetFileName(f).StartsWith(old, StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-181));

        await using (var again = StartRelay())
        {
            Assert.Equal(BuildShareFailure.NotFound, (await BuildShare.FetchAsync(EndpointOf(again), old)).Failure);
            Assert.Equal(BuildShareFailure.None, (await BuildShare.FetchAsync(EndpointOf(again), fresh)).Failure);
            Assert.False(File.Exists(file));
            Assert.Single(StoredFiles());
        }
    }

    [Fact]
    public void The_store_forgets_a_build_after_its_life_and_a_fetch_starts_the_life_over()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var store = new ShareStore(_directory, long.MaxValue, 100, TimeSpan.FromDays(180), () => now);
        store.Load();

        Assert.Equal(ShareOutcome.Stored, store.Put("STB1.first", out var first));
        Assert.Equal(ShareOutcome.Stored, store.Put("STB1.second", out var second));

        // Day 170: the first one is asked for, the second is not.
        now = now.AddDays(170);
        Assert.Equal("STB1.first", store.Get(first));

        // Day 190: the second has gone unasked for longer than a life.
        now = now.AddDays(20);
        Assert.Equal(1, store.Sweep());
        Assert.Null(store.Get(second));
        Assert.Equal("STB1.first", store.Get(first));
        Assert.Single(StoredFiles());

        // What a restart would read from the disk agrees with that.
        var reread = new ShareStore(_directory, long.MaxValue, 100, TimeSpan.FromDays(180), () => now);
        Assert.Equal(0, reread.Load());
        Assert.Equal(1, reread.Count);

        // Day 400: nobody came. It is not handed out even before a sweep gets to it.
        now = now.AddDays(210);
        Assert.Null(store.Get(first));
        Assert.Empty(StoredFiles());
        Assert.Equal(0, store.TotalBytes);
    }

    [Fact]
    public void The_store_keeps_no_more_than_it_was_told_to()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var byCount = new ShareStore(Path.Combine(_directory, "count"), long.MaxValue, 2, TimeSpan.FromDays(180), () => now);
        byCount.Load();

        Assert.Equal(ShareOutcome.Stored, byCount.Put("STB1.a", out _));
        Assert.Equal(ShareOutcome.Stored, byCount.Put("STB1.b", out _));
        Assert.Equal(ShareOutcome.Full, byCount.Put("STB1.c", out _));

        // What is already kept can still be shared again.
        Assert.Equal(ShareOutcome.Refreshed, byCount.Put("STB1.a", out _));

        // Room appears when the old ones expire.
        now = now.AddDays(181);
        Assert.Equal(ShareOutcome.Stored, byCount.Put("STB1.c", out _));

        var bySize = new ShareStore(Path.Combine(_directory, "size"), 20, 100, TimeSpan.FromDays(180), () => now);
        bySize.Load();

        Assert.Equal(ShareOutcome.Stored, bySize.Put("STB1.123456789", out _));
        Assert.Equal(ShareOutcome.Full, bySize.Put("STB1.abcdefghi", out _));
    }

    [Fact]
    public void The_store_leaves_files_that_are_not_its_own_alone()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "README.txt"), "not a build");
        File.WriteAllText(Path.Combine(_directory, "something.stb"), "not a build either");
        File.WriteAllText(Path.Combine(_directory, "0000000000." + new string('a', 64) + ".stb.tmp"), "half a write");

        var store = new ShareStore(_directory, long.MaxValue, 100, TimeSpan.FromDays(180));
        store.Load();

        Assert.Equal(0, store.Count);
        Assert.True(File.Exists(Path.Combine(_directory, "README.txt")));
        Assert.True(File.Exists(Path.Combine(_directory, "something.stb")));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task A_relay_without_a_place_to_keep_builds_says_so()
    {
        await using var relay = StartRelay(keepsBuilds: false);

        Assert.Equal(BuildShareFailure.Unsupported, (await BuildShare.PublishAsync(EndpointOf(relay), LongCode())).Failure);
        Assert.Equal(BuildShareFailure.Unsupported, (await BuildShare.FetchAsync(EndpointOf(relay), "0000000000")).Failure);
        Assert.Equal(new[] { "ERR NOSHARE" }, await Talk(relay.Port, "SHARE BEGIN"));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task A_relay_from_before_sharing_is_told_apart_from_one_that_is_down()
    {
        // What the relay released before this answers to a first line it does not know.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var old = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
                await reader.ReadLineAsync();
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("ERR BADCMD\n"));
            }
        });

        var relay = new RelayEndpoint("127.0.0.1", port);

        Assert.Equal(BuildShareFailure.Unsupported, (await BuildShare.PublishAsync(relay, LongCode())).Failure);
        Assert.Equal(BuildShareFailure.Unsupported, (await BuildShare.FetchAsync(relay, "0000000000")).Failure);

        await old;
        listener.Stop();

        // And now nobody is there at all.
        Assert.Equal(BuildShareFailure.Unreachable, (await BuildShare.PublishAsync(relay, LongCode())).Failure);
        Assert.Equal(BuildShareFailure.Unreachable, (await BuildShare.FetchAsync(relay, "0000000000")).Failure);
    }

    [Fact]
    public async Task The_lines_older_launchers_say_are_answered_as_before()
    {
        await using var relay = StartRelay();

        Assert.Equal(new[] { "ERR NOROOM" }, await Talk(relay.Port, "FETCH 0000000000"));
        Assert.Equal(new[] { "ERR BADCMD" }, await Talk(relay.Port, "SHARE"));
        Assert.Equal(new[] { "ERR BADCMD" }, await Talk(relay.Port, "SHARE NOW"));
        Assert.Equal(new[] { "ERR BADCMD" }, await Talk(relay.Port, "GETSHARE"));
    }

    [Theory]
    [InlineData("SB-7KQ2M-X9P1A", "7KQ2MX9P1A")]
    [InlineData("sb-7kq2m-x9p1a", "7KQ2MX9P1A")]
    [InlineData("SB7KQ2MX9P1A", "7KQ2MX9P1A")]
    [InlineData("SB 7KQ2M X9P1A", "7KQ2MX9P1A")]
    [InlineData("  SB - 7KQ2M - X9P1A  ", "7KQ2MX9P1A")]
    [InlineData("SB—7KQ2M—X9P1A", "7KQ2MX9P1A")]
    [InlineData("держи сборку: SB-7KQ2M-X9P1A, вставь в лаунчер", "7KQ2MX9P1A")]
    [InlineData("(SB-7KQ2M-X9P1A)", "7KQ2MX9P1A")]
    [InlineData("SB-OIL00-ABCDE", "01100ABCDE")]
    public void A_short_code_is_found_however_it_was_typed(string text, string expected)
    {
        Assert.True(BuildShare.LooksLikeShortCode(text));
        Assert.True(BuildShare.TryFindShortCode(text, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ST-7KQ2M-X9P1A")]
    [InlineData("SB-7KQ2M-X9P1")]
    [InlineData("SB-7KQ2M-X9P1AB")]
    [InlineData("XSB-7KQ2M-X9P1A")]
    [InlineData("SB-7KQ2U-X9P1A")]
    [InlineData("sb hello world")]
    [InlineData("STB1.SB-7KQ2M")]
    public void What_is_not_a_short_code_is_not_taken_for_one(string? text)
    {
        Assert.False(BuildShare.LooksLikeShortCode(text));
    }

    [Fact]
    public void A_pasted_text_is_told_apart_by_kind()
    {
        Assert.Equal(PastedCodeKind.Build, PastedCode.Classify("лови " + LongCode(), out _));
        Assert.Equal(PastedCodeKind.None, PastedCode.Classify("просто текст", out _));

        Assert.Equal(PastedCodeKind.ShortBuild, PastedCode.Classify("SB-7KQ2M-X9P1A", out var build));
        Assert.Equal("7KQ2MX9P1A", build);

        Assert.Equal(PastedCodeKind.ShortInvite, PastedCode.Classify("заходи: ST-7KQ2M-X9P1A", out var invite));
        Assert.Equal("7KQ2MX9P1A", invite);

        // A long code is the build itself: a short one beside it is not asked for.
        Assert.Equal(PastedCodeKind.Build, PastedCode.Classify("SB-7KQ2M-X9P1A или " + LongCode(), out _));
    }

    [Fact]
    public void The_relay_reads_where_to_keep_builds_from_its_options()
    {
        Assert.True(RelayOptions.TryParse(Array.Empty<string>(), _ => null, out var off, out _));
        Assert.Null(off.ShareDirectory);
        Assert.Equal(256 * 1024, off.ShareMaxBytes);
        Assert.Equal(20, off.SharesPerHour);
        Assert.Equal(240, off.ShareFetchesPerHour);
        Assert.Equal(TimeSpan.FromDays(180), off.ShareLife);
        Assert.Equal(2048L * 1024 * 1024, off.ShareMaxTotalBytes);

        Assert.True(RelayOptions.TryParse(
            new[] { "--share-dir", "/var/lib/stlauncher-relay/shares", "--share-days=30", "--share-max-kb", "64" },
            name => name == "STRELAY_SHARE_DIR" ? "/from/the/environment" : null,
            out var on,
            out _));

        Assert.Equal("/var/lib/stlauncher-relay/shares", on.ShareDirectory);
        Assert.Equal(TimeSpan.FromDays(30), on.ShareLife);
        Assert.Equal(64 * 1024, on.ShareMaxBytes);

        Assert.True(RelayOptions.TryParse(Array.Empty<string>(), name => name == "STRELAY_SHARE_DIR" ? "/from/the/environment" : null, out var env, out _));
        Assert.Equal("/from/the/environment", env.ShareDirectory);
    }
}
