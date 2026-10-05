using Avalonia.Controls;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views.Pages;

/// <summary>
/// The server's mods tab. It asks the view model to start following the selected server
/// once it has one, so a launcher whose player never opens the tab reads no mods folder.
/// </summary>
public partial class HostModsView : UserControl
{
    public HostModsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => (DataContext as MainWindowViewModel)?.WatchHostMods();
    }
}
