using System.Text.Json;
using System.Text.Json.Serialization;

namespace STlauncher.Core.Metadata;

public static class MetadataJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        options.Converters.Add(new GameArgumentConverter());
        options.Converters.Add(new TolerantDateTimeOffsetConverter());
        return options;
    }
}