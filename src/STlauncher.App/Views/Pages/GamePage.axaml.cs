using Avalonia.Controls;
using Avalonia.Interactivity;

namespace STlauncher.App.Views.Pages;

public partial class GamePage : UserControl
{
    public GamePage()
    {
        InitializeComponent();
    }

    /// <summary>The button over the stage: the figure moves on to its next clip.</summary>
    private void OnNextPoseClick(object? sender, RoutedEventArgs e) => Stage.NextPose();
}
