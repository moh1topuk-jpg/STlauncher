using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Metadata;

public sealed class GameArgument
{
    public List<Rule> Rules { get; init; } = new();

    public List<string> Values { get; init; } = new();

    public bool IsConditional => Rules.Count > 0;
}

public sealed class GameArgumentConverter : JsonConverter<GameArgument>
{
    public override GameArgument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new GameArgument { Values = { reader.GetString() ?? string.Empty } };

            case JsonTokenType.StartObject:
                return ReadObject(ref reader, options);

            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for a game argument.");
        }
    }

    private static GameArgument ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        List<Rule>? rules = null;
        var values = new List<string>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var propertyName = reader.GetString();
            reader.Read();

            switch (propertyName)
            {
                case "rules":
                    rules = JsonSerializer.Deserialize<List<Rule>>(ref reader, options) ?? new List<Rule>();
                    break;

                case "value":
                    values.AddRange(ReadValues(ref reader));
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        return new GameArgument
        {
            Rules = rules ?? new List<Rule>(),
            Values = values
        };
    }

    private static List<string> ReadValues(ref Utf8JsonReader reader)
    {
        var values = new List<string>();

        if (reader.TokenType == JsonTokenType.String)
        {
            var single = reader.GetString();
            if (single is not null)
            {
                values.Add(single);
            }

            return values;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return values;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var value = reader.GetString();
                if (value is not null)
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    public override void Write(Utf8JsonWriter writer, GameArgument value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}