using System;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace STlauncher.Core.Skins;

/// <summary>
/// The launcher's one entry in CustomSkinLoader's load list, and how it gets there.
/// </summary>
/// <remarks>
/// The mod keeps <c>CustomSkinLoader/CustomSkinLoader.json</c> in the game folder: a few
/// switches and a <c>loadlist</c> of places to ask for a skin, mixed in order - the first
/// place that has a skin for a name decides it. Its own default list starts with a Mojang
/// lookup by nickname. An offline player's nickname may well be a stranger's licensed
/// account, so the launcher's entry goes first: the file wins, and nobody else's skin is
/// taken for the player's own.
///
/// The entry is a "Legacy" one, the mod's type for plain files by name. A path that is
/// not http(s) is read from the mod's data folder, and <c>{USERNAME}</c> stays in it on
/// purpose: a fixed file name would put the player's skin on everybody they meet.
///
/// Everything else in the file is the mod's or the player's and is carried over as it is.
/// </remarks>
public static class InGameSkinConfig
{
    /// <summary>The name of the launcher's entry; an entry with this name is the launcher's to rewrite.</summary>
    public const string EntryName = "STlauncher";

    /// <summary>The mod's folder inside the game directory.</summary>
    public const string DataFolder = "CustomSkinLoader";

    public const string ConfigFileName = "CustomSkinLoader.json";

    /// <summary>
    /// Where entries wait for the mod to add them itself: on its next start it puts each
    /// one in front of the load list and removes the file.
    /// </summary>
    public const string ExtraListFolder = "ExtraList";

    public const string ExtraListFileName = "STlauncher.json";

    /// <summary>The launcher's own folder for the skin copy, apart from the mod's LocalSkin where a player may keep files.</summary>
    public const string SkinFolder = "STlauncher/skins";

    public const string SkinPattern = SkinFolder + "/{USERNAME}.png";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,

        // The pattern's braces and a player's Cyrillic entry names stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>The mod's names for the two arm widths.</summary>
    public static string ModelName(bool slim) => slim ? "slim" : "default";

    /// <summary>The launcher's entry: the skin by nickname from the launcher's folder, and the model it was drawn for.</summary>
    public static JsonObject Entry(bool slim) => new()
    {
        ["name"] = EntryName,
        ["type"] = "Legacy",
        ["checkPNG"] = false,
        ["skin"] = SkinPattern,
        ["model"] = ModelName(slim)
    };

    /// <summary>The entry as a file for the mod's ExtraList folder.</summary>
    public static string ExtraListText(bool slim) => Entry(slim).ToJsonString(WriteOptions);

    /// <summary>
    /// The config with the launcher's entry first. Null when the text already says exactly
    /// that, so a file nobody needs to touch is not rewritten before every launch.
    /// </summary>
    /// <exception cref="JsonException">The text is not the mod's config: not JSON, or not an object with a list.</exception>
    public static string? Merge(string existing, bool slim)
    {
        var root = JsonNode.Parse(existing, documentOptions: ReadOptions) as JsonObject
                   ?? throw new JsonException("The config is not a JSON object.");

        var entry = Entry(slim);
        JsonArray list;

        switch (root["loadlist"])
        {
            case null:
                list = new JsonArray();
                root["loadlist"] = list;
                break;
            case JsonArray array:
                list = array;
                break;
            default:
                throw new JsonException("The load list is not a list.");
        }

        var ours = list.Where(IsOurs).ToList();

        if (ours.Count == 1 && ReferenceEquals(list[0], ours[0]) && JsonNode.DeepEquals(ours[0], entry))
        {
            return null;
        }

        foreach (var old in ours)
        {
            list.Remove(old);
        }

        list.Insert(0, entry);
        return root.ToJsonString(WriteOptions);
    }

    private static bool IsOurs(JsonNode? node)
        => node is JsonObject item &&
           item["name"] is JsonValue name &&
           name.TryGetValue<string>(out var text) &&
           string.Equals(text, EntryName, StringComparison.OrdinalIgnoreCase);
}
