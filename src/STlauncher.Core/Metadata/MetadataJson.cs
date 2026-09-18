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

        // Profiles written by other launchers are still valid JSON, just not the shape
        // Mojang uses: whole numbers arrive as 1.0 and 21.0 and would otherwise take the
        // entire document down with them.
        options.Converters.Add(new TolerantInt32Converter());
        return options;
    }
}