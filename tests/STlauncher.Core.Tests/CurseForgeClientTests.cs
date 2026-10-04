using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using STlauncher.Core.Instances;
using STlauncher.Core.Loaders;
using STlauncher.Core.Modpacks;
using STlauncher.Core.Mods;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// CurseForge cannot be called from a test: there is no key. The answers below are written
/// from the documented response shapes (docs.curseforge.com/rest-api), with the fields the
/// client reads and a few it must ignore.
/// </summary>
public class CurseForgeClientTests : IDisposable
{
    private const string Base = "https://mirror.test/cf";

    private static readonly byte[] JarBytes = Encoding.ASCII.GetBytes("a jar, as far as a hash can tell");
    private static readonly string JarSha1 = Convert.ToHexString(SHA1.HashData(JarBytes)).ToLowerInvariant();

    private const string SearchJson = """
        {
          "data": [
            {
              "id": 238222,
              "gameId": 432,
              "name": "Just Enough Items (JEI)",
              "slug": "jei",
              "links": {
                "websiteUrl": "https://www.curseforge.com/minecraft/mc-mods/jei",
                "wikiUrl": null,
                "issuesUrl": "https://github.com/mezz/JustEnoughItems/issues",
                "sourceUrl": "https://github.com/mezz/JustEnoughItems"
              },
              "summary": "View Items and Recipes",
              "status": 4,
              "downloadCount": 350123456,
              "isFeatured": false,
              "primaryCategoryId": 423,
              "categories": [
                { "id": 6, "gameId": 432, "name": "Mods", "slug": "mc-mods", "isClass": true, "classId": null, "parentCategoryId": null },
                { "id": 423, "gameId": 432, "name": "Map and Information", "slug": "map-information", "isClass": false, "classId": 6, "parentCategoryId": 6 },
                { "id": 421, "gameId": 432, "name": "API and Library", "slug": "library-api", "isClass": false, "classId": 6, "parentCategoryId": 6 }
              ],
              "classId": 6,
              "authors": [ { "id": 17072262, "name": "mezz", "url": "https://www.curseforge.com/members/mezz" } ],
              "logo": {
                "id": 29069,
                "modId": 238222,
                "title": "logo.jpeg",
                "description": "",
                "thumbnailUrl": "https://media.forgecdn.net/avatars/thumbnails/29/69/256/256/logo.jpeg",
                "url": "https://media.forgecdn.net/avatars/29/69/logo.jpeg"
              },
              "screenshots": [
                {
                  "id": 31417,
                  "modId": 238222,
                  "title": "Recipes",
                  "description": "",
                  "thumbnailUrl": "https://media.forgecdn.net/attachments/thumbnails/31/417/310/172/recipes.png",
                  "url": "https://media.forgecdn.net/attachments/31/417/recipes.png"
                }
              ],
              "mainFileId": 5846880,
              "latestFiles": [],
              "latestFilesIndexes": [
                { "gameVersion": "1.21.1", "fileId": 5846880, "filename": "jei-1.21.1-fabric-19.21.0.247.jar", "releaseType": 1, "gameVersionTypeId": 77784, "modLoader": 4 }
              ],
              "dateCreated": "2015-11-23T22:55:58.84Z",
              "dateModified": "2024-10-25T07:05:38.3Z",
              "dateReleased": "2024-10-25T06:51:46.273Z",
              "allowModDistribution": true,
              "gamePopularityRank": 3,
              "isAvailable": true,
              "thumbsUpCount": 0
            },
            {
              "id": 900001,
              "gameId": 432,
              "name": "Closed Mod",
              "slug": "closed-mod",
              "links": { "websiteUrl": "https://evil.example.com/closed-mod" },
              "summary": "Its author switched off third-party downloads",
              "downloadCount": 1234567.0,
              "categories": [],
              "classId": 6,
              "authors": [],
              "logo": null,
              "screenshots": [],
              "allowModDistribution": false
            }
          ],
          "pagination": { "index": 20, "pageSize": 20, "resultCount": 2, "totalCount": 15234 }
        }
        """;

    private static string FilesJson => """
        {
          "data": [
            {
              "id": 5800000,
              "gameId": 432,
              "modId": 238222,
              "isAvailable": true,
              "displayName": "jei-1.21.1-fabric-19.20.0.1.jar",
              "fileName": "jei-1.21.1-fabric-19.20.0.1.jar",
              "releaseType": 2,
              "fileStatus": 4,
              "hashes": [ { "value": "1111111111111111111111111111111111111111", "algo": 1 }, { "value": "22222222222222222222222222222222", "algo": 2 } ],
              "fileDate": "2024-09-01T10:00:00.000Z",
              "fileLength": 1200000,
              "downloadCount": 1000,
              "downloadUrl": "https://edge.forgecdn.net/files/5800/0/jei-1.21.1-fabric-19.20.0.1.jar",
              "gameVersions": [ "1.21.1", "Fabric" ],
              "dependencies": [],
              "isServerPack": false,
              "fileFingerprint": 111
            },
            {
              "id": 5846880,
              "gameId": 432,
              "modId": 238222,
              "isAvailable": true,
              "displayName": "jei-1.21.1-fabric-19.21.0.247.jar",
              "fileName": "jei-1.21.1-fabric-19.21.0.247.jar",
              "releaseType": 1,
              "fileStatus": 4,
              "hashes": [ { "value": "22222222222222222222222222222222", "algo": 2 }, { "value": "SHA1HERE", "algo": 1 } ],
              "fileDate": "2024-10-25T06:51:46.273Z",
              "fileLength": JARLENGTH,
              "downloadCount": 99000,
              "downloadUrl": "https://edge.forgecdn.net/files/5846/880/jei-1.21.1-fabric-19.21.0.247.jar",
              "gameVersions": [ "Fabric", "1.21.1", "Client", "Server", "Java 21" ],
              "sortableGameVersions": [],
              "dependencies": [ { "modId": 306612, "relationType": 3 }, { "modId": 250398, "relationType": 2 } ],
              "alternateFileId": 0,
              "isServerPack": false,
              "fileFingerprint": 2070800629,
              "modules": [ { "name": "META-INF", "fingerprint": 1 } ]
            },
            {
              "id": 5900001,
              "modId": 238222,
              "isAvailable": true,
              "displayName": "Closed 2.0",
              "fileName": "closed-2.0.jar",
              "releaseType": 1,
              "hashes": [ { "value": "3333333333333333333333333333333333333333", "algo": 1 } ],
              "fileDate": "2024-11-01T00:00:00Z",
              "fileLength": 500,
              "downloadUrl": null,
              "gameVersions": [ "1.21.1", "NeoForge" ],
              "dependencies": []
            },
            {
              "id": 5900002,
              "modId": 238222,
              "isAvailable": true,
              "displayName": "Elsewhere",
              "fileName": "elsewhere.jar",
              "releaseType": 1,
              "hashes": [ { "value": "4444444444444444444444444444444444444444", "algo": 1 } ],
              "fileDate": "2024-08-01T00:00:00Z",
              "fileLength": 500,
              "downloadUrl": "https://evil.example.com/files/elsewhere.jar",
              "gameVersions": [ "1.21.1", "Fabric" ],
              "dependencies": []
            },
            {
              "id": 5900003,
              "modId": 238222,
              "isAvailable": true,
              "displayName": "Climber",
              "fileName": "../climber.jar",
              "releaseType": 1,
              "hashes": [ { "value": "5555555555555555555555555555555555555555", "algo": 1 } ],
              "fileDate": "2024-07-01T00:00:00Z",
              "fileLength": 500,
              "downloadUrl": "https://edge.forgecdn.net/files/5900/3/climber.jar",
              "gameVersions": [ "1.21.1", "Fabric" ],
              "dependencies": []
            },
            {
              "id": 5900004,
              "modId": 238222,
              "isAvailable": true,
              "displayName": "Server pack",
              "fileName": "server-pack.zip",
              "releaseType": 1,
              "hashes": [],
              "fileDate": "2024-12-01T00:00:00Z",
              "fileLength": 500,
              "downloadUrl": "https://edge.forgecdn.net/files/5900/4/server-pack.zip",
              "gameVersions": [ "1.21.1" ],
              "dependencies": [],
              "isServerPack": true
            }
          ],
          "pagination": { "index": 0, "pageSize": 50, "resultCount": 6, "totalCount": 6 }
        }
        """.Replace("SHA1HERE", JarSha1).Replace("JARLENGTH", JarBytes.Length.ToString());

    private const string CategoriesJson = """
        {
          "data": [
            { "id": 6, "gameId": 432, "name": "Mods", "slug": "mc-mods", "isClass": true, "classId": null, "parentCategoryId": null },
            { "id": 412, "gameId": 432, "name": "Technology", "slug": "technology", "isClass": false, "classId": 6, "parentCategoryId": 6 },
            { "id": 417, "gameId": 432, "name": "Energy", "slug": "technology-energy", "isClass": false, "classId": 6, "parentCategoryId": 412 },
            { "id": 434, "gameId": 432, "name": "Armor, Tools, and Weapons", "slug": "armor-weapons-tools", "isClass": false, "classId": 6, "parentCategoryId": 6 }
          ]
        }
        """;

    /// <summary>Answers by address and remembers what was asked.</summary>
    private sealed class Mirror : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        public List<string> Urls => Requests.Select(r => r.RequestUri!.ToString()).ToList();

        public Func<HttpRequestMessage, HttpResponseMessage?> Answer { get; set; } = _ => null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Answer(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") });
        }

        public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent(status.ToString()) };
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    private static (CurseForgeClient Client, Mirror Mirror) Build(Func<HttpRequestMessage, HttpResponseMessage?>? answer = null)
    {
        var mirror = new Mirror();

        if (answer is not null)
        {
            mirror.Answer = answer;
        }

        var http = new HttpClient(mirror);
        return (new CurseForgeClient(http, new DownloadClient(http, maxAttempts: 1)) { BaseUrl = Base + "/" }, mirror);
    }

    private static HttpResponseMessage? Standard(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;

        return path switch
        {
            "/cf/v1/mods/search" => Mirror.Json(SearchJson),
            "/cf/v1/mods/238222/files" => Mirror.Json(FilesJson),
            "/cf/v1/categories" => Mirror.Json(CategoriesJson),
            "/cf/v1/mods/238222/files/5900001/download-url" => Mirror.Status(HttpStatusCode.Forbidden),
            "/files/5846/880/jei-1.21.1-fabric-19.21.0.247.jar" when request.RequestUri.Host == "edge.forgecdn.net"
                => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(JarBytes) },
            _ => null
        };
    }

    // ===================== Availability =====================

    [Fact]
    public async Task The_source_is_there_once_the_mirror_says_it_has_a_key_and_is_asked_only_once()
    {
        var (client, mirror) = Build(_ => Mirror.Status(HttpStatusCode.OK));

        Assert.True(await client.IsAvailableAsync());
        Assert.True(await client.IsAvailableAsync());

        Assert.Equal(new[] { Base + "/ping" }, mirror.Urls);
        Assert.True(mirror.Requests[0].Headers.Contains("X-STlauncher"));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_mirror_without_a_key_keeps_the_source_hidden_for_the_session(HttpStatusCode status)
    {
        var (client, mirror) = Build(_ => Mirror.Status(status));

        Assert.False(await client.IsAvailableAsync());
        Assert.False(await client.IsAvailableAsync());
        Assert.Single(mirror.Requests);
    }

    [Fact]
    public async Task A_mirror_that_could_not_be_reached_is_asked_again()
    {
        var calls = 0;
        var (client, _) = Build(_ => ++calls == 1 ? throw new HttpRequestException("offline") : Mirror.Status(HttpStatusCode.OK));

        Assert.False(await client.IsAvailableAsync());
        Assert.True(await client.IsAvailableAsync());
    }

    [Fact]
    public async Task No_address_means_no_source_and_no_request()
    {
        var (client, mirror) = Build(_ => Mirror.Status(HttpStatusCode.OK));
        client.BaseUrl = CurseForgeClient.ResolveBaseUrl(string.Empty, Base);

        Assert.False(await client.IsAvailableAsync());
        Assert.Empty(mirror.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync("jei", "1.21.1", LoaderKind.Fabric));

        Assert.Equal(Base, CurseForgeClient.ResolveBaseUrl(null, Base));
        Assert.Equal("https://other.test/cf", CurseForgeClient.ResolveBaseUrl(" https://other.test/cf ", Base));
    }

    // ===================== Search =====================

    [Fact]
    public async Task Search_asks_for_minecraft_mods_of_the_build_and_reads_the_hits()
    {
        var (client, mirror) = Build(Standard);

        var page = await client.SearchAsync("just enough", "1.21.1", LoaderKind.Fabric, sort: "downloads", limit: 20, offset: 20);

        var asked = mirror.Requests.Single();
        Assert.StartsWith(Base + "/v1/mods/search?", asked.RequestUri!.ToString());
        Assert.True(asked.Headers.Contains("X-STlauncher"));

        var query = asked.RequestUri.Query;
        Assert.Contains("gameId=432", query);
        Assert.Contains("classId=6", query);
        Assert.Contains("gameVersion=1.21.1", query);
        Assert.Contains("modLoaderType=4", query);
        Assert.Contains("searchFilter=just%20enough", query);
        Assert.Contains("sortField=6", query);
        Assert.Contains("index=20", query);
        Assert.Contains("pageSize=20", query);

        // The API does not page past ten thousand, so neither does the browser.
        Assert.Equal(10_000, page.TotalHits);
        Assert.Equal(2, page.Items.Count);

        var jei = page.Items[0];
        Assert.Equal(ModSource.CurseForge, jei.Source);
        Assert.Equal("238222", jei.ProjectId);
        Assert.Equal("jei", jei.Slug);
        Assert.Equal("Just Enough Items (JEI)", jei.Title);
        Assert.Equal("View Items and Recipes", jei.Description);
        Assert.Equal("mezz", jei.Author);
        Assert.Equal(350123456, jei.Downloads);
        Assert.Equal("https://media.forgecdn.net/avatars/thumbnails/29/69/256/256/logo.jpeg", jei.IconUrl);
        Assert.Equal(new[] { "map-information", "library-api" }, jei.Categories);
        Assert.Equal("https://www.curseforge.com/minecraft/mc-mods/jei", jei.PageUrl);

        // A page address that is not curseforge.com is never passed on to the browser.
        var closed = page.Items[1];
        Assert.Equal(1234567, closed.Downloads);
        Assert.Null(closed.Author);
        Assert.Equal("https://www.curseforge.com/projects/900001", closed.PageUrl);
    }

    [Fact]
    public async Task Packs_are_searched_in_their_own_class_and_without_a_loader()
    {
        var (client, mirror) = Build(Standard);

        await client.SearchAsync(string.Empty, "1.21.1", LoaderKind.Fabric, projectType: ProjectTypes.ResourcePack);
        await client.SearchAsync(string.Empty, "1.21.1", LoaderKind.Fabric, projectType: ProjectTypes.Shader);

        Assert.Contains("classId=12", mirror.Urls[0]);
        Assert.Contains("classId=6552", mirror.Urls[1]);
        Assert.All(mirror.Urls, url => Assert.DoesNotContain("modLoaderType", url));
        Assert.All(mirror.Urls, url => Assert.DoesNotContain("searchFilter", url));
    }

    [Fact]
    public async Task Categories_are_the_top_level_ones_and_search_takes_their_slug_back()
    {
        var (client, mirror) = Build(Standard);

        var categories = await client.GetCategoriesAsync();

        Assert.Equal(new[] { "armor-weapons-tools", "technology" }, categories.Select(c => c.Name));
        Assert.Equal("Armor, Tools, and Weapons", categories[0].Display);

        await client.SearchAsync(string.Empty, "1.21.1", LoaderKind.Forge, category: "technology");
        Assert.Contains("categoryId=412", mirror.Urls.Last());
        Assert.Contains("modLoaderType=1", mirror.Urls.Last());

        // A Modrinth category this source does not have: nothing, rather than everything.
        var before = mirror.Requests.Count;
        var none = await client.SearchAsync(string.Empty, "1.21.1", LoaderKind.Forge, category: "optimization");
        Assert.Empty(none.Items);
        Assert.Equal(before, mirror.Requests.Count);
    }

    // ===================== Files =====================

    [Fact]
    public async Task Files_come_newest_first_in_the_shape_the_browser_installs_from()
    {
        var (client, mirror) = Build(Standard);

        var versions = await client.GetVersionsAsync("238222", "1.21.1", LoaderKind.Fabric);

        Assert.Contains("gameVersion=1.21.1", mirror.Urls.Single());
        Assert.Contains("modLoaderType=4", mirror.Urls.Single());

        // The server pack is not a client file and is left out.
        Assert.Equal(new[] { "5900001", "5846880", "5800000", "5900002", "5900003" }, versions.Select(v => v.Id));

        var release = versions[1];
        Assert.Equal(ModSource.CurseForge, release.Source);
        Assert.Equal("238222", release.ProjectId);
        Assert.Equal("jei-1.21.1-fabric-19.21.0.247", release.VersionNumber);
        Assert.Equal("release", release.VersionType);
        Assert.Equal(new[] { "1.21.1" }, release.GameVersions);
        Assert.Equal(new[] { "fabric" }, release.Loaders);

        var file = release.PrimaryFile!;
        Assert.Equal("jei-1.21.1-fabric-19.21.0.247.jar", file.FileName);
        Assert.Equal("https://edge.forgecdn.net/files/5846/880/jei-1.21.1-fabric-19.21.0.247.jar", file.Url);
        Assert.Equal(JarSha1, file.Sha1);
        Assert.Equal(JarBytes.Length, file.Size);

        // Relation type 3 is the only one that is installed along.
        Assert.Equal(new[] { "306612" }, release.Dependencies.Where(d => d.IsRequired).Select(d => d.ProjectId));
        Assert.Equal(2, release.Dependencies.Count);

        Assert.Equal("beta", versions[2].VersionType);

        // The browser's own picking works on these as on Modrinth's: releases before betas.
        Assert.Equal("5900001", ModrinthClient.SelectPreferred(versions)!.Id);
        Assert.Same(file, ModrinthClient.SelectFile(release, "1.21.1", LoaderKind.Fabric));
    }

    [Fact]
    public async Task A_file_closed_to_third_parties_is_blocked_with_a_page_to_open_instead()
    {
        var (client, mirror) = Build(Standard);

        // The search is what teaches the client the mod's page.
        await client.SearchAsync("jei", "1.21.1", LoaderKind.Fabric);
        var versions = await client.GetVersionsAsync("238222", "1.21.1", LoaderKind.Vanilla);
        Assert.DoesNotContain("modLoaderType", mirror.Urls.Last());

        var closed = versions.Single(v => v.Id == "5900001");
        Assert.Equal(string.Empty, closed.PrimaryFile!.Url);

        var known = CurseForgeClient.CheckDownload(closed);
        Assert.Equal(CurseForgeFileState.Blocked, known.State);
        Assert.Equal("https://www.curseforge.com/minecraft/mc-mods/jei/files/5900001", known.PageUrl);

        // Installing asks CurseForge once more, hears 403, and writes nothing.
        var outcome = await client.InstallAsync(closed, _root, "mods");
        Assert.Equal(CurseForgeFileState.Blocked, outcome.State);
        Assert.Null(outcome.InstalledPath);
        Assert.EndsWith("/v1/mods/238222/files/5900001/download-url", mirror.Urls.Last());
        Assert.False(Directory.Exists(Path.Combine(_root, "mods")));
    }

    [Fact]
    public async Task An_address_outside_the_cdn_is_never_downloaded_from()
    {
        var (client, mirror) = Build(Standard);
        var versions = await client.GetVersionsAsync("238222", "1.21.1", LoaderKind.Fabric);

        var elsewhere = versions.Single(v => v.Id == "5900002");
        Assert.Equal(string.Empty, elsewhere.PrimaryFile!.Url);
        Assert.Equal(CurseForgeFileState.Blocked, (await client.InstallAsync(elsewhere, _root, "mods")).State);

        // A file name that climbs out of the folder is not a file at all.
        var climber = versions.Single(v => v.Id == "5900003");
        Assert.Null(climber.PrimaryFile);
        Assert.Equal(CurseForgeFileState.Unavailable, (await client.InstallAsync(climber, _root, "mods")).State);

        Assert.DoesNotContain(mirror.Urls, url => url.Contains("evil.example.com"));
        Assert.False(Directory.Exists(Path.Combine(_root, "mods")));

        Assert.True(CurseForgeClient.IsCdnUrl("https://mediafilez.forgecdn.net/files/1/2/a.jar"));
        Assert.False(CurseForgeClient.IsCdnUrl("http://edge.forgecdn.net/files/1/2/a.jar"));
        Assert.False(CurseForgeClient.IsCdnUrl("https://edge.forgecdn.net.evil.example.com/a.jar"));
    }

    [Fact]
    public async Task A_ready_file_lands_in_the_build_checked_against_the_hash_from_the_api()
    {
        var (client, _) = Build(Standard);
        var release = (await client.GetVersionsAsync("238222", "1.21.1", LoaderKind.Fabric)).Single(v => v.Id == "5846880");

        var outcome = await client.InstallAsync(release, _root, "mods");

        Assert.Equal(CurseForgeFileState.Ready, outcome.State);
        Assert.Equal(Path.Combine(_root, "mods", "jei-1.21.1-fabric-19.21.0.247.jar"), outcome.InstalledPath);
        Assert.Equal(JarBytes, File.ReadAllBytes(outcome.InstalledPath!));

        var record = CurseForgeClient.RecordFor(release, outcome.File!, "jei", "Just Enough Items (JEI)", null, "mods");
        Assert.Equal(ModSource.CurseForge, record.Source);
        Assert.Equal("jei", record.Id);
        Assert.Equal("238222", record.ProjectId);
        Assert.Equal("5846880", record.FileId);
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_hash_is_refused()
    {
        var (client, _) = Build(request => request.RequestUri!.Host == "edge.forgecdn.net"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("something else entirely!!!!!!!!!")) }
            : Standard(request));

        var release = (await client.GetVersionsAsync("238222", "1.21.1", LoaderKind.Fabric)).Single(v => v.Id == "5846880");

        await Assert.ThrowsAsync<IOException>(() => client.InstallAsync(release, _root, "mods"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "mods")));
    }

    [Fact]
    public async Task The_download_address_call_can_open_a_file_the_list_left_without_one()
    {
        var (client, _) = Build(request => request.RequestUri!.AbsolutePath.EndsWith("/download-url")
            ? Mirror.Json("""{ "data": "https://mediafilez.forgecdn.net/files/5900/1/closed-2.0.jar" }""")
            : Standard(request));

        var closed = (await client.GetVersionsAsync("238222", "1.21.1", LoaderKind.NeoForge)).Single(v => v.Id == "5900001");
        var outcome = await client.ResolveDownloadAsync(closed);

        Assert.Equal(CurseForgeFileState.Ready, outcome.State);
        Assert.Equal("https://mediafilez.forgecdn.net/files/5900/1/closed-2.0.jar", outcome.File!.Url);
    }

    // ===================== Files already in the folder =====================

    [Fact]
    public void The_fingerprint_skips_whitespace_bytes()
    {
        static uint Of(string text) => CurseForgeFingerprint.Compute(new MemoryStream(Encoding.ASCII.GetBytes(text)));

        // MurmurHash2 of nothing with seed 1.
        Assert.Equal(0x5BD15E36u, Of(string.Empty));
        Assert.Equal(0x5BD15E36u, Of(" \t\r\n"));

        Assert.Equal(Of("abcdefg"), Of("ab cd\tef\r\ng"));
        Assert.NotEqual(Of("abcdefg"), Of("abcdefh"));
        Assert.NotEqual(Of("abcd"), Of("abcde"));
    }

    [Fact]
    public async Task Fingerprints_are_matched_in_one_post_and_come_back_as_files()
    {
        string? posted = null;

        var (client, mirror) = Build(request =>
        {
            using var reader = new StreamReader(request.Content!.ReadAsStream());
            posted = reader.ReadToEnd();

            return Mirror.Json("""
                {
                  "data": {
                    "isCacheBuilt": true,
                    "exactMatches": [
                      {
                        "id": 238222,
                        "file": {
                          "id": 5846880,
                          "modId": 238222,
                          "displayName": "jei-1.21.1-fabric-19.21.0.247.jar",
                          "fileName": "jei-1.21.1-fabric-19.21.0.247.jar",
                          "releaseType": 1,
                          "hashes": [ { "value": "1111111111111111111111111111111111111111", "algo": 1 } ],
                          "fileDate": "2024-10-25T06:51:46.273Z",
                          "fileLength": 1300000,
                          "downloadUrl": "https://edge.forgecdn.net/files/5846/880/jei-1.21.1-fabric-19.21.0.247.jar",
                          "gameVersions": [ "1.21.1", "Fabric" ],
                          "dependencies": [],
                          "fileFingerprint": 2070800629
                        },
                        "latestFiles": []
                      }
                    ],
                    "exactFingerprints": [ 2070800629 ],
                    "partialMatches": [],
                    "partialMatchFingerprints": {},
                    "installedFingerprints": [ 2070800629, 7 ],
                    "unmatchedFingerprints": [ 7 ]
                  }
                }
                """);
        });

        var matches = await client.MatchFingerprintsAsync(new uint[] { 2070800629, 7, 7 });

        Assert.Equal(HttpMethod.Post, mirror.Requests.Single().Method);
        Assert.Equal(Base + "/v1/fingerprints/432", mirror.Urls.Single());
        Assert.Equal("""{"fingerprints":[2070800629,7]}""", posted);

        var match = Assert.Single(matches);
        Assert.Equal(2070800629u, match.Key);
        Assert.Equal("238222", match.Value.ProjectId);
        Assert.Equal("5846880", match.Value.Id);
    }

    // ===================== What the rest of the launcher keeps =====================

    [Fact]
    public void A_curseforge_record_round_trips_and_other_records_do_not_change_on_disk()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

        var record = new InstalledModRecord { FileName = "jei.jar", Source = ModSource.CurseForge, Id = "jei", ProjectId = "238222", FileId = "5846880" };
        var json = JsonSerializer.Serialize(record, options);
        var back = JsonSerializer.Deserialize<InstalledModRecord>(json, options)!;

        Assert.Contains("\"source\":\"CurseForge\"", json);
        Assert.Equal(ModSource.CurseForge, back.Source);
        Assert.Equal("238222", back.ProjectId);
        Assert.Equal("5846880", back.FileId);

        var modrinth = JsonSerializer.Serialize(new InstalledModRecord { FileName = "sodium.jar", Source = ModSource.Modrinth, Id = "sodium" }, options);
        Assert.DoesNotContain("projectId", modrinth);
        Assert.DoesNotContain("fileId", modrinth);
    }

    [Fact]
    public void A_build_code_carries_curseforge_files_and_a_modpack_index_does_not()
    {
        const string cdn = "https://edge.forgecdn.net/files/5846/880/jei-1.21.1-fabric-19.21.0.247.jar";
        const string other = "https://mediafilez.forgecdn.net/files/1/2/pack.zip";

        var code = BuildCode.Encode(new BuildCodePayload(
            "With JEI",
            "1.21.1",
            LoaderKind.Fabric,
            "0.16.5",
            new[]
            {
                new BuildCodeFile("mods/jei.jar", cdn, "aa11", 10),
                new BuildCodeFile("resourcepacks/pack.zip", other, "bb22", 20),
                new BuildCodeFile("mods/evil.jar", "https://evil.example.com/evil.jar", "cc33", 30)
            },
            Array.Empty<string>()));

        Assert.True(BuildCode.TryDecode(code, out var back));
        Assert.Equal(new[] { cdn, other }, back!.Files.Select(f => f.Url));

        // The .mrpack format names its hosts, and the CDN is not among them: on export
        // such a file is carried inside the pack instead.
        Assert.True(BuildCode.IsAllowedDownload(cdn));
        Assert.False(ModpackWriter.IsAllowedDownload(cdn));
        Assert.False(ModpackWriter.IsAllowedDownload(other));
    }

    [Fact]
    public void Two_sources_make_one_list_without_showing_a_mod_twice()
    {
        static ModSearchResult Hit(string slug, string title, ModSource source)
            => new(slug, slug, title, string.Empty, null, 0, null) { Source = source };

        var modrinth = new ModSearchPage(
            new[] { Hit("sodium", "Sodium", ModSource.Modrinth), Hit("jei", "Just Enough Items", ModSource.Modrinth) },
            40);

        var curseForge = new ModSearchPage(
            new[]
            {
                Hit("sodium", "Sodium", ModSource.CurseForge),
                Hit("just-enough-items", "Just Enough Items!", ModSource.CurseForge),
                Hit("twilightforest", "The Twilight Forest", ModSource.CurseForge),
                Hit("mekanism", "Mekanism", ModSource.CurseForge)
            },
            900);

        var merged = ModSearch.Merge(modrinth, curseForge);

        Assert.Equal(new[] { "sodium", "twilightforest", "jei", "mekanism" }, merged.Items.Select(i => i.Slug));
        Assert.Equal(ModSource.Modrinth, merged.Items[0].Source);
        Assert.Equal(900, merged.TotalHits);
    }
}
