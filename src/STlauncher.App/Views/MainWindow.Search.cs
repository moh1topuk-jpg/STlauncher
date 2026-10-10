using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using STlauncher.App.ViewModels;
using STlauncher.App.Views.Pages;

namespace STlauncher.App.Views;

/// <summary>
/// The window's side of the search for everything: Ctrl+K, the palette made on first use,
/// the focus given back when it closes, and the entry in the top strip folding to an icon
/// when the strip is narrow.
/// </summary>
public partial class MainWindow
{
    /// <summary>The entry's full width and the air it wants around it.</summary>
    private const double SearchEntryRoom = 300 + 24;

    private MainWindowViewModel? _searchSource;

    private IInputElement? _focusBeforeSearch;

    private void InitializeSearch()
    {
        // On the way up, not down: a control that has its own use for Ctrl+K handles the
        // key first and the search stays out of its way.
        AddHandler(KeyDownEvent, OnSearchHotkey, RoutingStrategies.Bubble);

        DataContextChanged += (_, _) =>
        {
            if (_searchSource is not null)
            {
                _searchSource.PropertyChanged -= OnSearchSourceChanged;
            }

            _searchSource = DataContext as MainWindowViewModel;

            if (_searchSource is not null)
            {
                _searchSource.PropertyChanged += OnSearchSourceChanged;
            }
        };

        if (TopStripSlot.Parent is Control strip)
        {
            strip.SizeChanged += (_, _) => FitSearchEntry();
            TopStripContext.SizeChanged += (_, _) => FitSearchEntry();
        }
    }

    private void OnSearchHotkey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control && _searchSource is { } viewModel)
        {
            viewModel.ToggleSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSearchSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsSearchOpen):
                if (_searchSource?.IsSearchOpen == true)
                {
                    ShowSearch();
                }
                else
                {
                    RestoreFocusAfterSearch();
                }

                break;

            case nameof(MainWindowViewModel.SettingsSearchText):
                // After the section has changed and the page is on screen.
                Dispatcher.UIThread.Post(PushSettingsSearch, DispatcherPriority.Background);
                break;
        }
    }

    private void ShowSearch()
    {
        _focusBeforeSearch = FocusManager?.GetFocusedElement();

        if (SearchHost.Content is not SearchPalette palette)
        {
            SearchHost.Content = palette = new SearchPalette();
        }

        // The palette has just become visible; it can take focus once it is laid out.
        Dispatcher.UIThread.Post(palette.FocusQuery, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Esc puts the keyboard back where it was. After a result that led to another page
    /// that place is gone, and focus is simply let go of.
    /// </summary>
    private void RestoreFocusAfterSearch()
    {
        var previous = _focusBeforeSearch;
        _focusBeforeSearch = null;

        Dispatcher.UIThread.Post(() =>
        {
            if (previous is Control { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } control &&
                control.GetVisualRoot() is not null)
            {
                control.Focus();
            }
            else
            {
                FocusManager?.ClearFocus();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Types the text the search asked for into the settings page's own search box. The
    /// box is found by its name, so the page needs no code for this; a page that binds
    /// the box to <see cref="MainWindowViewModel.SettingsSearchText"/> already has the
    /// text, and nothing is done.
    /// </summary>
    private void PushSettingsSearch()
    {
        if (_searchSource is not { } viewModel)
        {
            return;
        }

        var box = this.GetVisualDescendants()
            .OfType<SettingsPage>()
            .FirstOrDefault()
            ?.FindControl<TextBox>("SearchBox");

        if (box is not null && !string.Equals(box.Text ?? string.Empty, viewModel.SettingsSearchText, StringComparison.Ordinal))
        {
            box.Text = viewModel.SettingsSearchText;
        }
    }

    /// <summary>The field when the strip has room for it beside the page's label, the icon when it has not.</summary>
    private void FitSearchEntry()
    {
        if (TopStripSlot.Parent is not Grid strip || strip.Children.Count < 3)
        {
            return;
        }

        var taken = TopStripContext.Bounds.Width + TopStripContext.Margin.Left + strip.Children[2].Bounds.Width;
        var compact = strip.Bounds.Width - taken < SearchEntryRoom;

        SearchEntry.Classes.Set("compact", compact);
    }
}
