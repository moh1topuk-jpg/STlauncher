using Avalonia.Controls;
using Avalonia.Interactivity;

namespace STlauncher.App.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>The index on the left scrolls to the section the button names in its Tag.</summary>
    private void OnIndexClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && this.FindControl<Control>(name) is { } section)
        {
            section.BringIntoView();
        }
    }
}
