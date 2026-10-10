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

        BuildTitle.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
            {
                FitTitle();
            }
        };
        SizeChanged += (_, _) => FitTitle();
    }

    /// <summary>The button beside the pose list: the figure moves on to its next clip.</summary>
    private void OnNextPoseClick(object? sender, RoutedEventArgs e) => Stage.NextPose();

    // ===================== The build's name =====================

    /// <summary>
    /// The name stands at full size while two lines can hold it. A longer one gives up size
    /// first, and only then letters: a build called after its whole mod list should still
    /// be told apart from its neighbour. A narrow window moves every name one step down.
    /// </summary>
    private void FitTitle()
    {
        var length = BuildTitle.Text?.Length ?? 0;
        var step = length > 44 ? 2 : length > 26 ? 1 : 0;

        if (Bounds.Width is > 0 and < 960 && length > 12)
        {
            step++;
        }

        BuildTitle.Classes.Set("long", step == 1);
        BuildTitle.Classes.Set("longer", step >= 2);
    }

    // ===================== What is new =====================

    /// <summary>"Got it" under the list of changes: the list closes, and the line that led to it goes with it.</summary>
    private void OnWhatsNewRead(object? sender, RoutedEventArgs e)
    {
        WhatsNewButton.Flyout?.Hide();
        (DataContext as MainWindowViewModel)?.DismissWhatsNewCommand.Execute(null);
    }

    // ===================== Emotes =====================

    /// <summary>The build's emotes are looked for only once there is a home screen to show them on.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        (DataContext as MainWindowViewModel)?.WatchHomeEmotes();
    }

    /// <summary>
    /// One of the figure's own poses, picked from the list on the pose button. The list
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

    /// <summary>Skins: the link in the "Change skin" flyout. The flyout is closed first, or it would hang over the new page.</summary>
    private void OnOpenSkinsClick(object? sender, RoutedEventArgs e)
    {
        MySkinButton.Flyout?.Hide();
        (DataContext as STlauncher.App.ViewModels.MainWindowViewModel)?.OpenSkinsCommand.Execute(null);
    }
}
