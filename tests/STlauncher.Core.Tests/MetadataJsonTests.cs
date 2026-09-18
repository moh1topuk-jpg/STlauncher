using System;
using System.Text.Json;
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
public class TolerantNumberTests
{
    /// <summary>
    /// Whole numbers written with a fractional part. TLauncher does this, and it used to
    /// take the entire profile down: no parse, no resolve, no launch.
    /// </summary>
    [Fact]
    public void Parse_AcceptsWholeNumbersWrittenAsDecimals()
    {
        var json = JsonSerializer.Deserialize<VersionJson>("""
            {
              "id": "OptiFine 1.21.11",
              "mainClass": "net.minecraft.launchwrapper.Launch",
              "complianceLevel": 1.0,
              "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21.0 }
            }
            """, MetadataJson.Options);

        Assert.NotNull(json);
        Assert.Equal(1, json!.ComplianceLevel);
        Assert.Equal(21, json.JavaVersion?.MajorVersion);
    }

    [Theory]
    [InlineData("17", 17)]
    [InlineData("17.0", 17)]
    [InlineData("", 0)]
    [InlineData("не число", 0)]
    public void Parse_AcceptsNumbersWrittenAsStrings(string raw, int expected)
    {
        var json = JsonSerializer.Deserialize<VersionJson>(
            $$"""{ "id": "x", "javaVersion": { "majorVersion": "{{raw}}" } }""",
            MetadataJson.Options);

        Assert.Equal(expected, json!.JavaVersion?.MajorVersion);
    }

    [Fact]
    public void RequiredJavaMajor_FallsBackWhenTheProfileStatesNothingUsable()
    {
        var stated = ResolvedVersion.FromLeaf(new VersionJson
        {
            Id = "x",
            JavaVersion = new JavaVersionRef { MajorVersion = 21 }
        });

        // 0 is what an unreadable or missing value parses to; asking for "Java 0" would
        // send the runtime downloader after a version that does not exist.
        var unstated = ResolvedVersion.FromLeaf(new VersionJson
        {
            Id = "x",
            JavaVersion = new JavaVersionRef { MajorVersion = 0 }
        });

        var missing = ResolvedVersion.FromLeaf(new VersionJson { Id = "x" });

        Assert.Equal(21, stated.RequiredJavaMajor);
        Assert.Equal(8, unstated.RequiredJavaMajor);
        Assert.Equal(8, missing.RequiredJavaMajor);
    }
}
