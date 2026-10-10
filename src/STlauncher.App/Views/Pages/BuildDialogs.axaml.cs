using Avalonia.Controls;

namespace STlauncher.App.Views.Pages;

/// <summary>
/// The dialogs of the builds page: import from other launchers, the new build, the
/// build's settings. All of it is bindings; the view model's partials do the work.
/// </summary>
public partial class BuildDialogs : UserControl
{
    public BuildDialogs()
    {
        InitializeComponent();
    }
}
