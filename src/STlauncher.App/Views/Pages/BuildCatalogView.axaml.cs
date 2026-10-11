using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views.Pages;

/// <summary>
/// The catalog of the builds page. The view model decides what is found and installed;
/// the view keeps what is only about the screen: where "back" leads in words, the picked
/// row opening beside the list, and what gives way in a narrow window.
/// </summary>
public partial class BuildCatalogView : UserControl
{
    /// <summary>Below this the list and the details do not fit side by side.</summary>
    private const double SideBySideWidth = 760;

    /// <summary>The width the page offers this view, as of the last measure.</summary>
    private double _offeredWidth;

    private MainWindowViewModel? _viewModel;

    /// <summary>The tab the catalog was opened from: the back button names it.</summary>
    private BuildTab _openedFrom = BuildTab.Mods;

    public BuildCatalogView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = DataContext as MainWindowViewModel;

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                TrackTab(_viewModel.BuildTab);
                ArrangePanes();
            }
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.BuildTab):
                TrackTab(_viewModel.BuildTab);
                break;

            // Another mod opened in the details starts from the top: what "Add" will bring
            // is written there, and the panel would otherwise stay where the last mod was left.
            case nameof(MainWindowViewModel.OpenedProject):
                if (this.FindControl<ScrollViewer>("ProjectScroll") is { } scroll)
                {
                    scroll.Offset = default;
                }

                break;

            case nameof(MainWindowViewModel.IsProjectOpen):
                ArrangePanes();
                break;
        }
    }

    /// <summary>"To the build's mods", "to the build's shaders": the way back says where it leads.</summary>
    private void TrackTab(BuildTab tab)
    {
        if (tab is not BuildTab.Catalog and not BuildTab.Settings)
        {
            _openedFrom = tab;
            return;
        }

        if (tab != BuildTab.Catalog || this.FindControl<TextBlock>("BackLabel") is not { } label)
        {
            return;
        }

        label.Text = _openedFrom switch
        {
            BuildTab.Mods => MainWindowViewModel.Localize("Catalog_BackMods", "To the build's mods"),
            BuildTab.ResourcePacks => MainWindowViewModel.Localize("Catalog_BackPacks", "To the build's resource packs"),
            BuildTab.Shaders => MainWindowViewModel.Localize("Catalog_BackShaders", "To the build's shaders"),
            _ => MainWindowViewModel.Localize("Catalog_Back", "To the build")
        };
    }

    /// <summary>
    /// What the page offers is the measure of "narrow", not what the view ends up with:
    /// the details card has a fixed width, and with it the view asks for - and is given -
    /// more than the window has, so its own bounds never look narrow.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Width) && System.Math.Abs(availableSize.Width - _offeredWidth) > 0.5)
        {
            _offeredWidth = availableSize.Width;
            ArrangePanes();
        }

        return base.MeasureOverride(availableSize);
    }

    /// <summary>
    /// The details stand beside the search, the list and the pager while all fit, as tall
    /// as the view. In a narrow window the details take the whole view - the search and
    /// the pager step aside with the list - until they are closed.
    /// </summary>
    private void ArrangePanes()
    {
        if (this.FindControl<Panel>("ListPane") is not { } list ||
            this.FindControl<Border>("DetailsPane") is not { } details ||
            this.FindControl<StackPanel>("Filters") is not { } filters ||
            this.FindControl<Border>("Pager") is not { } pager)
        {
            return;
        }

        var narrow = _offeredWidth > 0 && _offeredWidth < SideBySideWidth;
        var open = _viewModel?.IsProjectOpen == true;

        list.IsVisible = !(narrow && open);
        filters.IsVisible = !(narrow && open);
        pager.IsVisible = !(narrow && open);
        details.Width = narrow ? double.NaN : 344;
        details.Margin = narrow ? new Thickness(0, 0, 0, 14) : new Thickness(22, 0, 0, 14);
        Grid.SetColumn(details, narrow ? 0 : 1);
        Grid.SetColumnSpan(details, narrow ? 2 : 1);
    }

    /// <summary>Opens the details when a result's row is clicked.</summary>
    private void OnModRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ModBrowserItem item } ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        // Clicks on the row's own button belong to that button.
        if (e.Source is Button || e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        viewModel.OpenProjectCommand.Execute(item);
    }

    /// <summary>The chip row scrolls sideways with the wheel, since it has no vertical extent.</summary>
    private void OnChipWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is ScrollViewer scroll)
        {
            scroll.Offset = new Vector(scroll.Offset.X - e.Delta.Y * 60, scroll.Offset.Y);
            e.Handled = true;
        }
    }
}
