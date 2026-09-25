using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Launch;

namespace STlauncher.App.ViewModels;

/// <summary>
/// Two settings that change what the machine does rather than what the launcher does:
/// how big the interface is drawn, and which graphics card Windows gives the game.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The scales offered, as the settings page lists them.</summary>
    public static readonly double[] UiScales = { 0.9, 1.0, 1.1, 1.25 };

    /// <summary>1.0 is the designed size. The window multiplies its compact factor by this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUiScale90))]
    [NotifyPropertyChangedFor(nameof(IsUiScale100))]
    [NotifyPropertyChangedFor(nameof(IsUiScale110))]
    [NotifyPropertyChangedFor(nameof(IsUiScale125))]
    private double _uiScale = 1.0;

    public bool IsUiScale90 => Math.Abs(UiScale - 0.9) < 0.01;

    public bool IsUiScale100 => Math.Abs(UiScale - 1.0) < 0.01;

    public bool IsUiScale110 => Math.Abs(UiScale - 1.1) < 0.01;

    public bool IsUiScale125 => Math.Abs(UiScale - 1.25) < 0.01;

    partial void OnUiScaleChanged(double value) => PersistSettings();

    /// <summary>A value from settings.json that is not one of the choices snaps to the nearest.</summary>
    private static double NormalizeUiScale(double value)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return 1.0;
        }

        var best = 1.0;

        foreach (var option in UiScales)
        {
            if (Math.Abs(option - value) < Math.Abs(best - value))
            {
                best = option;
            }
        }

        return best;
    }

    [RelayCommand]
    private void SelectUiScale(string? value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
        {
            UiScale = NormalizeUiScale(scale);
        }
    }

    // ===================== Graphics card =====================

    /// <summary>
    /// On by default: a laptop with two cards runs Java on the weak one until someone
    /// changes a setting most players have never seen. Off leaves that setting alone.
    /// </summary>
    [ObservableProperty]
    private bool _preferDiscreteGpu = true;

    partial void OnPreferDiscreteGpuChanged(bool value) => PersistSettings();

    /// <summary>Before each launch, for whichever Java the launch resolved to. Never blocks a launch.</summary>
    private void ApplyGpuPreference(string javaPath)
    {
        if (!PreferDiscreteGpu)
        {
            return;
        }

        var written = GpuPreference.TryPreferHighPerformance(javaPath, out var error);

        if (written)
        {
            AppendConsole($"[gpu] {javaPath}: set to the high-performance graphics card in Windows");
        }
        else if (error is not null)
        {
            AppendConsole($"[gpu] could not set the graphics card for {javaPath}: {error}");
        }
    }
}
