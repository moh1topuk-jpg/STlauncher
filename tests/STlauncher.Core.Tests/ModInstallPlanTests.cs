using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// The plan an "Add" is: one resolution that both the panel and the installer go by. The
/// sources here are lists in memory, so every case is exact about what was asked.
/// </summary>
public class ModInstallPlanTests
{
    private const string Game = "1.21.1";

    private sealed class StubSource : IModSource
    {
        private readonly Dictionary<string, ModProject> _projects = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ModVersion>> _versions = new(StringComparer.OrdinalIgnoreCase);

        public StubSource(ModSource source = ModSource.Modrinth)
        {
            Source = source;
        }

        public ModSource Source { get; }

        /// <summary>Project ids whose file list cannot be had right now.</summary>
        public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> VersionRequests { get; } = new();

        /// <summary>A project with one release for the test's game version; returns that version.</summary>
        public ModVersion Add(string slug, string title, params ModDependency[] dependencies)
            => Add(slug, title, ProjectTypes.Mod, dependencies);

        public ModVersion Add(string slug, string title, string projectType, params ModDependency[] dependencies)
        {
            var id = "P-" + slug;
            var project = new ModProject(id, slug, title, string.Empty, null, null, 0, Array.Empty<string>())
            {
                ProjectType = projectType,
                Source = Source
            };

            _projects[id] = project;
            _projects[slug] = project;

            var version = new ModVersion(
                "V-" + slug,
                title,
                "1.0",
                new[] { Game },
                new[] { "fabric" },
                new[] { new ModFile($"https://cdn.test/{slug}.jar", $"{slug}-1.0.jar", null, null, 1000, true) })
            {
                Dependencies = dependencies,
                ProjectId = id,
                Source = Source
            };

            _versions[id] = new List<ModVersion> { version };
            return version;
        }

        public void NoVersions(string slug) => _versions["P-" + slug] = new List<ModVersion>();

        public Task<ModProject?> GetProjectAsync(string idOrSlug, CancellationToken cancellationToken = default)
            => Task.FromResult(_projects.TryGetValue(idOrSlug, out var project) ? project : null);

        public Task<IReadOnlyList<ModVersion>> GetVersionsAsync(string projectIdOrSlug, string? gameVersion, LoaderKind loader, CancellationToken cancellationToken = default)
        {
            VersionRequests.Add(projectIdOrSlug);

            if (Failing.Contains(projectIdOrSlug))
            {
                throw new InvalidOperationException("the site did not answer");
            }

            return Task.FromResult<IReadOnlyList<ModVersion>>(
                _versions.TryGetValue(projectIdOrSlug, out var versions) ? versions : new List<ModVersion>());
        }

        public Task<ModSearchPage> SearchAsync(string query, string? gameVersion, LoaderKind loader, string? category = null, string sort = "relevance", int limit = 20, int offset = 0, CancellationToken cancellationToken = default, string projectType = ProjectTypes.Mod)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ModCategory>> GetCategoriesAsync(string projectType = ProjectTypes.Mod, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ModCategory>>(Array.Empty<ModCategory>());
    }

    private static ModDependency Requires(string slug) => new("P-" + slug, null, "required");

    private static ModDependency GoesWith(string slug) => new("P-" + slug, null, "optional");

    private static ModDependency Refuses(string slug) => new("P-" + slug, null, "incompatible");

    private static ModInstallRequest Request(ModVersion version, string slug, string title)
        => new(version, slug, title, null, ProjectTypes.Mod, Game, LoaderKind.Fabric);

    private static InstalledBuild Build(IEnumerable<InstalledModRecord>? records = null, IEnumerable<BuildJar>? jars = null)
        => new(records ?? Array.Empty<InstalledModRecord>(), jars ?? Array.Empty<BuildJar>(), LoaderKind.Fabric, Game);

    private static BuildJar FabricJar(string fileName, string id, params string[] provides)
        => new(fileName, true, new[]
        {
            new ModMetadata(fileName, id, id, "1.0", LoaderKind.Fabric, Array.Empty<ModDependency2>(), null, provides)
        });

    /// <summary>What the launcher's installer does with a plan: every download becomes a file and a record.</summary>
    private static InstalledBuild Carry(ModInstallPlan plan, List<InstalledModRecord> records)
    {
        foreach (var item in plan.Downloads)
        {
            records.Add(new InstalledModRecord { FileName = item.File!.FileName, Source = item.Source, Id = item.Slug, Name = item.Title });
        }

        return Build(records);
    }

    [Fact]
    public async Task Plan_NamesEveryRequiredModAllTheWayDown_DependenciesFirst()
    {
        var source = new StubSource();
        source.Add("base", "Base Library");
        source.Add("api", "Some API", Requires("base"));
        source.Add("ui", "UI Kit", Requires("api"), Requires("base"));
        source.Add("extra", "Extra");
        source.Add("deep-optional", "Not Ours");
        source.Add("ui-friend", "Friend Of UI", GoesWith("deep-optional"));
        var mod = source.Add("mod", "The Mod", Requires("ui"), Requires("ui-friend"), GoesWith("extra"), new ModDependency("P-embedded", null, "embedded"));

        var plan = await new ModInstallResolver(new[] { source }).ResolveAsync(Request(mod, "mod", "The Mod"), Build());

        Assert.True(plan.IsComplete);
        Assert.Equal(ModPlanState.Install, plan.Root.State);

        // Three levels down, each mod once, and nothing before what it needs.
        Assert.Equal(new[] { "base", "api", "ui", "ui-friend" }, plan.Required.Select(i => i.Slug).ToArray());
        Assert.All(plan.Required, i => Assert.Equal(ModPlanState.Install, i.State));
        Assert.Equal("Some API", plan.Required[0].RequiredBy);

        // What the mod goes well with is named and not brought; what a dependency goes well with is not ours to name.
        Assert.Equal("extra", Assert.Single(plan.Optional).Slug);
        Assert.Equal(ModPlanState.Optional, plan.Optional[0].State);

        Assert.Equal(new[] { "base-1.0.jar", "api-1.0.jar", "ui-1.0.jar", "ui-friend-1.0.jar", "mod-1.0.jar" }, plan.Downloads.Select(i => i.File!.FileName).ToArray());
        Assert.Equal(5000, plan.TotalBytes);
        Assert.True(plan.BringsOthers);
    }

    [Fact]
    public async Task PlanAndInstall_Agree_CarryingThePlanOutLeavesNothingMoreToBring()
    {
        var source = new StubSource();
        source.Add("base", "Base Library");
        source.Add("api", "Some API", Requires("base"));
        var mod = source.Add("mod", "The Mod", Requires("api"));
        var resolver = new ModInstallResolver(new[] { source });

        var plan = await resolver.ResolveAsync(Request(mod, "mod", "The Mod"), Build());

        // The plan holds the very versions and files, so the installer has nothing to look up.
        Assert.All(plan.Downloads, i =>
        {
            Assert.NotNull(i.Version);
            Assert.NotNull(i.File);
            Assert.False(string.IsNullOrEmpty(i.File!.Url));
        });

        var records = new List<InstalledModRecord>();
        var after = Carry(plan, records);

        Assert.Equal(new[] { "base", "api", "mod" }, records.Select(r => r.Id).ToArray());

        // Asked again of the build as it now is: everything is there, only the mod itself would be re-fetched.
        var again = await resolver.ResolveAsync(Request(mod, "mod", "The Mod"), after);

        Assert.False(again.BringsOthers);
        Assert.Equal("api", Assert.Single(again.Required).Slug);
        Assert.Equal(ModPlanState.Satisfied, again.Required[0].State);
        Assert.Equal("api-1.0.jar", again.Required[0].SatisfiedBy);
        Assert.Equal("mod-1.0.jar", Assert.Single(again.Downloads).File!.FileName);
    }

    [Fact]
    public async Task ACircleOfRequirements_Ends()
    {
        var source = new StubSource();
        var mod = source.Add("mod", "The Mod", Requires("a"));
        source.Add("a", "A", Requires("b"));
        source.Add("b", "B", Requires("a"), Requires("mod"), Requires("b"));

        var plan = await new ModInstallResolver(new[] { source }).ResolveAsync(Request(mod, "mod", "The Mod"), Build());

        Assert.True(plan.IsComplete);
        Assert.Equal(new[] { "b", "a" }, plan.Required.Select(i => i.Slug).ToArray());
        Assert.Equal(3, plan.Downloads.Count);
        Assert.Equal(2, source.VersionRequests.Count);
    }

    [Fact]
    public async Task AModAlreadyThere_IsNotBroughtAgain_ByRecordByJarIdOrByProvides()
    {
        var source = new StubSource();
        source.Add("recorded", "Recorded");
        source.Add("ferrite-core", "FerriteCore");
        source.Add("architectury-api", "Architectury");
        source.Add("cloth-config", "Cloth Config");
        source.Add("nested-module", "Nested Module");
        source.Add("same-file", "Same File");
        source.Add("absent", "Absent");
        var mod = source.Add(
            "mod",
            "The Mod",
            Requires("recorded"), Requires("ferrite-core"), Requires("architectury-api"), Requires("cloth-config"),
            Requires("nested-module"), Requires("same-file"), Requires("absent"));

        var build = Build(
            new[] { new InstalledModRecord { FileName = "recorded-0.9.jar.disabled", Source = ModSource.Modrinth, Id = "recorded" } },
            new[]
            {
                // Dropped in by hand: no record, only what the jars say.
                FabricJar("ferritecore-7.0.jar", "ferritecore"),
                FabricJar("architectury-13.jar", "architectury"),
                FabricJar("cloth.jar", "cloth_config_v15", "cloth-config", "cloth-config2"),
                FabricJar("umbrella.jar", "umbrella", "nested_module"),
                new BuildJar("same-file-1.0.jar", true, Array.Empty<ModMetadata>())
            });

        var plan = await new ModInstallResolver(new[] { source }).ResolveAsync(Request(mod, "mod", "The Mod"), build);

        var states = plan.Required.ToDictionary(i => i.Slug, i => (i.State, i.SatisfiedBy));

        Assert.Equal((ModPlanState.Satisfied, "recorded-0.9.jar.disabled"), states["recorded"]);
        Assert.Equal((ModPlanState.Satisfied, "ferritecore-7.0.jar"), states["ferrite-core"]);
        Assert.Equal((ModPlanState.Satisfied, "architectury-13.jar"), states["architectury-api"]);
        Assert.Equal((ModPlanState.Satisfied, "cloth.jar"), states["cloth-config"]);
        Assert.Equal((ModPlanState.Satisfied, "umbrella.jar"), states["nested-module"]);
        Assert.Equal((ModPlanState.Satisfied, "same-file-1.0.jar"), states["same-file"]);
        Assert.Equal(ModPlanState.Install, states["absent"].State);

        Assert.Equal(new[] { "absent-1.0.jar", "mod-1.0.jar" }, plan.Downloads.Select(i => i.File!.FileName).ToArray());

        // What is in the build is not asked about further: only the two that come needed a file list.
        Assert.Equal(new[] { "P-same-file", "P-absent" }, source.VersionRequests.ToArray());
    }

    [Fact]
    public async Task ARequiredModWithNoFileForThisBuild_IsNamedAsMissing_AndWhatItNeedsIsNotBrought()
    {
        var source = new StubSource();
        source.Add("needs-more", "Needs More", Requires("never"));
        source.Add("never", "Never Asked");
        source.NoVersions("needs-more");
        var mod = source.Add("mod", "The Mod", Requires("needs-more"));

        var plan = await new ModInstallResolver(new[] { source }).ResolveAsync(Request(mod, "mod", "The Mod"), Build());

        Assert.True(plan.IsComplete);

        var missing = Assert.Single(plan.Missing);
        Assert.Equal("Needs More", missing.Title);
        Assert.Equal("The Mod", missing.RequiredBy);
        Assert.Null(missing.File);
        Assert.Equal("mod-1.0.jar", Assert.Single(plan.Downloads).File!.FileName);
    }

    [Fact]
    public async Task AFileListThatCannotBeHad_MakesThePlanIncomplete_NotShorter()
    {
        var source = new StubSource();
        source.Add("flaky", "Flaky");
        source.Add("fine", "Fine");
        source.Failing.Add("P-flaky");
        var mod = source.Add("mod", "The Mod", Requires("flaky"), Requires("fine"));

        var plan = await new ModInstallResolver(new[] { source }).ResolveAsync(Request(mod, "mod", "The Mod"), Build());

        Assert.False(plan.IsComplete);
        Assert.Contains("the site did not answer", plan.Failure);
        Assert.Equal("fine", Assert.Single(plan.Required).Slug);
    }

    [Fact]
    public async Task Incompatible_IsAConflict_WithWhatIsInTheBuildAndWithWhatThePlanBrings()
    {
        var source = new StubSource();
        source.Add("optifine-like", "Old Renderer");
        source.Add("not-here", "Not Here");
        source.Add("helper", "Helper", Refuses("sibling"));
        source.Add("sibling", "Sibling");
        var mod = source.Add("mod", "The Mod", Refuses("optifine-like"), Refuses("not-here"), Requires("helper"), Requires("sibling"));

        var build = Build(new[] { new InstalledModRecord { FileName = "old-renderer.jar", Source = ModSource.Modrinth, Id = "optifine-like" } });
        var plan = await new ModInstallResolver(new[] { source }).ResolveAsync(Request(mod, "mod", "The Mod"), build);

        Assert.Equal(2, plan.Conflicts.Count);

        var withBuild = Assert.Single(plan.Conflicts, c => c.InstalledFile is not null);
        Assert.Equal(("The Mod", "Old Renderer", "old-renderer.jar"), (withBuild.Title, withBuild.With, withBuild.InstalledFile));

        var withinPlan = Assert.Single(plan.Conflicts, c => c.InstalledFile is null);
        Assert.Equal(("Helper", "Sibling"), (withinPlan.Title, withinPlan.With));

        // A conflict is told, not acted on: the plan still brings what was asked.
        Assert.Equal(3, plan.Downloads.Count);
    }

    [Fact]
    public async Task ABlockedFile_IsInThePlanAsBlocked_AndWhatItNeedsStillComes()
    {
        var source = new StubSource(ModSource.CurseForge);
        source.Add("lib", "Library");
        var closed = source.Add("closed", "Closed To Programs", Requires("lib"));
        var mod = source.Add("mod", "The Mod", Requires("closed"));

        var resolver = new ModInstallResolver(new[] { source }, (version, _) => Task.FromResult(ReferenceEquals(version, closed)));
        var plan = await resolver.ResolveAsync(Request(mod, "mod", "The Mod") with { }, Build());

        Assert.Equal("closed", Assert.Single(plan.Blocked).Slug);
        Assert.Equal(new[] { "lib-1.0.jar", "mod-1.0.jar" }, plan.Downloads.Select(i => i.File!.FileName).ToArray());
        Assert.All(plan.Required, i => Assert.Equal(ModSource.CurseForge, i.Source));

        // The mod itself closed: the plan says so before anything is written.
        var rootBlocked = await new ModInstallResolver(new[] { source }, (_, _) => Task.FromResult(true))
            .ResolveAsync(Request(mod, "mod", "The Mod"), Build());

        Assert.Equal(ModPlanState.Blocked, rootBlocked.Root.State);
        Assert.Empty(rootBlocked.Downloads);
    }

    [Fact]
    public async Task AVersionWithNoFile_HasNothingToPlan()
    {
        var source = new StubSource();
        source.Add("lib", "Library");
        var empty = new ModVersion("V", "Empty", "1.0", new[] { Game }, new[] { "fabric" }, Array.Empty<ModFile>())
        {
            Dependencies = new[] { Requires("lib") },
            ProjectId = "P-mod"
        };

        var plan = await new ModInstallResolver(new[] { source }).ResolveAsync(Request(empty, "mod", "The Mod"), Build());

        Assert.Equal(ModPlanState.Unavailable, plan.Root.State);
        Assert.Empty(plan.Required);
        Assert.Empty(plan.Downloads);
    }

    [Fact]
    public async Task APinnedDependencyVersion_IsTheOnePicked_AndAShadersModGoesByTheLoader()
    {
        var source = new StubSource();
        var iris = source.Add("iris", "Iris");
        var shader = source.Add("shader", "A Shader", ProjectTypes.Shader, new ModDependency("P-iris", iris.Id, "required"));

        var plan = await new ModInstallResolver(new[] { source })
            .ResolveAsync(new ModInstallRequest(shader, "shader", "A Shader", null, ProjectTypes.Shader, Game, LoaderKind.Fabric), Build());

        var dependency = Assert.Single(plan.Required);
        Assert.Same(iris, dependency.Version);

        // The folder follows each item's own kind: the shader is a pack, what it needs is a mod.
        Assert.Equal(ProjectTypes.Mod, dependency.ProjectType);
        Assert.Equal(ProjectTypes.Shader, plan.Root.ProjectType);
    }
}
