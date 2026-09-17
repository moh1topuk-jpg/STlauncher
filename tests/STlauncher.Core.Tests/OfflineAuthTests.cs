using System;
using STlauncher.Core.Auth;
using Xunit;

namespace STlauncher.Core.Tests;

public class OfflineAuthTests
{
    [Fact]
    public void ComputeUuid_MatchesJavaOfflineUuid_ForNotch()
    {
        var uuid = OfflineAuth.ComputeUuid("Notch");

        Assert.Equal(Guid.Parse("b50ad385-829d-3141-a216-7e7d7539ba7f"), uuid);
    }

    [Theory]
    [InlineData("Notch", true)]
    [InlineData("Player_1", true)]
    [InlineData("abc", true)]
    [InlineData("ab", false)]
    [InlineData("this_name_is_way_too_long", false)]
    [InlineData("bad name", false)]
    [InlineData("bad-name", false)]
    [InlineData("", false)]
    public void IsValidUsername_ValidatesAccordingToMojangRules(string username, bool expected)
        => Assert.Equal(expected, OfflineAuth.IsValidUsername(username));

    [Fact]
    public void ComputeUuid_IsDeterministic()
    {
        Assert.Equal(OfflineAuth.ComputeUuid("Steve"), OfflineAuth.ComputeUuid("Steve"));
        Assert.NotEqual(OfflineAuth.ComputeUuid("Steve"), OfflineAuth.ComputeUuid("Alex"));
    }

    [Fact]
    public void Login_ProducesVersion3Uuid()
    {
        var account = OfflineAuth.Login("Player");

        Assert.Equal('3', account.Uuid.ToString("D")[14]);
        Assert.Equal(account.Username, account.Username.Trim());
        Assert.False(string.IsNullOrWhiteSpace(account.AccessToken));
    }

    [Fact]
    public void Login_Throws_ForInvalidUsername()
    {
        Assert.Throws<ArgumentException>(() => OfflineAuth.Login("no"));
    }
}