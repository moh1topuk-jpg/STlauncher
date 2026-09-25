using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using STlauncher.Core.Http;
using STlauncher.Core.Launch;

namespace STlauncher.App.ViewModels;

public enum LaunchStageState
{
    Pending,
    Active,
    Done,
    Failed
}

/// <summary>One step of getting the game up, as the player sees it: a name and a state.</summary>
public partial class LaunchStageItem : ObservableObject
{
    public LaunchStageItem(string name)
    {
        Name = name;
    }

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private LaunchStageState _state = LaunchStageState.Pending;

    [ObservableProperty]
    private string _detail = string.Empty;

    public bool IsPending => State == LaunchStageState.Pending;

    public bool IsActive => State == LaunchStageState.Active;

    public bool IsDone => State == LaunchStageState.Done;

    public bool IsFailed => State == LaunchStageState.Failed;
}

/// <summary>
/// The first launch is minutes of downloading with nothing to look at but a status line.
/// This keeps a short list of steps - loader, mods, game files, Java, start - and marks
/// each one as it goes, so the player can see the launcher is working, and where.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<LaunchStageItem> LaunchStages { get; } = new();

    private LaunchStageItem? _stageLoader;
    private LaunchStageItem? _stageMods;
    private LaunchStageItem? _stageFiles;
    private LaunchStageItem? _stageJava;
    private LaunchStageItem? _stageStart;

    public bool HasLaunchStages => LaunchStages.Count > 0;

    /// <summary>The full list for a launch: the loader step only when there is a loader to install.</summary>
    private void BeginLaunchStages(bool withLoader)
    {
        LaunchStages.Clear();

        _stageLoader = withLoader ? Add(Localize("Launch_StageLoader", "Loader {0}", SelectedLoader)) : null;
        _stageMods = Add(Localize("Launch_StageMods", "Build mods"));
        _stageFiles = Add(Localize("Launch_StageFiles", "Game files"));
        _stageJava = Add(Localize("Launch_StageJava", "Java"));
        _stageStart = Add(Localize("Launch_StageStart", "Start"));

        OnPropertyChanged(nameof(HasLaunchStages));

        LaunchStageItem Add(string name)
        {
            var item = new LaunchStageItem(name);
            LaunchStages.Add(item);
            return item;
        }
    }

    /// <summary>A background sync of the build shows its one step in the same place.</summary>
    private void BeginSyncOnlyStages()
    {
        if (IsBusy)
        {
            // A launch is already telling this story; the sync feeds its mods step.
            return;
        }

        LaunchStages.Clear();
        _stageLoader = _stageFiles = _stageJava = _stageStart = null;
        _stageMods = new LaunchStageItem(Localize("Launch_StageMods", "Build mods"));
        _stageMods.State = LaunchStageState.Active;
        LaunchStages.Add(_stageMods);
        OnPropertyChanged(nameof(HasLaunchStages));
    }

    private static void Mark(LaunchStageItem? stage, LaunchStageState state, string? detail = null)
    {
        if (stage is null)
        {
            return;
        }

        stage.State = state;

        if (detail is not null)
        {
            stage.Detail = detail;
        }
    }

    /// <summary>Whatever step was running when the launch failed carries the mark.</summary>
    private void FailActiveStage()
    {
        foreach (var stage in LaunchStages.Where(s => s.IsActive))
        {
            stage.State = LaunchStageState.Failed;
            stage.Detail = Localize("Launch_Failed", "failed");
        }
    }

    /// <summary>Called by the build sync for each file it installs.</summary>
    private void ReportSyncProgress(int index, int total, string itemName)
    {
        Mark(_stageMods, LaunchStageState.Active, Localize("Launch_Files", "{0} of {1}", index, total) + " · " + itemName);
    }

    private void OnLaunchPhase(LaunchPhase phase)
    {
        switch (phase)
        {
            case LaunchPhase.Resolving:
                Mark(_stageFiles, LaunchStageState.Active, Localize("Launch_Resolving", "reading the version"));
                break;
            case LaunchPhase.Downloading:
                Mark(_stageFiles, LaunchStageState.Active);
                break;
            case LaunchPhase.Natives:
            case LaunchPhase.Assets:
                Mark(_stageFiles, LaunchStageState.Active, Localize("Launch_Unpacking", "unpacking"));
                break;
            case LaunchPhase.Java:
                Mark(_stageFiles, LaunchStageState.Done, Localize("Launch_Done", "done"));
                Mark(_stageJava, LaunchStageState.Active, Localize("Launch_JavaCheck", "checking, downloading if missing"));
                break;
            case LaunchPhase.Ready:
                Mark(_stageJava, LaunchStageState.Done, Localize("Launch_Done", "done"));
                Mark(_stageStart, LaunchStageState.Active);
                break;
        }
    }

    private void OnLaunchDownload(DownloadProgress p)
    {
        Progress = p.Fraction * 100;

        var detail = p.Failed > 0
            ? Localize("Status_FilesFailed", "Files {0}/{1}, failed: {2}", p.Completed, p.Total, p.Failed)
            : Localize("Launch_Files", "{0} of {1}", p.Completed, p.Total);

        Mark(_stageFiles, LaunchStageState.Active, detail);
        Status = p.Failed > 0
            ? Localize("Status_FilesFailed", "Files {0}/{1}, failed: {2}", p.Completed, p.Total, p.Failed)
            : Localize("Status_Files", "Files {0}/{1}", p.Completed, p.Total);
    }
}
