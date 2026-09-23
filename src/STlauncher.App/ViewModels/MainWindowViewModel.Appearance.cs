using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.App.Services;

namespace STlauncher.App.ViewModels;

/// <summary>A swatch in the settings: the accent's colour and whether it is the current one.</summary>
public partial class AccentOption : ObservableObject
{
    public AccentOption(AccentPreset preset)
    {
        Key = preset.Key;
        Brush = new SolidColorBrush(Color.Parse(preset.Hex));
        Display = preset.Key switch
        {
            "crimson" => MainWindowViewModel.Localize("Accent_Crimson", "Crimson"),
            "orange" => MainWindowViewModel.Localize("Accent_Orange", "Orange"),
            "gold" => MainWindowViewModel.Localize("Accent_Gold", "Gold"),
            "green" => MainWindowViewModel.Localize("Accent_Green", "Green"),
            "teal" => MainWindowViewModel.Localize("Accent_Teal", "Teal"),
            "blue" => MainWindowViewModel.Localize("Accent_Blue", "Blue"),
            "violet" => MainWindowViewModel.Localize("Accent_Violet", "Violet"),
            _ => preset.Key
        };
    }

    public string Key { get; }

    public IBrush Brush { get; }

    public string Display { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Dark or light, and the accent colour: applied at once, saved with the rest.</summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<AccentOption> AccentOptions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThemeDark))]
    [NotifyPropertyChangedFor(nameof(IsThemeLight))]
    private string _theme = ThemeService.DefaultTheme;

    [ObservableProperty]
    private string _accent = ThemeService.DefaultAccent;

    public bool IsThemeDark => Theme != "light";

    public bool IsThemeLight => Theme == "light";

    private bool _appearanceLoading;

    private void LoadAppearance(string? theme, string? accent)
    {
        _appearanceLoading = true;

        AccentOptions.Clear();

        foreach (var preset in ThemeService.Accents)
        {
            AccentOptions.Add(new AccentOption(preset));
        }

        Theme = ThemeService.NormalizeTheme(theme);
        Accent = ThemeService.NormalizeAccent(accent);
        MarkSelectedAccent();

        _appearanceLoading = false;
    }

    [RelayCommand]
    private void SelectTheme(string? theme) => Theme = ThemeService.NormalizeTheme(theme);

    [RelayCommand]
    private void SelectAccent(string? accent) => Accent = ThemeService.NormalizeAccent(accent);

    partial void OnThemeChanged(string value) => ApplyAppearance();

    partial void OnAccentChanged(string value)
    {
        MarkSelectedAccent();
        ApplyAppearance();
    }

    private void MarkSelectedAccent()
    {
        foreach (var option in AccentOptions)
        {
            option.IsSelected = string.Equals(option.Key, Accent, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ApplyAppearance()
    {
        if (_appearanceLoading)
        {
            return;
        }

        _themes.Apply(Theme, Accent);
        PersistSettings();
    }
}
