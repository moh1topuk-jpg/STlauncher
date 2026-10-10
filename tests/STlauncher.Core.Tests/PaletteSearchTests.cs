using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using STlauncher.Core.Search;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>The matching and ranking behind the search for everything (Ctrl+K).</summary>
public class PaletteSearchTests
{
    private static readonly SearchItem[] Items =
    {
        new(SearchGroup.Places, "Главная", "home домой", showWhenEmpty: true),
        new(SearchGroup.Places, "Настройки", "settings параметры", showWhenEmpty: true),
        new(SearchGroup.Places, "Новая сборка", "создать new build"),
        new(SearchGroup.Builds, "SHOWTIME 1.21.11 fabric", "Fabric 1.21.11", showWhenEmpty: true),
        new(SearchGroup.Builds, "Выживание с другом", "Forge 1.20.1"),
        new(SearchGroup.Mods, "Sodium", "sodium-fabric-0.8.12.jar"),
        new(SearchGroup.Mods, "Sodium Extra", "sodium-extra-0.6.0.jar"),
        new(SearchGroup.Mods, "Ёлочные игрушки", "festive.jar"),
        new(SearchGroup.Mods, "Привет, сосед", "hello.jar"),
        new(SearchGroup.Settings, "Память для игры", "ram озу memory xmx"),
        new(SearchGroup.Settings, "Тема", "темная светлая theme dark light")
    };

    private static List<string> Titles(string query, int cap = PaletteSearch.DefaultCap)
        => PaletteSearch.Find(Items, query, cap).Hits.Select(h => h.Item.Title).ToList();

    [Fact]
    public void WordPrefix_Matches_IgnoringCase()
    {
        Assert.Equal(new[] { "Sodium", "Sodium Extra" }, Titles("SOD"));
        Assert.Contains("Sodium Extra", Titles("ext"));
        Assert.Contains("Выживание с другом", Titles("друг"));
    }

    [Fact]
    public void Substring_Matches_ButOneLetterOnlyAtTheStartOfAWord()
    {
        Assert.Contains("Sodium", Titles("odiu"));
        Assert.Contains("Настройки", Titles("стройк"));

        // "d" begins no word here; inside "Sodium" it would match half the list.
        Assert.DoesNotContain("Sodium", Titles("d"));
    }

    [Fact]
    public void Yo_IsTheSameLetterAsYe()
    {
        Assert.Contains("Ёлочные игрушки", Titles("елочные"));
        Assert.Contains("Тема", Titles("тёмная"));
    }

    [Fact]
    public void Keywords_FindWhatTheTitleDoesNotSay()
    {
        Assert.Equal(new[] { "Память для игры" }, Titles("ram"));
        Assert.Contains("SHOWTIME 1.21.11 fabric", Titles("1.21"));
        Assert.Contains("Выживание с другом", Titles("forge"));

        // Only by the beginning of a keyword: "mor" is inside "memory" and means something else.
        Assert.DoesNotContain("Память для игры", Titles("mor"));
    }

    [Fact]
    public void EveryWordOfTheQuery_HasToMatch()
    {
        Assert.Equal(new[] { "Sodium Extra" }, Titles("sodium extra"));
        Assert.Empty(Titles("sodium память"));
    }

    [Fact]
    public void WrongLayout_IsTriedOnlyWhenNothingMatchedAsTyped()
    {
        var swapped = PaletteSearch.Find(Items, "ghbdtn");
        Assert.Equal("привет", swapped.CorrectedQuery);
        Assert.Equal("Привет, сосед", Assert.Single(swapped.Hits).Item.Title);

        // The other way round: "sodium" typed with the Russian layout on.
        var back = PaletteSearch.Find(Items, "ыщвшгь");
        Assert.Equal("sodium", back.CorrectedQuery);
        Assert.Equal(2, back.Hits.Count);

        Assert.Null(PaletteSearch.Find(Items, "sodium").CorrectedQuery);
        Assert.Equal("привет", PaletteSearch.SwapLayout("ghbdtn"));
        Assert.Equal("любовь", PaletteSearch.SwapLayout("k.,jdm"));
    }

    [Fact]
    public void NoMatch_InEitherLayout_IsEmpty()
    {
        var result = PaletteSearch.Find(Items, "qqqqzzzz");
        Assert.Empty(result.Hits);
        Assert.Null(result.CorrectedQuery);
    }

    [Fact]
    public void TitlePrefix_RanksFirst_ThenTheKindsInTheirOrder()
    {
        var items = new[]
        {
            new SearchItem(SearchGroup.Places, "Настройки сборки", "build settings"),
            new SearchItem(SearchGroup.Builds, "Моя сборка", "fabric"),
            new SearchItem(SearchGroup.Mods, "Сборка мусора", "gc.jar"),
            new SearchItem(SearchGroup.Settings, "Папка игры", "сборки minecraft")
        };

        var hits = PaletteSearch.Find(items, "сборк").Hits;

        // The mod's title begins with the query, so its group leads; the rest keep the kind order.
        Assert.Equal(
            new[] { "Сборка мусора", "Настройки сборки", "Моя сборка", "Папка игры" },
            hits.Select(h => h.Item.Title));
        Assert.Equal(PaletteSearch.RankTitlePrefix, hits[0].Rank);
        Assert.Equal(PaletteSearch.RankKeyword, hits[3].Rank);
    }

    [Fact]
    public void InsideAGroup_BetterMatchesComeFirst_AndGroupsStayTogether()
    {
        var items = new[]
        {
            new SearchItem(SearchGroup.Mods, "Better Fps", "fps.jar"),
            new SearchItem(SearchGroup.Builds, "Fabric 1.21", null),
            new SearchItem(SearchGroup.Mods, "Fabric API", "fabric-api.jar"),
            new SearchItem(SearchGroup.Mods, "Indium", "needs fabric renderer")
        };

        Assert.Equal(
            new[] { "Fabric 1.21", "Fabric API", "Indium" },
            PaletteSearch.Find(items, "fabric").Hits.Select(h => h.Item.Title));
    }

    [Fact]
    public void EachGroup_IsCapped()
    {
        var items = Enumerable.Range(1, 20).Select(i => new SearchItem(SearchGroup.Mods, $"Mod {i}"))
            .Concat(Enumerable.Range(1, 9).Select(i => new SearchItem(SearchGroup.Builds, $"Mod pack {i}")))
            .ToList();

        var hits = PaletteSearch.Find(items, "mod").Hits;

        Assert.Equal(PaletteSearch.DefaultCap, hits.Count(h => h.Item.Group == SearchGroup.Mods));
        Assert.Equal(PaletteSearch.DefaultCap, hits.Count(h => h.Item.Group == SearchGroup.Builds));
        Assert.Equal(3, PaletteSearch.Find(items, "mod", cap: 3).Hits.Count(h => h.Item.Group == SearchGroup.Mods));

        // The cap keeps the first ones given, not an arbitrary six.
        Assert.Equal("Mod 1", hits.First(h => h.Item.Group == SearchGroup.Mods).Item.Title);
    }

    [Fact]
    public void EmptyQuery_ShowsOnlyWhatIsMarkedForIt()
    {
        Assert.Equal(new[] { "Главная", "Настройки", "SHOWTIME 1.21.11 fabric" }, Titles("   "));
        Assert.Equal(new[] { "Главная", "Настройки", "SHOWTIME 1.21.11 fabric" }, Titles(null!));
    }

    [Fact]
    public void Normalize_DropsPunctuationAndCase()
    {
        Assert.Equal("showtime 1 21 11 fabric", PaletteSearch.Normalize("  SHOWTIME 1.21.11 — fabric! "));
        Assert.Equal(string.Empty, PaletteSearch.Normalize(" .,; "));
    }

    /// <summary>
    /// The settings the search knows are a table in code, not the page: every title and
    /// section key in it has to exist in both language files, or the result has no name.
    /// </summary>
    [Fact]
    public void SettingsSearchIndex_UsesExistingLanguageKeys()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "STlauncher.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var appDir = Path.Combine(directory!.FullName, "src", "STlauncher.App");
        var source = File.ReadAllText(Path.Combine(appDir, "Controls", "SettingsSearchIndex.cs"));
        var entries = Regex.Matches(source, @"new\(""([A-Za-z0-9_]+)"",\s*""([A-Za-z0-9_]+)"",\s*""([^""]*)""");

        Assert.True(entries.Count >= 20, "The table of settings was not read: has its shape changed?");

        var keyRegex = new Regex("x:Key=\"([^\"]+)\"");

        foreach (var file in new[] { "ru.axaml", "en.axaml" })
        {
            var defined = keyRegex.Matches(File.ReadAllText(Path.Combine(appDir, "Assets", "Lang", file)))
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            var missing = entries
                .SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value })
                .Distinct()
                .Where(key => !defined.Contains(key))
                .ToList();

            Assert.True(missing.Count == 0, $"Not in {file}: " + string.Join(", ", missing));
        }

        // A setting nobody can find by a word is a line that does nothing.
        Assert.All(entries, m => Assert.False(string.IsNullOrWhiteSpace(m.Groups[3].Value)));
        Assert.Equal(entries.Count, entries.Select(m => m.Groups[1].Value).Distinct().Count());
    }
}
