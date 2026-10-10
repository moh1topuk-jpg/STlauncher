using System;
using System.Linq;
using STlauncher.Core.Diagnostics;
using Xunit;

namespace STlauncher.Core.Tests;

public class LogRedactorTests
{
    private static readonly LogRedactionContext Context = new(
        @"C:\Users\vasya",
        "vasya",
        new[] { "mc.showtime.su" });

    private static string Redact(string text) => LogRedactor.Redact(text, Context);

    [Theory]
    [InlineData(@"Java: C:\Users\vasya\AppData\Roaming\STlauncher\java\21\bin\javaw.exe", @"Java: <home>\AppData\Roaming\STlauncher\java\21\bin\javaw.exe")]
    [InlineData("--gameDir, C:/Users/vasya/AppData/Roaming/.minecraft, --assetsDir", "--gameDir, <home>/AppData/Roaming/.minecraft, --assetsDir")]
    [InlineData(@"""path"": ""C:\\Users\\vasya\\mods\\a.jar""", @"""path"": ""<home>\\mods\\a.jar""")]
    [InlineData("c:\\users\\VASYA\\x", "<home>\\x")]
    [InlineData("jar:file:///C:/Users/vasya/mods/a.jar!/b", "jar:file://<home>/mods/a.jar!/b")]
    [InlineData(@"Home is C:\Users\vasya", "Home is <home>")]
    public void The_home_folder_goes_in_every_spelling(string line, string expected)
        => Assert.Equal(expected, Redact(line));

    [Theory]
    [InlineData(@"D:\Games\vasya\instances\a", @"D:\Games\<user>\instances\a")]
    [InlineData("/mnt/c/Users/vasya/x", "/mnt/c/Users/<user>/x")]
    [InlineData(@"C:\Users\vasya.old\x", @"C:\Users\vasya.old\x")]
    [InlineData(@"C:\Users\vasyapupkin\x", @"C:\Users\vasyapupkin\x")]
    public void The_account_name_goes_only_as_a_whole_folder(string line, string expected)
        => Assert.Equal(expected, Redact(line));

    [Fact]
    public void The_nickname_stays_even_when_it_is_the_account_name()
    {
        const string line = "[12:00:00] [Render thread/INFO]: Setting user: vasya";

        Assert.Equal(line, Redact(line));
        Assert.Equal("--username, vasya, --version", Redact("--username, vasya, --version"));
    }

    [Theory]
    [InlineData("--accessToken eyJhbGciOi.abc.def --version 1.21", "--accessToken <hidden> --version 1.21")]
    // As a loader prints its argument list (captured from Forge's first line, the token made up).
    [InlineData("args [--username, MoH1Top, --accessToken, 0a1b2c3d, --width, 1000]", "args [--username, MoH1Top, --accessToken, <hidden>, --width, 1000]")]
    [InlineData("accessToken=0a1b2c3d&x=1", "accessToken=<hidden>&x=1")]
    [InlineData(@"{""accessToken"": ""0a1b2c3d"", ""name"": ""MoH1Top""}", @"{""accessToken"": ""<hidden>"", ""name"": ""MoH1Top""}")]
    [InlineData("--uuid 5f8eb73b25be4c5aa50fd27d65e30ca0 --userType msa", "--uuid <hidden> --userType msa")]
    [InlineData("--uuid, 5f8eb73b-25be-4c5a-a50f-d27d65e30ca0, --clientId, abc", "--uuid, <hidden>, --clientId, <hidden>")]
    [InlineData("[12:00:00] [main/INFO]: (Session ID is token:0a1b2c3d:5f8eb73b25be4c5aa50fd27d65e30ca0)", "[12:00:00] [main/INFO]: (Session ID is <hidden>)")]
    public void Tokens_and_account_ids_go(string line, string expected)
        => Assert.Equal(expected, Redact(line));

    [Fact]
    public void The_word_uuid_in_ordinary_lines_is_left_alone()
    {
        const string line = "[Render thread/INFO]: UUID of player MoH1Top is 5f8eb73b-25be-4c5a-a50f-d27d65e30ca0";

        Assert.Equal(line, Redact(line));
    }

    [Theory]
    [InlineData("[Render thread/INFO]: Connecting to 203.0.113.28, 25124", "[Render thread/INFO]: Connecting to <ip>, 25124")]
    [InlineData("Connection refused: no further information: /192.168.1.5:25565", "Connection refused: no further information: /<ip>:25565")]
    [InlineData("[voicechat] Connecting to voice chat server: '203.0.113.175:25592'", "[voicechat] Connecting to voice chat server: '<ip>:25592'")]
    [InlineData("remote [2001:db8::7]:25565 closed", "remote [<ip>]:25565 closed")]
    [InlineData("peer fe80::1ff:fe23:4567:890a%eth0", "peer <ip>%eth0")]
    public void Addresses_of_others_go(string line, string expected)
        => Assert.Equal(expected, Redact(line));

    [Theory]
    [InlineData("[20:43:22] [Render thread/INFO]: Connecting to mc.showtime.su, 25565")]
    [InlineData("Listening on 127.0.0.1:25565 and 0.0.0.0:25565, also ::1")]
    [InlineData("[01:03:28] [Render thread/INFO]: time stays 01:03:28 as it is")]
    [InlineData("java version 17.0.8.1 on OpenJDK")]
    [InlineData("LWJGL Version: 3.3.3.75")]
    [InlineData("forge-47.2.0.1.jar and fabric-api-0.100.1.2+1.21.jar")]
    [InlineData("at net.minecraft.class_310.method_1514(class_310.java:1021)")]
    [InlineData("mods: Blocks::register, minecraft:overworld")]
    public void What_is_not_private_is_not_touched(string line)
        => Assert.Equal(line, Redact(line));

    [Fact]
    public void A_friends_host_name_goes_wherever_it_appears()
    {
        var text = string.Join('\n',
            "[12:00:00] [Render thread/INFO]: Connecting to Petya-Home.ddns.example, 25570",
            "[12:00:05] [Render thread/ERROR]: Couldn't connect to server",
            "java.net.UnknownHostException: petya-home.ddns.example",
            "[12:01:00] [Server Pinger #1/INFO]: Pinging other.friend.example, 25565",
            "[12:02:00] [Render thread/INFO]: Connecting to mc.showtime.su, 25565");

        var redacted = Redact(text);

        Assert.DoesNotContain("petya", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other.friend", redacted, StringComparison.Ordinal);
        Assert.Contains("Connecting to <server>, 25570", redacted, StringComparison.Ordinal);
        Assert.Contains("UnknownHostException: <server>", redacted, StringComparison.Ordinal);
        Assert.Contains("Connecting to mc.showtime.su, 25565", redacted, StringComparison.Ordinal);
        Assert.Equal(5, redacted.Split('\n').Length);
    }

    [Fact]
    public void A_public_server_given_as_a_number_stays()
    {
        var context = new LogRedactionContext(null, null, new[] { "203.0.113.9:25570" });

        Assert.Equal(
            "Connecting to 203.0.113.9, 25570 and <ip>",
            LogRedactor.Redact("Connecting to 203.0.113.9, 25570 and 203.0.113.10", context));
    }

    [Fact]
    public void Nothing_to_know_about_the_machine_is_not_an_error()
    {
        var context = new LogRedactionContext(null, " ", null);

        Assert.Equal("plain line", LogRedactor.Redact("plain line", context));
        Assert.Equal(string.Empty, LogRedactor.Redact(null, context));
    }

    [Fact]
    public void A_short_text_is_shared_as_it_is()
        => Assert.Equal("a\nb", LogShare.Fit("a\nb"));

    [Fact]
    public void A_long_text_keeps_its_head_and_its_tail()
    {
        var text = string.Join('\n', Enumerable.Range(1, 1000).Select(i => "line " + i));

        var fitted = LogShare.Fit(text, maxBytes: 1024 * 1024, maxLines: 100);
        var lines = fitted.Split('\n');

        Assert.True(lines.Length <= 100);
        Assert.Equal("line 1", lines[0]);
        Assert.Equal("line 1000", lines[^1]);
        Assert.Contains(lines, l => l.Contains("left out", StringComparison.Ordinal));

        var small = LogShare.Fit(text, maxBytes: 2000, maxLines: 100000);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(small) <= 2000);
        Assert.EndsWith("line 1000", small, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"{""success"":true,""id"":""HpAwPry"",""url"":""https://mclo.gs/HpAwPry"",""raw"":""https://api.mclo.gs/1/raw/HpAwPry""}", true, "https://mclo.gs/HpAwPry")]
    [InlineData(@"{""success"":false,""error"":""Required POST argument 'content' is empty.""}", false, "Required POST argument 'content' is empty.")]
    [InlineData(@"{""success"":true,""url"":""javascript:alert(1)""}", false, "unexpected answer")]
    [InlineData("<html>502</html>", false, "unexpected answer")]
    [InlineData("", false, "unexpected answer")]
    public void The_services_answer_is_read(string body, bool ok, string expected)
    {
        Assert.Equal(ok, LogShare.TryReadLink(body, out var link, out var error));
        Assert.Equal(expected, ok ? link : error);
    }
}
