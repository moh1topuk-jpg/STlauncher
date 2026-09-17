using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace STlauncher.Core.Launch;

/// <summary>
/// Prepares options.txt before the first launch so a player skips the language picker
/// and the accessibility onboarding, and starts with the server resource pack accepted.
/// Existing values are never overwritten - only missing keys are added.
/// </summary>
public static class GameOptions
{
    public const string FileName = "options.txt";

    public static void EnsureDefaults(string gameDirectory, string languageCode = "ru_ru")
    {
        try
        {
            Directory.CreateDirectory(gameDirectory);

            var path = Path.Combine(gameDirectory, FileName);
            var lines = File.Exists(path)
                ? File.ReadAllLines(path).ToList()
                : new List<string>();

            var changed = false;

            changed |= Ensure(lines, "lang", languageCode);
            changed |= Ensure(lines, "narrator", "0");
            changed |= Ensure(lines, "onboardAccessibility", "false");
            changed |= Ensure(lines, "skipMultiplayerWarning", "true");

            if (changed)
            {
                AtomicFile.WriteAllLines(path, lines);
            }
        }
        catch (Exception)
        {
            // Game options are a convenience; a failure must not stop the launch.
        }
    }

    private static bool Ensure(List<string> lines, string key, string value)
    {
        if (lines.Any(line => line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        lines.Add($"{key}:{value}");
        return true;
    }
}