using STlauncher.Core.Text;
using Xunit;

namespace STlauncher.Core.Tests;

public class MarkdownTextTests
{
    [Fact]
    public void DropsIframesBannersAndBadges_KeepsProse()
    {
        var body = """
            <center><img src="https://cdn.modrinth.com/banner.png" width="600"></center>

            Showcase
            <iframe width="560" height="315" src="https://www.youtube-nocookie.com/embed/yrfJf8dtMT0" title="YouTube video player" frameborder="0" allow="accelerometer; autoplay" allowfullscreen></iframe>

            ----------------------------------

            [![Discord](https://img.shields.io/discord/123)](https://discord.gg/abc) [![Ko-fi](https://ko-fi.com/img)](https://ko-fi.com/x)

            **Solas Shader** is a shader pack for *Minecraft* with `volumetric` lighting &amp; clouds.
            """;

        var text = MarkdownText.ToPlainText(body);

        Assert.Equal("Solas Shader is a shader pack for Minecraft with volumetric lighting & clouds.", text);
    }

    [Fact]
    public void HeadingsListsQuotesAndTables_BecomeReadableBlocks()
    {
        var body = """
            ## Features
            - Realistic **water**
            - Soft shadows
            1. First
            2. Second
            > Requires [Iris](https://modrinth.com/mod/iris) or Optifine.

            | Setting | Value |
            |---|---|
            | Shadows | High |

            <details><summary>Compatibility</summary>Works with Sodium.</details>
            """;

        var text = MarkdownText.ToPlainText(body);

        Assert.Equal(
            "Features\n\n• Realistic water\n\n• Soft shadows\n\n1. First\n\n2. Second\n\nRequires Iris or Optifine.\n\nSetting · Value\n\nShadows · High\n\nCompatibility\n\nWorks with Sodium.",
            text);
    }

    [Fact]
    public void JoinsWrappedParagraphLines_AndSkipsCodeFences()
    {
        var body = "First line\nsecond line of the same paragraph.\n\n```json\n{ \"a\": 1 }\n```\n\nNext paragraph.";

        Assert.Equal("First line second line of the same paragraph.\n\nNext paragraph.", MarkdownText.ToPlainText(body));
    }

    [Fact]
    public void EmptyOrWhitespace_GivesEmpty()
    {
        Assert.Equal(string.Empty, MarkdownText.ToPlainText(null));
        Assert.Equal(string.Empty, MarkdownText.ToPlainText("  \n\n"));
        Assert.Equal(string.Empty, MarkdownText.ToPlainText("<iframe src=\"x\"></iframe>\n---\n"));
    }
}
