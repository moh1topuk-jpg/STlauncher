using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>Which part of the build's mod list is on screen.</summary>
public enum ModsScope
{
    All,
    Catalog,
    Own,
    Disabled
}

/// <summary>
/// The screens' shape: what is folded away, which sheet is open, which slice of the mod
/// list is shown. None of it is saved; every start opens with the important things up and
/// the rare things folded.
/// </summary>
public partial class MainWindowViewModel
{
    // ===================== Builds: the settings sheet and the catalog =====================

    /// <summary>The build's own settings slide in from the right; the page stays where it was.</summary>
    [ObservableProperty]
    private bool _isBuildSettingsOpen;

    [RelayCommand]
    private void OpenBuildSettings()
    {
        Section = ShellSection.Builds;
        IsBuildSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseBuildSettings() => IsBuildSettingsOpen = false;

    /// <summary>The tab to return to when the catalog closes: the one it was opened from.</summary>
    private BuildTab _tabBeforeCatalog = BuildTab.Mods;

    [RelayCommand]
    private void OpenCatalogForMods()
    {
        SelectBrowserKind(ProjectTypes.Mod);
        BuildTab = BuildTab.Catalog;
    }

    [RelayCommand]
    private void CloseCatalog() => BuildTab = _tabBeforeCatalog;

    // ===================== Builds: the mod list's slices =====================

    [ObservableProperty]
    private ModsScope _modsScope = ModsScope.All;

    partial void OnModsScopeChanged(ModsScope value)
    {
        ApplyModsFilter();
        OnPropertyChanged(nameof(IsScopeAll));
        OnPropertyChanged(nameof(IsScopeCatalog));
        OnPropertyChanged(nameof(IsScopeOwn));
        OnPropertyChanged(nameof(IsScopeDisabled));
    }

    [RelayCommand]
    private void SelectModsScope(string? scope)
    {
        if (Enum.TryParse<ModsScope>(scope, ignoreCase: true, out var parsed))
        {
            ModsScope = parsed;
        }
    }

    public bool IsScopeAll => ModsScope == ModsScope.All;

    public bool IsScopeCatalog => ModsScope == ModsScope.Catalog;

    public bool IsScopeOwn => ModsScope == ModsScope.Own;

    public bool IsScopeDisabled => ModsScope == ModsScope.Disabled;

    public int ModsCountAll => InstalledMods.Count(m => m.IsMod);

    public int ModsCountCatalog => InstalledMods.Count(m => m.IsMod && m.IsCatalog);

    public int ModsCountOwn => InstalledMods.Count(m => m.IsMod && !m.IsCatalog);

    public int ModsCountDisabled => InstalledMods.Count(m => m.IsMod && !m.Enabled);

    private void RaiseModsScopeCounts()
    {
        OnPropertyChanged(nameof(ModsCountAll));
        OnPropertyChanged(nameof(ModsCountCatalog));
        OnPropertyChanged(nameof(ModsCountOwn));
        OnPropertyChanged(nameof(ModsCountDisabled));
    }

    /// <summary>True when a row belongs to the slice the player picked.</summary>
    private bool IsInModsScope(InstalledModItem mod) => ModsScope switch
    {
        ModsScope.Catalog => mod.IsCatalog,
        ModsScope.Own => !mod.IsCatalog,
        ModsScope.Disabled => !mod.Enabled,
        _ => true
    };

    // ===================== Settings: what is folded =====================

    /// <summary>"More": the rarely needed settings, folded to one line until asked for.</summary>
    [ObservableProperty]
    private bool _isMoreSettingsOpen;

    [RelayCommand]
    private void ToggleMoreSettings() => IsMoreSettingsOpen = !IsMoreSettingsOpen;

    /// <summary>The backup schedule, limits and list, folded under the one-line summary.</summary>
    [ObservableProperty]
    private bool _isBackupDetailsOpen;

    [RelayCommand]
    private void ToggleBackupDetails() => IsBackupDetailsOpen = !IsBackupDetailsOpen;

    /// <summary>Memory and resolution on the settings page belong to the build that is open.</summary>
    public string GameSettingsScopeLabel => SelectedInstance is null
        ? string.Empty
        : Localize("Settings_ForBuild", "Values for the build \"{0}\"", SelectedInstance.Name);
}
