using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// Joining a server with Dream Displays waited on the game's thread for the mod's own FFmpeg
/// download; on a slow line the game froze. The launcher puts the files in place first.
/// </summary>
public sealed class DreamDisplaysFilesTests : IDisposable
{
    private static readonly Uri Mirror = new("https://mirror.test/");

    private readonly string _game = Directory.CreateTempSubdirectory("dd-test-").FullName;

    public void Dispose() => Directory.Delete(_game, recursive: true);

    private static byte[] Bundle(params string[] names)
    {
        using var memory = new MemoryStream();

        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in names)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("content of " + name);
            }
        }

        return memory.ToArray();
    }

    private static readonly byte[] GoodBundle = Bundle(
        "ffmpeg/windows-x64-9.0/ffmpeg.exe",
        "native/windows-x64/avcodec-62.dll",
        "native/windows-x64/LICENSE.txt");

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private void AddMod(string name = "dreamdisplays-fabric-1.21.11-1.10.8.jar")
    {
        var mods = ModManager.ModsDirectory(_game);
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, name), "jar");
    }

    private string Placed(string relative) => Path.Combine(_game, relative);

    [Fact]
    public void NotNeeded_WithoutTheMod_OrWithItSwitchedOff()
    {
        Assert.False(DreamDisplaysFiles.AreNeeded(_game));

        AddMod("dreamdisplays-fabric-1.21.11-1.10.8.jar.disabled");

        Assert.False(DreamDisplaysFiles.AreNeeded(_game));
    }

    [Fact]
    public async Task Install_PutsTheFilesWhereTheModLooks_AndThenNothingIsNeeded()
    {
        AddMod();
        Assert.True(DreamDisplaysFiles.AreNeeded(_game));

        var files = new DreamDisplaysFiles(new HttpClient(new Serve(GoodBundle)), Hash(GoodBundle));
        await files.InstallAsync(Mirror, _game);

        Assert.True(File.Exists(Placed("dreamdisplays/ffmpeg/windows-x64-9.0/ffmpeg.exe")));
        Assert.True(File.Exists(Placed("dreamdisplays/native/windows-x64/avcodec-62.dll")));
        Assert.False(File.Exists(Placed("dreamdisplays/ffmpeg-bundle.part")));
        Assert.False(DreamDisplaysFiles.AreNeeded(_game));
    }

    [Fact]
    public async Task Install_AsksTheServerForTheBundle()
    {
        var serve = new Serve(GoodBundle);
        await new DreamDisplaysFiles(new HttpClient(serve), Hash(GoodBundle)).InstallAsync(Mirror, _game);

        Assert.Equal("https://mirror.test/" + DreamDisplaysFiles.BundlePath, serve.LastUrl);
    }

    [Fact]
    public async Task Install_WithAWrongHash_PlacesNothing_AndKeepsNoPartFile()
    {
        var files = new DreamDisplaysFiles(new HttpClient(new Serve(GoodBundle)), new string('0', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() => files.InstallAsync(Mirror, _game));

        Assert.False(File.Exists(Placed("dreamdisplays/ffmpeg/windows-x64-9.0/ffmpeg.exe")));
        Assert.False(File.Exists(Placed("dreamdisplays/ffmpeg-bundle.part")));
    }

    [Fact]
    public async Task Install_GoesOnFromWhereTheLastDownloadStopped()
    {
        Directory.CreateDirectory(Placed("dreamdisplays"));
        var part = GoodBundle.Take(GoodBundle.Length / 2).ToArray();
        await File.WriteAllBytesAsync(Placed("dreamdisplays/ffmpeg-bundle.part"), part);

        var serve = new Serve(GoodBundle);
        await new DreamDisplaysFiles(new HttpClient(serve), Hash(GoodBundle)).InstallAsync(Mirror, _game);

        Assert.Equal(part.Length, serve.LastRangeFrom);
        Assert.True(File.Exists(Placed("dreamdisplays/ffmpeg/windows-x64-9.0/ffmpeg.exe")));
    }

    [Fact]
    public async Task Install_ServerIgnoringTheRange_StartsOver()
    {
        Directory.CreateDirectory(Placed("dreamdisplays"));
        await File.WriteAllBytesAsync(Placed("dreamdisplays/ffmpeg-bundle.part"), new byte[] { 1, 2, 3 });

        await new DreamDisplaysFiles(new HttpClient(new Serve(GoodBundle) { HonoursRange = false }), Hash(GoodBundle))
            .InstallAsync(Mirror, _game);

        Assert.True(File.Exists(Placed("dreamdisplays/native/windows-x64/avcodec-62.dll")));
    }

    [Fact]
    public async Task Install_TakesOnlyTheTwoFolders()
    {
        var bundle = Bundle(
            "ffmpeg/windows-x64-9.0/ffmpeg.exe",
            "../../outside.exe",
            "mods/evil.jar",
            "native/windows-x64/../../../climbed.dll");

        await new DreamDisplaysFiles(new HttpClient(new Serve(bundle)), Hash(bundle)).InstallAsync(Mirror, _game);

        Assert.True(File.Exists(Placed("dreamdisplays/ffmpeg/windows-x64-9.0/ffmpeg.exe")));
        Assert.Single(Directory.GetFiles(_game, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(_game, "..", "outside.exe")));
    }

    [Fact]
    public async Task Install_ReportsProgressInBytes()
    {
        (long Done, long Total) last = default;
        var progress = new Direct(p => last = p);

        await new DreamDisplaysFiles(new HttpClient(new Serve(GoodBundle)), Hash(GoodBundle))
            .InstallAsync(Mirror, _game, progress);

        Assert.Equal(GoodBundle.Length, last.Done);
        Assert.Equal(GoodBundle.Length, last.Total);
    }

    private sealed class Direct : IProgress<(long Done, long Total)>
    {
        private readonly Action<(long Done, long Total)> _report;

        public Direct(Action<(long Done, long Total)> report) => _report = report;

        public void Report((long Done, long Total) value) => _report(value);
    }

    private sealed class Serve : HttpMessageHandler
    {
        private readonly byte[] _bytes;

        public Serve(byte[] bytes) => _bytes = bytes;

        public bool HonoursRange { get; init; } = true;

        public string? LastUrl { get; private set; }

        public long? LastRangeFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.AbsoluteUri;
            var from = request.Headers.Range?.Ranges.First().From;
            LastRangeFrom = from;

            if (from is { } start && HonoursRange)
            {
                var part = _bytes.Skip((int)start).ToArray();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(part)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_bytes)
            });
        }
    }
}
