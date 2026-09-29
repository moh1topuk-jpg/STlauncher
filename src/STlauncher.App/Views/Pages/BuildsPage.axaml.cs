using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views.Pages;

public partial class BuildsPage : UserControl
{
    private MainWindowViewModel? _viewModel;

    public BuildsPage()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.RevealModRequested -= RevealMod;
            }

            _viewModel = DataContext as MainWindowViewModel;

            if (_viewModel is not null)
            {
                _viewModel.RevealModRequested += RevealMod;
            }
        };
    }

    // ===================== Files dragged in =====================

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accepted = HasFiles(e);
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;

        if (_viewModel is not null)
        {
            _viewModel.IsDropHover = accepted;
        }

        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.IsDropHover = false;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var paths = (e.DataTransfer.TryGetFiles() ?? Enumerable.Empty<IStorageItem>())
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToList();

            e.Handled = true;
            await _viewModel.AddLocalFilesAsync(paths);
        }
        catch (Exception ex)
        {
            _viewModel.IsDropHover = false;
            _viewModel.ReportUiFailure(ex);
        }
    }

    // ===================== Scroll to a fresh mod =====================

    /// <summary>
    /// Brings the row into view once the list has laid itself out; the tab may have just
    /// switched, so this waits for a layout pass rather than measuring an invisible panel.
    /// </summary>
    private void RevealMod(string fileName)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel is null || this.FindControl<ItemsControl>("ModsItems") is not { } items)
            {
                return;
            }

            var index = -1;

            for (var i = 0; i < _viewModel.InstalledMods.Count; i++)
            {
                if (string.Equals(_viewModel.InstalledMods[i].FileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return;
            }

            items.UpdateLayout();
            items.ContainerFromIndex(index)?.BringIntoView();
        }, Avalonia.Threading.DispatcherPriority.Background);
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

    private void OnPackRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ResourcePackItem item } ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        // The switch and the arrows on the row do their own thing.
        if (e.Source is Button or ToggleSwitch ||
            e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null ||
            e.Source is Visual visual2 && visual2.FindAncestorOfType<ToggleSwitch>() is not null)
        {
            return;
        }

        viewModel.SelectResourcePackCommand.Execute(item);
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
