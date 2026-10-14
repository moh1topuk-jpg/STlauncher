using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views;

/// <summary>
/// The "what will be installed" dialog of a build code. The view model does the work;
/// this side gives the dialog the keyboard when it opens and makes Esc mean "Cancel"
/// wherever the focus happens to be.
/// </summary>
public partial class BuildCodePreviewView : UserControl
{
    private MainWindowViewModel? _viewModel;

    private TopLevel? _topLevel;

    public BuildCodePreviewView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelChanged;
            }

            _viewModel = DataContext as MainWindowViewModel;

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // On the window and on the way down, handled keys too: the window binds Esc to
        // closing the screenshot viewer, and a key binding marks the key handled first.
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _topLevel = null;

        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel is { IsBuildPreviewOpen: true } viewModel)
        {
            viewModel.CancelBuildPreviewCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.BuildPreviewState) || _viewModel is not { IsBuildPreviewOpen: true })
        {
            return;
        }

        // The dialog has just changed what it shows; its controls can take focus once laid out.
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel is not { } viewModel)
            {
                return;
            }

            if (viewModel.IsBuildPreviewReady)
            {
                // A new build's list starts at its top, whatever the last one was scrolled to.
                FilesScroll.ScrollToHome();
                NameBox.Focus();
                NameBox.CaretIndex = NameBox.Text?.Length ?? 0;
            }
            else if (viewModel.IsBuildPreviewFetching)
            {
                FetchCancel.Focus();
            }
            else if (viewModel.IsBuildPreviewProblem)
            {
                ProblemClose.Focus();
            }
        }, DispatcherPriority.Loaded);
    }
}
