using STlauncher.Core.Diagnostics;

namespace STlauncher.Core.Tests;

public class StreamerRedactorTests
{
    private const string D = StreamerRedactor.Dots;
    private const string G = "•••••";

    private static readonly string[] Public = { "mc.showtime.su" };

    private static readonly StreamerRedactionContext Context = new(
        @"C:\Users\Дима",
        "Дима",
        "Steve_Pro",
        "Игрок",
        Public,
        new[] { "myhome.ddns.net:25565" });

    // ===================== Addresses =====================

    [Theory]
    [InlineData("93.184.216.34:25565", D + ":" + G)]
    [InlineData("192.168.1.20", D)]
    [InlineData("[2001:db8::1]:25565", D + ":" + G)]
    [InlineData("2001:db8::1", D)]
    [InlineData("myhome.ddns.net:25565", D + ":" + G)]
    [InlineData("myhome.ddns.net", D)]
    [InlineData("mc.showtime.su", "mc.showtime.su")]
    [InlineData("mc.showtime.su:25565", "mc.showtime.su:25565")]
    [InlineData("MC.Showtime.SU", "MC.Showtime.SU")]
    [InlineData("", "")]
    public void An_address_is_dots_unless_it_is_the_public_server(string address, string expected)
        => Assert.Equal(expected, StreamerRedactor.MaskAddress(address, Public));

    // ===================== Codes =====================

    [Theory]
    [InlineData("ST-AB12C-3DE4F", "ST-" + G + "-" + G)]
    [InlineData("SB-0123K-XYZ98", "SB-" + G + "-" + G)]
    [InlineData("STS1.eyJhbGciOi_JIUzI1NiJ9-abc", "STS1." + G)]
    [InlineData("STB1.H4sIAAAAAAAA", "STB1." + G)]
    [InlineData("something else", G + "-" + G)]
    public void A_code_keeps_its_kind_and_loses_the_rest(string code, string expected)
        => Assert.Equal(expected, StreamerRedactor.MaskCode(code));

    // ===================== Paths =====================

    [Theory]
    [InlineData(@"C:\Users\Дима\AppData\Roaming\STlauncher", @"C:\Users\•••\AppData\Roaming\STlauncher")]
    [InlineData(@"D:\Games\Дима\instances\a", @"D:\Games\•••\instances\a")]
    [InlineData("C:/Users/дима/.minecraft", "C:/Users/•••/.minecraft")]
    [InlineData(@"C:\Users\Дима", @"C:\Users\•••")]
    [InlineData(@"C:\Users\Димас\x", @"C:\Users\Димас\x")]
    [InlineData(@"E:\STlauncher\data", @"E:\STlauncher\data")]
    public void A_path_loses_the_user_folder_only(string path, string expected)
        => Assert.Equal(expected, StreamerRedactor.MaskPath(path, @"C:\Users\Дима", "Дима"));

    [Fact]
    public void A_profile_folder_named_otherwise_than_the_account_is_masked_too()
        => Assert.Equal(@"C:\Users\•••\x", StreamerRedactor.MaskPath(@"C:\Users\dima9\x", @"C:\Users\dima9", "Дмитрий"));

    // ===================== Lines =====================

    [Fact]
    public void A_console_line_shows_as_it_is_with_the_mode_off()
    {
        const string line = @"[12:00:00] [Render thread/INFO]: Setting user: Steve_Pro, joined 93.184.216.34:25565 from C:\Users\Дима\x";

        Assert.Equal(line, StreamerRedactor.Show(line, streamerMode: false, Context));
    }

    [Fact]
    public void A_console_line_loses_the_nickname_the_address_and_the_user_with_the_mode_on()
    {
        const string line = @"[12:00:00] [Render thread/INFO]: Setting user: Steve_Pro, joined 93.184.216.34:25565 from C:\Users\Дима\x";

        var shown = StreamerRedactor.Show(line, streamerMode: true, Context);

        Assert.Equal(@"[12:00:00] [Render thread/INFO]: Setting user: Игрок, joined •••:••••• from C:\Users\•••\x", shown);
    }

    [Theory]
    [InlineData("Connecting to myhome.ddns.net, 25565", "Connecting to •••, 25565")]
    [InlineData("Connected directly, the address is play.friend.example:25570", "Connected directly, the address is •••:•••••")]
    [InlineData("by the public address myhome.ddns.net", "by the public address •••")]
    [InlineData("LAN: 10.0.0.5 and fe80::1c2b:3a4d:5e6f:7a8b", "LAN: ••• and •••")]
    [InlineData("Invite ST-AB12C-3DE4F copied", "Invite ST-•••••-••••• copied")]
    [InlineData("Build code SB-0123K-XYZ98 copied.", "Build code SB-•••••-••••• copied.")]
    [InlineData("--accessToken abc.def", "--accessToken •••")]
    [InlineData("Joining mc.showtime.su:25565", "Joining mc.showtime.su:25565")]
    [InlineData("Loopback 127.0.0.1:25565 stays", "Loopback 127.0.0.1:25565 stays")]
    [InlineData("You (steve_pro) go on the list", "You (Игрок) go on the list")]
    [InlineData("Steve_Proud is somebody else", "Steve_Proud is somebody else")]
    [InlineData("Java 17.0.8 build 1.21.1", "Java 17.0.8 build 1.21.1")]
    public void What_a_line_hides_and_what_it_keeps(string line, string expected)
        => Assert.Equal(expected, StreamerRedactor.Redact(line, Context));
}
