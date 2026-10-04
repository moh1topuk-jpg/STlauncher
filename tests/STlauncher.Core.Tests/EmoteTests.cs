using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using STlauncher.Core.Emotes;
using Xunit;

namespace STlauncher.Core.Tests;

public class EmoteTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The shape of the emote the mod ships as "waving": written by hand, in degrees, old "torso".</summary>
    private const string Wave = """
        {
          "name": { "translate": "emotecraft.emote.waving.name" },
          "author": { "player": { "id": [1, 2, 3, 4] }, "extra": [" §7KosmX"], "color": "#ffffff" },
          "description": "Says hello",
          "emote": {
            "beginTick": 4,
            "endTick": 40,
            "stopTick": 50,
            "degrees": true,
            "moves": [
              { "tick": 0, "easing": "inQuad", "torso": { "roll": 0 } },
              { "tick": 10, "easing": "linear", "rightArm": { "roll": 180 }, "rightLeg": { "y": 12.5 } },
              { "tick": 20, "easing": "EASEINOUTQUAD", "rightArm": { "roll": 90 }, "torso": { "roll": 10, "y": 0.5 } },
              { "tick": 30, "easing": "InOutSine", "turn": 1, "head": { "yaw": 45 } }
            ]
          }
        }
        """;

    [Fact]
    public void Json_ReadsDegreesPartsAndText()
    {
        var emote = EmoteReader.TryRead(Utf8(Wave), "waving",
            key => key == "emotecraft.emote.waving.name" ? "Махать рукой" : null);

        Assert.NotNull(emote);
        Assert.Equal("Махать рукой", emote!.Name);
        Assert.Equal("KosmX", emote.Author);
        Assert.Equal("Says hello", emote.Description);
        Assert.Equal((4, 40, 50), (emote.BeginTick, emote.EndTick, emote.StopTick));
        Assert.False(emote.IsLoop);

        var roll = emote.Keyframes(EmotePart.RightArm, EmoteAxis.Roll);
        Assert.Equal(new[] { 10, 20 }, roll.Select(k => k.Tick));
        Assert.Equal(Math.PI, roll[0].Value, 4);
        Assert.Equal(Math.PI / 2, roll[1].Value, 4);
        Assert.Equal(EmoteEasing.InOutQuad, roll[1].Easing);

        // Before format version 3 "torso" is the whole body, and positions stay as written.
        Assert.Equal(2, emote.Keyframes(EmotePart.Body, EmoteAxis.Roll).Count);
        Assert.Empty(emote.Keyframes(EmotePart.Torso, EmoteAxis.Roll));
        Assert.Equal(0.5f, emote.Keyframes(EmotePart.Body, EmoteAxis.Y).Single().Value);
        Assert.Equal(12.5f, emote.Keyframes(EmotePart.RightLeg, EmoteAxis.Y).Single().Value);

        // "turn" leaves a second keyframe on the same tick, whole circles further on.
        var yaw = emote.Keyframes(EmotePart.Head, EmoteAxis.Yaw);
        Assert.Equal(new[] { 30, 30 }, yaw.Select(k => k.Tick));
        Assert.Equal(Math.PI / 4, yaw[0].Value, 4);
        Assert.Equal(Math.PI / 4 + Math.PI * 2, yaw[1].Value, 4);
    }

    [Fact]
    public void Json_WithoutNameTranslation_FallsBackToTheFileName()
    {
        var emote = EmoteReader.TryRead(Utf8(Wave), "Waving");

        Assert.Equal("Waving", emote!.Name);
    }

    [Fact]
    public void Json_RadiansLoopAsStringAndNewTorso()
    {
        const string json = """
            {
              "version": 3,
              "name": "Spin",
              "emote": {
                "isLoop": "true", "returnTick": 10, "endTick": 20, "degrees": false,
                "moves": [
                  { "tick": 0, "torso": { "pitch": 1.5 }, "body": { "yaw": 0 }, "cape": { "pitch": 1 } },
                  { "tick": 20, "body": { "yaw": 3 } }
                ]
              }
            }
            """;

        var emote = EmoteReader.TryRead(Utf8(json), "spin");

        Assert.NotNull(emote);
        Assert.True(emote!.IsLoop);
        Assert.Equal(10, emote.ReturnTick);
        Assert.Equal(1.5f, emote.Keyframes(EmotePart.Torso, EmoteAxis.Pitch).Single().Value);
        Assert.Equal(3f, emote.Keyframes(EmotePart.Body, EmoteAxis.Yaw)[1].Value);
        Assert.Equal(1.05, emote.Seconds, 3);
        Assert.Equal(0.55, emote.LoopSeconds, 3);
    }

    [Fact]
    public void Json_MovesWrittenOutOfOrder_ComeOutByTick()
    {
        const string json = """
            {
              "name": "Backwards",
              "emote": {
                "endTick": 40, "degrees": false,
                "moves": [
                  { "tick": 30, "head": { "yaw": 3 } },
                  { "tick": 10, "head": { "yaw": 1 } },
                  { "tick": 30, "head": { "yaw": 4 } },
                  { "tick": 20, "head": { "yaw": 2 } }
                ]
              }
            }
            """;

        var yaw = EmoteReader.TryRead(Utf8(json), "backwards")!.Keyframes(EmotePart.Head, EmoteAxis.Yaw);

        // By tick, and the two that share a tick in the order the file has them.
        Assert.Equal(new[] { 10, 20, 30, 30 }, yaw.Select(k => k.Tick));
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, yaw.Select(k => k.Value));

        // A long file written from the last tick to the first is put in order once, at
        // the end. Kept in order one keyframe at a time, it cost the square of its length.
        const int count = 60_000;
        var moves = string.Join(",", Enumerable.Range(0, count).Reverse().Select(tick => "{\"tick\":" + tick + ",\"head\":{\"yaw\":1}}"));
        var lengthy = EmoteReader.TryRead(Utf8("{\"emote\":{\"endTick\":" + count + ",\"degrees\":false,\"moves\":[" + moves + "]}}"), "lengthy")!;

        Assert.Equal(Enumerable.Range(0, count), lengthy.Keyframes(EmotePart.Head, EmoteAxis.Yaw).Select(k => k.Tick));
    }

    [Fact]
    public void Sample_EasesInHoldsAndEasesOut()
    {
        var emote = EmoteReader.TryRead(Utf8(Wave), "waving")!;

        // Nothing has moved at the very start, and the arm is on its way by tick 5.
        Assert.Equal(0, emote.Sample(0).RightArm.Roll, 4);
        Assert.InRange(emote.Sample(5).RightArm.Roll, 0.01, Math.PI - 0.01);

        // On a keyframe, the keyframe; halfway along a linear stretch, halfway.
        Assert.Equal(Math.PI, emote.Sample(10).RightArm.Roll, 4);
        Assert.Equal(Math.PI * 0.75, emote.Sample(15).RightArm.Roll, 4);

        // Positions come out as the distance from where the joint rests.
        Assert.Equal(0.5, emote.Sample(10).RightLeg.Y, 4);

        // The last value is held to the end tick, let go by the stop tick, and gone after.
        Assert.Equal(Math.PI / 2, emote.Sample(39.5).RightArm.Roll, 4);
        Assert.InRange(emote.Sample(45).RightArm.Roll, 0.2, Math.PI / 2 - 0.2);
        Assert.Equal(default(EmoteFrame), emote.Sample(50));
        Assert.Equal(default(EmoteFrame), emote.Sample(5000));

        // A keyframe with a turn is reached as written, and from then on stands a whole
        // circle further: the same pose, with the long way round ahead of it.
        Assert.Equal(Math.PI / 4, emote.Sample(29.99).Head.Yaw, 2);
        Assert.Equal(Math.PI / 4 + Math.PI * 2, emote.Sample(30).Head.Yaw, 4);
    }

    [Fact]
    public void Sample_LoopTurnsBackToTheReturnTick()
    {
        const string json = """
            {
              "name": "Loop",
              "emote": {
                "isLoop": true, "returnTick": 10, "endTick": 29, "degrees": false,
                "moves": [
                  { "tick": 0, "head": { "yaw": 0 } },
                  { "tick": 10, "head": { "yaw": 1 } },
                  { "tick": 20, "head": { "yaw": 2 } },
                  { "tick": 29, "head": { "yaw": 1 } }
                ]
              }
            }
            """;

        var emote = EmoteReader.TryRead(Utf8(json), "loop")!;

        Assert.Equal(0.5, emote.Sample(5).Head.Yaw, 4);
        Assert.Equal(2, emote.Sample(20).Head.Yaw, 4);

        // A cycle is ticks 10 to 29: tick 30 is tick 10 again, 45 is 25, and so on for ever.
        Assert.Equal(emote.Sample(10).Head.Yaw, emote.Sample(30).Head.Yaw, 4);
        Assert.Equal(emote.Sample(25).Head.Yaw, emote.Sample(45).Head.Yaw, 4);
        Assert.Equal(emote.Sample(25).Head.Yaw, emote.Sample(25 + 20 * 500).Head.Yaw, 3);

        foreach (var tick in new[] { 0.0, 29.5, 30, 1e9, double.NaN, -3 })
        {
            Assert.True(float.IsFinite(emote.Sample(tick).Head.Yaw));
        }
    }

    /// <summary>A facepalm the way emotes made with bends write one: the elbow folds, the chest bows a little.</summary>
    private const string Bends = """
        {
          "version": 3,
          "name": "Bends",
          "emote": {
            "beginTick": 0, "endTick": 40, "stopTick": 50, "degrees": true,
            "moves": [
              { "tick": 10, "easing": "linear", "rightArm": { "pitch": -90, "bend": 0, "axis": 180 } },
              { "tick": 20, "easing": "linear", "rightArm": { "bend": 120 }, "torso": { "bend": 30, "axis": -90 } },
              { "tick": 20, "easing": "linear", "turn": 1, "leftLeg": { "bend": 45 }, "head": { "bend": 60, "axis": 60, "pitch": 10 } },
              { "tick": 30, "easing": "linear", "body": { "bend": 20 } }
            ]
          }
        }
        """;

    [Fact]
    public void Json_ReadsBendsAndTheirDirection()
    {
        var emote = EmoteReader.TryRead(Utf8(Bends), "bends")!;

        // Both are angles: in degrees when the rest of the file is.
        var bend = emote.Keyframes(EmotePart.RightArm, EmoteAxis.Bend);
        Assert.Equal(new[] { 10, 20 }, bend.Select(k => k.Tick));
        Assert.Equal(Math.PI * 2 / 3, bend[1].Value, 4);
        Assert.Equal(Math.PI, emote.Keyframes(EmotePart.RightArm, EmoteAxis.BendAxis).Single().Value, 4);
        Assert.Equal(-Math.PI / 2, emote.Keyframes(EmotePart.Torso, EmoteAxis.BendAxis).Single().Value, 4);

        // The chest and the whole body each keep their own; whoever draws adds them up.
        Assert.Equal(Math.PI / 6, emote.Keyframes(EmotePart.Torso, EmoteAxis.Bend).Single().Value, 4);
        Assert.Equal(Math.PI / 9, emote.Keyframes(EmotePart.Body, EmoteAxis.Bend).Single().Value, 4);

        // A bend is an angle for "turn" too: the same keyframe again, a whole circle on.
        var knee = emote.Keyframes(EmotePart.LeftLeg, EmoteAxis.Bend);
        Assert.Equal(new[] { 20, 20 }, knee.Select(k => k.Tick));
        Assert.Equal(Math.PI / 4 + Math.PI * 2, knee[1].Value, 4);

        // The head does not bend, whatever a file says; the rest of what it says stands.
        Assert.Empty(emote.Keyframes(EmotePart.Head, EmoteAxis.Bend));
        Assert.Empty(emote.Keyframes(EmotePart.Head, EmoteAxis.BendAxis));
        Assert.Equal(2, emote.Keyframes(EmotePart.Head, EmoteAxis.Pitch).Count);
        Assert.True(emote.Moves(EmotePart.Torso));
    }

    [Fact]
    public void Json_BeforeVersionThree_TheBendOfTorsoIsTheBodys()
    {
        const string json = """
            { "emote": { "endTick": 20, "degrees": false, "moves": [ { "tick": 5, "torso": { "bend": 0.5, "axis": 1.5 } } ] } }
            """;

        var emote = EmoteReader.TryRead(Utf8(json), "old")!;

        Assert.Equal(0.5f, emote.Keyframes(EmotePart.Body, EmoteAxis.Bend).Single().Value);
        Assert.Equal(1.5f, emote.Keyframes(EmotePart.Body, EmoteAxis.BendAxis).Single().Value);
        Assert.False(emote.Moves(EmotePart.Torso));
        Assert.Equal(0.5, emote.Sample(10).Body.Bend, 4);
    }

    [Fact]
    public void Sample_BendsMoveLikeAnyOtherAxis()
    {
        var emote = EmoteReader.TryRead(Utf8(Bends), "bends")!;

        // Straight at the start, and still straight on the keyframe that says so.
        Assert.Equal(0, emote.Sample(0).RightArm.Bend, 4);
        Assert.Equal(0, emote.Sample(10).RightArm.Bend, 4);

        // Half-way along a linear stretch, half the fold; on the keyframe, all of it.
        Assert.Equal(Math.PI / 3, emote.Sample(15).RightArm.Bend, 4);
        Assert.Equal(Math.PI * 2 / 3, emote.Sample(20).RightArm.Bend, 4);

        // The direction has one keyframe: eased into, then held while the fold changes.
        Assert.InRange(emote.Sample(5).RightArm.BendAxis, 0.1, Math.PI - 0.1);
        Assert.Equal(Math.PI, emote.Sample(15).RightArm.BendAxis, 4);
        Assert.Equal(-Math.PI / 2, emote.Sample(25).Torso.BendAxis, 4);

        // Held to the end tick, let go by the stop tick, gone after it.
        Assert.Equal(Math.PI * 2 / 3, emote.Sample(39.5).RightArm.Bend, 4);
        Assert.InRange(emote.Sample(45).RightArm.Bend, 0.2, Math.PI * 2 / 3 - 0.2);
        Assert.Equal(default(EmoteFrame), emote.Sample(50));

        // A part the file does not bend is not bent, and the head never is.
        Assert.Equal(0, emote.Sample(20).LeftArm.Bend);
        Assert.Equal(0, emote.Sample(20).Head.Bend);
        Assert.Equal(Math.PI / 18, emote.Sample(19.999).Head.Pitch, 3);

        // Between two frames a bend goes with everything else.
        var half = EmoteFrame.Lerp(emote.Sample(10), emote.Sample(20), 0.5f);
        Assert.Equal(Math.PI / 3, half.RightArm.Bend, 4);
        Assert.Equal((emote.Sample(10).Torso.Bend + emote.Sample(20).Torso.Bend) / 2, half.Torso.Bend, 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not an emote at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"format_version\": \"1.8.0\", \"animations\": {}}")]
    [InlineData("{\"emote\": 5}")]
    [InlineData("{\"emote\": {\"endTick\": 0, \"moves\": []}}")]
    [InlineData("{\"emote\": {\"endTick\": 10}}")]
    [InlineData("{\"emote\": {\"endTick\": 10, \"isLoop\": true, \"returnTick\": 11, \"moves\": []}}")]
    [InlineData("{\"emote\": {\"endTick\": 10, \"moves\": [{\"tick\": 1, \"head\": {\"yaw\": 1e999}}, 7, {\"head\": {}}]}, \"name\": [[[[[[[[[[\"deep\"]]]]]]]]]]}")]
    public void Malformed_NeverThrows(string content)
    {
        var emote = EmoteReader.TryRead(Utf8(content), "broken");

        // Only the last one is an emote at all: it has an end and a list of moves.
        Assert.Equal(content.Contains("1e999"), emote is not null);
    }

    [Theory]
    [InlineData("EASEINOUTQUAD", EmoteEasing.InOutQuad)]
    [InlineData("InOutSine", EmoteEasing.InOutSine)]
    [InlineData("inoutcirc", EmoteEasing.InOutCirc)]
    [InlineData("ease_out_bounce", EmoteEasing.OutBounce)]
    [InlineData("CONSTANT", EmoteEasing.Constant)]
    [InlineData("LINEAR", EmoteEasing.Linear)]
    [InlineData("12", EmoteEasing.Linear)]
    [InlineData("no such thing", EmoteEasing.Linear)]
    public void Easing_NamesAreReadTheWayTheModReadsThem(string name, EmoteEasing expected)
    {
        Assert.Equal(expected, EmoteEasings.Parse(name));
    }

    [Fact]
    public void Easing_EveryCurveStartsAtZeroAndEndsAtOne()
    {
        foreach (var easing in Enum.GetValues<EmoteEasing>().Where(e => e != EmoteEasing.Constant))
        {
            Assert.Equal(0, EmoteEasings.Apply(easing, 0), 6);
            Assert.Equal(1, EmoteEasings.Apply(easing, 1), 6);
            Assert.True(double.IsFinite(EmoteEasings.Apply(easing, 0.37)));
        }
    }

    // ===================== The binary form =====================

    /// <summary>Writes the binary form the way the mod's source lays it out, for the reader to read back.</summary>
    private sealed class BinaryEmote
    {
        private readonly MemoryStream _stream = new();

        public byte[] Bytes => _stream.ToArray();

        public BinaryEmote Int(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            _stream.Write(bytes);
            return this;
        }

        public BinaryEmote Float(float value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteSingleBigEndian(bytes, value);
            _stream.Write(bytes);
            return this;
        }

        public BinaryEmote Byte(int value)
        {
            _stream.WriteByte((byte)value);
            return this;
        }

        public BinaryEmote Text(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Int(bytes.Length);
            _stream.Write(bytes);
            return this;
        }

        public BinaryEmote Raw(byte[] bytes)
        {
            _stream.Write(bytes);
            return this;
        }

        /// <summary>One axis: whether it is used, and its keyframes as (tick, value, easing id).</summary>
        public BinaryEmote Track(int version, params (int Tick, float Value, int Easing)[] keys)
        {
            if (version >= 2)
            {
                Byte(keys.Length > 0 ? 1 : 0);
            }

            Int(keys.Length);

            foreach (var (tick, value, easing) in keys)
            {
                Int(tick).Float(value).Byte(easing);

                if (version >= 4)
                {
                    Float(0);
                }
            }

            return this;
        }
    }

    private static byte[] BinaryFile(int version, bool withHeader = true, int trailingBytes = 0)
    {
        var animation = new BinaryEmote()
            .Int(0)                       // the tick of a running emote
            .Int(2).Int(40).Int(44)       // begin, end, stop
            .Byte(1).Int(5)               // loops, back to tick 5
            .Byte(0).Byte(0)              // easing taken from the keyframe behind; not nsfw
            .Byte(version >= 4 ? 13 : 9); // bytes per keyframe

        void Part(string name, bool bends, params (int, float, int)[] rollKeys)
        {
            animation.Text(name);

            for (var axis = 0; axis < 5; axis++)
            {
                animation.Track(version);
            }

            animation.Track(version, rollKeys);

            if (bends)
            {
                // The direction of the bend first, then the bend: the mod's order.
                animation.Track(version, (12, 1.25f, 0));
                animation.Track(version, (12, -0.75f, 0), (30, 2f, 0));
            }

            for (var scale = 0; scale < (version >= 3 ? 3 : 0); scale++)
            {
                animation.Track(version, (3, 9f, 0));
            }
        }

        animation.Int(3);
        Part("head", bends: false, (10, 0.5f, 0), (20, 1.5f, 14));
        Part("rightArm", bends: true, (0, 3f, 8));
        Part("torso", bends: true, (7, -1f, 1));
        animation.Raw(new byte[16 + trailingBytes]);

        var header = new BinaryEmote().Text("\"Binary wave\"").Text("{\"text\":\"Described\"}").Text("Someone");

        var file = new BinaryEmote().Int(8).Byte(0).Byte(withHeader ? 3 : 2);

        // An icon first: a part the reader has no use for and must step over.
        file.Byte(0x12).Byte(1).Int(5).Raw(new byte[5]);

        if (withHeader)
        {
            file.Byte(0x11).Byte(1).Int(header.Bytes.Length).Raw(header.Bytes);
        }

        file.Byte(0).Byte(version).Int(animation.Bytes.Length).Raw(animation.Bytes);
        return file.Bytes;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Binary_ReadsKeyframesAndHeader(int version)
    {
        var emote = EmoteReader.TryRead(BinaryFile(version), "file name");

        Assert.NotNull(emote);
        Assert.Equal("Binary wave", emote!.Name);
        Assert.Equal("Described", emote.Description);
        Assert.Equal("Someone", emote.Author);
        Assert.Equal((2, 40, 44, 5), (emote.BeginTick, emote.EndTick, emote.StopTick, emote.ReturnTick));
        Assert.True(emote.IsLoop);

        var head = emote.Keyframes(EmotePart.Head, EmoteAxis.Roll);
        Assert.Equal(new[] { new EmoteKeyframe(10, 0.5f, EmoteEasing.Linear), new EmoteKeyframe(20, 1.5f, EmoteEasing.InOutQuad) }, head);
        Assert.Equal(new EmoteKeyframe(0, 3f, EmoteEasing.InOutSine), emote.Keyframes(EmotePart.RightArm, EmoteAxis.Roll).Single());

        // Inside the binary "torso" is already the chest.
        Assert.Equal(new EmoteKeyframe(7, -1f, EmoteEasing.Constant), emote.Keyframes(EmotePart.Torso, EmoteAxis.Roll).Single());
        Assert.False(emote.Moves(EmotePart.Body));

        // The bend and its direction are kept, each from its own track; the head has
        // neither in the file, and the scale that follows is nobody's.
        foreach (var part in new[] { EmotePart.RightArm, EmotePart.Torso })
        {
            Assert.Equal(new EmoteKeyframe(12, 1.25f, EmoteEasing.Linear), emote.Keyframes(part, EmoteAxis.BendAxis).Single());
            Assert.Equal(new[] { -0.75f, 2f }, emote.Keyframes(part, EmoteAxis.Bend).Select(k => k.Value));
        }

        Assert.Empty(emote.Keyframes(EmotePart.Head, EmoteAxis.Bend));
        Assert.Empty(emote.Keyframes(EmotePart.RightArm, EmoteAxis.X));
        Assert.Equal(-0.75, emote.Sample(12).RightArm.Bend, 4);
        Assert.Equal(1.25, emote.Sample(12).Torso.BendAxis, 4);
    }

    [Fact]
    public void Binary_WithoutHeader_IsNamedAfterTheFile()
    {
        Assert.Equal("file name", EmoteReader.TryRead(BinaryFile(2, withHeader: false), "file name")!.Name);
    }

    [Fact]
    public void Binary_ThatDoesNotEndWhereItShould_IsRefused()
    {
        var good = BinaryFile(3);

        Assert.Null(EmoteReader.TryRead(BinaryFile(3, trailingBytes: 3), "x"));
        Assert.Null(EmoteReader.TryRead(good[..^9], "x"));
        Assert.Null(EmoteReader.TryRead(good[..7], "x"));

        // The newest mod versions save another animation format under another id: skipped, quietly.
        var newer = new BinaryEmote().Int(8).Byte(0).Byte(1).Byte(0x99).Byte(1).Int(4).Raw(new byte[4]).Bytes;
        Assert.Null(EmoteReader.TryRead(newer, "x"));

        var random = new byte[4096];
        new Random(7).NextBytes(random);
        Assert.Null(EmoteReader.TryRead(random, "x"));
    }

    // ===================== A build's emotes =====================

    [Fact]
    public void Library_FindsLooseFilesAndTheOnesInsideTheModJar()
    {
        var game = Path.Combine(Path.GetTempPath(), "stl-emotes-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(Path.Combine(game, "emotes", "pack"));
            Directory.CreateDirectory(Path.Combine(game, "mods"));

            File.WriteAllText(Path.Combine(game, "emotes", "pack", "my_dance.json"),
                "{\"emote\": {\"endTick\": 10, \"moves\": [{\"tick\": 1, \"head\": {\"yaw\": 20}}]}}");
            File.WriteAllBytes(Path.Combine(game, "emotes", "saved.emotecraft"), BinaryFile(2));
            File.WriteAllText(Path.Combine(game, "emotes", "broken.json"), "{ \"emote\": ");
            File.WriteAllText(Path.Combine(game, "emotes", "notes.txt"), "not an emote");

            WriteJar(Path.Combine(game, "mods", "emotecraft-fabric-for-MC1.21.11-3.2.0.jar"));
            WriteJar(Path.Combine(game, "mods", "emotecraft-old.jar.disabled"));

            var library = new EmoteLibrary();

            // The player's own first, then the mod's. Whether Cyrillic sorts before Latin
            // depends on the machine's language, so the mod's two are compared as a set.
            var russian = library.Load(game, "ru_ru");
            Assert.Equal(new[] { "Binary wave", "My dance" }, russian.Take(2).Select(e => e.Name));
            Assert.Equal(new[] { "Clap", "Махать рукой" }, russian.Skip(2).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));

            // The same list for as long as nothing changes; a new one when the language or the files do.
            Assert.Same(russian, library.Load(game, "ru_ru"));
            Assert.Equal(new[] { "Binary wave", "My dance", "Clap", "Waving" }, library.Load(game, "en_us").Select(e => e.Name));

            File.Delete(Path.Combine(game, "emotes", "saved.emotecraft"));
            Assert.Equal(3, library.Load(game, "ru_ru").Count);
        }
        finally
        {
            Directory.Delete(game, recursive: true);
        }
    }

    [Fact]
    public void Library_BuildWithoutEmotes_IsAnEmptyList()
    {
        var library = new EmoteLibrary();

        Assert.Empty(library.Load(Path.Combine(Path.GetTempPath(), "stl-no-such-build-" + Guid.NewGuid().ToString("N")), "ru_ru"));
        Assert.Empty(library.Load("", "ru_ru"));
    }

    private static void WriteJar(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        void Entry(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        Entry("assets/emotecraft/emotes/waving.json", Wave);
        Entry("assets/emotecraft/emotes/clap.json", "{\"name\": {\"translate\": \"emotecraft.emote.clap.name\"}, \"emote\": {\"endTick\": 10, \"moves\": []}}");
        Entry("assets/emotecraft/emotes/clap.png", "not a picture, and not looked at");
        Entry("assets/emotecraft/lang/en_us.json", "{\"emotecraft.emote.waving.name\": \"Waving\", \"emotecraft.emote.clap.name\": \"Clap\"}");
        Entry("assets/emotecraft/lang/ru_ru.json", "{\"emotecraft.emote.waving.name\": \"Махать рукой\"}");
        Entry("fabric.mod.json", "{\"id\": \"emotecraft\"}");
    }
}
