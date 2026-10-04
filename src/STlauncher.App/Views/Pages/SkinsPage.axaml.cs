using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views.Pages;

public partial class SkinsPage : UserControl
{
    private MainWindowViewModel? _viewModel;
    private TopLevel? _topLevel;

    public SkinsPage()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Sheet.ColourPicked += (_, _) => _viewModel?.SkinColourPicked();

        DataContextChanged += (_, _) =>
        {
            _viewModel = DataContext as MainWindowViewModel;
            LoadWhenShown();
        };

        AttachedToVisualTree += (_, _) =>
        {
            _topLevel = TopLevel.GetTopLevel(this);

            // On the window, and on the way down: undo has to work wherever the focus
            // happens to be, not only after a click on the canvas.
            _topLevel?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

            if (_topLevel is Window window)
            {
                window.Closing += OnWindowClosing;
            }
        };

        DetachedFromVisualTree += (_, _) =>
        {
            _topLevel?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);

            if (_topLevel is Window window)
            {
                window.Closing -= OnWindowClosing;
            }

            _topLevel = null;
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty)
        {
            LoadWhenShown();
        }
    }

    /// <summary>The library is read when the page is first looked at, not when the launcher starts.</summary>
    private void LoadWhenShown()
    {
        // Asked of the section rather than of IsVisible: a page is "visible" for a moment
        // before its binding first says otherwise, and that moment is the launcher's start.
        if (_viewModel is { IsSkinsSection: true } viewModel)
        {
            viewModel.EnsureSkinLibraryLoaded();
        }
    }

    /// <summary>
    /// In a narrow window the canvas matters more than the figure beside it: below this
    /// width the preview steps aside and the squares get the room. XAML has no width
    /// queries, so it is decided here.
    /// </summary>
    private void OnEditorSizeChanged(object? sender, SizeChangedEventArgs e)
        => PreviewPanel.IsVisible = e.NewSize.Width >= 900;

    // ===================== Keys =====================

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { IsSkinsSection: true, IsSkinEditorOpen: true, IsSkinUnsavedPromptOpen: false } viewModel ||
            !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        // In the hex box Ctrl+Z belongs to the text.
        if (_topLevel?.FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Y:
                viewModel.RedoSkinCommand.Execute(null);
                break;
            case Key.Z:
                viewModel.UndoSkinCommand.Execute(null);
                break;
            case Key.S:
                viewModel.SaveSkinCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Closing the launcher is leaving the editor too. With unsaved pixels the window
    /// stays, the editor comes to the front and asks; once that is answered the next
    /// close goes through. A shutdown of the whole application is never held up.
    /// </summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason != WindowCloseReason.WindowClosing ||
            _viewModel is not { IsSkinEditorOpen: true, IsSkinEditorDirty: true } viewModel)
        {
            return;
        }

        e.Cancel = true;
        viewModel.Section = ShellSection.Skins;
        viewModel.CloseSkinEditorCommand.Execute(null);
    }

    // ===================== New skin, and files brought in =====================

    private void OnNewSkinClick(object? sender, RoutedEventArgs e)
    {
        NewSkinButton.Flyout?.Hide();
        _viewModel?.NewSkinCommand.Execute((sender as Control)?.Tag as string);
    }

    private async void OnAddPngClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } viewModel)
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
                Title = MainWindowViewModel.Localize("Skins_PickTitle", "Choose skin files"),
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(MainWindowViewModel.Localize("Skins_PickType", "Minecraft skin (PNG)"))
                    {
                        Patterns = new[] { "*.png" }
                    }
                }
            });

            var paths = files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToList();

            if (paths.Count > 0)
            {
                viewModel.AddSkinFiles(paths);
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportUiFailure(ex);
        }
    }

    /// <summary>Files are taken on the library only: over the editor a dropped picture would be ambiguous.</summary>
    private bool AcceptsDrop(DragEventArgs e)
        => _viewModel is { IsSkinEditorOpen: false } && e.DataTransfer.Contains(DataFormat.File);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accepted = AcceptsDrop(e);
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;

        if (_viewModel is not null)
        {
            _viewModel.IsSkinDropHover = accepted;
        }

        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.IsSkinDropHover = false;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        try
        {
            e.Handled = true;

            if (!AcceptsDrop(e))
            {
                viewModel.IsSkinDropHover = false;
                return;
            }

            var paths = (e.DataTransfer.TryGetFiles() ?? Enumerable.Empty<IStorageItem>())
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToList();

            viewModel.AddSkinFiles(paths);
        }
        catch (Exception ex)
        {
            viewModel.IsSkinDropHover = false;
            viewModel.ReportUiFailure(ex);
        }
    }
}
