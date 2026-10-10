using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// Jars the player dropped in, told apart in two passes: Modrinth by SHA-1 for all of
/// them, CurseForge by fingerprint for the rest. Both sites are stubs that count what
/// they were asked.
/// </summary>
public class ModFileRecognizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));

    public ModFileRecognizerTests()
    {
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

    private UnidentifiedFile File_(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
        return new UnidentifiedFile(name, path);
    }

    private static string Sha1(UnidentifiedFile file) => ModManager.TryComputeSha1(file.Path)!;

    private static uint Print(UnidentifiedFile file) => CurseForgeFingerprint.TryComputeFile(file.Path)!.Value;

    private static ModVersion Version(string id, string projectId, string number, ModSource source = ModSource.Modrinth)
        => new(id, number, number, new[] { "1.21.1" }, new[] { "fabric" }, new[] { new ModFile("https://cdn.test/x.jar", "published-name.jar", null, null, 10, true) })
        {
            ProjectId = projectId,
            Source = source
        };

    private sealed class Site : IModSource, IModHashLookup, IModFingerprintLookup
    {
        public Site(ModSource source)
        {
            Source = source;
        }

        public ModSource Source { get; }

        public Dictionary<string, ModVersion> ByHash { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<uint, ModVersion> ByFingerprint { get; } = new();

        public Dictionary<string, ModProject> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Down { get; set; }

        public List<string> HashesAsked { get; } = new();

        public List<uint> FingerprintsAsked { get; } = new();

        public int HashRequests { get; private set; }

        public int FingerprintRequests { get; private set; }

        public void Project(string id, string slug, string title)
            => Projects[id] = new ModProject(id, slug, title, string.Empty, null, "https://cdn.test/" + slug + ".png", 0, Array.Empty<string>()) { Source = Source };

        public Task<IReadOnlyDictionary<string, ModVersion>> GetVersionsByHashesAsync(IEnumerable<string> sha1Hashes, CancellationToken cancellationToken = default)
        {
            HashRequests++;

            if (Down)
            {
                throw new InvalidOperationException("offline");
            }

            var asked = sha1Hashes.ToList();
            HashesAsked.AddRange(asked);

            return Task.FromResult<IReadOnlyDictionary<string, ModVersion>>(
                asked.Where(ByHash.ContainsKey).ToDictionary(h => h, h => ByHash[h], StringComparer.OrdinalIgnoreCase));
        }

        public Task<IReadOnlyDictionary<uint, ModVersion>> MatchFingerprintsAsync(IEnumerable<uint> fingerprints, CancellationToken cancellationToken = default)
        {
            FingerprintRequests++;

            if (Down)
            {
                throw new InvalidOperationException("offline");
            }

            var asked = fingerprints.ToList();
            FingerprintsAsked.AddRange(asked);

            return Task.FromResult<IReadOnlyDictionary<uint, ModVersion>>(
                asked.Where(ByFingerprint.ContainsKey).ToDictionary(f => f, f => ByFingerprint[f]));
        }

        public Task<ModProject?> GetProjectAsync(string idOrSlug, CancellationToken cancellationToken = default)
            => Task.FromResult(Projects.TryGetValue(idOrSlug, out var project) ? project : null);

        public Task<IReadOnlyList<ModVersion>> GetVersionsAsync(string projectIdOrSlug, string? gameVersion, LoaderKind loader, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ModSearchPage> SearchAsync(string query, string? gameVersion, LoaderKind loader, string? category = null, string sort = "relevance", int limit = 20, int offset = 0, CancellationToken cancellationToken = default, string projectType = ProjectTypes.Mod)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ModCategory>> GetCategoriesAsync(string projectType = ProjectTypes.Mod, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task ModrinthFirstForAll_ThenCurseForgeForWhatIsLeft()
    {
        var onModrinth = File_("sodium-renamed.jar", "sodium bytes");
        var onBoth = File_("jei.jar", "jei bytes");
        var onCurseForge = File_("only-cf.jar.disabled", "curseforge only");
        var nowhere = File_("my-own.jar", "nobody knows this one");

        var modrinth = new Site(ModSource.Modrinth);
        modrinth.ByHash[Sha1(onModrinth)] = Version("mr-v1", "AANobbMI", "0.6.13");
        modrinth.ByHash[Sha1(onBoth)] = Version("mr-v2", "u6dRKJwZ", "19.21.0");
        modrinth.Project("AANobbMI", "sodium", "Sodium");
        modrinth.Project("u6dRKJwZ", "jei", "Just Enough Items");

        var curseForge = new Site(ModSource.CurseForge);
        curseForge.ByFingerprint[Print(onBoth)] = Version("111", "238222", "jei-19.21.0", ModSource.CurseForge);
        curseForge.ByFingerprint[Print(onCurseForge)] = Version("5846880", "999", "Only CF 2.0", ModSource.CurseForge);
        curseForge.Project("238222", "jei", "Just Enough Items (JEI)");
        curseForge.Project("999", "only-cf", "Only On CurseForge");

        var recognizer = new ModFileRecognizer(modrinth, modrinth, curseForge, curseForge);
        var found = await recognizer.IdentifyAsync(new[] { onModrinth, onBoth, onCurseForge, nowhere });

        // One request to each site, and CurseForge is only asked about what Modrinth did not know.
        Assert.Equal(1, modrinth.HashRequests);
        Assert.Equal(4, modrinth.HashesAsked.Count);
        Assert.Equal(1, curseForge.FingerprintRequests);
        Assert.Equal(new[] { Print(onCurseForge), Print(nowhere) }.OrderBy(p => p), curseForge.FingerprintsAsked.OrderBy(p => p));

        Assert.Equal(3, found.Count);
        Assert.DoesNotContain(found, f => f.FileName == "my-own.jar");

        var sodium = found.Single(f => f.FileName == "sodium-renamed.jar").ToRecord(null);
        Assert.Equal(ModSource.Modrinth, sodium.Source);
        Assert.Equal("sodium", sodium.Id);
        Assert.Equal("Sodium", sodium.Name);
        Assert.Equal("0.6.13", sodium.Version);
        Assert.Equal("https://cdn.test/sodium.png", sodium.IconUrl);
        Assert.Equal("mods", sodium.Folder);

        // The record keeps the name the file has on disk, not the one it was published under.
        Assert.Equal("sodium-renamed.jar", sodium.FileName);
        Assert.Null(sodium.ProjectId);
        Assert.Null(sodium.FileId);

        Assert.Equal(ModSource.Modrinth, found.Single(f => f.FileName == "jei.jar").Source);

        var existing = new InstalledModRecord { FileName = "only-cf.jar.disabled", Source = ModSource.Manual, Name = "only-cf", Folder = "mods", DisabledByUser = true };
        var stamped = found.Single(f => f.FileName == "only-cf.jar.disabled").ToRecord(existing);

        Assert.Equal(ModSource.CurseForge, stamped.Source);
        Assert.Equal("only-cf", stamped.Id);
        Assert.Equal("Only On CurseForge", stamped.Name);
        Assert.Equal("999", stamped.ProjectId);
        Assert.Equal("5846880", stamped.FileId);
        Assert.Equal("only-cf.jar.disabled", stamped.FileName);

        // What the player did to the file is still on the record.
        Assert.True(stamped.DisabledByUser);
    }

    [Fact]
    public async Task AnAnswerTheCallerAlreadyHas_SavesTheRequest_AndCurseForgeIsLeftAloneWhenItIsNotThere()
    {
        var known = File_("a.jar", "aaa");
        var unknown = File_("b.jar", "bbb");

        var modrinth = new Site(ModSource.Modrinth);
        modrinth.Project("P1", "a-mod", "A Mod");

        var curseForge = new Site(ModSource.CurseForge);
        curseForge.ByFingerprint[Print(unknown)] = Version("2", "20", "B", ModSource.CurseForge);
        curseForge.Project("20", "b-mod", "B Mod");

        var answers = new Dictionary<string, ModVersion>(StringComparer.OrdinalIgnoreCase) { [Sha1(known)] = Version("v", "P1", "1.0") };
        var recognizer = new ModFileRecognizer(modrinth, modrinth, curseForge, curseForge);

        var found = await recognizer.IdentifyAsync(
            new[] { known with { Sha1 = Sha1(known) }, unknown },
            answers,
            useCurseForge: false);

        Assert.Equal("a-mod", Assert.Single(found).Project.Slug);
        Assert.Equal(0, modrinth.HashRequests);
        Assert.Equal(0, curseForge.FingerprintRequests);

        // With CurseForge there, the same call recognises the second file as well.
        var both = await recognizer.IdentifyAsync(new[] { known, unknown }, answers);
        Assert.Equal(new[] { "a-mod", "b-mod" }, both.Select(f => f.Project.Slug).OrderBy(s => s).ToArray());
    }

    [Fact]
    public async Task WhenASiteCannotBeAsked_NothingIsGuessed()
    {
        var file = File_("a.jar", "aaa");
        var second = File_("b.jar", "bbb");

        var modrinth = new Site(ModSource.Modrinth) { Down = true };
        var curseForge = new Site(ModSource.CurseForge);
        curseForge.ByFingerprint[Print(file)] = Version("2", "20", "A", ModSource.CurseForge);
        curseForge.Project("20", "a-mod", "A Mod");

        var recognizer = new ModFileRecognizer(modrinth, modrinth, curseForge, curseForge);

        // Modrinth is the first opinion: without it a file both sites carry would be filed under CurseForge.
        Assert.Empty(await recognizer.IdentifyAsync(new[] { file, second }));
        Assert.Equal(0, curseForge.FingerprintRequests);

        // CurseForge failing takes nothing from what Modrinth said.
        modrinth.Down = false;
        modrinth.ByHash[Sha1(file)] = Version("v", "P1", "1.0");
        modrinth.Project("P1", "a-mod", "A Mod");
        curseForge.Down = true;

        Assert.Equal("a.jar", Assert.Single(await recognizer.IdentifyAsync(new[] { file, second })).FileName);
    }

    [Fact]
    public async Task AFileWhoseProjectPageCannotBeHad_IsLeftForNextTime()
    {
        var file = File_("a.jar", "aaa");

        var modrinth = new Site(ModSource.Modrinth);
        modrinth.ByHash[Sha1(file)] = Version("v", "gone", "1.0");

        var curseForge = new Site(ModSource.CurseForge);
        curseForge.ByFingerprint[Print(file)] = Version("2", "20", "A", ModSource.CurseForge);
        curseForge.Project("20", "a-mod", "A Mod");

        var found = await new ModFileRecognizer(modrinth, modrinth, curseForge, curseForge).IdentifyAsync(new[] { file });

        // No slug to mark it by yet - and Modrinth does know the file, so it is not CurseForge's to claim.
        Assert.Empty(found);
        Assert.Equal(0, curseForge.FingerprintRequests);
    }

    [Fact]
    public void OnlyFilesWithNoOriginAreAskedAbout()
    {
        Assert.True(ModFileRecognizer.NeedsIdentifying(null));
        Assert.True(ModFileRecognizer.NeedsIdentifying(new InstalledModRecord { FileName = "a.jar", Source = ModSource.Manual, Name = "a" }));

        Assert.False(ModFileRecognizer.NeedsIdentifying(new InstalledModRecord { FileName = "a.jar", Source = ModSource.Modrinth, Id = "a" }));
        Assert.False(ModFileRecognizer.NeedsIdentifying(new InstalledModRecord { FileName = "a.jar", Source = ModSource.CurseForge, Id = "a" }));
        Assert.False(ModFileRecognizer.NeedsIdentifying(new InstalledModRecord { FileName = "a.jar", Source = ModSource.Catalog, Id = "a" }));
        Assert.False(ModFileRecognizer.NeedsIdentifying(new InstalledModRecord { FileName = "a.jar", Source = ModSource.Modpack }));
    }
}
