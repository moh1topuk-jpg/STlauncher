using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Metadata;

/// <summary>
/// Mojang uses "2000-01-01T00:00:00+00:00" while Fabric/Quilt profiles use
/// "2000-01-01T00:00:00+0000". System.Text.Json only accepts the first form,
/// so dates are parsed leniently here.
/// </summary>
public sealed class TolerantDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var text = reader.GetString();
                if (!string.IsNullOrEmpty(text) &&
                    DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
                {
                    return value;
                }

                return default;

            case JsonTokenType.Number:
                return reader.TryGetInt64(out var milliseconds)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                    : default;

            default:
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
}