using Avalonia.Controls;

namespace STlauncher.App.Views.Pages;

/// <summary>
/// The result of "why does it lag?": a row per place with a dot and its milliseconds, then
/// the conclusion. The same view stands in Settings and on the Showtime page.
/// </summary>
public partial class LagCheckView : UserControl
{
    public LagCheckView()
    {
        InitializeComponent();
    }
}
