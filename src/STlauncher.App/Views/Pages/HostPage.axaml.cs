using System.Collections.Specialized;
using Avalonia.Controls;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views.Pages;

public partial class HostPage : UserControl
{
    private MainWindowViewModel? _viewModel;
    private bool _followConsole = true;

    public HostPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch(DataContext as MainWindowViewModel);
        ConsoleScroll.ScrollChanged += OnConsoleScrolled;
    }

    private void Watch(MainWindowViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.HostConsole.CollectionChanged -= OnConsoleChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.HostConsole.CollectionChanged += OnConsoleChanged;
        }
    }

    /// <summary>
    /// The console follows its last line, the way a terminal does - until the player
    /// scrolls up to read something, and again once they are back at the end. It is the
    /// console's own scroller that moves, never BringIntoView: that would also scroll the
    /// page around it, and every new line would drag the page down to the console.
    /// </summary>
    private void OnConsoleScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
        {
            // The text grew (or the box did): stay at the end if that is where the view was.
            if (_followConsole)
            {
                ConsoleScroll.ScrollToEnd();
            }

            return;
        }

        if (e.OffsetDelta.Y != 0)
        {
            _followConsole = ConsoleScroll.Offset.Y + ConsoleScroll.Viewport.Height >= ConsoleScroll.Extent.Height - 24;
        }
    }

    /// <summary>A console that was cleared - another server, a new start - is followed from its first line again.</summary>
    private void OnConsoleChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _followConsole = true;
        }
    }
}
