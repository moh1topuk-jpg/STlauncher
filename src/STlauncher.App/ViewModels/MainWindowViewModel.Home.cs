using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Instances;

namespace STlauncher.App.ViewModels;

/// <summary>
/// What the home screen shows besides the build to continue with: the other builds one
/// click away, and the server as a way to play rather than as a statistic.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Up to three builds other than the selected one, the most recently played first.</summary>
    public ObservableCollection<Instance> HomeBuilds { get; } = new();

    private void RefreshHomeBuilds()
    {
        HomeBuilds.Clear();

        foreach (var instance in _allInstances
                     .Where(i => !ReferenceEquals(i, SelectedInstance))
                     .OrderByDescending(i => i.LastPlayedAt ?? DateTimeOffset.MinValue)
                     .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(3))
        {
            HomeBuilds.Add(instance);
        }

        OnPropertyChanged(nameof(PlayOnServerLabel));
    }

    /// <summary>Makes a build from the home screen's strip the one to play.</summary>
    [RelayCommand]
    private void SelectBuild(Instance? instance)
    {
        if (instance is null || IsBusy || IsGameRunning)
        {
            return;
        }

        SelectedInstance = instance;
    }

    /// <summary>
    /// "Play" on a row of the home screen's list: that build becomes the one on screen and
    /// starts, as if it had been picked and the big button pressed.
    /// </summary>
    [RelayCommand]
    private async Task PlayBuildAsync(Instance? instance)
    {
        if (instance is null || IsBusy || IsGameRunning)
        {
            return;
        }

        SelectedInstance = instance;

        // The build's own loader version is read right after it is selected. Starting
        // before it has arrived would install whichever version the picker falls back to.
        for (var waited = 0; IsLoaderBusy && waited < 100; waited++)
        {
            await Task.Delay(50);
        }

        if (ReferenceEquals(SelectedInstance, instance))
        {
            await PlayAsync();
        }
    }

    /// <summary>The wizard lives on the builds page; the home screen's "new build" goes there and opens it.</summary>
    [RelayCommand]
    private void NewBuildFromHome()
    {
        Section = ShellSection.Builds;
        OpenNewBuild();
    }

    /// <summary>"Play on Showtime": the server tile's title.</summary>
    public string PlayOnServerLabel => Localize("Home_PlayOn", "Play on {0}", ServerName);

    /// <summary>"13 online · Peak: 71 · Record: 76", or that the server is quiet.</summary>
    public string ServerTileLine
    {
        get
        {
            if (!IsServerOnline)
            {
                return Localize("Home_ServerQuiet", "not responding right now");
            }

            var online = Localize("Home_OnlineShort", "{0} online", ServerOnlineValue);
            return string.IsNullOrEmpty(ServerPeakLine) ? online : online + " · " + ServerPeakLine;
        }
    }

    partial void OnServerPeakLineChanged(string value) => OnPropertyChanged(nameof(ServerTileLine));
}
