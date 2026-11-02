using STlauncher.Core.Diagnostics;
using Xunit;

namespace STlauncher.Core.Tests;

public sealed class ReportContactTests
{
    [Theory]
    [InlineData("@ded_rura", "@ded_rura")]
    [InlineData("ded_rura", "@ded_rura")]
    [InlineData("  @ded_rura  ", "@ded_rura")]
    [InlineData("https://t.me/ded_rura", "@ded_rura")]
    [InlineData("t.me/ded_rura/", "@ded_rura")]
    public void Normalize_TakesTheWaysPeopleTypeIt(string typed, string expected)
        => Assert.Equal(expected, ReportContact.Normalize(typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_NothingTyped_IsFine(string? typed)
        => Assert.Equal(string.Empty, ReportContact.Normalize(typed));

    [Theory]
    [InlineData("@abc")]
    [InlineData("@1abcdef")]
    [InlineData("@ник_игрока")]
    [InlineData("@has space")]
    [InlineData("+79990000000")]
    [InlineData("https://example.com/ded_rura")]
    public void Normalize_NotAUsername_IsNull(string typed)
        => Assert.Null(ReportContact.Normalize(typed));

    [Fact]
    public void Normalize_TooLong_IsNull()
        => Assert.Null(ReportContact.Normalize("@" + new string('a', 33)));

    [Fact]
    public void Compose_PutsTheContactFirst()
    {
        Assert.Equal("Telegram: @ded_rura\nзависает", ReportContact.Compose("@ded_rura", "зависает"));
        Assert.Equal("Telegram: @ded_rura", ReportContact.Compose("@ded_rura", string.Empty));
        Assert.Equal("зависает", ReportContact.Compose(string.Empty, "зависает"));
    }
}
