using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using STlauncher.App.ViewModels;
using STlauncher.Core.Emotes;

namespace STlauncher.App.Views.Pages;

public partial class GamePage : UserControl
{
    public GamePage()
    {
        InitializeComponent();
    }

    /// <summary>The button over the stage: the figure moves on to its next clip.</summary>
    private void OnNextPoseClick(object? sender, RoutedEventArgs e) => Stage.NextPose();

    // ===================== Emotes =====================

    /// <summary>The build's emotes are looked for only once there is a home screen to show them on.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        (DataContext as MainWindowViewModel)?.WatchHomeEmotes();
    }

    /// <summary>
    /// One of the figure's own poses, picked from the list on the pose pill. The list
    /// stays open: it is beside the stage, and the next thing a player does is try another.
    /// </summary>
    private void OnPosePick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PoseChoice pose })
        {
            Stage.PlayPose(pose.Key);
        }
    }

    /// <summary>One of the build's emotes, picked from the same list.</summary>
    private void OnEmotePick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Emote emote })
        {
            Stage.PlayEmote(emote);
        }
    }
}
