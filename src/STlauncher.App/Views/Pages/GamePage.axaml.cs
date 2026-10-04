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

    /// <summary>Skins: the link in the "My skin" flyout. The flyout is closed first, or it would hang over the new page.</summary>
    private void OnOpenSkinsClick(object? sender, RoutedEventArgs e)
    {
        MySkinButton.Flyout?.Hide();
        (DataContext as STlauncher.App.ViewModels.MainWindowViewModel)?.OpenSkinsCommand.Execute(null);
    }
}
