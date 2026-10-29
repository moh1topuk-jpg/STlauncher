using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Content;

/// <summary>
/// Accepts "resourcepack", "resourcePack", "ResourcePack" and "resource_pack" alike,
/// and falls back to a caller-defined value for unknown members so that a newer catalog
/// never breaks an older launcher.
/// </summary>
public sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly TEnum _fallback;
    private readonly (string Alias, TEnum Value)[] _aliases;

    /// <param name="aliases">Other spellings of a member, such as "shader" for ShaderPack.</param>
    public TolerantEnumConverter(TEnum fallback, params (string Alias, TEnum Value)[] aliases)
    {
        _fallback = fallback;
        _aliases = aliases.Select(a => (Normalize(a.Alias), a.Value)).ToArray();
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var normalized = Normalize(reader.GetString());

                foreach (var (alias, value) in _aliases)
                {
                    if (alias == normalized)
                    {
                        return value;
                    }
                }

                foreach (var name in Enum.GetNames<TEnum>())
                {
                    if (Normalize(name) == normalized)
                    {
                        return Enum.Parse<TEnum>(name);
                    }
                }

                return _fallback;

            case JsonTokenType.Number when reader.TryGetInt32(out var value):
                return Enum.IsDefined(typeof(TEnum), value) ? (TEnum)Enum.ToObject(typeof(TEnum), value) : _fallback;

            default:
                return _fallback;
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        var name = value.ToString();
        writer.WriteStringValue(char.ToLowerInvariant(name[0]) + name[1..]);
    }

    private static string Normalize(string? value)
        => new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
}