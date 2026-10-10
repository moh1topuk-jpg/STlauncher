using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using STlauncher.Core.Modpacks;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

public class ModpackInstallTests : IDisposable
{
    private const string Project = "AANobbMI";
    private const string Version = "Yp8wLY1P";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "stl-modpack-" + Guid.NewGuid().ToString("N"));
    private readonly string _instance;
    private readonly Web _web = new();

    public ModpackInstallTests()
    {
        _instance = Path.Combine(_root, "instance");
        Directory.CreateDirectory(_root);
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

    private sealed class Web : HttpMessageHandler
    {
        private readonly Dictionary<string, (byte[] Body, string Type)> _answers = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = new();

        public void Serve(string url, byte[] bytes) => _answers[url] = (bytes, "application/octet-stream");

        public void ServeJson(string url, string json) => _answers[url] = (Encoding.UTF8.GetBytes(json), "application/json");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            lock (Asked)
            {
                Asked.Add(url);
            }

            if (!_answers.TryGetValue(url, out var answer))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var content = new ByteArrayContent(answer.Body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(answer.Type);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static string Sha1Of(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static string Sha512Of(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();

    private static string CdnUrl(string file) => $"https://cdn.modrinth.com/data/{Project}/versions/{Version}/{file}";

    private sealed record Entry(string Path, string Url, string? Sha1, string? Sha512);

    private string Pack(params Entry[] entries)
    {
        var index = new
        {
            formatVersion = 1,
            game = "minecraft",
            versionId = "1.0.0",
            name = "Test Pack",
            dependencies = new Dictionary<string, string> { ["minecraft"] = "1.21.1", ["fabric-loader"] = "0.16.0" },
            files = entries.Select(e =>
            {
                var hashes = new Dictionary<string, string>();

                if (e.Sha1 is not null)
                {
                    hashes["sha1"] = e.Sha1;
                }

                if (e.Sha512 is not null)
                {
                    hashes["sha512"] = e.Sha512;
                }

                return new { path = e.Path, hashes, downloads = new[] { e.Url }, fileSize = 10 };
            })
        };

        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".mrpack");

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open());
        writer.Write(JsonSerializer.Serialize(index));

        return path;
    }

    private Entry Good(string name, int seed)
    {
        var bytes = Encoding.UTF8.GetBytes("file " + seed);
        var url = CdnUrl(name);
        _web.Serve(url, bytes);
        return new Entry("mods/" + name, url, Sha1Of(bytes), Sha512Of(bytes));
    }

    private ModpackInstaller Installer(bool withModrinth = true)
    {
        var http = new HttpClient(_web);
        return new ModpackInstaller(new DownloadClient(http, maxAttempts: 1), withModrinth ? new ModrinthClient(http) : null);
    }

    private void ModrinthPublishes(string file, byte[] bytes, string? url = null, string project = Project)
        => _web.ServeJson(
            $"https://api.modrinth.com/v2/version/{Version}",
            JsonSerializer.Serialize(new
            {
                id = Version,
                project_id = project,
                name = "v",
                version_number = "1.0",
                files = new[]
                {
                    new { url = url ?? CdnUrl(file), filename = file, size = bytes.Length, primary = true, hashes = new { sha1 = Sha1Of(bytes), sha512 = Sha512Of(bytes) } }
                }
            }));

    // ---------- the whole index is read before the first byte ----------

    [Fact]
    public async Task A_bad_last_entry_stops_the_install_before_anything_is_fetched()
    {
        var pack = Pack(
            Good("a.jar", 1),
            Good("b.jar", 2),
            new Entry("mods/c.jar", "https://evil.example/c.jar", new string('a', 40), null));

        var error = await Assert.ThrowsAsync<ModpackIndexException>(() => Installer().InstallAsync(pack, _instance));

        var problem = Assert.Single(error.Problems);
        Assert.Equal(ModpackIndexProblemKind.HostNotAllowed, problem.Kind);
        Assert.Equal("mods/c.jar", problem.Path);
        Assert.Contains("mods/c.jar", error.Message);

        Assert.Empty(_web.Asked);
        Assert.False(Directory.Exists(Path.Combine(_instance, "mods")));
    }

    [Fact]
    public async Task Every_problem_of_the_index_is_reported_at_once()
    {
        var good = Good("a.jar", 1);

        var pack = Pack(
            good,
            good with { Path = "mods/A.jar" },
            new Entry("mods/plain.jar", "http://cdn.modrinth.com/data/x/versions/y/plain.jar", new string('a', 40), null),
            new Entry("mods/nohash.jar", CdnUrl("nohash.jar"), null, null),
            new Entry("mods/badhash.jar", CdnUrl("badhash.jar"), "aa", null),
            new Entry("mods/badhash512.jar", CdnUrl("badhash512.jar"), null, new string('z', 128)));

        var error = await Assert.ThrowsAsync<ModpackIndexException>(() => Installer().InstallAsync(pack, _instance));

        Assert.Equal(
            new[]
            {
                ModpackIndexProblemKind.DuplicatePath,
                ModpackIndexProblemKind.HostNotAllowed,
                ModpackIndexProblemKind.HashMissing,
                ModpackIndexProblemKind.HashMalformed,
                ModpackIndexProblemKind.HashMalformed
            },
            error.Problems.Select(p => p.Kind));

        Assert.Empty(_web.Asked);
    }

    [Fact]
    public async Task A_sound_pack_installs_as_before()
    {
        var pack = Pack(Good("a.jar", 1), Good("b.jar", 2));

        var result = await Installer().InstallAsync(pack, _instance);

        Assert.Equal(2, result.InstalledFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.Empty(result.AcceptedByModrinthHash);
        Assert.True(File.Exists(Path.Combine(_instance, "mods", "a.jar")));
        Assert.DoesNotContain(_web.Asked, u => u.Contains("api.modrinth.com", StringComparison.Ordinal));
    }

    // ---------- a hash in the pack that matches nothing ----------

    [Fact]
    public async Task A_wrong_hash_is_forgiven_when_the_bytes_match_what_modrinth_publishes_for_that_file()
    {
        var bytes = Encoding.UTF8.GetBytes("the real sodium");
        _web.Serve(CdnUrl("sodium.jar"), bytes);
        ModrinthPublishes("sodium.jar", bytes);

        var pack = Pack(
            Good("a.jar", 1),
            new Entry("mods/sodium.jar", CdnUrl("sodium.jar"), new string('1', 40), new string('2', 128)));

        var result = await Installer().InstallAsync(pack, _instance);

        Assert.Equal(2, result.InstalledFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.Equal(new[] { "mods/sodium.jar" }, result.AcceptedByModrinthHash);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_instance, "mods", "sodium.jar")));
    }

    [Fact]
    public async Task Bytes_that_do_not_match_modrinth_either_fail_as_before()
    {
        _web.Serve(CdnUrl("sodium.jar"), Encoding.UTF8.GetBytes("something else entirely"));
        ModrinthPublishes("sodium.jar", Encoding.UTF8.GetBytes("the real sodium"));

        var pack = Pack(new Entry("mods/sodium.jar", CdnUrl("sodium.jar"), new string('1', 40), null));

        var result = await Installer().InstallAsync(pack, _instance);

        Assert.Equal(0, result.InstalledFiles);
        Assert.Equal(1, result.FailedFiles);
        Assert.Empty(result.AcceptedByModrinthHash);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("mods/sodium.jar", failure.Path);
        Assert.Contains("hash mismatch", failure.Reason);
        Assert.False(File.Exists(Path.Combine(_instance, "mods", "sodium.jar")));
    }

    [Fact]
    public async Task The_hash_of_another_file_of_the_version_or_of_another_project_is_not_taken()
    {
        var bytes = Encoding.UTF8.GetBytes("the real sodium");
        _web.Serve(CdnUrl("sodium.jar"), bytes);
        var pack = Pack(new Entry("mods/sodium.jar", CdnUrl("sodium.jar"), new string('1', 40), null));

        // The version has a file with these bytes, but under another address.
        ModrinthPublishes("sodium-sources.jar", bytes);
        Assert.Equal(1, (await Installer().InstallAsync(pack, _instance)).FailedFiles);

        // The address names one project, the version belongs to another.
        ModrinthPublishes("sodium.jar", bytes, project: "OtherProj");
        Assert.Equal(1, (await Installer().InstallAsync(pack, _instance)).FailedFiles);

        Assert.False(File.Exists(Path.Combine(_instance, "mods", "sodium.jar")));
    }

    [Fact]
    public async Task Modrinth_is_asked_only_about_its_own_files_and_only_about_a_digest()
    {
        var bytes = Encoding.UTF8.GetBytes("from github");
        _web.Serve("https://github.com/someone/mod/releases/download/v1/mod.jar", bytes);
        ModrinthPublishes("mod.jar", bytes);

        var pack = Pack(
            // A wrong hash, but not a Modrinth address.
            new Entry("mods/mod.jar", "https://github.com/someone/mod/releases/download/v1/mod.jar", new string('1', 40), null),
            // A Modrinth address, but the file is not there at all: not a digest failure.
            new Entry("mods/gone.jar", CdnUrl("gone.jar"), new string('1', 40), null));

        var result = await Installer().InstallAsync(pack, _instance);

        Assert.Equal(2, result.FailedFiles);
        Assert.DoesNotContain(_web.Asked, u => u.Contains("api.modrinth.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_modrinth_or_when_it_does_not_answer_the_file_fails_as_before()
    {
        var bytes = Encoding.UTF8.GetBytes("the real sodium");
        _web.Serve(CdnUrl("sodium.jar"), bytes);
        var pack = Pack(new Entry("mods/sodium.jar", CdnUrl("sodium.jar"), new string('1', 40), null));

        // No answer for the version: 404.
        Assert.Equal(1, (await Installer().InstallAsync(pack, _instance)).FailedFiles);

        ModrinthPublishes("sodium.jar", bytes);
        Assert.Equal(1, (await Installer(withModrinth: false).InstallAsync(pack, _instance)).FailedFiles);
    }

    [Theory]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/versions/Yp8wLY1P/sodium-fabric-0.6.0%2Bmc1.21.1.jar", "AANobbMI", "Yp8wLY1P", "sodium-fabric-0.6.0+mc1.21.1.jar")]
    [InlineData("http://cdn.modrinth.com/data/AANobbMI/versions/Yp8wLY1P/a.jar", null, null, null)]
    [InlineData("https://cdn.modrinth.com.evil.example/data/AANobbMI/versions/Yp8wLY1P/a.jar", null, null, null)]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/versions/Yp8wLY1P/extra/a.jar", null, null, null)]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/Yp8wLY1P/a.jar", null, null, null)]
    [InlineData("https://cdn.modrinth.com/data/AAN..bMI/versions/Yp8wLY1P/a.jar", null, null, null)]
    [InlineData("https://github.com/data/AANobbMI/versions/Yp8wLY1P/a.jar", null, null, null)]
    public void Only_the_one_shape_of_a_modrinth_file_address_is_read(string url, string? project, string? version, string? file)
    {
        var parsed = ModrinthCdnFile.TryParse(url);

        Assert.Equal(project, parsed?.ProjectId);
        Assert.Equal(version, parsed?.VersionId);
        Assert.Equal(file, parsed?.FileName);
    }
}
