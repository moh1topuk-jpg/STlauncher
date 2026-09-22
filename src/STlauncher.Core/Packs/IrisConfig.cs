using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace STlauncher.Core.Packs;

/// <summary>The shader pack Iris will load, and whether shaders are on at all.</summary>
public sealed record IrisSettings(string? ShaderPack, bool Enabled);

/// <summary>
/// Iris keeps its choice in config/iris.properties: <c>shaderPack=Name.zip</c> and
/// <c>enableShaders=true</c>. Writing those two keys from the launcher is the whole
/// trick behind "make this shader active" - the same thing the in-game menu does.
/// </summary>
public static class IrisConfig
{
    public const string RelativePath = "config/iris.properties";

    public static IrisSettings Read(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            return new IrisSettings(null, false);
        }

        try
        {
            string? pack = null;
            var enabled = false;

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();

                if (line.Length == 0 || line[0] is '#' or '!')
                {
                    continue;
                }

                var eq = line.IndexOf('=');

                if (eq <= 0)
                {
                    continue;
                }

                var key = line[..eq].Trim();
                var value = Unescape(line[(eq + 1)..].Trim());

                if (key == "shaderPack")
                {
                    pack = value.Length == 0 ? null : value;
                }
                else if (key == "enableShaders")
                {
                    enabled = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                }
            }

            return new IrisSettings(pack, enabled);
        }
        catch (Exception)
        {
            return new IrisSettings(null, false);
        }
    }

    /// <summary>Sets the active pack, or turns shaders off with null. Other keys are kept.</summary>
    public static void Write(string gameDirectory, string? shaderPack)
    {
        var path = Path.Combine(gameDirectory, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();

        Set(lines, "shaderPack", shaderPack is null ? string.Empty : Escape(shaderPack));
        Set(lines, "enableShaders", shaderPack is null ? "false" : "true");

        AtomicFile.WriteAllLines(path, lines);
    }

    private static void Set(List<string> lines, string key, string value)
    {
        var index = lines.FindIndex(l => l.TrimStart().StartsWith(key + "=", StringComparison.Ordinal));
        var line = key + "=" + value;

        if (index >= 0)
        {
            lines[index] = line;
        }
        else
        {
            lines.Add(line);
        }
    }

    // Java properties escape backslashes, colons and equals signs in values.
    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace(":", "\\:").Replace("=", "\\=");

    private static string Unescape(string value)
        => value.Replace("\\:", ":").Replace("\\=", "=").Replace("\\\\", "\\");
}
