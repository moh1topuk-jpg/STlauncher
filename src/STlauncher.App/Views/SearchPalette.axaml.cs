using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views;

public partial class SearchPalette : UserControl
{
    private Point? _lastPointer;

    public SearchPalette()
    {
        InitializeComponent();

        // Before the text box sees them: Up and Down would move its caret, Tab would leave it.
        // Handled ones too: the window binds Esc to closing the screenshot viewer, and a
        // key binding marks the key handled before any handler here is asked.
        AddHandler(KeyDownEvent, OnPaletteKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>The field takes the keyboard every time the search opens.</summary>
    public void FocusQuery()
    {
        _lastPointer = null;
        Scroller.ScrollToHome();
        QueryBox.Focus();
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                Move(viewModel, 1);
                break;
            case Key.Up:
                Move(viewModel, -1);
                break;
            case Key.PageDown:
                Move(viewModel, 6);
                break;
            case Key.PageUp:
                Move(viewModel, -6);
                break;
            case Key.Tab:
                viewModel.ToggleSearchSecondary();
                break;
            case Key.Enter:
                viewModel.RunSearchRowCommand.Execute(null);
                break;
            case Key.Escape:
                viewModel.CloseSearchCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Move(MainWindowViewModel viewModel, int delta)
    {
        viewModel.MoveSearchSelection(delta);

        if (viewModel.SelectedSearchRow is not { } row)
        {
            return;
        }

        // The first row brings its caption along.
        if (viewModel.SearchRows.Count > 1 && ReferenceEquals(viewModel.SearchRows[1], row))
        {
            Scroller.ScrollToHome();
        }
        else
        {
            Rows.ContainerFromItem(row)?.BringIntoView();
        }
    }

    private void OnQueryChanged(object? sender, TextChangedEventArgs e) => Scroller.ScrollToHome();

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        ViewModel?.CloseSearchCommand.Execute(null);
        e.Handled = true;
    }

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        // The row's own button has its own click.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (sender is Control { DataContext: SearchRow row })
        {
            ViewModel?.RunSearchRowCommand.Execute(row);
        }
    }

    private void OnSecondaryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SearchRow row })
        {
            ViewModel?.RunSearchRowSecondaryCommand.Execute(row);
        }
    }

    /// <summary>
    /// The pointer selects the row it moves over. Only a real move counts: a list that
    /// scrolls under a resting pointer must not take the selection from the arrow keys.
    /// </summary>
    private void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        var moved = _lastPointer is { } last && last != position;
        _lastPointer = position;

        if (moved && sender is Control { DataContext: SearchRow { IsSelected: false } row })
        {
            ViewModel?.SelectSearchRow(row);
        }
    }
}
