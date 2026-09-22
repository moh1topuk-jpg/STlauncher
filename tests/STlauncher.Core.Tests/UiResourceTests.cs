using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace STlauncher.Core.Tests;

/// <summary>
/// Guards the UI resources: every {DynamicResource} key used in XAML must exist, and both
/// language dictionaries must define the same keys. Missing keys render as empty text,
/// which is invisible in build output.
/// </summary>
public class UiResourceTests
{
    private static readonly Regex KeyRegex = new("x:Key=\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex ResourceRegex = new(
        @"(?:DynamicResource|StaticResource)\s+([A-Za-z0-9_]+)",
        RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "STlauncher.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static Dictionary<string, HashSet<string>> ReadKeys(string path)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var text = File.ReadAllText(path);

        foreach (Match match in KeyRegex.Matches(text))
        {
            var key = match.Groups[1].Value;

            if (!result.TryGetValue(key, out var files))
            {
                files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[key] = files;
            }

            files.Add(Path.GetFileName(path));
        }

        return result;
    }

    [Fact]
    public void LanguageDictionaries_DefineTheSameKeys()
    {
        var root = RepoRoot();
        var langDir = Path.Combine(root, "src", "STlauncher.App", "Assets", "Lang");

        var ru = ReadKeys(Path.Combine(langDir, "ru.axaml"));
        var en = ReadKeys(Path.Combine(langDir, "en.axaml"));

        var missingInEn = ru.Keys.Except(en.Keys, StringComparer.Ordinal).OrderBy(k => k).ToList();
        var missingInRu = en.Keys.Except(ru.Keys, StringComparer.Ordinal).OrderBy(k => k).ToList();

        Assert.True(missingInEn.Count == 0, "Missing in en.axaml: " + string.Join(", ", missingInEn));
        Assert.True(missingInRu.Count == 0, "Missing in ru.axaml: " + string.Join(", ", missingInRu));
    }

    [Fact]
    public void EveryResourceUsedInXaml_IsDefined()
    {
        var root = RepoRoot();
        var appDir = Path.Combine(root, "src", "STlauncher.App");

        var defined = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dictionary in new[]
                 {
                     Path.Combine(appDir, "Styles", "Colors.axaml"),
                     Path.Combine(appDir, "Styles", "Fluent.axaml"),
                Path.Combine(appDir, "Styles", "Icons.axaml"),
                     Path.Combine(appDir, "Assets", "Lang", "ru.axaml"),
                     Path.Combine(appDir, "Assets", "Lang", "en.axaml")
                 })
        {
            foreach (var key in ReadKeys(dictionary).Keys)
            {
                defined.Add(key);
            }
        }

        var used = new SortedSet<string>(StringComparer.Ordinal);
        var sources = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appDir, "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            sources.Add(file);

            foreach (Match match in ResourceRegex.Matches(text))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(sources);

        // Theme-derived keys provided by FluentTheme are resolved at runtime; keep the
        // allow-list explicit so a genuinely missing key still fails.
        var externallyProvided = new HashSet<string>(StringComparer.Ordinal)
        {
            "SystemControlForegroundBaseHighBrush",
            "SystemControlBackgroundChromeMediumLowBrush"
        };

        var missing = used
            .Where(key => !defined.Contains(key) && !externallyProvided.Contains(key))
            .ToList();

        Assert.True(missing.Count == 0, "Undefined resource keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void LocalizeCalls_UseExistingKeys()
    {
        var root = RepoRoot();
        var appDir = Path.Combine(root, "src", "STlauncher.App");
        var langDir = Path.Combine(appDir, "Assets", "Lang");

        var defined = ReadKeys(Path.Combine(langDir, "ru.axaml")).Keys.ToHashSet(StringComparer.Ordinal);
        var callRegex = new Regex(@"Localize\(\s*""([^""]+)""", RegexOptions.Compiled);

        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in callRegex.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Value;

                if (!defined.Contains(key))
                {
                    missing.Add($"{key} ({Path.GetFileName(file)})");
                }
            }
        }

        Assert.True(missing.Count == 0, "Unknown localization keys: " + string.Join(", ", missing));
    }
}