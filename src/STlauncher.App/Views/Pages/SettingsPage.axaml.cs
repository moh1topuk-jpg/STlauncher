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
        if (sender is not Button { Tag: string name } || this.FindControl<Control>(name) is not { } section)
        {
            return;
        }

        // Pointing at the folded section unfolds it; scrolling to a one-line card helps nobody.
        if (name == "MoreSection" && DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.IsMoreSettingsOpen = true;
        }

        section.BringIntoView();
    }

    /// <summary>One pass over the rows per keystroke: the page is a few dozen controls.</summary>
    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplySearch();

    private void OnSearchKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape && sender is TextBox { Text.Length: > 0 } box)
        {
            box.Text = string.Empty;
            e.Handled = true;
        }
    }

    private void ApplySearch()
    {
        if (this.FindControl<Control>("SearchRoot") is not { } root ||
            this.FindControl<TextBox>("SearchBox") is not { } box)
        {
            return;
        }

        var matched = Controls.SettingsSearch.Filter(root, box.Text);

        if (this.FindControl<Control>("SearchEmpty") is { } empty)
        {
            empty.IsVisible = matched == 0;
        }

        if (matched > 0)
        {
            this.FindControl<ScrollViewer>("SettingsScroll")?.ScrollToHome();
        }
    }
}
