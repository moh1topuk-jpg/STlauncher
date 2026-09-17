using STlauncher.Core.Loaders;
using Xunit;

namespace STlauncher.Core.Tests;

public class LoaderServiceTests
{
    [Theory]
    [InlineData("1.20.1", null)]
    [InlineData("1.20.2", "20.2")]
    [InlineData("1.20.4", "20.4")]
    [InlineData("1.20.6", "20.6")]
    [InlineData("1.21.1", "21.1")]
    [InlineData("1.19.4", "19.4")]
    public void NeoForgePrefix_MapsGameVersionToMavenPrefix(string gameVersion, string? expected)
        => Assert.Equal(expected, LoaderService.NeoForgePrefix(gameVersion));
}