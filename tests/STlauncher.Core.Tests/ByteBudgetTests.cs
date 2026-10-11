using STlauncher.Core.Storage;

namespace STlauncher.Core.Tests;

/// <summary>
/// The image cache is held to a number of bytes and lets go of what was used longest ago.
/// </summary>
public class ByteBudgetTests
{
    [Fact]
    public void Entries_within_the_budget_all_stay()
    {
        var budget = new ByteBudget<string>(100);

        Assert.Empty(budget.Add("a", 40));
        Assert.Empty(budget.Add("b", 60));

        Assert.Equal(100, budget.TotalBytes);
        Assert.Equal(2, budget.Count);
    }

    [Fact]
    public void Over_the_budget_the_oldest_go_first_until_it_fits()
    {
        var budget = new ByteBudget<string>(100);

        budget.Add("a", 40);
        budget.Add("b", 40);
        budget.Add("c", 20);

        var evicted = budget.Add("d", 50);

        Assert.Equal(new[] { "a", "b" }, evicted);
        Assert.Equal(70, budget.TotalBytes);
        Assert.False(budget.Contains("a"));
        Assert.True(budget.Contains("c"));
        Assert.True(budget.Contains("d"));
    }

    [Fact]
    public void An_entry_that_was_used_is_the_last_to_go()
    {
        var budget = new ByteBudget<string>(100);

        budget.Add("a", 40);
        budget.Add("b", 40);
        budget.Touch("a");

        var evicted = budget.Add("c", 40);

        Assert.Equal(new[] { "b" }, evicted);
        Assert.True(budget.Contains("a"));
    }

    [Fact]
    public void The_entry_just_added_is_never_evicted_even_when_it_alone_is_too_large()
    {
        var budget = new ByteBudget<string>(100);

        budget.Add("small", 10);

        var evicted = budget.Add("huge", 500);

        Assert.Equal(new[] { "small" }, evicted);
        Assert.True(budget.Contains("huge"));
        Assert.Equal(500, budget.TotalBytes);

        // The next arrival pushes it out in its turn.
        Assert.Equal(new[] { "huge" }, budget.Add("next", 10));
        Assert.Equal(10, budget.TotalBytes);
    }

    [Fact]
    public void Adding_a_known_key_again_replaces_its_size_and_makes_it_newest()
    {
        var budget = new ByteBudget<string>(100);

        budget.Add("a", 1);     // a placeholder while the picture is on its way
        budget.Add("b", 30);
        budget.Add("a", 60);    // the picture arrived

        Assert.Equal(90, budget.TotalBytes);
        Assert.Equal(2, budget.Count);

        // "b" is now the oldest.
        Assert.Equal(new[] { "b" }, budget.Add("c", 30));
    }

    [Fact]
    public void Remove_and_clear_give_the_bytes_back()
    {
        var budget = new ByteBudget<string>(100);

        budget.Add("a", 40);
        budget.Add("b", 40);

        Assert.True(budget.Remove("a"));
        Assert.False(budget.Remove("a"));
        Assert.Equal(40, budget.TotalBytes);

        budget.Clear();

        Assert.Equal(0, budget.TotalBytes);
        Assert.Equal(0, budget.Count);
        Assert.Empty(budget.Add("c", 100));
    }

    [Fact]
    public void Keys_follow_the_comparer_given()
    {
        var budget = new ByteBudget<string>(100, StringComparer.OrdinalIgnoreCase);

        budget.Add("Logo.png", 10);
        budget.Add("LOGO.PNG", 20);

        Assert.Equal(1, budget.Count);
        Assert.Equal(20, budget.TotalBytes);
    }

    [Fact]
    public void A_negative_size_counts_as_nothing_and_a_budget_must_be_positive()
    {
        var budget = new ByteBudget<string>(10);

        budget.Add("a", -5);

        Assert.Equal(0, budget.TotalBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ByteBudget<string>(0));
    }
}
