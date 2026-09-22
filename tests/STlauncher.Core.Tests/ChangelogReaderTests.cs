using System.IO;
using System.Linq;
using STlauncher.Core.Text;
using Xunit;

namespace STlauncher.Core.Tests;

public class ChangelogReaderTests
{
    private const string Sample = """
        # Список изменений

        ## [Не выпущено]

        ### Исправлено
        - Что-то в работе.

        ## [0.3.1] — 2026-09-22

        ### Добавлено
        - Каталог модов переделан в витрину: категории — чипами, моды —
          карточками с автором.
        - Моды ставятся вместе со своими `зависимостями`.

        ### Исправлено
        - Переключение на English ломало интерфейс.

        ## [0.3.0] — 2026-09-22

        ### Добавлено
        - Скин игрока в 3D.

        [0.3.1]: https://example.com/compare/v0.3.0...v0.3.1
        """;

    [Fact]
    public void ReadsOneVersion_WithItsGroups_AndJoinsWrappedBullets()
    {
        var notes = ChangelogReader.SectionFor(Sample, "0.3.1")!;

        Assert.Equal("2026-09-22", notes.Date);
        Assert.Equal(new[] { "Добавлено", "Исправлено" }, notes.Groups.Select(g => g.Title));
        Assert.Equal(2, notes.Groups[0].Items.Count);
        Assert.StartsWith("Каталог модов переделан в витрину: категории — чипами, моды — карточками", notes.Groups[0].Items[0]);
        Assert.Single(notes.Groups[1].Items);
    }

    [Fact]
    public void StopsAtTheNextVersion()
    {
        var notes = ChangelogReader.SectionFor(Sample, "0.3.1")!;

        Assert.DoesNotContain(notes.Groups.SelectMany(g => g.Items), i => i.Contains("3D"));
    }

    [Fact]
    public void UnknownVersion_IsNull()
        => Assert.Null(ChangelogReader.SectionFor(Sample, "9.9.9"));

    [Fact]
    public void Plain_DropsInlineMarkdown()
        => Assert.Equal("зависимостями и ссылка", ChangelogReader.Plain("`зависимостями` и [ссылка](https://x)"));

    [Fact]
    public void TheRealChangelog_HasNotesForEveryReleasedVersion()
    {
        // The launcher shows this file to the player after an update; a version whose
        // section cannot be parsed would show nothing.
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CHANGELOG.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var text = File.ReadAllText(Path.Combine(directory!.FullName, "CHANGELOG.md"));

        foreach (var version in new[] { "0.3.1", "0.3.0", "0.2.9" })
        {
            var notes = ChangelogReader.SectionFor(text, version);
            Assert.True(notes is { IsEmpty: false }, $"No readable notes for {version}");
        }
    }
}
