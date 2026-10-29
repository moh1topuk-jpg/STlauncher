using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views.Pages;

public partial class BuildsPage : UserControl
{
    /// <summary>The page's width under which the three panels no longer fit at full size.</summary>
    private const double CompactBelow = 980;

    static BuildsPage()
    {
        // A question that opens under the row it was asked from ("Download · 1 MB") is
        // scrolled to, or a small window would keep its buttons below the edge.
        IsVisibleProperty.Changed.AddClassHandler<Border>((border, _) =>
        {
            if (border.IsVisible && border.Classes.Contains("scroll-to"))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => border.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
            }
        });
    }

    private MainWindowViewModel? _viewModel;

    public BuildsPage()
    {
        InitializeComponent();

        // XAML has no width queries: below this the page is "compact", and its styles give
        // the content the room the list and the wide labels took.
        SizeChanged += (_, e) =>
            this.FindControl<Grid>("Root")?.Classes.Set("compact", e.NewSize.Width < CompactBelow);

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

                // The list is one row wide; the view model lays it out in lines of as many
                // mods as it is told.
                _viewModel.ModColumns = 1;
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
    /// Brings the mod's row into view once the list has laid itself out; the tab may have
    /// just switched, so this waits for a layout pass rather than measuring an invisible
    /// panel. The list keeps only the rows on screen, so the row is asked for by its
    /// number: its control may not exist yet.
    /// </summary>
    private void RevealMod(string fileName)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel is null || this.FindControl<ItemsControl>("ModsItems") is not { } items)
            {
                return;
            }

            var index = _viewModel.ModRowIndexOf(fileName);

            if (index < 0)
            {
                return;
            }

            items.UpdateLayout();
            items.ScrollIntoView(index);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>A click on a pack's row picks it: its details show in the card beside the list.</summary>
    private void OnPackRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ResourcePackItem item } control ||
            DataContext is not MainWindowViewModel viewModel ||
            !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // The switch and the buttons on the row do their own thing.
        if (e.Source is Button or ToggleSwitch ||
            e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null ||
            e.Source is Visual visual2 && visual2.FindAncestorOfType<ToggleSwitch>() is not null)
        {
            return;
        }

        viewModel.SelectResourcePackCommand.Execute(item);
    }

    // ===================== The rows' menus =====================

    /// <summary>The "..." of a row opens the row's own right-click menu.</summary>
    private void OnRowMoreClick(object? sender, RoutedEventArgs e) => RowMenu.OpenFrom(sender);

    /// <summary>
    /// An entry of a row's menu that needs the page's view model. The menu lives in a
    /// popup, where a binding to the page does not reach, so the entry names its action
    /// in Tag and the row's item says what it acts on.
    /// </summary>
    private void OnRowMenuClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } viewModel || sender is not MenuItem { Tag: string action } entry)
        {
            return;
        }

        var item = entry.DataContext;

        System.Windows.Input.ICommand? command = (item, action) switch
        {
            (InstalledModItem, "update") => viewModel.UpdateModCommand,
            (InstalledModItem, "skip") => viewModel.SkipModVersionCommand,
            (InstalledModItem, "page") => viewModel.OpenModPageCommand,
            (InstalledModItem, "delete") => viewModel.UninstallModCommand,
            (ResourcePackItem, "update") => viewModel.UpdateResourcePackCommand,
            (ResourcePackItem, "up") => viewModel.MoveResourcePackUpCommand,
            (ResourcePackItem, "down") => viewModel.MoveResourcePackDownCommand,
            (ResourcePackItem, "page") => viewModel.OpenResourcePackOnModrinthCommand,
            (ResourcePackItem, "delete") => viewModel.DeleteResourcePackCommand,
            (ShaderPackItem, "update") => viewModel.UpdateShaderCommand,
            (ShaderPackItem, "page") => viewModel.OpenShaderOnModrinthCommand,
            (ShaderPackItem, "delete") => viewModel.DeleteShaderCommand,
            (ScreenshotItem, "open") => viewModel.OpenScreenshotCommand,
            (ScreenshotItem, "copy") => viewModel.CopyScreenshotCommand,
            (ScreenshotItem, "delete") => viewModel.DeleteScreenshotCommand,
            (InstalledModItem or ResourcePackItem or ShaderPackItem, "file") => viewModel.ShowContentFileCommand,
            _ => null
        };

        if (command?.CanExecute(item) == true)
        {
            command.Execute(item);
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

    /// <summary>Asks where to save the build as a .mrpack; the view model does the packing.</summary>
    private async void OnExportModpackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || viewModel.SelectedInstance is null)
        {
            return;
        }

        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;

            if (storage is null)
            {
                return;
            }

            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var suggested = new string(viewModel.SelectedInstance.Name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = MainWindowViewModel.Localize("Export_Title", "Save the build as a modpack"),
                SuggestedFileName = suggested + ".mrpack",
                DefaultExtension = "mrpack",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(MainWindowViewModel.Localize("Modpack_PickType", "Modrinth modpack"))
                    {
                        Patterns = new[] { "*.mrpack" }
                    }
                }
            });

            var path = file?.TryGetLocalPath();

            if (!string.IsNullOrEmpty(path))
            {
                await viewModel.ExportModpackAsync(path);
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportUiFailure(ex);
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
