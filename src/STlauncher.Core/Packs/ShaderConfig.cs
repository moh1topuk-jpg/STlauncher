using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace STlauncher.Core.Packs;

/// <summary>The mods that load shader packs. Oculus is Iris ported to Forge, config included.</summary>
public enum ShaderLoader
{
    /// <summary>Fabric, Quilt and NeoForge.</summary>
    Iris,

    /// <summary>Forge.</summary>
    Oculus
}

/// <summary>The shader pack the loader will use, and whether shaders are on at all.</summary>
public sealed record ShaderSettings(string? ShaderPack, bool Enabled);

/// <summary>
/// Iris keeps its choice in config/iris.properties: <c>shaderPack=Name.zip</c> and
/// <c>enableShaders=true</c>; Oculus keeps the same two keys in config/oculus.properties.
/// Writing them from the launcher is the whole trick behind "make this shader active" -
/// the same thing the in-game menu does.
/// </summary>
public static class ShaderConfig
{
    public static string RelativePath(ShaderLoader loader) => loader switch
    {
        ShaderLoader.Oculus => "config/oculus.properties",
        _ => "config/iris.properties"
    };

    /// <summary>Which shader loaders are in the build, judged by the enabled jars' names.</summary>
    public static IReadOnlyList<ShaderLoader> Detect(IEnumerable<string> enabledModFileNames)
    {
        var found = new List<ShaderLoader>();

        foreach (var name in enabledModFileNames)
        {
            if (name.StartsWith("iris", StringComparison.OrdinalIgnoreCase) && !found.Contains(ShaderLoader.Iris))
            {
                found.Add(ShaderLoader.Iris);
            }
            else if (name.StartsWith("oculus", StringComparison.OrdinalIgnoreCase) && !found.Contains(ShaderLoader.Oculus))
            {
                found.Add(ShaderLoader.Oculus);
            }
        }

        return found;
    }

    public static ShaderSettings Read(string gameDirectory, ShaderLoader loader = ShaderLoader.Iris)
    {
        var path = FullPath(gameDirectory, loader);

        if (!File.Exists(path))
        {
            return new ShaderSettings(null, false);
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

            return new ShaderSettings(pack, enabled);
        }
        catch (Exception)
        {
            return new ShaderSettings(null, false);
        }
    }

    /// <summary>Sets the active pack, or turns shaders off with null. Other keys are kept.</summary>
    public static void Write(string gameDirectory, string? shaderPack, ShaderLoader loader = ShaderLoader.Iris)
    {
        var path = FullPath(gameDirectory, loader);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();

        Set(lines, "shaderPack", shaderPack is null ? string.Empty : Escape(shaderPack));
        Set(lines, "enableShaders", shaderPack is null ? "false" : "true");

        AtomicFile.WriteAllLines(path, lines);
    }

    private static string FullPath(string gameDirectory, ShaderLoader loader)
        => Path.Combine(gameDirectory, RelativePath(loader).Replace('/', Path.DirectorySeparatorChar));

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
