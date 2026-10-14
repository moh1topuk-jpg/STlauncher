using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using STlauncher.Core.Loaders;
using STlauncher.Core.Modpacks;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// What a build code is shown as before the build exists: the files by kind, their
/// weight, where each comes from - and the codes that are refused instead of shown.
/// </summary>
public class BuildCodePreviewTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static BuildCodeFile Modrinth(string path, long size)
        => new(path, "https://cdn.modrinth.com/data/AANobbMI/versions/abc/" + Path.GetFileName(path), Sha, size);

    private static BuildCodePayload Payload(params BuildCodeFile[] files)
        => new("Showtime plus", "1.21.11", LoaderKind.Fabric, "0.17.2", files, Array.Empty<string>());

    [Fact]
    public void Files_are_grouped_by_the_folder_they_go_into()
    {
        var preview = BuildCodePreview.From(Payload(
            Modrinth("mods/sodium.jar", 1000),
            Modrinth("config/sodium-options.json", 10),
            Modrinth("shaderpacks/Complementary.zip", 300),
            Modrinth("mods/Iris.jar", 2000),
            Modrinth("resourcepacks/Faithful.zip", 500),
            Modrinth("options.txt", 5)));

        Assert.Equal(
            new[] { BuildFileKind.Mod, BuildFileKind.ResourcePack, BuildFileKind.Shader, BuildFileKind.Other },
            preview.Groups.Select(g => g.Kind));

        // By name inside a kind, whatever the case.
        Assert.Equal(new[] { "Iris.jar", "sodium.jar" }, preview.Groups[0].Files.Select(f => f.Name));
        Assert.Equal(new[] { "options.txt", "sodium-options.json" }, preview.Groups[3].Files.Select(f => f.Name));
        Assert.Equal(2, preview.ModCount);
    }

    [Fact]
    public void Sizes_add_up_per_kind_and_overall()
    {
        var preview = BuildCodePreview.From(Payload(
            Modrinth("mods/a.jar", 1000),
            Modrinth("mods/b.jar", 2000),
            Modrinth("resourcepacks/c.zip", 500),
            // A size a code lies about must not take away from the others.
            Modrinth("mods/negative.jar", -700)));

        Assert.Equal(4, preview.FileCount);
        Assert.Equal(3500, preview.TotalSize);
        Assert.Equal(3000, preview.Groups.Single(g => g.Kind == BuildFileKind.Mod).Size);
        Assert.Equal(500, preview.Groups.Single(g => g.Kind == BuildFileKind.ResourcePack).Size);
        Assert.Equal(0, preview.Groups[0].Files.Single(f => f.Name == "negative.jar").Size);
    }

    [Theory]
    [InlineData("mods/a.jar", BuildFileKind.Mod)]
    [InlineData("MODS/a.jar", BuildFileKind.Mod)]
    [InlineData("mods\\a.jar", BuildFileKind.Mod)]
    [InlineData("resourcepacks/a.zip", BuildFileKind.ResourcePack)]
    [InlineData("shaderpacks/a.zip", BuildFileKind.Shader)]
    [InlineData("config/a.toml", BuildFileKind.Other)]
    [InlineData("config/mods/a.jar", BuildFileKind.Other)]
    [InlineData("a.jar", BuildFileKind.Other)]
    [InlineData("", BuildFileKind.Other)]
    public void The_kind_is_told_from_the_first_folder(string path, BuildFileKind expected)
        => Assert.Equal(expected, BuildCodePreview.KindOf(path));

    [Theory]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/versions/abc/sodium.jar", "Modrinth")]
    [InlineData("https://edge.forgecdn.net/files/4567/890/jei.jar", "CurseForge")]
    [InlineData("https://mediafilez.forgecdn.net/files/4567/890/jei.jar", "CurseForge")]
    [InlineData("https://github.com/o/r/releases/download/1/pack.zip", "GitHub")]
    [InlineData("https://raw.githubusercontent.com/o/r/main/pack.zip", "GitHub")]
    [InlineData("https://gitlab.com/o/r/-/raw/main/pack.zip", "GitLab")]
    [InlineData("https://files.example.org/a.jar", "files.example.org")]
    [InlineData("not an address", "")]
    public void The_source_tag_is_told_from_the_host(string url, string expected)
        => Assert.Equal(expected, BuildCodePreview.SourceOf(url));

    [Fact]
    public void A_host_that_only_looks_like_modrinth_is_not_called_modrinth()
    {
        Assert.Equal("cdn.modrinth.com.evil.example", BuildCodePreview.SourceOf("https://cdn.modrinth.com.evil.example/a.jar"));
        Assert.Equal("evilforgecdn.net", BuildCodePreview.SourceOf("https://evilforgecdn.net/a.jar"));
    }

    [Fact]
    public void Missing_files_are_listed_by_name_once()
    {
        var payload = Payload(Modrinth("mods/a.jar", 1)) with
        {
            Missing = new[] { "my-own-mod.jar", "mods/optifine.jar", "MY-OWN-MOD.jar", " ", "line\nbreak.jar" }
        };

        var preview = BuildCodePreview.From(payload);

        Assert.Equal(new[] { "my-own-mod.jar", "optifine.jar", "line break.jar" }, preview.Missing);
    }

    [Theory]
    [InlineData("Showtime plus", "Showtime plus")]
    [InlineData("  two\r\nlines\t here ", "two lines here")]
    [InlineData("", "Shared build")]
    [InlineData(null, "Shared build")]
    public void A_name_from_a_code_is_made_one_clean_line(string? name, string expected)
        => Assert.Equal(expected, BuildCodePreview.CleanName(name));

    [Fact]
    public void A_name_from_a_code_is_cut_to_what_a_row_shows()
        => Assert.Equal(BuildCodePreview.MaxNameLength, BuildCodePreview.CleanName(new string('x', 500)).Length);

    [Fact]
    public void A_build_with_the_same_mods_is_recognised_by_file_names()
    {
        var preview = BuildCodePreview.From(Payload(
            Modrinth("mods/sodium.jar", 1),
            Modrinth("mods/iris.jar", 1),
            Modrinth("resourcepacks/pack.zip", 1)));

        Assert.True(preview.HasSameMods(new[] { "IRIS.jar", "sodium.jar" }));
        Assert.False(preview.HasSameMods(new[] { "sodium.jar" }));
        Assert.False(preview.HasSameMods(new[] { "sodium.jar", "iris.jar", "lithium.jar" }));

        // Two builds without mods are not "the same build".
        Assert.False(BuildCodePreview.From(Payload()).HasSameMods(Array.Empty<string>()));
    }

    // ===================== Reading a code for showing =====================

    [Fact]
    public void A_sound_code_is_ready_to_show()
    {
        var code = BuildCode.Encode(Payload(Modrinth("mods/sodium.jar", 1234), Modrinth("resourcepacks/pack.zip", 99)) with
        {
            Missing = new[] { "my-own-mod.jar" }
        });

        var reading = BuildCodeInspector.Read("держи: " + code);

        Assert.Equal(BuildCodeVerdict.Ready, reading.Verdict);
        Assert.Empty(reading.Problems);
        Assert.Equal(2, reading.Preview!.FileCount);
        Assert.Equal(1333, reading.Preview.TotalSize);
        Assert.Equal(new[] { "my-own-mod.jar" }, reading.Preview.Missing);
        Assert.All(reading.Preview.Groups.SelectMany(g => g.Files), f => Assert.Equal("Modrinth", f.Source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("STB1.!!!")]
    [InlineData("SB-7KQ2M-X9P1A")]
    public void Text_without_a_build_code_is_not_a_code(string text)
        => Assert.Equal(BuildCodeVerdict.NotACode, BuildCodeInspector.Read(text).Verdict);

    [Fact]
    public void A_code_that_names_a_forbidden_host_is_refused_before_any_preview()
    {
        var code = BuildCode.Encode(Payload(
            Modrinth("mods/sodium.jar", 1),
            new BuildCodeFile("mods/evil.jar", "https://evil.example/evil.jar", Sha, 1)));

        var reading = BuildCodeInspector.Read(code);

        Assert.Equal(BuildCodeVerdict.Refused, reading.Verdict);
        Assert.Null(reading.Preview);

        var problem = Assert.Single(reading.Problems);
        Assert.Equal(BuildCodeProblemKind.HostNotAllowed, problem.Kind);
        Assert.Equal("mods/evil.jar", problem.Path);
        Assert.Equal("https://evil.example", problem.Host);
    }

    [Theory]
    [InlineData("../outside.jar")]
    [InlineData("mods/../../outside.jar")]
    [InlineData("C:/Windows/evil.jar")]
    [InlineData("/etc/evil.jar")]
    public void A_code_with_a_path_out_of_the_build_is_refused_before_any_preview(string path)
    {
        var code = BuildCode.Encode(Payload(Modrinth("mods/sodium.jar", 1), Modrinth(path, 1)));

        var reading = BuildCodeInspector.Read(code);

        Assert.Equal(BuildCodeVerdict.Refused, reading.Verdict);
        Assert.Null(reading.Preview);
        Assert.Contains(reading.Problems, p => p.Kind == BuildCodeProblemKind.UnsafePath && p.Path == path);
    }

    [Fact]
    public void A_plain_http_address_is_refused()
    {
        var code = BuildCode.Encode(Payload(new BuildCodeFile("mods/a.jar", "http://cdn.modrinth.com/data/a/versions/b/a.jar", Sha, 1)));

        var reading = BuildCodeInspector.Read(code);

        Assert.Equal(BuildCodeVerdict.Refused, reading.Verdict);
        Assert.Equal(BuildCodeProblemKind.HostNotAllowed, Assert.Single(reading.Problems).Kind);
    }

    /// <summary>
    /// The installer downloads a code's files as a modpack, and a modpack may not name
    /// CurseForge's CDN. Such a code used to make an empty build and then fail; it is
    /// refused while there is still nothing to clean up.
    /// </summary>
    [Fact]
    public void What_the_installer_would_refuse_is_refused_before_any_preview()
    {
        var curse = BuildCode.Encode(Payload(new BuildCodeFile("mods/jei.jar", "https://edge.forgecdn.net/files/4567/890/jei.jar", Sha, 1)));
        var badHash = BuildCode.Encode(Payload(new BuildCodeFile("mods/a.jar", "https://cdn.modrinth.com/data/a/versions/b/a.jar", "aa11", 1)));
        var twice = BuildCode.Encode(Payload(Modrinth("mods/a.jar", 1), Modrinth("mods/a.jar", 1)));

        Assert.Equal(BuildCodeProblemKind.HostNotAllowed, Assert.Single(BuildCodeInspector.Read(curse).Problems).Kind);
        Assert.Equal(BuildCodeProblemKind.BadHash, Assert.Single(BuildCodeInspector.Read(badHash).Problems).Kind);
        Assert.Equal(BuildCodeProblemKind.DuplicatePath, Assert.Single(BuildCodeInspector.Read(twice).Problems).Kind);

        // And it is the installer's own check that says so.
        foreach (var code in new[] { curse, badHash, twice })
        {
            Assert.True(BuildCode.TryDecode(code, out var payload));
            Assert.NotEmpty(ModpackIndexCheck.Check(new ModpackPlan(
                payload!.Name, "1.0.0", payload.GameVersion, payload.Loader, payload.LoaderVersion,
                payload.Files.Select(f => new ModpackFilePlan(f.Path, f.Url, f.Sha1, null, f.Size)).ToList(), false)));
        }
    }

    [Fact]
    public void The_plain_reading_still_drops_what_it_dropped_and_now_says_what()
    {
        var code = BuildCode.Encode(Payload(
            Modrinth("mods/sodium.jar", 1),
            new BuildCodeFile("mods/evil.jar", "https://evil.example/evil.jar", Sha, 1),
            Modrinth("../x.jar", 1)));

        Assert.True(BuildCode.TryDecode(code, out var lenient));
        Assert.Single(lenient!.Files);

        Assert.True(BuildCode.TryDecode(code, out var payload, out var rejected));
        Assert.Single(payload!.Files);
        Assert.Equal(
            new[] { BuildCodeRejection.HostNotAllowed, BuildCodeRejection.UnsafePath },
            rejected.Select(r => r.Reason));
    }

    [Fact]
    public void Three_hundred_files_are_read_and_grouped_at_once()
    {
        var files = Enumerable.Range(0, 300)
            .Select(i => new BuildCodeFile(
                (i % 10 == 0 ? "resourcepacks/pack-" : "mods/some-mod-") + i.ToString("D3") + (i % 10 == 0 ? ".zip" : ".jar"),
                $"https://cdn.modrinth.com/data/P{i:D7}/versions/V{i:D7}/file-{i}.jar",
                Sha,
                1_000_000 + i))
            .ToArray();

        var code = BuildCode.Encode(Payload(files) with { Missing = Enumerable.Range(0, 40).Select(i => $"own-{i}.jar").ToArray() });

        var clock = Stopwatch.StartNew();
        var reading = BuildCodeInspector.Read(code);
        clock.Stop();

        Assert.Equal(BuildCodeVerdict.Ready, reading.Verdict);

        var preview = reading.Preview!;

        Assert.Equal(300, preview.FileCount);
        Assert.Equal(270, preview.Groups.Single(g => g.Kind == BuildFileKind.Mod).Files.Count);
        Assert.Equal(30, preview.Groups.Single(g => g.Kind == BuildFileKind.ResourcePack).Files.Count);
        Assert.Equal(files.Sum(f => f.Size), preview.TotalSize);
        Assert.Equal(40, preview.Missing.Count);
        Assert.Equal(270, preview.ModCount);

        // Generous: the point is that this is nothing a button press has to wait for.
        Assert.True(clock.ElapsedMilliseconds < 2000, $"reading took {clock.ElapsedMilliseconds} ms");
    }

    // ===================== What else the build needs =====================

    [Fact]
    public void An_empty_launcher_folder_has_neither_the_game_nor_the_loader()
    {
        using var folder = new Scratch();

        var found = BuildPrerequisiteProbe.Probe(folder.Paths, "1.21.11", LoaderKind.Fabric, "0.17.2");

        Assert.Equal(Presence.Absent, found.Game);
        Assert.Equal(Presence.Absent, found.Loader);
        Assert.Null(found.JavaMajor);
        Assert.Equal(Presence.Unknown, found.Java);
    }

    [Fact]
    public void The_game_and_a_fabric_profile_on_disk_are_found()
    {
        using var folder = new Scratch();

        folder.Write(folder.Paths.VersionJsonPath("1.21.11"), "{\"id\":\"1.21.11\",\"javaVersion\":{\"component\":\"java-runtime-delta\",\"majorVersion\":21}}");
        folder.Write(folder.Paths.VersionJarPath("1.21.11"), "jar");
        folder.Write(folder.Paths.VersionJsonPath("fabric-loader-0.17.2-1.21.11"), "{}");

        var found = BuildPrerequisiteProbe.Probe(folder.Paths, "1.21.11", LoaderKind.Fabric, "0.17.2");

        Assert.Equal(Presence.Present, found.Game);
        Assert.Equal(Presence.Present, found.Loader);
        Assert.Equal(21, found.JavaMajor);

        // No runtime of the launcher's own: a Java elsewhere may still do, so nothing is claimed.
        Assert.Equal(Presence.Unknown, found.Java);

        Assert.Equal(Presence.Absent, BuildPrerequisiteProbe.Probe(folder.Paths, "1.21.11", LoaderKind.Fabric, "0.16.0").Loader);
    }

    [Fact]
    public void The_game_without_its_jar_is_not_there_yet()
    {
        using var folder = new Scratch();

        folder.Write(folder.Paths.VersionJsonPath("1.20.1"), "{\"javaVersion\":{\"majorVersion\":17}}");

        var found = BuildPrerequisiteProbe.Probe(folder.Paths, "1.20.1", LoaderKind.Vanilla, null);

        Assert.Equal(Presence.Absent, found.Game);
        Assert.Equal(17, found.JavaMajor);
    }

    [Theory]
    [InlineData(LoaderKind.Forge, "47.2.0")]
    [InlineData(LoaderKind.NeoForge, "21.1.77")]
    [InlineData(LoaderKind.Fabric, null)]
    [InlineData(LoaderKind.Vanilla, null)]
    public void Where_the_folders_cannot_tell_nothing_is_claimed(LoaderKind loader, string? loaderVersion)
    {
        using var folder = new Scratch();

        Assert.Equal(Presence.Unknown, BuildPrerequisiteProbe.Probe(folder.Paths, "1.20.1", loader, loaderVersion).Loader);
    }

    [Theory]
    [InlineData("../../outside")]
    [InlineData("1.21/../..")]
    [InlineData("")]
    public void A_version_that_is_not_a_plain_name_is_not_looked_for(string version)
    {
        using var folder = new Scratch();

        var found = BuildPrerequisiteProbe.Probe(folder.Paths, version, LoaderKind.Fabric, "0.17.2");

        Assert.Equal(Presence.Unknown, found.Game);
        Assert.Equal(Presence.Unknown, found.Loader);
    }

    [Fact]
    public void A_broken_version_file_tells_nothing_about_java()
    {
        using var folder = new Scratch();

        folder.Write(folder.Paths.VersionJsonPath("1.21.11"), "{ not json");
        folder.Write(folder.Paths.VersionJarPath("1.21.11"), "jar");

        var found = BuildPrerequisiteProbe.Probe(folder.Paths, "1.21.11", LoaderKind.Vanilla, null);

        Assert.Equal(Presence.Present, found.Game);
        Assert.Null(found.JavaMajor);
        Assert.Equal(Presence.Unknown, found.Java);
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stl-preview-" + Guid.NewGuid().ToString("N"));

        public Scratch()
        {
            Directory.CreateDirectory(_root);
            Paths = new LauncherPaths(_root);
        }

        public LauncherPaths Paths { get; }

        public void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
