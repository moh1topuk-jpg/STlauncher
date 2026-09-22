using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core;

namespace STlauncher.App.ViewModels;

/// <summary>
/// Where the builds live. Ten gigabytes of versions, libraries and worlds do not belong
/// on a small system drive; the player picks another folder, the launcher carries
/// everything over, then restarts itself pointed at the new place.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Raised when the launcher should start a fresh copy of itself and quit.</summary>
    public event Action? RequestRestartLauncher;

    /// <summary>The folder everything is in right now.</summary>
    public string DataDirectory => _paths.Root;

    public bool IsDefaultDataDirectory => DataLocation.IsSame(_paths.Root, DataLocation.DefaultRoot);

    [ObservableProperty]
    private bool _isMovingData;

    [ObservableProperty]
    private string _dataMoveStatus = string.Empty;

    [RelayCommand]
    private void OpenDataDirectory()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _paths.Root,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = Localize("Error_OpenFolder", "Failed to open the folder: {0}", ex.Message);
        }
    }

    /// <summary>Picks a folder, moves everything there, restarts.</summary>
    [RelayCommand]
    private async Task ChangeDataDirectoryAsync()
    {
        if (IsMovingData || PickFolderAsync is null)
        {
            return;
        }

        if (IsGameRunning || IsBusy || IsBuildSyncBusy)
        {
            DataMoveStatus = Localize("DataFolder_Busy", "Wait for the game and downloads to finish first");
            return;
        }

        var picked = await PickFolderAsync();

        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        await MoveDataToAsync(picked);
    }

    /// <summary>Back to AppData, by the same route.</summary>
    [RelayCommand]
    private Task ResetDataDirectoryAsync()
        => IsDefaultDataDirectory ? Task.CompletedTask : MoveDataToAsync(DataLocation.DefaultRoot);

    private async Task MoveDataToAsync(string target)
    {
        if (DataLocation.IsSame(target, _paths.Root))
        {
            DataMoveStatus = Localize("DataFolder_Same", "That is the current folder");
            return;
        }

        try
        {
            IsMovingData = true;

            var size = DataDirectoryMover.EstimateSize(_paths.Root);
            DataMoveStatus = Localize("DataFolder_Moving", "Moving {0} to {1}…", FormatSize(size), target);
            AppendConsole($"[data] moving {_paths.Root} -> {target}");

            var progress = new Progress<string>(part =>
                DataMoveStatus = Localize("DataFolder_MovingPart", "Moving: {0}…", part));

            var result = await DataDirectoryMover.MoveAsync(_paths.Root, target, progress);

            // The pointer is written only once every file is across; the launcher
            // reads it on the next start, which comes right now.
            DataLocation.Write(DataLocation.IsSame(target, DataLocation.DefaultRoot) ? null : target);

            AppendConsole($"[data] moved {result.Files} file(s), {result.Bytes} bytes; not removed: {string.Join(", ", result.NotRemoved)}");
            DataMoveStatus = Localize("DataFolder_Done", "Moved {0} file(s). Restarting the launcher…", result.Files);

            // Nothing else may write into the old folder from here on.
            _dataMoved = true;
            RequestRestartLauncher?.Invoke();
        }
        catch (Exception ex)
        {
            DataMoveStatus = Localize("DataFolder_Failed", "Could not move the data: {0}", ex.Message);
            AppendConsole($"[data] move failed: {ex}");
        }
        finally
        {
            IsMovingData = false;
        }
    }

    /// <summary>Set once the data is elsewhere; settings saves are dropped from then on.</summary>
    private bool _dataMoved;
}
