using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;

namespace STlauncher.App.Services;

/// <summary>One accent the player can pick: a name for the settings and the base colour.</summary>
public sealed record AccentPreset(string Key, string Hex);

/// <summary>
/// The look of the launcher: a dark or light palette and an accent colour. Both are
/// resource dictionaries swapped at runtime; every control binds its colours with
/// {DynamicResource}, so a change repaints the window at once, the same way a language
/// change does.
/// </summary>
public sealed class ThemeService
{
    public const string DefaultTheme = "dark";
    public const string DefaultAccent = "crimson";

    private static readonly Uri BaseUri = new("avares://STlauncher.App/");

    public static readonly IReadOnlyList<AccentPreset> Accents = new[]
    {
        new AccentPreset("crimson", "#A3243F"),
        new AccentPreset("orange", "#C2561C"),
        new AccentPreset("gold", "#A8801A"),
        new AccentPreset("green", "#2E8B57"),
        new AccentPreset("teal", "#1F8A7A"),
        new AccentPreset("blue", "#2F6FB5"),
        new AccentPreset("violet", "#6D3FA8")
    };

    private ResourceInclude? _colors;
    private ResourceInclude? _fluent;
    private ResourceDictionary? _accent;

    public string Theme { get; private set; } = DefaultTheme;

    public string Accent { get; private set; } = DefaultAccent;

    public static string NormalizeTheme(string? theme)
        => string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : DefaultTheme;

    public static string NormalizeAccent(string? accent)
        => Accents.Any(a => string.Equals(a.Key, accent, StringComparison.OrdinalIgnoreCase))
            ? accent!.ToLowerInvariant()
            : DefaultAccent;

    public void Apply(string? theme, string? accent)
    {
        Theme = NormalizeTheme(theme);
        Accent = NormalizeAccent(accent);

        if (Application.Current is not { } app)
        {
            return;
        }

        var merged = app.Resources.MergedDictionaries;

        // Palette first, accent after it: later dictionaries win on a shared key, so the
        // accent overrides the palette's default crimson, and the language dictionary,
        // added by LocalizationService after both, shares no keys with either.
        var colors = Include(Theme == "light" ? "Styles/ColorsLight.axaml" : "Styles/Colors.axaml");
        var fluent = Include(Theme == "light" ? "Styles/FluentLight.axaml" : "Styles/Fluent.axaml");
        var accentDictionary = BuildAccent(Accents.First(a => a.Key == Accent).Hex, Theme == "light");

        Replace(merged, ref _colors, colors, preferredIndex: 0);
        Replace(merged, ref _fluent, fluent, preferredIndex: 1);
        Replace(merged, ref _accent, accentDictionary, preferredIndex: 2);
    }

    private static ResourceInclude Include(string path)
        => new(BaseUri) { Source = new Uri(BaseUri, path) };

    private static void Replace<T>(IList<IResourceProvider> merged, ref T? current, T next, int preferredIndex)
        where T : class, IResourceProvider
    {
        if (current is not null)
        {
            var index = merged.IndexOf(current);

            if (index >= 0)
            {
                merged[index] = next;
                current = next;
                return;
            }
        }

        // First run: the startup dictionaries from App.axaml sit at 0 (colours), 1 (fluent),
        // 2 (icons). Colours and fluent are replaced in place; the accent goes after icons.
        if (current is null && preferredIndex < 2 && merged.Count > preferredIndex && merged[preferredIndex] is ResourceInclude)
        {
            merged[preferredIndex] = next;
        }
        else
        {
            merged.Add(next);
        }

        current = next;
    }

    /// <summary>
    /// Everything that carries the accent, derived from one colour: the three button
    /// states, the hero gradient and glow, the hairline, and the Fluent keys behind
    /// toggles, focused inputs and selection.
    /// </summary>
    private static ResourceDictionary BuildAccent(string hex, bool light)
    {
        var accent = Color.Parse(hex);
        var hover = Shift(accent, light ? -0.06 : 0.08);
        var pressed = Shift(accent, -0.14);
        var lighter = Shift(accent, 0.18);
        var lightest = Shift(accent, 0.30);
        var darker = Shift(accent, -0.22);

        var dictionary = new ResourceDictionary
        {
            ["AccentColor"] = accent,
            ["AccentHoverColor"] = hover,
            ["AccentPressedColor"] = pressed,
            ["AccentBrush"] = new SolidColorBrush(accent),
            ["AccentHoverBrush"] = new SolidColorBrush(hover),
            ["AccentPressedBrush"] = new SolidColorBrush(pressed),
            ["AccentHairlineBrush"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(WithAlpha(accent, 0), 0),
                    new GradientStop(WithAlpha(accent, 0.79), 0.5),
                    new GradientStop(WithAlpha(accent, 0), 1)
                }
            },
            ["HeroGradientBrush"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(hover, 0), new GradientStop(accent, 1) }
            },
            ["HeroShadow"] = BoxShadows.Parse($"0 6 22 0 {ToHex(WithAlpha(accent, 0.28))}"),
            ["HeroShadowHover"] = BoxShadows.Parse($"0 10 34 0 {ToHex(WithAlpha(accent, 0.45))}"),

            ["SystemAccentColor"] = accent,
            ["SystemAccentColorDark1"] = pressed,
            ["SystemAccentColorDark2"] = darker,
            ["SystemAccentColorDark3"] = Shift(accent, -0.3),
            ["SystemAccentColorLight1"] = hover,
            ["SystemAccentColorLight2"] = lighter,
            ["SystemAccentColorLight3"] = lightest,
            ["ToggleSwitchFillOn"] = new SolidColorBrush(accent),
            ["ToggleSwitchFillOnPointerOver"] = new SolidColorBrush(hover),
            ["ToggleSwitchFillOnPressed"] = new SolidColorBrush(pressed),
            ["TextControlBorderBrushFocused"] = new SolidColorBrush(accent),
            ["ComboBoxBorderBrushPressed"] = new SolidColorBrush(accent),
            ["CheckBoxCheckBackgroundFillChecked"] = new SolidColorBrush(accent),
            ["CheckBoxCheckBackgroundFillCheckedPointerOver"] = new SolidColorBrush(hover)
        };

        return dictionary;
    }

    /// <summary>Lightens (positive) or darkens (negative) a colour by a fraction of the way to white or black.</summary>
    private static Color Shift(Color color, double amount)
    {
        byte Channel(byte c) => amount >= 0
            ? (byte)Math.Round(c + (255 - c) * amount)
            : (byte)Math.Round(c * (1 + amount));

        return Color.FromArgb(color.A, Channel(color.R), Channel(color.G), Channel(color.B));
    }

    private static Color WithAlpha(Color color, double alpha)
        => Color.FromArgb((byte)Math.Round(255 * alpha), color.R, color.G, color.B);

    private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
