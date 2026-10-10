using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views.Pages;

public partial class ConsolePage : UserControl
{
    public ConsolePage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The selected lines, in the order they stand in the log; with nothing selected, all
    /// of it. The log is a list, so there is no other way to take text out of it.
    /// </summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        try
        {
            var selected = Lines.SelectedItems?.OfType<string>().ToHashSet();
            var lines = selected is { Count: > 0 }
                ? viewModel.Console.Where(selected.Contains)
                : viewModel.Console;
            var text = string.Join(Environment.NewLine, lines);

            if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportUiFailure(ex);
        }
    }
}
