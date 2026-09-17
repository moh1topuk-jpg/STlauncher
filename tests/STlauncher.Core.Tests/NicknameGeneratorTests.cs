using System;
using System.Collections.Generic;
using System.Linq;
using STlauncher.Core.Auth;
using Xunit;

namespace STlauncher.Core.Tests;

public class NicknameGeneratorTests
{
    [Fact]
    public void Next_AlwaysProducesAUsableName()
    {
        // The generated name goes straight into the Play button's validation, so an
        // invalid one would block the launcher on first run.
        for (var i = 0; i < 200; i++)
        {
            var nickname = NicknameGenerator.Next();

            Assert.True(
                OfflineAuth.IsValidUsername(nickname),
                $"'{nickname}' is not a valid Minecraft nickname");
        }
    }

    [Fact]
    public void Next_VariesBetweenCalls()
    {
        var names = Enumerable.Range(0, 50).Select(_ => NicknameGenerator.Next()).ToHashSet();

        Assert.True(names.Count > 1, "the generator returned the same name every time");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Player", true)]
    [InlineData("player", true)]
    [InlineData("Player1", false)]
    [InlineData("SwiftFox42", false)]
    public void IsPlaceholder_DetectsTheNameNobodyChose(string? username, bool expected)
        => Assert.Equal(expected, NicknameGenerator.IsPlaceholder(username));

    [Fact]
    public void NextUnused_AvoidsNamesAlreadyTaken()
    {
        var taken = new List<string>();

        for (var i = 0; i < 20; i++)
        {
            var nickname = NicknameGenerator.NextUnused(taken);

            Assert.DoesNotContain(nickname, taken, StringComparer.OrdinalIgnoreCase);
            taken.Add(nickname);
        }
    }

    [Fact]
    public void GeneratedNames_ProduceDistinctOfflineUuids()
    {
        // The whole point of not shipping one shared default: two players must not end up
        // with the same offline identity.
        var first = NicknameGenerator.Next();
        var second = NicknameGenerator.NextUnused(new[] { first });

        Assert.NotEqual(OfflineAuth.ComputeUuid(first), OfflineAuth.ComputeUuid(second));
    }
}
