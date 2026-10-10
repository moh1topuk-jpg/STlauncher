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
    private const double SideBySideWidth = 660;

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

    private void OnBodySizeChanged(object? sender, SizeChangedEventArgs e) => ArrangePanes();

    /// <summary>
    /// The details stand beside the list while both fit; in a narrow window the details
    /// take the list's place until they are closed.
    /// </summary>
    private void ArrangePanes()
    {
        if (this.FindControl<Grid>("Body") is not { } body ||
            this.FindControl<Panel>("ListPane") is not { } list ||
            this.FindControl<Border>("DetailsPane") is not { } details)
        {
            return;
        }

        var narrow = body.Bounds.Width > 0 && body.Bounds.Width < SideBySideWidth;
        var open = _viewModel?.IsProjectOpen == true;

        list.IsVisible = !(narrow && open);
        details.Width = narrow ? double.NaN : 320;
        details.Margin = narrow ? default : new Thickness(18, 0, 0, 0);
        details.Padding = narrow ? default : new Thickness(18, 0, 0, 0);
        details.BorderThickness = narrow ? default : new Thickness(1, 0, 0, 0);
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
