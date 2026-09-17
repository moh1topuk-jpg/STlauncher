using System;
using STlauncher.Core.Metadata;
using Xunit;

namespace STlauncher.Core.Tests;

public class MetadataJsonTests
{
    [Theory]
    [InlineData("2000-01-01T00:00:00+00:00")]
    [InlineData("2026-09-16T11:05:43+0000")]
    [InlineData("2013-10-01T00:00:00-07:00")]
    public void ParsesBothMojangAndFabricDateFormats(string timestamp)
    {
        var json = $$"""
        { "id": "test", "mainClass": "Main", "releaseTime": "{{timestamp}}", "time": "{{timestamp}}" }
        """;

        var version = System.Text.Json.JsonSerializer.Deserialize<VersionJson>(json, MetadataJson.Options)!;

        Assert.NotEqual(default, version.ReleaseTime);
        Assert.NotEqual(default, version.Time);
    }

    [Fact]
    public void ParsesFabricProfileTimestamp_WithCompactOffset()
    {
        const string json = """
        { "id": "fabric-loader-0.19.5-1.20.1", "inheritsFrom": "1.20.1",
          "releaseTime": "2026-09-16T11:05:43+0000", "time": "2026-09-16T11:05:43+0000",
          "type": "release", "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient" }
        """;

        var version = System.Text.Json.JsonSerializer.Deserialize<VersionJson>(json, MetadataJson.Options)!;

        Assert.Equal(new DateTimeOffset(2026, 9, 16, 11, 5, 43, TimeSpan.Zero), version.ReleaseTime);
        Assert.Equal("1.20.1", version.InheritsFrom);
    }

    [Fact]
    public void UnparseableDate_DoesNotThrow()
    {
        const string json = """{ "id": "x", "releaseTime": "not-a-date" }""";

        var version = System.Text.Json.JsonSerializer.Deserialize<VersionJson>(json, MetadataJson.Options)!;

        Assert.Equal(default, version.ReleaseTime);
    }
}