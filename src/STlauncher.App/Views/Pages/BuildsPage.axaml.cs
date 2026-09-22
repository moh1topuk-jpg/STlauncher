using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views.Pages;

public partial class BuildsPage : UserControl
{
    public BuildsPage()
    {
        InitializeComponent();
    }

    /// <summary>Opens the project card when a mod row is clicked.</summary>
    private void OnModRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ModBrowserItem item } ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        // Clicks on the row's own buttons belong to those buttons.
        if (e.Source is Button || e.Source is TextBlock { TemplatedParent: Button })
        {
            return;
        }

        viewModel.OpenProjectCommand.Execute(item);
    }

    /// <summary>
    /// Cards per row from the width there is: one below 560px, two to 840, three above.
    /// XAML has no width queries, so the count is set here whenever the area resizes.
    /// </summary>
    private void OnBrowserSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (this.FindControl<ItemsControl>("BrowserItems")?.ItemsPanelRoot is Avalonia.Controls.Primitives.UniformGrid grid)
        {
            var width = e.NewSize.Width;
            grid.Columns = width >= 840 ? 3 : width >= 560 ? 2 : 1;
        }
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

    /// <summary>A build picked from the "From the catalog" submenu.</summary>
    private void OnCatalogBuildClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem { DataContext: STlauncher.Core.Content.CatalogBuild build } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.AddCatalogBuildCommand.Execute(build);
        }
    }

    private async void OnImportModpackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        // The picker and the import are both fallible; an async void handler with no
        // try/catch turns any of that into an unhandled exception.
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                return;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = MainWindowViewModel.Localize("Modpack_PickTitle", "Select a modpack"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(
                        MainWindowViewModel.Localize("Modpack_PickType", "Modrinth modpack"))
                    {
                        Patterns = new[] { "*.mrpack" }
                    }
                }
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                await viewModel.ImportModpackAsync(path);
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportUiFailure(ex);
        }
    }
}
