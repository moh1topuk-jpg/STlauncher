using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Metadata;

/// <summary>
/// Reads an integer that was not necessarily written as one.
/// </summary>
/// <remarks>
/// Version profiles are not all written by Mojang. TLauncher, for one, writes
/// <c>"complianceLevel": 1.0</c> and <c>"majorVersion": 21.0</c> - valid JSON numbers that
/// System.Text.Json refuses to hand to an <c>int</c>, fractional notation and all. The
/// whole profile then fails to parse, which meant the launcher could not resolve - let
/// alone start - any version another launcher had installed.
///
/// Anything that is not a number at all reads as 0; callers treat that as "not stated",
/// which is what a missing field means anyway.
/// </remarks>
public sealed class TolerantInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var value))
                {
                    return value;
                }

                // "21.0" and the like: truncate rather than refuse the whole document.
                return reader.TryGetDouble(out var number) ? (int)number : 0;

            case JsonTokenType.String:
                var text = reader.GetString();

                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble)
                    ? (int)asDouble
                    : 0;

            case JsonTokenType.True:
                return 1;

            case JsonTokenType.False:
            case JsonTokenType.Null:
                return 0;

            default:
                reader.Skip();
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
