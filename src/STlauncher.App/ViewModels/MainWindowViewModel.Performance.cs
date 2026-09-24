using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Launch;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>
/// One button instead of twelve settings: a preset writes the graphics options the game
/// reads at start, sets the memory for this machine, and, for a build of the player's
/// own, adds the optimisation mods the server build already ships with.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The mods that make Fabric fast without changing how the game plays.</summary>
    private static readonly (string Slug, string Title)[] OptimizationMods =
    {
        ("sodium", "Sodium"),
        ("lithium", "Lithium"),
        ("ferrite-core", "FerriteCore"),
        ("entityculling", "Entity Culling"),
        ("immediatelyfast", "ImmediatelyFast")
    };

    [ObservableProperty]
    private bool _isApplyingPreset;

    [ObservableProperty]
    private string _presetStatus = string.Empty;

    /// <summary>Only a player's own Fabric or Quilt build can take the mods; the catalog build has them.</summary>
    public bool CanInstallOptimizationMods
        => !IsCatalogInstance && SelectedLoader is LoaderKind.Fabric or LoaderKind.Quilt && IsBuildConfigured;

    [RelayCommand]
    private async Task ApplyPerformancePresetAsync(string? preset)
    {
        if (IsGameRunning || IsApplyingPreset || !Enum.TryParse<PerformancePreset>(preset, ignoreCase: true, out var parsed))
        {
            return;
        }

        try
        {
            IsApplyingPreset = true;

            GameOptions.ApplyPerformancePreset(InstanceDirectory, parsed);

            // Memory follows the machine, not the preset: a weak PC gets less because it has
            // less, a strong one gets the recommended quarter of its RAM.
            MaxMemoryMb = parsed == PerformancePreset.Low
                ? Math.Min(RecommendedMemoryMb, 3072)
                : RecommendedMemoryMb;

            var installed = 0;

            if (CanInstallOptimizationMods && parsed != PerformancePreset.High)
            {
                installed = await InstallOptimizationModsAsync();
            }

            PresetStatus = installed > 0
                ? Localize("Perf_AppliedWithMods", "Applied: graphics settings, {0} MB of memory, {1} optimisation mods added", (int)MaxMemoryMb, installed)
                : Localize("Perf_Applied", "Applied: graphics settings and {0} MB of memory. The game reads them on the next start.", (int)MaxMemoryMb);
        }
        catch (Exception ex)
        {
            PresetStatus = Localize("Perf_Failed", "Could not apply the preset: {0}", ex.Message);
            AppendConsole(ex.ToString());
        }
        finally
        {
            IsApplyingPreset = false;
        }
    }

    private async Task<int> InstallOptimizationModsAsync()
    {
        var installed = 0;

        foreach (var (slug, title) in OptimizationMods)
        {
            if (IsProjectInstalled(slug))
            {
                continue;
            }

            var project = await _modrinth.GetProjectAsync(slug);

            if (project is null)
            {
                continue;
            }

            var versions = await _modrinth.GetVersionsAsync(project.Id, SelectedVersion?.Id, LoaderFor(ProjectTypes.Mod));
            var pick = ModrinthClient.SelectPreferred(versions);

            if (pick is null)
            {
                AppendConsole($"[perf] {title}: no version for this build, skipped");
                continue;
            }

            PresetStatus = Localize("Perf_Installing", "Adding {0}…", title);
            await InstallProjectWithDependenciesAsync(pick, project.Slug, project.Title, project.IconUrl, 0, ProjectTypes.Mod);
            installed++;
        }

        if (installed > 0)
        {
            RefreshMods();
        }

        return installed;
    }
}
