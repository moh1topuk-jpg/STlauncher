using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Instances;
using STlauncher.Core.Launch;

namespace STlauncher.App.ViewModels;

/// <summary>
/// Game settings from one build into another: controls, sound, graphics, interface. A new
/// build starts with the current one's settings unless told not to; an existing build can
/// take them from any other build on the settings tab.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Every build but the selected one, for the "copy settings from" picker.</summary>
    public ObservableCollection<Instance> OtherInstances { get; } = new();

    [ObservableProperty]
    private Instance? _copySettingsSource;

    public bool HasOtherInstances => OtherInstances.Count > 0;

    /// <summary>The wizard's checkbox: take the current build's settings into the new one.</summary>
    [ObservableProperty]
    private bool _newBuildCopySettings = true;

    public string NewBuildCopySettingsLabel => SelectedInstance is null
        ? Localize("Builds_WizardCopySettings", "Take game settings from “{0}”", string.Empty)
        : Localize("Builds_WizardCopySettings", "Take game settings from “{0}”", SelectedInstance.Name);

    public bool CanCopyNewBuildSettings => SelectedInstance is not null;

    private void RefreshOtherInstances()
    {
        var keep = CopySettingsSource?.Id;

        OtherInstances.Clear();

        foreach (var instance in _allInstances.Where(i => !ReferenceEquals(i, SelectedInstance)))
        {
            OtherInstances.Add(instance);
        }

        CopySettingsSource = OtherInstances.FirstOrDefault(i => i.Id == keep)
                             ?? OtherInstances.OrderByDescending(i => i.LastPlayedAt ?? DateTimeOffset.MinValue).FirstOrDefault();

        OnPropertyChanged(nameof(HasOtherInstances));
        OnPropertyChanged(nameof(NewBuildCopySettingsLabel));
        OnPropertyChanged(nameof(CanCopyNewBuildSettings));
    }

    [RelayCommand]
    private void CopyGameSettings()
    {
        if (CopySettingsSource is null || SelectedInstance is null || IsGameRunning)
        {
            return;
        }

        try
        {
            var copied = GameOptions.CopySettings(_instances.GameDirectory(CopySettingsSource), InstanceDirectory);
            Status = copied == 0
                ? Localize("Transfer_Nothing", "“{0}” has no saved game settings yet", CopySettingsSource.Name)
                : Localize("Transfer_Done", "Game settings from “{0}” copied: {1} values", CopySettingsSource.Name, copied);
        }
        catch (Exception ex)
        {
            Status = Localize("Transfer_Failed", "Could not copy the settings: {0}", ex.Message);
        }
    }

    /// <summary>Called by the wizard after the new build's folder exists.</summary>
    private void CopySettingsIntoNewBuild(Instance created)
    {
        if (!NewBuildCopySettings || SelectedInstance is null || ReferenceEquals(SelectedInstance, created))
        {
            return;
        }

        try
        {
            GameOptions.CopySettings(_instances.GameDirectory(SelectedInstance), _instances.GameDirectory(created));
        }
        catch (Exception ex)
        {
            AppendConsole($"[transfer] settings not copied into {created.Name}: {ex.Message}");
        }
    }
}
