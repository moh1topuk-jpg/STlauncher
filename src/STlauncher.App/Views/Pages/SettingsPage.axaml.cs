using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace STlauncher.App.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();

        // The first group is where the page opens.
        Loaded += (_, _) => MarkActiveGroup();
    }

    /// <summary>The index on the left scrolls to the group the button names in its Tag.</summary>
    private void OnIndexClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } || this.FindControl<Control>(name) is not { } section)
        {
            return;
        }

        // Pointing at the folded group unfolds it; scrolling to a one-line heading helps nobody.
        if (name == "MoreSection" && DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.IsMoreSettingsOpen = true;
        }

        // After layout: a group that has just unfolded has its real height only then.
        Dispatcher.UIThread.Post(() => ScrollToGroup(section), DispatcherPriority.Background);
    }

    /// <summary>Puts the group's heading at the top of the view, as far as the page's length allows.</summary>
    private void ScrollToGroup(Control section)
    {
        if (this.FindControl<ScrollViewer>("SettingsScroll") is not { Content: Control content } scroll ||
            section.TranslatePoint(default, content) is not { } origin)
        {
            return;
        }

        scroll.Offset = new Vector(0, Math.Max(0, origin.Y));
    }

    private void OnSettingsScrolled(object? sender, ScrollChangedEventArgs e) => MarkActiveGroup();

    /// <summary>
    /// The index marks the group the player is reading: the last one whose heading has
    /// reached the top of the view, or the last group once the page is scrolled to its end.
    /// </summary>
    private void MarkActiveGroup()
    {
        if (this.FindControl<ScrollViewer>("SettingsScroll") is not { Content: Control content } scroll ||
            this.FindControl<Panel>("IndexPanel") is not { } index)
        {
            return;
        }

        var atEnd = scroll.Extent.Height > scroll.Viewport.Height &&
                    scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1;
        Button? active = null;

        foreach (var button in index.Children.OfType<Button>())
        {
            if (button.Tag is not string name || this.FindControl<Control>(name) is not { IsEffectivelyVisible: true } section ||
                section.TranslatePoint(default, content) is not { } origin)
            {
                continue;
            }

            // A third of the view: the group counts as reached a little before its heading touches the top.
            if (active is null || atEnd || origin.Y <= scroll.Offset.Y + scroll.Viewport.Height / 3)
            {
                active = button;
            }
        }

        foreach (var button in index.Children.OfType<Button>())
        {
            button.Classes.Set("active", ReferenceEquals(button, active));
        }
    }

    /// <summary>"As in the game" or the player's own size: the two boxes appear with the second.</summary>
    private void OnResolutionModeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode } && DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.UseDefaultResolution = mode == "default";
        }
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
