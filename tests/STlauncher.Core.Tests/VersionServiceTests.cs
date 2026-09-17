using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using STlauncher.Core;
using STlauncher.Core.Metadata;
using STlauncher.Core.Versions;
using Xunit;

namespace STlauncher.Core.Tests;

public class VersionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));

    private readonly HttpClient _http = new();

    private VersionService Service()
    {
        var paths = new LauncherPaths(_root);
        paths.EnsureCreated();

        return new VersionService(new MetadataClient(_http, paths), paths);
    }

    private void WriteVersion(string id, string json)
    {
        var directory = Path.Combine(_root, "versions", id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), json);
    }

    [Fact]
    public async Task Resolve_MergesAnInheritedProfile()
    {
        WriteVersion("1.20.1", """
            {
              "id": "1.20.1",
              "type": "release",
              "mainClass": "net.minecraft.client.main.Main",
              "libraries": [ { "name": "base:lib:1" } ]
            }
            """);

        WriteVersion("fabric-1.20.1", """
            {
              "id": "fabric-1.20.1",
              "inheritsFrom": "1.20.1",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [ { "name": "fabric:loader:1" } ]
            }
            """);

        var resolved = await Service().ResolveAsync("fabric-1.20.1");

        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", resolved.MainClass);
        Assert.Equal(2, resolved.Libraries.Count);
    }

    [Fact]
    public async Task Resolve_RejectsAProfileThatInheritsFromItself()
    {
        // Without the visited set this recursed until the stack overflowed, which takes
        // the process down in a way no catch block can reach.
        WriteVersion("loop", """{ "id": "loop", "inheritsFrom": "loop" }""");

        await Assert.ThrowsAsync<InvalidDataException>(() => Service().ResolveAsync("loop"));
    }

    [Fact]
    public async Task Resolve_RejectsAnInheritanceCycle()
    {
        WriteVersion("a", """{ "id": "a", "inheritsFrom": "b" }""");
        WriteVersion("b", """{ "id": "b", "inheritsFrom": "a" }""");

        await Assert.ThrowsAsync<InvalidDataException>(() => Service().ResolveAsync("a"));
    }

    public void Dispose()
    {
        _http.Dispose();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
