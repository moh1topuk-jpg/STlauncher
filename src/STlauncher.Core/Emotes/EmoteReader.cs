using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace STlauncher.Core.Emotes;

/// <summary>
/// Reads an Emotecraft emote file: the JSON form (<c>.json</c>, and <c>.emotecraft</c>
/// files that hold JSON) and the older binary <c>.emotecraft</c> form.
/// </summary>
/// <remarks>
/// The formats are the mod's own, taken from its source: AnimationJson in playerAnimator
/// for the JSON, EmotePacket and LegacyAnimationBinary for the binary. What is not an
/// emote this reader knows - a GeckoLib animation, the binary form the newest mod
/// versions write, a broken file - comes back as null. Nothing here throws: these are
/// files a player downloaded from anywhere, and a bad one must cost the launcher nothing.
/// </remarks>
public static class EmoteReader
{
    /// <summary>Larger than any emote; a bigger file is something else, or has music and an icon inside.</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

    /// <param name="data">The whole file.</param>
    /// <param name="fallbackName">The name to show when the file has none: usually its file name.</param>
    /// <param name="translate">Looks a translation key up, for the names of the emotes the mod ships with.</param>
    public static Emote? TryRead(byte[] data, string fallbackName, Func<string, string?>? translate = null)
    {
        if (data is null || data.Length == 0 || data.Length > MaxFileBytes)
        {
            return null;
        }

        try
        {
            return LooksLikeJson(data)
                ? ReadJson(data, fallbackName, translate)
                : ReadBinary(data, fallbackName, translate);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool LooksLikeJson(ReadOnlySpan<byte> data)
    {
        data = SkipBom(data);

        foreach (var value in data)
        {
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return value == (byte)'{';
        }

        return false;
    }

    private static ReadOnlySpan<byte> SkipBom(ReadOnlySpan<byte> data)
        => data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? data[3..] : data;

    // ===================== JSON =====================

    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static Emote? ReadJson(byte[] data, string fallbackName, Func<string, string?>? translate)
    {
        var start = data.Length - SkipBom(data).Length;
        using var document = JsonDocument.Parse(data.AsMemory(start), Lenient);
        var root = document.RootElement;

        // A file without "emote" is some other animation format the mod also plays.
        if (!root.TryGetProperty("emote", out var body) || body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var version = root.TryGetProperty("version", out var versionNode) && TryNumber(versionNode, out var number)
            ? (int)number
            : 1;

        if (!body.TryGetProperty("endTick", out var endNode) || !TryNumber(endNode, out var end))
        {
            return null;
        }

        var builder = new EmoteBuilder { EndTick = ToTick(end) };

        if (body.TryGetProperty("beginTick", out var beginNode) && TryNumber(beginNode, out var begin))
        {
            builder.BeginTick = ToTick(begin);
        }

        if (body.TryGetProperty("stopTick", out var stopNode) && TryNumber(stopNode, out var stop))
        {
            builder.StopTick = ToTick(stop);
        }

        // The mod takes a loop only when both are given.
        if (body.TryGetProperty("isLoop", out var loopNode) &&
            body.TryGetProperty("returnTick", out var returnNode) && TryNumber(returnNode, out var returnTick))
        {
            builder.IsLoop = ToBool(loopNode);
            builder.ReturnTick = ToTick(returnTick);
        }

        if (body.TryGetProperty("easeBeforeKeyframe", out var easeNode))
        {
            builder.EaseBeforeKeyframe = ToBool(easeNode);
        }

        // Degrees unless the file says otherwise: that is the mod's default, and the
        // files written by hand rely on it.
        var degrees = !body.TryGetProperty("degrees", out var degreesNode) || ToBool(degreesNode);

        if (!body.TryGetProperty("moves", out var moves) || moves.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var move in moves.EnumerateArray())
        {
            if (move.ValueKind != JsonValueKind.Object ||
                !move.TryGetProperty("tick", out var tickNode) || !TryNumber(tickNode, out var tickValue))
            {
                continue;
            }

            var tick = ToTick(tickValue);
            var easing = move.TryGetProperty("easing", out var easingNode) && easingNode.ValueKind == JsonValueKind.String
                ? EmoteEasings.Parse(easingNode.GetString())
                : EmoteEasing.Linear;
            var turn = move.TryGetProperty("turn", out var turnNode) && TryNumber(turnNode, out var turns)
                ? (int)Math.Clamp(turns, -64, 64)
                : 0;

            foreach (var property in move.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object || PartOf(property.Name, version) is not { } part)
                {
                    continue;
                }

                foreach (var (name, axis) in AxisNames)
                {
                    if (!property.Value.TryGetProperty(name, out var valueNode) || !TryNumber(valueNode, out var value))
                    {
                        continue;
                    }

                    var angle = axis >= EmoteAxis.Pitch;
                    var amount = (float)(angle && degrees ? value * Math.PI / 180 : value);

                    builder.Add(part, axis, tick, amount, easing);

                    // "turn" asks for whole extra turns on the way to the next keyframe:
                    // the same angle a few circles on, reached in no time, and the way
                    // from there to the next keyframe is that much longer.
                    if (angle && turn != 0)
                    {
                        builder.Add(part, axis, tick, (float)(amount + Math.PI * 2 * turn), easing);
                    }
                }
            }
        }

        return builder.Build(
            NameOr(root, "name", translate, fallbackName),
            NameOr(root, "author", translate, string.Empty),
            NameOr(root, "description", translate, string.Empty));
    }

    private static readonly (string Name, EmoteAxis Axis)[] AxisNames =
    {
        ("x", EmoteAxis.X), ("y", EmoteAxis.Y), ("z", EmoteAxis.Z),
        ("pitch", EmoteAxis.Pitch), ("yaw", EmoteAxis.Yaw), ("roll", EmoteAxis.Roll),
        ("bend", EmoteAxis.Bend), ("axis", EmoteAxis.BendAxis)
    };

    /// <summary>The order the binary form keeps a part's axes in; the direction of a bend comes before the bend.</summary>
    private static readonly EmoteAxis[] BinaryAxes =
    {
        EmoteAxis.X, EmoteAxis.Y, EmoteAxis.Z, EmoteAxis.Pitch, EmoteAxis.Yaw, EmoteAxis.Roll
    };

    private static readonly EmoteAxis[] BinaryBendAxes = { EmoteAxis.BendAxis, EmoteAxis.Bend };

    /// <summary>
    /// The part behind a name in a file, or null for what the figure does not have: the
    /// cape, the items in the hands. Before format version 3 "torso" meant the whole body.
    /// </summary>
    private static EmotePart? PartOf(string name, int version) => name switch
    {
        "head" => EmotePart.Head,
        "body" => EmotePart.Body,
        "torso" => version < 3 ? EmotePart.Body : EmotePart.Torso,
        "rightArm" or "right_arm" => EmotePart.RightArm,
        "leftArm" or "left_arm" => EmotePart.LeftArm,
        "rightLeg" or "right_leg" => EmotePart.RightLeg,
        "leftLeg" or "left_leg" => EmotePart.LeftLeg,
        _ => null
    };

    private static string NameOr(JsonElement root, string property, Func<string, string?>? translate, string fallback)
    {
        var text = root.TryGetProperty(property, out var node) ? EmoteText.Flatten(node, translate) : string.Empty;
        return text.Length == 0 ? fallback : text;
    }

    private static bool TryNumber(JsonElement node, out double value)
    {
        value = 0;

        return node.ValueKind switch
        {
            JsonValueKind.Number => node.TryGetDouble(out value) && double.IsFinite(value),
            JsonValueKind.String => double.TryParse(node.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value) && double.IsFinite(value),
            _ => false
        };
    }

    /// <summary>The bundled emotes write <c>"isLoop": "true"</c>, a string, and the mod reads it as a boolean.</summary>
    private static bool ToBool(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => string.Equals(node.GetString(), "true", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static int ToTick(double value) => (int)Math.Clamp(value, int.MinValue / 2, int.MaxValue / 2);

    // ===================== Binary =====================

    private const byte AnimationPacket = 0;
    private const byte HeaderPacket = 0x11;

    /// <summary>
    /// The binary file is the packet the mod sends over the network, saved as it is: a
    /// version, a purpose, and a count of parts, each with an id, its own version and a
    /// length. The animation is part 0, the name and author part 0x11; the icon and the
    /// music are skipped.
    /// </summary>
    private static Emote? ReadBinary(byte[] data, string fallbackName, Func<string, string?>? translate)
    {
        var reader = new BigEndian(data);

        var networkVersion = reader.Int32();

        if (networkVersion < 0 || networkVersion > 0xFFFF)
        {
            return null;
        }

        reader.Byte();
        var count = reader.Byte();

        EmoteBuilder? animation = null;
        string? name = null, author = null, description = null;

        for (var i = 0; i < count; i++)
        {
            var id = reader.Byte();
            var version = reader.Byte();
            var size = reader.Int32();

            if (size < 0 || size > reader.Remaining)
            {
                return null;
            }

            var payload = data.AsSpan(reader.Position, size);
            reader.Skip(size);

            if (id == AnimationPacket)
            {
                // What the mod calls a part "bendable" decides how many axes it has in
                // the file, and for parts beyond the usual ones two readers of the mod
                // have disagreed. A reading is right when it ends exactly where the
                // payload does, so both are tried.
                animation = ReadAnimation(payload, version, unknownPartsBend: true)
                            ?? ReadAnimation(payload, version, unknownPartsBend: false);
            }
            else if (id == HeaderPacket)
            {
                try
                {
                    var header = new BigEndian(payload);
                    name = header.String();
                    description = header.String();
                    author = header.String();
                }
                catch (Exception)
                {
                    // The animation is still good without a name: the file has one.
                }
            }
        }

        return animation?.Build(
            TextOr(name, translate, fallbackName),
            TextOr(author, translate, string.Empty),
            TextOr(description, translate, string.Empty));
    }

    private static string TextOr(string? raw, Func<string, string?>? translate, string fallback)
    {
        var text = EmoteText.FromStored(raw, translate);
        return text.Length == 0 ? fallback : text;
    }

    private static readonly string[] FixedParts = { "head", "body", "rightArm", "leftArm", "rightLeg", "leftLeg" };

    private static EmoteBuilder? ReadAnimation(ReadOnlySpan<byte> payload, int version, bool unknownPartsBend)
    {
        if (version > 4)
        {
            return null;
        }

        try
        {
            var reader = new BigEndian(payload);

            // The tick a running emote is at when it is sent: nothing to a file.
            reader.Int32();

            var builder = new EmoteBuilder
            {
                BeginTick = reader.Int32(),
                EndTick = reader.Int32(),
                StopTick = reader.Int32(),
                IsLoop = reader.Bool(),
                ReturnTick = reader.Int32(),
                EaseBeforeKeyframe = reader.Bool()
            };

            reader.Bool();
            int keyframeSize = reader.Byte();

            if (keyframeSize < (version >= 4 ? 13 : 9))
            {
                return null;
            }

            if (version < 2)
            {
                foreach (var name in FixedParts)
                {
                    ReadPart(ref reader, builder, name, version, keyframeSize, unknownPartsBend);
                }
            }
            else
            {
                var parts = reader.Int32();

                if (parts < 0 || parts > 256)
                {
                    return null;
                }

                for (var i = 0; i < parts; i++)
                {
                    var name = reader.String() ?? string.Empty;
                    ReadPart(ref reader, builder, name, version, keyframeSize, unknownPartsBend);
                }
            }

            // The emote's id closes the payload; anything else left over means the
            // layout was misread, and a misread animation is worse than none.
            reader.Skip(16);
            return reader.Remaining == 0 ? builder : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ReadPart(ref BigEndian reader, EmoteBuilder builder, string name, int version, int keyframeSize, bool unknownPartsBend)
    {
        // Inside the binary the names are already the newer ones: "torso" is the chest.
        var part = PartOf(name, version: 3);

        foreach (var axis in BinaryAxes)
        {
            ReadTrack(ref reader, builder, part, axis, version, keyframeSize);
        }

        var bends = name switch
        {
            "head" or "leftItem" or "rightItem" or "left_item" or "right_item" => false,
            _ => part is not null || unknownPartsBend
        };

        if (bends)
        {
            foreach (var axis in BinaryBendAxes)
            {
                ReadTrack(ref reader, builder, part, axis, version, keyframeSize);
            }
        }

        // The scale: read past, the figure has none.
        for (var i = 0; i < (version >= 3 ? 3 : 0); i++)
        {
            ReadTrack(ref reader, builder, null, EmoteAxis.X, version, keyframeSize);
        }
    }

    private static void ReadTrack(ref BigEndian reader, EmoteBuilder builder, EmotePart? part, EmoteAxis axis, int version, int keyframeSize)
    {
        var enabled = version < 2 || reader.Bool();
        var count = reader.Int32();

        if (count < 0)
        {
            return;
        }

        if ((long)count * keyframeSize > reader.Remaining)
        {
            throw new InvalidDataException("A keyframe list runs past the end of the file.");
        }

        for (var i = 0; i < count; i++)
        {
            var next = reader.Position + keyframeSize;
            var tick = reader.Int32();
            var value = reader.Single();
            var easing = EmoteEasings.FromId(reader.Byte());
            reader.Position = next;

            if (enabled && part is { } target)
            {
                builder.Add(target, axis, tick, value, easing);
            }
        }
    }

    /// <summary>The file is Java's: every number most significant byte first.</summary>
    private ref struct BigEndian
    {
        private readonly ReadOnlySpan<byte> _data;

        public BigEndian(ReadOnlySpan<byte> data)
        {
            _data = data;
            Position = 0;
        }

        public int Position { get; set; }

        public int Remaining => _data.Length - Position;

        public byte Byte() => Take(1)[0];

        public bool Bool() => Byte() != 0;

        public int Int32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

        public float Single() => BinaryPrimitives.ReadSingleBigEndian(Take(4));

        public void Skip(int count) => Take(count);

        public string? String()
        {
            var length = Int32();
            return length < 0 ? null : Encoding.UTF8.GetString(Take(length));
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > Remaining)
            {
                throw new EndOfStreamException();
            }

            var slice = _data.Slice(Position, count);
            Position += count;
            return slice;
        }
    }
}

/// <summary>
/// An emote's name, author and description are Minecraft text: a plain string, or an
/// object with a translation key, colours and further pieces. Here it becomes one line.
/// </summary>
internal static class EmoteText
{
    public static string Flatten(JsonElement node, Func<string, string?>? translate)
    {
        var text = new StringBuilder();
        Append(text, node, translate, depth: 0);
        return Clean(text.ToString());
    }

    /// <summary>The binary form stores the same text as a string of JSON; a string that is not JSON is the text itself.</summary>
    public static string FromStored(string? raw, Func<string, string?>? translate)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.TrimStart();

        if (trimmed.Length > 0 && trimmed[0] is '{' or '[' or '"')
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                return Flatten(document.RootElement, translate);
            }
            catch (JsonException)
            {
            }
        }

        return Clean(raw);
    }

    private static void Append(StringBuilder text, JsonElement node, Func<string, string?>? translate, int depth)
    {
        if (depth > 8)
        {
            return;
        }

        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                text.Append(node.GetString());
                break;

            case JsonValueKind.Number:
                text.Append(node.GetRawText());
                break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    Append(text, item, translate, depth + 1);
                }

                break;

            case JsonValueKind.Object:
                if (node.TryGetProperty("text", out var literal) && literal.ValueKind == JsonValueKind.String)
                {
                    text.Append(literal.GetString());
                }
                else if (node.TryGetProperty("translate", out var key) && key.ValueKind == JsonValueKind.String)
                {
                    var translated = translate?.Invoke(key.GetString()!);

                    if (translated is null && node.TryGetProperty("fallback", out var fallback) &&
                        fallback.ValueKind == JsonValueKind.String)
                    {
                        translated = fallback.GetString();
                    }

                    text.Append(translated);
                }

                if (node.TryGetProperty("extra", out var extra))
                {
                    Append(text, extra, translate, depth + 1);
                }

                break;
        }
    }

    /// <summary>Without the colour codes (a section sign and a letter), on one line, and not endless.</summary>
    private static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '§')
            {
                i++;
                continue;
            }

            clean.Append(char.IsControl(text[i]) ? ' ' : text[i]);
        }

        var result = clean.ToString().Trim();
        return result.Length > 120 ? result[..120].TrimEnd() + "…" : result;
    }
}
