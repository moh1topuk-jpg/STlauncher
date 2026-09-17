using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;

namespace STlauncher.App.Services;

/// <summary>
/// Swaps the merged language dictionary at runtime. XAML binds strings with
/// {DynamicResource Key}, so switching the language updates the UI immediately.
/// </summary>
public sealed class LocalizationService
{
    public const string DefaultLanguage = "ru";

    private static readonly string[] Supported = { "ru", "en" };
    private static readonly Uri BaseUri = new("avares://STlauncher.App/");

    private ResourceInclude? _current;

    public IReadOnlyList<string> AvailableLanguages => Supported;

    public string Current { get; private set; } = DefaultLanguage;

    public static bool IsSupported(string? language)
        => !string.IsNullOrWhiteSpace(language) &&
           Supported.Contains(language, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? language)
        => IsSupported(language) ? language!.ToLowerInvariant() : DefaultLanguage;

    public void Apply(string? language)
    {
        var normalized = Normalize(language);
        Current = normalized;

        if (Application.Current is not { } app)
        {
            return;
        }

        var include = new ResourceInclude(BaseUri)
        {
            Source = new Uri(BaseUri, $"Assets/Lang/{normalized}.axaml")
        };

        var merged = app.Resources.MergedDictionaries;

        if (_current is not null)
        {
            merged.Remove(_current);
        }

        merged.Add(include);
        _current = include;

#if DEBUG
        // Catches a mistyped language file path or a missing key set during development.
        if (!app.TryFindResource("App_Title", out _))
        {
            Console.Error.WriteLine($"[localization] resources for '{normalized}' did not load");
        }
#endif
    }
}