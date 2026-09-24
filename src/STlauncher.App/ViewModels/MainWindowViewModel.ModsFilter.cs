using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace STlauncher.App.ViewModels;

/// <summary>Search inside the build's mod list, and the two bulk switches for the player's own mods.</summary>
public partial class MainWindowViewModel
{
    [ObservableProperty]
    private string _modsFilter = string.Empty;

    partial void OnModsFilterChanged(string value) => ApplyModsFilter();

    /// <summary>Hides rows that do not match; the counts in the summary stay for the whole build.</summary>
    private void ApplyModsFilter()
    {
        var query = ModsFilter.Trim();

        foreach (var mod in InstalledMods)
        {
            mod.IsHidden = query.Length > 0 &&
                           !mod.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                           !mod.FileName.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        OnPropertyChanged(nameof(HasOwnModsEnabled));
        OnPropertyChanged(nameof(HasOwnModsDisabled));
    }

    public bool HasOwnModsEnabled => InstalledMods.Any(m => m.IsMod && m.Enabled && !m.IsCatalog);

    public bool HasOwnModsDisabled => InstalledMods.Any(m => m.IsMod && !m.Enabled && !m.IsCatalog);

    [RelayCommand]
    private void DisableOwnMods() => SetOwnMods(enabled: false);

    [RelayCommand]
    private void EnableOwnMods() => SetOwnMods(enabled: true);

    /// <summary>The catalog's mods are the server's business; only the player's own are switched.</summary>
    private void SetOwnMods(bool enabled)
    {
        if (IsGameRunning)
        {
            return;
        }

        var targets = InstalledMods.Where(m => m.IsMod && !m.IsCatalog && m.Enabled != enabled).ToList();

        foreach (var mod in targets)
        {
            ToggleMod(mod);
        }

        Status = enabled
            ? Localize("Mods_OwnEnabled", "Your mods switched on: {0}", targets.Count)
            : Localize("Mods_OwnDisabled", "Your mods switched off: {0}", targets.Count);
    }
}
